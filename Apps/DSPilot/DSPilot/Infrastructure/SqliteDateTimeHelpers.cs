using System.Globalization;

namespace DSPilot.Infrastructure;

/// <summary>
/// SQLite UTC 문자열 ↔ DateTime 변환 헬퍼.
/// F# QueryHelpers의 toSqliteUtcString / fromSqliteUtcString 대체.
/// </summary>
public static class SqliteDateTimeHelpers
{
    private const string UtcFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    public static string ToSqliteUtcString(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        return utc.ToString(UtcFormat, CultureInfo.InvariantCulture) + "Z";
    }

    public static DateTime? FromSqliteUtcString(string? str)
    {
        if (string.IsNullOrEmpty(str)) return null;

        var trimmed = str.TrimEnd('Z');
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
        {
            return utc.ToLocalTime();
        }
        return null;
    }
}
