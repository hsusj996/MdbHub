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

    internal TableApplyPlan WithEdits(IReadOnlyList<CellEdit> edits)
    {
        if (edits.Count == 0) return this;

        var keyColumns = Target.PrimaryKey.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byRow = edits.GroupBy(e => RowKey.Of(e.Key)).ToDictionary(g => g.Key, g => g.ToList());
        var used = new HashSet<string>();
        var rows = new List<RowDiff>(Diff.Rows.Count);
        var unchanged = Diff.UnchangedCount;

        foreach (var row in Diff.Rows)
        {
            var key = RowKey.Of(row.Key);
            if (row.Kind == RowDiffKind.Removed || !byRow.TryGetValue(key, out var rowEdits))
            {
                rows.Add(row);
                continue;
            }
            used.Add(key);

            var right = (object?[])row.Right!.Clone();
            foreach (var edit in rowEdits)
            {
                if (keyColumns.Contains(edit.Column))
                    throw new InvalidOperationException($"[{Target.Name}] 키 컬럼 '{edit.Column}'은(는) 수정할 수 없습니다.");
                var index = Source.IndexOf(edit.Column);
                if (index < 0)
                    throw new InvalidOperationException($"[{Target.Name}] 수정한 컬럼 '{edit.Column}'이(가) 없습니다.");
                if (!TableComparer.ValueEquals(right[index], edit.OldValue))
                    throw new InvalidOperationException(
                        $"[{Target.Name}] 키 ({string.Join(", ", row.Key)})의 '{edit.Column}' 값이 수정할 때와 달라졌습니다. 반영할 파일이 바뀌었을 수 있으니 미리보기를 다시 만드세요.");
                right[index] = edit.NewValue;
            }

            if (row.Kind == RowDiffKind.Added)
            {
                rows.Add(row with { Right = right });
                continue;
            }

            // An overwrite: recompute which columns still differ from the target after the edits.
            var changed = new List<string>();
            for (var i = 0; i < Target.Columns.Count; i++)
            {
                var name = Target.Columns[i].Name;
                var sourceIndex = Source.IndexOf(name);
                if (sourceIndex >= 0 && !TableComparer.ValueEquals(row.Left![i], right[sourceIndex])) changed.Add(name);
            }
            if (changed.Count == 0) unchanged++;
            else rows.Add(row with { Right = right, ChangedColumns = changed });
        }

        var missing = byRow.Keys.FirstOrDefault(k => !used.Contains(k));
        if (missing is not null)
            throw new InvalidOperationException(
                $"[{Target.Name}] 수정한 행(키 {string.Join(", ", byRow[missing][0].Key)})이(가) 미리보기에 없습니다. 반영할 파일이 바뀌었을 수 있으니 미리보기를 다시 만드세요.");

        return this with { Diff = new TableDiff(Diff.Table, rows, unchanged) };
    }
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

    /// <summary>
    /// The plan as it becomes when the user's hand edits are applied to the patch rows (the patch file itself is untouched).
    /// An overwrite whose edited values end up equal to the target counts as unchanged. Throws <see cref="InvalidOperationException"/>
    /// when an edit cannot be applied: it targets a key column, an unknown column or row, or the patch value is no longer what was edited.
    /// </summary>
    public ApplyPlan WithEdits(IReadOnlyList<CellEdit>? edits)
    {
        if (edits is null || edits.Count == 0) return this;

        var unknownTable = edits.Select(e => e.Table).Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(t => !Tables.Any(p => string.Equals(p.Target.Name, t, StringComparison.OrdinalIgnoreCase)));
        if (unknownTable is not null)
            throw new InvalidOperationException($"수정한 테이블 '{unknownTable}'이(가) 반영 대상에 없습니다. 미리보기를 다시 만드세요.");

        return this with
        {
            Tables = Tables.Select(t => t.WithEdits(edits.Where(e => string.Equals(e.Table, t.Target.Name, StringComparison.OrdinalIgnoreCase)).ToList())).ToList(),
        };
    }
}

/// <param name="BackupPath">Null when nothing needed to change or the backup was turned off.</param>
/// <param name="EditLogPath">The file listing the hand-edited values that were applied; null when there were none.</param>
public sealed record ApplyResult(ApplyPlan Plan, string? BackupPath, string? EditLogPath = null);

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
        var differences = SchemaValidator.Compare(new DatabaseSchema(sourceTables), new DatabaseSchema(targetSubset), "반영할 mdb", "대상 mdb");
        if (differences.Count > 0) throw new SchemaMismatchException(differences, "반영할 mdb와 대상 mdb");

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
    /// <param name="edits">Values the user changed by hand in the preview; they are applied to the patch rows in memory (the patch file is not modified)
    /// and listed in an ".edits.csv" file next to the target after a successful apply.</param>
    public static ApplyResult Apply(string sourcePath, string targetPath, ApplyMode mode = ApplyMode.Upsert, bool backup = true, AppConfig? config = null,
        IReadOnlyList<CellEdit>? edits = null)
    {
        var plan = Preview(sourcePath, targetPath, config).WithEdits(edits);

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

        // The data is committed by now, so a log that cannot be written must not turn the apply into a failure.
        string? editLog = null;
        if (edits is { Count: > 0 })
        {
            try { editLog = EditLog.Write(targetPath, edits, DateTime.Now); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new ApplyResult(plan, backupPath, editLog);
    }

    private static void Insert(OleDbConnection conn, OleDbTransaction tx, TableApplyPlan table, RowDiff row) =>
        RowWriter.Insert(conn, tx, table.Target.Name, table.Source.Columns.Select(c => c.Name).ToList(), row.Right!);

    private static void Update(OleDbConnection conn, OleDbTransaction tx, TableApplyPlan table, RowDiff row) =>
        RowWriter.Update(conn, tx, table.Target.Name, table.Target.PrimaryKey, row.Key,
            row.ChangedColumns, row.ChangedColumns.Select(c => row.Right![table.Source.IndexOf(c)]).ToList());
}
