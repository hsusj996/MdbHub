namespace MdbHub.Data;

public static class MdbBackup
{
    /// <summary>Copies the mdb next to itself as "name.yyyyMMdd_HHmmss.bak.mdb" and returns the backup path.</summary>
    public static string Create(string path, DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
        var backup = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            $"{Path.GetFileNameWithoutExtension(path)}.{stamp}.bak{Path.GetExtension(path)}");

        File.Copy(path, backup, overwrite: false);
        return backup;
    }
}
