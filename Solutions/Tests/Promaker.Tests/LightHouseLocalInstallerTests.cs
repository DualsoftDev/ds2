using System.Security;
using System.Text;
using Promaker.Services;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **B4 (2026-05-27)** — SecureString → UTF-8 byte[] 변환 helper 의 contract 회귀 가드.
/// 본 helper 는 PSK / Cert PFX password 의 평문 lifetime 최소화 SSOT — 변환 결과의 정확도가 enable-ai.ps1
/// 의 read 정합과 directly tied. 잘못된 인코딩은 service 등록 시점에 silent corruption.
/// </summary>
public sealed class LightHouseLocalInstallerTests
{
    private static SecureString MakeSecure(string s)
    {
        var ss = new SecureString();
        foreach (var ch in s) ss.AppendChar(ch);
        ss.MakeReadOnly();
        return ss;
    }

    [Fact]
    public void SecureStringToUtf8Bytes_ASCII_정합()
    {
        using var ss = MakeSecure("hello-psk-123");
        var bytes = LightHouseLocalInstaller.SecureStringToUtf8Bytes(ss);
        Assert.Equal(Encoding.UTF8.GetBytes("hello-psk-123"), bytes);
    }

    [Fact]
    public void SecureStringToUtf8Bytes_한글_multibyte_정합()
    {
        using var ss = MakeSecure("비밀번호한글");
        var bytes = LightHouseLocalInstaller.SecureStringToUtf8Bytes(ss);
        Assert.Equal(Encoding.UTF8.GetBytes("비밀번호한글"), bytes);
    }

    [Fact]
    public void SecureStringToUtf8Bytes_빈_SecureString_빈_byte배열()
    {
        using var ss = new SecureString();
        ss.MakeReadOnly();
        var bytes = LightHouseLocalInstaller.SecureStringToUtf8Bytes(ss);
        Assert.Empty(bytes);
    }

    [Fact]
    public void SecureStringToUtf8Bytes_특수문자_혼합()
    {
        using var ss = MakeSecure("p@ssw0rd!#$한글-abc");
        var bytes = LightHouseLocalInstaller.SecureStringToUtf8Bytes(ss);
        // 결과 byte[] 를 UTF-8 decode 시 원본 정합 (round-trip 안전).
        Assert.Equal("p@ssw0rd!#$한글-abc", Encoding.UTF8.GetString(bytes));
    }
}
