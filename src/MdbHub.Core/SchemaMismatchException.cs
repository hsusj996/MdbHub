namespace MdbHub.Core;

/// <summary>Thrown when two schemas that have to match do not. <see cref="Differences"/> has every difference; the message shows the first few.</summary>
public sealed class SchemaMismatchException(IReadOnlyList<string> differences, string? subject = null)
    : Exception(BuildMessage(differences, subject))
{
    private const int MaxShown = 20;

    public IReadOnlyList<string> Differences { get; } = differences;

    private static string BuildMessage(IReadOnlyList<string> differences, string? subject)
    {
        var lines = differences.Take(MaxShown).ToList();
        if (differences.Count > MaxShown) lines.Add($"... 외 {differences.Count - MaxShown}건");
        return $"{subject ?? "두 mdb"}의 스키마가 달라 진행할 수 없습니다 ({differences.Count}건):{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }
}
