using System.Globalization;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// B5 phase 2 — DataGrid 의 cert thumbprint cell 표시용 converter. SHA-1 40-hex 의 마지막 8 자리만 표시.
/// </summary>
public class ThumbprintShortConverterTests
{
    private readonly ThumbprintShortConverter _conv = new();

    [Fact]
    public void SHA1_40hex_마지막_8자리_표시()
    {
        var thumb = "3A7F1BCD0011223344556677AABBCCDDEEFF1A2B";  // 40 hex
        var result = _conv.Convert(thumb, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("…EEFF1A2B", result);
    }

    [Fact]
    public void 빈문자_또는_null_시_없음()
    {
        Assert.Equal("(없음)", _conv.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("(없음)", _conv.Convert("", typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("(없음)", _conv.Convert("   ", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 짧은_thumbprint_8자_이하_전체_표시()
    {
        Assert.Equal("ABCD1234", _conv.Convert("ABCD1234", typeof(string), null, CultureInfo.InvariantCulture));
    }
}
