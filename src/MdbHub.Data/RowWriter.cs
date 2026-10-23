using System.Data.OleDb;

namespace MdbHub.Data;

/// <summary>Writes single rows inside a caller-owned transaction (shared by apply and merge).</summary>
internal static class RowWriter
{
    public static void Insert(OleDbConnection conn, OleDbTransaction tx, string table, IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {MdbFile.Quote(table)} ({string.Join(", ", columns.Select(MdbFile.Quote))}) " +
                          $"VALUES ({string.Join(", ", columns.Select(_ => "?"))})";
        foreach (var value in values) cmd.Parameters.AddWithValue("?", value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets <paramref name="setColumns"/> of the one row with the given key; anything but exactly one affected row is an error.</summary>
    public static void Update(OleDbConnection conn, OleDbTransaction tx, string table, IReadOnlyList<string> keyColumns, IReadOnlyList<object?> keyValues,
        IReadOnlyList<string> setColumns, IReadOnlyList<object?> setValues)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {MdbFile.Quote(table)} " +
                          $"SET {string.Join(", ", setColumns.Select(c => MdbFile.Quote(c) + " = ?"))} " +
                          $"WHERE {string.Join(" AND ", keyColumns.Select(k => MdbFile.Quote(k) + " = ?"))}";

        foreach (var value in setValues) cmd.Parameters.AddWithValue("?", value ?? DBNull.Value);
        foreach (var key in keyValues) cmd.Parameters.AddWithValue("?", key ?? DBNull.Value);

        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"[{table}] 키 ({string.Join(", ", keyValues)}) 수정 대상이 1건이 아닙니다.");
    }
}
