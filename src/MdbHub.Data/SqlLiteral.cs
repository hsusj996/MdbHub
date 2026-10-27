using System.Globalization;

namespace MdbHub.Data;

/// <summary>Writes a value as a literal in Access SQL (used where a parameter cannot be, e.g. engines that reject parameters in make-table queries).</summary>
internal static class SqlLiteral
{
    /// <exception cref="NotSupportedException">The value's type has no literal form here.</exception>
    public static string Of(object value) => value switch
    {
        string s => "'" + s.Replace("'", "''") + "'",
        bool b => b ? "True" : "False",
        DateTime d => "#" + d.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + "#",   // Jet reads date literals in US order
        Guid g => "{guid {" + g.ToString("D").ToUpperInvariant() + "}}",
        float or double => ((IFormattable)value).ToString("R", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new NotSupportedException($"{value.GetType().Name} 값은 SQL에 직접 쓸 수 없습니다."),
    };
}
