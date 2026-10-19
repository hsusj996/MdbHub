namespace MdbHub.Data;

/// <summary>Builds a sample spec mdb shaped like the real ones: ~15 tables, PK (CHEMICAL_NO, SUB_CHEMICAL_NO, NUM).</summary>
public static class SampleMdbBuilder
{
    public const int KeyedTableCount = 14;
    public const string UnkeyedTable = "TB_CODE";

    public static string KeyedTableName(int index) => $"TB_SPEC_{index:00}";

    /// <param name="chemicals">(CHEMICAL_NO, SUB_CHEMICAL_NO) pairs; every keyed table gets <paramref name="rowsPerChemical"/> rows per pair.</param>
    /// <param name="valueOffset">Added to SET_VALUE so two samples can intentionally differ for the compare feature.</param>
    public static void Create(string path, IReadOnlyList<(int Chemical, int Sub)> chemicals, int rowsPerChemical = 5, double valueOffset = 0)
    {
        MdbFile.Create(path);
        using var conn = MdbFile.Open(path);

        for (var t = 1; t <= KeyedTableCount; t++)
        {
            var table = KeyedTableName(t);
            Execute(conn, $"CREATE TABLE {table} (CHEMICAL_NO LONG, SUB_CHEMICAL_NO LONG, NUM LONG, " +
                          "ITEM_NAME TEXT(50), SET_VALUE DOUBLE, UPDATED_AT DATETIME, USE_YN YESNO, " +
                          $"CONSTRAINT PK_{table} PRIMARY KEY (CHEMICAL_NO, SUB_CHEMICAL_NO, NUM))");
        }
        Execute(conn, $"CREATE TABLE {UnkeyedTable} (CODE TEXT(20), DESCRIPTION TEXT(100))");

        using var tx = conn.BeginTransaction();
        for (var t = 1; t <= KeyedTableCount; t++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {KeyedTableName(t)} VALUES (?, ?, ?, ?, ?, ?, ?)";
            for (var i = 0; i < 7; i++) cmd.Parameters.Add(new System.Data.OleDb.OleDbParameter());

            foreach (var (chem, sub) in chemicals)
                for (var num = 1; num <= rowsPerChemical; num++)
                {
                    cmd.Parameters[0].Value = chem;
                    cmd.Parameters[1].Value = sub;
                    cmd.Parameters[2].Value = num;
                    cmd.Parameters[3].Value = $"ITEM_{t:00}_{num}";
                    cmd.Parameters[4].Value = t * 10 + num + chem / 100.0 + valueOffset;
                    cmd.Parameters[5].Value = new DateTime(2025, 1, 1).AddDays(num);
                    cmd.Parameters[6].Value = num % 2 == 0;
                    cmd.ExecuteNonQuery();
                }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {UnkeyedTable} VALUES ('A', 'sample code')";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void Execute(System.Data.OleDb.OleDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
