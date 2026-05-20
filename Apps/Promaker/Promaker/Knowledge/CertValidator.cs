using System;
using System.Security.Cryptography.X509Certificates;

namespace Promaker.Knowledge;

/// <summary>
/// **B5 phase 3 (s6-r68)** — client cert (mTLS) 의 정합 + validity 검증 helper.
///
/// LocalMachine\My + CurrentUser\My 두 X509Store 의 cert 탐색 → 일치 thumbprint 가 (a) 존재 + (b) 유효 기간 안 + (c)
/// 만료 30 일 이내 경고 분류. B5 phase 2 의 X509Store 선택 dialog 가 채운 thumbprint 의 후속 validity 검증 SSOT.
/// <para/>
/// 본 helper 는 ApplicationSettingsDialog 의 Save 시점 + KbManagerDialog 의 Test Connection 시점 호출. 결과 UI
/// 표시는 caller 책임 (ValidationResult / 만료일 / cert subject 표시).
/// </summary>
public static class CertValidator
{
    /// <summary>thumbprint 검증 결과 분류 (mutually exclusive).</summary>
    public enum ValidationResult
    {
        /// <summary>thumbprint 빈/null — caller 가 X509Store 선택 안내.</summary>
        Empty,
        /// <summary>LocalMachine\My + CurrentUser\My 모두에 일치 cert 없음 (만료 후 폐기 등).</summary>
        NotFound,
        /// <summary>cert.NotAfter &lt; now — 즉시 mTLS 거부 위험. 재발급 의무.</summary>
        Expired,
        /// <summary>cert.NotAfter &lt; now + 30 day — 임박한 만료 경고. 사전 재발급 권장.</summary>
        ExpiringSoon,
        /// <summary>유효 기간 안 + 만료 ≥ 30 day. 통과.</summary>
        Valid,
    }

    /// <summary>본 helper 의 진단 결과 — caller 가 UI 표시에 사용.</summary>
    public sealed record Diagnostic(
        ValidationResult Result,
        string? Subject,
        DateTime? NotAfter,
        StoreLocation? FoundLocation);

    /// <summary>**SSOT** — 만료 임박 경고 임계 (default 30 day). 본 const 변경 시 일관 영향.</summary>
    public const int ExpiringSoonThresholdDays = 30;

    /// <summary>
    /// thumbprint 가 SHA-1 (40 hex) 또는 SHA-256 (64 hex) 길이의 hex 만으로 normalize. ':' / 공백 / hyphen 제거 + 대문자.
    /// 비-hex 문자가 있으면 빈 string 반환 (caller 가 NotFound 분기로 처리).
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))
                sb.Append(char.ToUpperInvariant(ch));
            else if (ch == ':' || ch == ' ' || ch == '-' || ch == '\t')
                continue;
            else
                return string.Empty;  // 비-hex 문자 = 결함
        }
        var s = sb.ToString();
        return (s.Length == 40 || s.Length == 64) ? s : string.Empty;
    }

    /// <summary>
    /// thumbprint 로 cert 탐색 + validity 진단. cert 가 LocalMachine\My 우선, 없으면 CurrentUser\My.
    /// </summary>
    public static Diagnostic Validate(string? thumbprint, DateTime? nowOverride = null)
    {
        var normalized = Normalize(thumbprint);
        if (string.IsNullOrEmpty(normalized))
            return new Diagnostic(ValidationResult.Empty, null, null, null);

        var found = TryFind(normalized, StoreLocation.LocalMachine)
                    ?? TryFind(normalized, StoreLocation.CurrentUser);
        if (found is null)
            return new Diagnostic(ValidationResult.NotFound, null, null, null);

        var (cert, loc) = found.Value;
        var now = nowOverride ?? DateTime.Now;
        if (cert.NotAfter < now)
            return new Diagnostic(ValidationResult.Expired, cert.Subject, cert.NotAfter, loc);
        if (cert.NotAfter < now.AddDays(ExpiringSoonThresholdDays))
            return new Diagnostic(ValidationResult.ExpiringSoon, cert.Subject, cert.NotAfter, loc);
        return new Diagnostic(ValidationResult.Valid, cert.Subject, cert.NotAfter, loc);
    }

    private static (X509Certificate2 cert, StoreLocation loc)? TryFind(string thumbprint, StoreLocation loc)
    {
        using var store = new X509Store(StoreName.My, loc);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count == 0) return null;
        return (matches[0], loc);
    }

    /// <summary>사용자 표시용 한국어 메시지 (UI 의 SetTestResult 직접 호출 정합).</summary>
    public static string FormatMessage(Diagnostic d)
    {
        return d.Result switch
        {
            ValidationResult.Empty => "client cert 미선택 — '선택...' 버튼으로 X509Store 에서 선택.",
            ValidationResult.NotFound => "thumbprint 일치 cert 없음 — LocalMachine\\My / CurrentUser\\My 둘 다 탐색. cert 재발급 필요.",
            ValidationResult.Expired => $"cert 만료됨 (NotAfter={d.NotAfter:yyyy-MM-dd}) — 즉시 재발급 의무.",
            ValidationResult.ExpiringSoon => $"cert 만료 임박 (NotAfter={d.NotAfter:yyyy-MM-dd}, {ExpiringSoonThresholdDays} 일 이내) — 사전 재발급 권장.",
            ValidationResult.Valid => $"cert 유효 (subject={d.Subject}, NotAfter={d.NotAfter:yyyy-MM-dd}, store={d.FoundLocation}).",
            _ => "cert validity 미확인.",
        };
    }
}
