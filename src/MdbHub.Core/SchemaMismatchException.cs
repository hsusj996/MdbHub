namespace MdbHub.Core;

public sealed class SchemaMismatchException(IReadOnlyList<string> differences)
    : Exception("두 mdb의 스키마가 달라 진행할 수 없습니다:" + Environment.NewLine + string.Join(Environment.NewLine, differences))
{
    public IReadOnlyList<string> Differences { get; } = differences;
}
