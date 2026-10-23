using System.Text;
using MdbHub.Core;

namespace MdbHub.Data;

public static class EditLog
{
    /// <summary>
    /// Writes the hand-edited values to "name.yyyyMMdd_HHmmss.edits.csv" next to the target (opens in Excel; UTF-8 with BOM)
    /// so it can be traced later who changed what before an apply. Returns the file path.
    /// </summary>
    public static string Write(string targetPath, IReadOnlyList<CellEdit> edits, DateTime when)
    {
        var path = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(targetPath))!,
            $"{Path.GetFileNameWithoutExtension(targetPath)}.{when:yyyyMMdd_HHmmss}.edits.csv");

        var sb = new StringBuilder();
        sb.AppendLine("테이블,키,컬럼,반영할 mdb의 값,직접 수정한 값");
        foreach (var e in edits.OrderBy(e => e.Table, StringComparer.OrdinalIgnoreCase).ThenBy(e => RowKey.Of(e.Key), StringComparer.Ordinal).ThenBy(e => e.Column))
        {
            sb.AppendLine(string.Join(",", new[]
            {
                e.Table,
                string.Join(" / ", e.Key.Select(k => SideBySide.FormatValue(k))),
                e.Column,
                SideBySide.FormatValue(e.OldValue, markNull: true),
                SideBySide.FormatValue(e.NewValue, markNull: true),
            }.Select(Csv)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    internal static string CsvLine(IEnumerable<string> values) => string.Join(",", values.Select(Csv));

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
