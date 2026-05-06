using System.Data;
using Dapper;

namespace DSPilot.Infrastructure;

/// <summary>
/// Dapper 가 SQLite TEXT 컬럼 (예: dspCall.callId) 을 C# Guid 로 양방향 매핑하도록.
/// Microsoft.Data.Sqlite 가 Guid 를 BLOB 로 다루는 시도를 우회.
/// </summary>
public sealed class SqliteGuidHandler : SqlMapper.TypeHandler<Guid>
{
    public override Guid Parse(object? value)
    {
        return value switch
        {
            null => Guid.Empty,
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            byte[] bytes when bytes.Length == 16 => new Guid(bytes),
            _ => throw new DataException(
                $"Cannot convert {value.GetType().FullName ?? "null"} (value='{value}') to Guid"),
        };
    }

    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.Value = value.ToString();
    }
}

public sealed class SqliteNullableGuidHandler : SqlMapper.TypeHandler<Guid?>
{
    public override Guid? Parse(object? value)
    {
        if (value is null || value is DBNull) return null;
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            byte[] bytes when bytes.Length == 16 => new Guid(bytes),
            _ => null,
        };
    }

    public override void SetValue(IDbDataParameter parameter, Guid? value)
    {
        parameter.Value = value?.ToString() ?? (object)DBNull.Value;
    }
}
