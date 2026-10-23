using System.Globalization;

namespace MdbHub.Core;

/// <summary>
/// A value the user changed by hand in the apply preview, in the row of the patch identified by <see cref="Key"/>.
/// <see cref="OldValue"/> is the patch's own value when the preview was made; it guards against the patch having changed since.
/// </summary>
public sealed record CellEdit(string Table, IReadOnlyList<object?> Key, string Column, object? OldValue, object? NewValue);

/// <summary>A stable text form of a row key, for matching the same row across lists.</summary>
public static class RowKey
{
    public static string Of(IEnumerable<object?> values) =>
        string.Join('\u001f', values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
}

/// <summary>Turns text typed into a grid cell back into a typed value for a column.</summary>
public static class CellValueParser
{
    /// <summary>What the cell shows for null, so typing it back means null.</summary>
    public const string NullText = "(null)";

    /// <param name="column">The column (its OLE DB type name is used when no sample value is known).</param>
    /// <param name="sample">An existing value of the column, if any; its CLR type is what the new value must have.</param>
    /// <param name="text">Empty text or "(null)" means null.</param>
    public static bool TryParse(ColumnInfo column, object? sample, string text, out object? value, out string? error)
    {
        value = null;
        error = null;

        if (text.Length == 0 || text == NullText) return true;

        var type = sample?.GetType() ?? TypeOf(column.DataType);
        if (type is null || type == typeof(byte[]))
        {
            error = $"'{column.Name}' 컬럼({column.DataType})은 화면에서 수정할 수 없는 형식입니다.";
            return false;
        }

        try
        {
            if (type == typeof(string)) value = text;
            else if (type == typeof(bool)) value = ParseBool(text.Trim());
            else if (type == typeof(DateTime)) value = DateTime.Parse(text.Trim(), CultureInfo.CurrentCulture);
            else if (type == typeof(Guid)) value = Guid.Parse(text.Trim());
            else value = Convert.ChangeType(text.Trim(), type, CultureInfo.CurrentCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            error = $"'{text}'은(는) {Describe(type)} 값으로 읽을 수 없습니다.";
            return false;
        }
    }

    private static bool ParseBool(string text) => text.ToLowerInvariant() switch
    {
        "true" or "1" or "-1" or "yes" or "y" or "예" or "참" => true,
        "false" or "0" or "no" or "n" or "아니오" or "거짓" => false,
        _ => throw new FormatException(),
    };

    private static Type? TypeOf(string oleDbTypeName) => oleDbTypeName switch
    {
        "Integer" => typeof(int),
        "SmallInt" => typeof(short),
        "BigInt" => typeof(long),
        "UnsignedTinyInt" or "TinyInt" => typeof(byte),
        "Single" => typeof(float),
        "Double" => typeof(double),
        "Currency" or "Decimal" or "Numeric" or "VarNumeric" => typeof(decimal),
        "Date" or "DBDate" or "DBTimeStamp" or "Filetime" => typeof(DateTime),
        "Boolean" => typeof(bool),
        "Guid" => typeof(Guid),
        "WChar" or "VarWChar" or "LongVarWChar" or "Char" or "VarChar" or "LongVarChar" or "BSTR" => typeof(string),
        _ => null,
    };

    private static string Describe(Type type) =>
        type == typeof(bool) ? "참/거짓" :
        type == typeof(DateTime) ? "날짜/시간" :
        type == typeof(Guid) ? "GUID" :
        type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "숫자(소수 가능)" :
        "정수";
}
