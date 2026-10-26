namespace MdbHub.Core;

public static class SchemaValidator
{
    /// <summary>
    /// Returns human-readable differences between two schemas (empty when identical).
    /// Names are case-insensitive and column order is ignored; type and primary key must match.
    /// </summary>
    /// <param name="leftName">How the left schema is called in the messages (e.g. "반영할 mdb").</param>
    /// <param name="rightName">How the right schema is called in the messages (e.g. "대상 mdb").</param>
    public static IReadOnlyList<string> Compare(DatabaseSchema left, DatabaseSchema right, string leftName = "왼쪽", string rightName = "오른쪽")
    {
        var diffs = new List<string>();
        var leftNames = left.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightNames = right.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in leftNames.Except(rightNames, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"테이블 '{name}'이(가) {rightName}에 없습니다.");
        foreach (var name in rightNames.Except(leftNames, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"테이블 '{name}'이(가) {leftName}에 없습니다.");

        foreach (var l in left.Tables)
        {
            var r = right.Find(l.Name);
            if (r is not null) CompareTable(l, r, leftName, rightName, diffs);
        }
        return diffs;
    }

    private static void CompareTable(TableSchema l, TableSchema r, string leftName, string rightName, List<string> diffs)
    {
        foreach (var lc in l.Columns)
        {
            var idx = r.IndexOf(lc.Name);
            if (idx < 0) diffs.Add($"[{l.Name}] 컬럼 '{lc.Name}'이(가) {rightName}에 없습니다.");
            else if (r.Columns[idx].DataType != lc.DataType)
                diffs.Add($"[{l.Name}] 컬럼 '{lc.Name}' 타입 불일치: {leftName} {lc.DataType} / {rightName} {r.Columns[idx].DataType}");
        }
        foreach (var rc in r.Columns.Where(c => !l.HasColumn(c.Name)))
            diffs.Add($"[{l.Name}] 컬럼 '{rc.Name}'이(가) {leftName}에 없습니다.");

        var lk = new HashSet<string>(l.PrimaryKey, StringComparer.OrdinalIgnoreCase);
        if (!lk.SetEquals(r.PrimaryKey))
            diffs.Add($"[{l.Name}] 키 불일치: {leftName} ({string.Join(", ", l.PrimaryKey)}) / {rightName} ({string.Join(", ", r.PrimaryKey)})");
    }
}
