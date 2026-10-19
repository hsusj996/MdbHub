using System.Data.OleDb;

namespace MdbHub.Data;

public static class MdbFile
{
    private const string Provider = "Microsoft.ACE.OLEDB.12.0";

    public static string ConnectionString(string path, bool readOnly = false) =>
        $"Provider={Provider};Data Source={path};" + (readOnly ? "Mode=Read;" : "");

    public static OleDbConnection Open(string path, bool readOnly = false)
    {
        var conn = new OleDbConnection(ConnectionString(path, readOnly));
        conn.Open();
        return conn;
    }

    /// <summary>Creates an empty .mdb (Access 2000 format) via ADOX. Fails if the file already exists.</summary>
    public static void Create(string path)
    {
        if (File.Exists(path)) throw new IOException($"이미 존재하는 파일입니다: {path}");

        var type = Type.GetTypeFromProgID("ADOX.Catalog")
            ?? throw new InvalidOperationException("ADOX를 사용할 수 없습니다. Access Database Engine(64비트) 설치를 확인하세요.");
        dynamic catalog = Activator.CreateInstance(type)!;
        try
        {
            catalog.Create($"Provider={Provider};Data Source={path};Jet OLEDB:Engine Type=5;");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(catalog);
        }
    }

    internal static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
