using MdbHub.Data;

// Creates the sample mdb files used to try MdbHub.
//   dotnet run --project tools/MdbHub.SampleGen -- <output dir> [small|max|all]
// Requires the 64-bit Microsoft Access Database Engine (ACE). Existing files are replaced.
if (args.Length < 1)
{
    Console.WriteLine("usage: MdbHub.SampleGen <output dir> [small|max|all]");
    return 1;
}

var dir = args[0];
var which = args.Length > 1 ? args[1].ToLowerInvariant() : "small";
if (which is not ("small" or "max" or "all"))
{
    Console.WriteLine($"unknown set '{which}' (use small, max or all)");
    return 1;
}
Directory.CreateDirectory(dir);

void Make(string name, IReadOnlyList<(int, int)> chemicals, int rowsPerChemical, double valueOffset = 0)
{
    var path = Path.Combine(dir, name);
    if (File.Exists(path)) File.Delete(path);

    var started = DateTime.Now;
    SampleMdbBuilder.Create(path, chemicals, rowsPerChemical, valueOffset);
    Console.WriteLine($"{name}: {new FileInfo(path).Length / 1024.0 / 1024:F1} MB in {(DateTime.Now - started).TotalSeconds:F0}s");
}

if (which is "small" or "all")
{
    // Compare these two: shared codes differ by +1 in SET_VALUE (changed), 300/x only in A, 400/1 only in B.
    Make("sample_a.mdb", [(100, 1), (100, 2), (200, 1), (300, 1), (300, 2)], rowsPerChemical: 5);
    Make("sample_b.mdb", [(100, 1), (100, 2), (200, 1), (400, 1)], rowsPerChemical: 5, valueOffset: 1);
}

if (which is "max" or "all")
{
    // Largest expected real-world size: 70 chemical codes x 2000 rows = 140,000 rows in each of 14 tables (~149 MB, takes a few minutes).
    var chemicals = (from c in Enumerable.Range(1001, 10) from s in Enumerable.Range(1, 7) select (c, s)).ToArray();
    Make("sample_max.mdb", chemicals, rowsPerChemical: 2000);
}
return 0;
