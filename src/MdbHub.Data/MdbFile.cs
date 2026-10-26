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

    /// <summary>
    /// Creates an empty .mdb (Access 2000 format) via ADOX and leaves it closed. Fails if the file already exists.
    /// </summary>
    public static void Create(string path)
    {
        if (File.Exists(path)) throw new IOException($"이미 존재하는 파일입니다: {path}");

        var type = Type.GetTypeFromProgID("ADOX.Catalog")
            ?? throw new InvalidOperationException("ADOX를 사용할 수 없습니다. Access Database Engine(64비트) 설치를 확인하세요.");
        dynamic catalog = Activator.CreateInstance(type)!;
        object? connection = null;
        try
        {
            // Catalog.Create hands back the ADO connection that is open on the new file. It must be closed and released here:
            // otherwise the file stays open (and cannot even be deleted) until the garbage collector happens to run, and an engine
            // that refuses a second connection to it fails later with "already open".
            connection = catalog.Create($"Provider={Provider};Data Source={path};Jet OLEDB:Engine Type=5;");
        }
        finally
        {
            if (connection is not null)
            {
                try { ((dynamic)connection).Close(); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(connection);
            }
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(catalog);
        }
    }

    /// <summary>
    /// Deletes a file, retrying briefly: a virus scanner or sync client may be holding a just-written file for a moment.
    /// Returns false when it still could not be deleted. Never throws.
    /// </summary>
    public static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                OleDbConnection.ReleaseObjectPool();
                Thread.Sleep(150);
            }
        }
        return !File.Exists(path);
    }

    internal static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
