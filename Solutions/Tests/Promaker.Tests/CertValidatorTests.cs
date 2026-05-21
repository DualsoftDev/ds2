using System;
using Promaker.Knowledge;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **B5 phase 3 (s6-r68)** — CertValidator 의 Normalize / Validate path 검증.
///
/// X509Store 의존 분기 (NotFound/Expired/ExpiringSoon/Valid) 는 fact 안 cert install 의무 → backlog.
/// Empty 분기 + Normalize 의 hex sanitize 위주 박제 (production code path 안전망).
/// </summary>
public sealed class CertValidatorTests
{
    [Fact]
    public void Normalize_빈_null_입력_빈_string()
    {
        Assert.Equal(string.Empty, CertValidator.Normalize(null));
        Assert.Equal(string.Empty, CertValidator.Normalize(""));
        Assert.Equal(string.Empty, CertValidator.Normalize("   "));
    }

    [Fact]
    public void Normalize_콜론_공백_hyphen_제거()
    {
        var raw = "aa:bb:cc:dd:ee:ff:11:22:33:44:55:66:77:88:99:00:aa:bb:cc:dd";
        Assert.Equal("AABBCCDDEEFF11223344556677889900AABBCCDD", CertValidator.Normalize(raw));

        var raw2 = "aa bb cc dd ee ff 11 22 33 44 55 66 77 88 99 00 aa bb cc dd";
        Assert.Equal("AABBCCDDEEFF11223344556677889900AABBCCDD", CertValidator.Normalize(raw2));

        var raw3 = "aa-bb-cc-dd-ee-ff-11-22-33-44-55-66-77-88-99-00-aa-bb-cc-dd";
        Assert.Equal("AABBCCDDEEFF11223344556677889900AABBCCDD", CertValidator.Normalize(raw3));
    }

    [Fact]
    public void Normalize_소문자_대문자_혼재_대문자_통일()
    {
        Assert.Equal("AABBCCDDEEFF11223344556677889900AABBCCDD",
            CertValidator.Normalize("AaBbCcDdEeFf1122334455667788990 0AaBbCcDd"));
    }

    [Fact]
    public void Normalize_비_hex_문자_빈_string()
    {
        Assert.Equal(string.Empty, CertValidator.Normalize("GG:HH:11"));   // G/H 비 hex
        Assert.Equal(string.Empty, CertValidator.Normalize("12345xyz"));   // 알파벳 비 hex
    }

    [Fact]
    public void Normalize_SHA1_SHA256_외_길이_빈_string()
    {
        Assert.Equal(string.Empty, CertValidator.Normalize("DEADBEEF"));         // 8 hex (너무 짧음)
        Assert.Equal(string.Empty, CertValidator.Normalize(new string('A', 30))); // 30 hex
        Assert.Equal(string.Empty, CertValidator.Normalize(new string('A', 50))); // 50 hex
        // SHA-1 (40) / SHA-256 (64) 만 통과
        Assert.Equal(new string('A', 40), CertValidator.Normalize(new string('a', 40)));
        Assert.Equal(new string('B', 64), CertValidator.Normalize(new string('b', 64)));
    }

    [Fact]
    public void Validate_빈_thumbprint_Empty_결과()
    {
        var diag = CertValidator.Validate(null);
        Assert.Equal(CertValidator.ValidationResult.Empty, diag.Result);
        Assert.Null(diag.Subject);
        Assert.Null(diag.NotAfter);
        Assert.Null(diag.FoundLocation);

        var diag2 = CertValidator.Validate("");
        Assert.Equal(CertValidator.ValidationResult.Empty, diag2.Result);
    }

    [Fact]
    public void Validate_비_hex_입력_Empty_결과_정합()
    {
        // Normalize 가 빈 string 반환 = Empty 분기 진입.
        var diag = CertValidator.Validate("GG:HH:11");
        Assert.Equal(CertValidator.ValidationResult.Empty, diag.Result);
    }

    [Fact]
    public void Validate_존재_안_하는_thumbprint_NotFound()
    {
        // hex 40 문자 (SHA-1 형식) 이지만 LocalMachine\My / CurrentUser\My 에 존재 안 함.
        // CI 환경에서 일관 — 본 thumbprint 가 실 cert 와 충돌 가능성 ~0 (랜덤 hex 40).
        var randomHex = "0123456789ABCDEF0123456789ABCDEF01234567";
        var diag = CertValidator.Validate(randomHex);
        Assert.Equal(CertValidator.ValidationResult.NotFound, diag.Result);
    }

    [Fact]
    public void ExpiringSoonThresholdDays_const_SSOT()
    {
        // **SSOT** — 30 day 임계. 본 const 가 의도 박제. 변경 시 다른 caller 영향.
        Assert.Equal(30, CertValidator.ExpiringSoonThresholdDays);
    }

    [Fact]
    public void FormatMessage_각_분기별_한국어_메시지()
    {
        var emptyMsg = CertValidator.FormatMessage(
            new CertValidator.Diagnostic(CertValidator.ValidationResult.Empty, null, null, null));
        Assert.Contains("미선택", emptyMsg);

        var notFoundMsg = CertValidator.FormatMessage(
            new CertValidator.Diagnostic(CertValidator.ValidationResult.NotFound, null, null, null));
        Assert.Contains("일치 cert 없음", notFoundMsg);

        var expiredMsg = CertValidator.FormatMessage(
            new CertValidator.Diagnostic(CertValidator.ValidationResult.Expired, "CN=test",
                new DateTime(2025, 1, 1), System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine));
        Assert.Contains("만료", expiredMsg);
        Assert.Contains("2025-01-01", expiredMsg);

        var expiringMsg = CertValidator.FormatMessage(
            new CertValidator.Diagnostic(CertValidator.ValidationResult.ExpiringSoon, "CN=test",
                new DateTime(2026, 6, 1), System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine));
        Assert.Contains("임박", expiringMsg);

        var validMsg = CertValidator.FormatMessage(
            new CertValidator.Diagnostic(CertValidator.ValidationResult.Valid, "CN=test",
                new DateTime(2027, 6, 1), System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser));
        Assert.Contains("유효", validMsg);
    }
}
