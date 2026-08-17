using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Promaker.Shared;

public static class AidValueCodec
{
    // Central UA transport quota와 동일하게 맞춰, 기록됐지만 Collector가 읽지 못하는 값을 차단한다.
    public const int MaxUaScalarBytes = 1_048_576;

    public static object ConvertScalar(object? value, string valueType)
    {
        if (value is JsonElement json) value = JsonScalar(json);
        if (value is null) throw new FormatException("Telemetry value is null.");
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return valueType switch
        {
            "double" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            "float" => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            "int" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            "long" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            "uint" => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            "ulong" => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            "boolean" => ParseBoolean(value, text),
            "dateTime" => value is DateTime dateTime
                ? dateTime.ToUniversalTime()
                : DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            "byteString" => EnsureByteString(value is byte[] bytes ? bytes : Convert.FromBase64String(text)),
            _ => EnsureString(text)
        };
    }

    public static JsonElement ExtractJson(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var current = document.RootElement;
        if (string.IsNullOrWhiteSpace(path) || path == "$") return current.Clone();
        var index = path[0] == '$' ? 1 : 0;
        while (index < path.Length)
        {
            if (path[index] == '.') index++;
            if (index >= path.Length) break;
            if (path[index] == '[')
            {
                var close = path.IndexOf(']', index + 1);
                if (close < 0) throw new FormatException($"Invalid JSON path '{path}'.");
                var token = path[(index + 1)..close].Trim();
                if ((token.StartsWith('"') && token.EndsWith('"')) ||
                    (token.StartsWith('\'') && token.EndsWith('\'')))
                {
                    token = token[1..^1];
                    if (!current.TryGetProperty(token, out current))
                        throw new KeyNotFoundException($"JSON path '{path}' has no property '{token}'.");
                }
                else
                {
                    var arrayIndex = int.Parse(token, CultureInfo.InvariantCulture);
                    if (current.ValueKind != JsonValueKind.Array || arrayIndex < 0 || arrayIndex >= current.GetArrayLength())
                        throw new IndexOutOfRangeException($"JSON path '{path}' array index is out of range.");
                    current = current[arrayIndex];
                }
                index = close + 1;
            }
            else
            {
                var end = index;
                while (end < path.Length && path[end] is not '.' and not '[') end++;
                var property = path[index..end];
                if (!current.TryGetProperty(property, out current))
                    throw new KeyNotFoundException($"JSON path '{path}' has no property '{property}'.");
                index = end;
            }
        }
        return current.Clone();
    }

    public static object DecodeModbusRegisters(
        ushort[] registers, string valueType, bool mostSignificantWord, double scale, double offset)
    {
        if (registers.Length == 0) throw new FormatException("Modbus register response is empty.");
        var words = mostSignificantWord ? registers : Enumerable.Reverse(registers).ToArray();
        var bytes = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), words[i]);

        object raw = valueType switch
        {
            "boolean" => words[0] != 0,
            "int" when bytes.Length >= 4 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            "int" => (int)BinaryPrimitives.ReadInt16BigEndian(bytes),
            "uint" when bytes.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            "uint" => (uint)BinaryPrimitives.ReadUInt16BigEndian(bytes),
            "long" => BinaryPrimitives.ReadInt64BigEndian(Require(bytes, 8)),
            "ulong" => BinaryPrimitives.ReadUInt64BigEndian(Require(bytes, 8)),
            "float" => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Require(bytes, 4))),
            "double" when bytes.Length >= 8 =>
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8))),
            "double" when scale != 1.0 || offset != 0.0 =>
                (double)BinaryPrimitives.ReadInt32BigEndian(Require(bytes, 4)),
            "double" =>
                (double)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Require(bytes, 4))),
            "byteString" => bytes,
            "dateTime" => DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(Require(bytes, 8))).UtcDateTime,
            _ => Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ')
        };
        if (raw is IConvertible && raw is not bool && raw is not string && raw is not DateTime)
        {
            var scaled = Convert.ToDouble(raw, CultureInfo.InvariantCulture) * scale + offset;
            return valueType switch
            {
                "int" => (object)checked((int)scaled),
                "uint" => checked((uint)scaled),
                "long" => checked((long)scaled),
                "ulong" => checked((ulong)scaled),
                "float" => (float)scaled,
                _ => scaled
            };
        }
        return raw;
    }

    public static ushort RequiredRegisters(string valueType) => valueType switch
    {
        "long" or "ulong" or "double" or "dateTime" => 4,
        "int" or "uint" or "float" => 2,
        _ => 1
    };

    private static ReadOnlySpan<byte> Require(byte[] bytes, int length)
    {
        if (bytes.Length < length) throw new FormatException($"Expected {length / 2} Modbus registers, got {bytes.Length / 2}.");
        return bytes.AsSpan(0, length);
    }

    private static object? JsonScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.GetRawText()
    };

    private static bool ParseBoolean(object value, string text)
    {
        if (value is bool boolean) return boolean;
        if (bool.TryParse(text, out boolean)) return boolean;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer != 0;
        throw new FormatException($"'{text}' is not a boolean.");
    }

    private static byte[] EnsureByteString(byte[] value)
    {
        if (value.Length > MaxUaScalarBytes)
            throw new InvalidDataException($"Telemetry byteString exceeds {MaxUaScalarBytes} bytes.");
        return value;
    }

    private static string EnsureString(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaxUaScalarBytes)
            throw new InvalidDataException($"Telemetry string exceeds {MaxUaScalarBytes} UTF-8 bytes.");
        return value;
    }
}
