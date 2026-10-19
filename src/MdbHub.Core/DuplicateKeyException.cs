namespace MdbHub.Core;

public sealed class DuplicateKeyException(string table, string side, IReadOnlyList<string> keyColumns, IReadOnlyList<object?> sampleKey, int extraRows)
    : InvalidOperationException(
        $"테이블 '{table}' ({side})에 같은 키를 가진 행이 있어 진행할 수 없습니다.\n" +
        $"키 컬럼: {string.Join(", ", keyColumns)}\n" +
        $"중복 예: ({string.Join(", ", sampleKey.Select(v => v?.ToString() ?? "null"))}) - 겹치는 행 {extraRows:N0}건\n" +
        "설정한 키가 행을 유일하게 구분하는지 확인하세요.")
{
    public string Table { get; } = table;
}
