using System.IO;
using Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

public sealed class AidValueCodecTests
{
    [Fact]
    public void JsonPath_and_typed_conversion_preserve_numeric_type()
    {
        var value = AidValueCodec.ExtractJson("{\"machine\":{\"values\":[1,12.5]}}", "$.machine.values[1]");
        var typed = AidValueCodec.ConvertScalar(value, "double");
        Assert.IsType<double>(typed);
        Assert.Equal(12.5, (double)typed);
    }

    [Fact]
    public void Modbus_word_order_scale_and_offset_are_applied()
    {
        // IEEE-754 1.5f = 0x3fc00000, MSW first.
        var value = AidValueCodec.DecodeModbusRegisters([0x3fc0, 0x0000], "float", true, 10.0, 2.0);
        Assert.IsType<float>(value);
        Assert.Equal(17.0f, (float)value);
    }

    [Fact]
    public void Boolean_conversion_accepts_PLC_numeric_values()
    {
        Assert.True((bool)AidValueCodec.ConvertScalar("1", "boolean"));
        Assert.False((bool)AidValueCodec.ConvertScalar(0, "boolean"));
    }

    [Fact]
    public void Scaled_two_register_value_can_publish_as_double()
    {
        var value = AidValueCodec.DecodeModbusRegisters([0x0000, 0x03e8], "double", true, 0.1, 0.0);
        Assert.IsType<double>(value);
        Assert.Equal(100.0, (double)value);
    }

    [Fact]
    public void Oversized_scalar_is_rejected_before_OPC_UA_write()
    {
        var value = new string('x', AidValueCodec.MaxUaScalarBytes + 1);

        Assert.Throws<InvalidDataException>(() => AidValueCodec.ConvertScalar(value, "string"));
    }
}
