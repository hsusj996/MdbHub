using MdbHub.Data;

namespace MdbHub.Tests;

public abstract class TempDirTestBase : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MdbHubTests_" + Guid.NewGuid().ToString("N"))).FullName;

    protected string P(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        MdbFile.ReleasePool();   // let go of pooled file locks before deleting
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Directory.Delete(_dir, recursive: true);
    }
}
