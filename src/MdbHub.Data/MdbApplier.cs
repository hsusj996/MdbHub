using System.Data.OleDb;
using MdbHub.Core;

namespace MdbHub.Data;

public enum ApplyMode
{
    /// <summary>Overwrite rows whose PK already exists, insert the rest.</summary>
    Upsert,
    /// <summary>Insert only; abort without writing if any existing row would have to change.</summary>
    InsertOnly,
}

/// <param name="Source">Table schema in the patch mdb.</param>
/// <param name="Target">Table schema in the mdb being modified.</param>
/// <param name="Diff">Left = target, Right = source: Added = to insert, Changed = to overwrite. Target-only rows are left alone.</param>
public sealed record TableApplyPlan(TableSchema Source, TableSchema Target, TableDiff Diff)
{
    public int Inserts => Diff.Rows.Count(r => r.Kind == RowDiffKind.Added);
    public int Updates => Diff.Rows.Count(r => r.Kind == RowDiffKind.Changed);
    public int Unchanged => Diff.UnchangedCount;
}

/// <param name="SkippedTablesWithoutPrimaryKey">Patch tables that have no usable key (neither configured nor declared) and were left out. Tables the config says to ignore are not listed.</param>
/// <param name="KeyProblems">Config rules that do not fit the actual tables (e.g. a key column the table lacks).</param>
public sealed record ApplyPlan(
    IReadOnlyList<TableApplyPlan> Tables,
    IReadOnlyList<string> SkippedTablesWithoutPrimaryKey,
    IReadOnlyList<string> KeyProblems)
{
    public int TotalInserts => Tables.Sum(t => t.Inserts);
    public int TotalUpdates => Tables.Sum(t => t.Updates);
}

/// <param name="BackupPath">Null when nothing needed to change or the backup was turned off.</param>
public sealed record ApplyResult(ApplyPlan Plan, string? BackupPath);

public static class MdbApplier
{
    /// <summary>
    /// Computes what <see cref="Apply"/> would do without touching either file.
    /// Row keys come from <paramref name="config"/> where it has a rule, otherwise from the declared primary key.
    /// </summary>
    public static ApplyPlan Preview(string sourcePath, string targetPath, AppConfig? config = null)
    {
        var resolvedSource = KeyResolver.Resolve(MdbReader.ReadSchema(sourcePath), config);
        var resolvedTarget = KeyResolver.Resolve(MdbReader.ReadSchema(targetPath), config);
        var (sourceSchema, targetSchema) = (resolvedSource.Schema, resolvedTarget.Schema);

        var skipped = resolvedSource.NeedingKey.Select(t => t.Name).ToList();
        var sourceTables = sourceSchema.Tables.Where(t => t.PrimaryKey.Count > 0).ToList();
        var keyProblems = resolvedSource.Problems.Concat(resolvedTarget.Problems).Distinct().ToList();

        // The patch may hold only some tables, so compare just those against the target.
        var targetSubset = targetSchema.Tables.Where(t => sourceTables.Any(s => string.Equals(s.Name, t.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var differences = SchemaValidator.Compare(new DatabaseSchema(sourceTables), new DatabaseSchema(targetSubset));
        if (differences.Count > 0) throw new SchemaMismatchException(differences);

        var tables = sourceTables.Select(s =>
        {
            var t = targetSchema.Find(s.Name)!;
            var sourceData = MdbReader.ReadTable(sourcePath, s);
            var diff = TableComparer.Compare(ReadRelatedTargetRows(targetPath, t, sourceData), sourceData, "대상 mdb", "반영할 mdb");
            return new TableApplyPlan(s, t, diff);
        }).ToList();

        return new ApplyPlan(tables, skipped, keyProblems);
    }

    /// <summary>A PK column with more distinct patch values than this is not used to narrow the target read (keeps the IN list small).</summary>
    private const int MaxFilterValuesPerColumn = 200;

    /// <summary>
    /// Reads only target rows that could match a patch row: the patch's distinct values per (low-cardinality) PK column
    /// are used as IN filters. The result is a superset of the matching rows; target rows the patch doesn't mention are never touched anyway.
    /// </summary>
    private static TableData ReadRelatedTargetRows(string targetPath, TableSchema target, TableData source)
    {
        if (source.Rows.Count == 0) return new TableData(target, []);

        var filters = new List<ColumnFilter>();
        foreach (var key in source.Schema.PrimaryKey)
        {
            var index = source.Schema.IndexOf(key);
            var values = source.Rows.Select(r => r[index]).OfType<object>().Distinct().ToList();
            if (values.Count > 0 && values.Count <= MaxFilterValuesPerColumn)
                filters.Add(new ColumnFilter(key, values));
        }
        return MdbReader.ReadTable(targetPath, target, filters);
    }

    /// <summary>
    /// Validates the schema, checks the mode, backs up the target (unless disabled), then applies every change in one transaction.
    /// Any failure rolls the whole change back.
    /// </summary>
    public static ApplyResult Apply(string sourcePath, string targetPath, ApplyMode mode = ApplyMode.Upsert, bool backup = true, AppConfig? config = null)
    {
        var plan = Preview(sourcePath, targetPath, config);

        if (mode == ApplyMode.InsertOnly && plan.TotalUpdates > 0)
            throw new InvalidOperationException($"이미 존재하는 행 {plan.TotalUpdates}개가 달라서 삽입 전용 모드로 진행할 수 없습니다.");
        if (plan.TotalInserts == 0 && plan.TotalUpdates == 0)
            return new ApplyResult(plan, null);

        var backupPath = backup ? MdbBackup.Create(targetPath) : null;

        using var conn = MdbFile.Open(targetPath);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in plan.Tables)
            {
                foreach (var row in table.Diff.Rows)
                {
                    if (row.Kind == RowDiffKind.Added) Insert(conn, tx, table, row);
                    else if (row.Kind == RowDiffKind.Changed) Update(conn, tx, table, row);
                }
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return new ApplyResult(plan, backupPath);
    }

    private static void Insert(OleDbConnection conn, OleDbTransaction tx, TableApplyPlan table, RowDiff row)
    {
        var columns = table.Source.Columns.Select(c => c.Name).ToList();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {MdbFile.Quote(table.Target.Name)} ({string.Join(", ", columns.Select(MdbFile.Quote))}) " +
                          $"VALUES ({string.Join(", ", columns.Select(_ => "?"))})";
        foreach (var value in row.Right!) cmd.Parameters.AddWithValue("?", value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void Update(OleDbConnection conn, OleDbTransaction tx, TableApplyPlan table, RowDiff row)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {MdbFile.Quote(table.Target.Name)} " +
                          $"SET {string.Join(", ", row.ChangedColumns.Select(c => MdbFile.Quote(c) + " = ?"))} " +
                          $"WHERE {string.Join(" AND ", table.Target.PrimaryKey.Select(k => MdbFile.Quote(k) + " = ?"))}";

        foreach (var column in row.ChangedColumns)
            cmd.Parameters.AddWithValue("?", row.Right![table.Source.IndexOf(column)] ?? DBNull.Value);
        foreach (var key in row.Key) cmd.Parameters.AddWithValue("?", key ?? DBNull.Value);

        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"[{table.Target.Name}] PK ({string.Join(", ", row.Key)}) 수정 대상이 1건이 아닙니다.");
    }
}
