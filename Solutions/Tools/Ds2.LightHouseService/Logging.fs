namespace Ds2.LightHouseService

open System
open System.Security.Cryptography
open System.Text

/// log4net logger SSOT (todo-lighthouse-kb-server.md §3.11).
///
/// `Logs\service-YYYYMMDD.log` (운영 진단) / `Audit\audit-YYYYMMDD.log` (CR4/M11 등록·삭제·search audit)
/// 별 appender 분리 — log4net.config 의 `<logger name="Ds2.LightHouseService.Audit">` 가 audit-only 라우팅.
[<RequireQualifiedAccess>]
module internal Log =

    /// service 운영 진단용 — Kestrel / config / DPAPI / storage 등.
    let service = log4net.LogManager.GetLogger("Ds2.LightHouseService")

    /// CR4 / M11 audit log — collection 등록 / 삭제 / payload swap / search 호출. user identity + collection id + timestamp.
    /// `auditRetentionDays` (default 365) 별도 retention. 보안 추적 사유로 service log 와 분리.
    let audit = log4net.LogManager.GetLogger("Ds2.LightHouseService.Audit")

    /// session token → SHA-256 fingerprint (8 hex). review IM-11 (R6 M4):
    /// audit log 에 token 평문 박제 시 log leak → token 재사용 위험. fingerprint 만 박제 (식별만 가능, 재사용 불가).
    let tokenFingerprint (token: string) : string =
        if String.IsNullOrEmpty token then "(empty)"
        else
            use sha = SHA256.Create()
            let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes token)
            Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant()

    /// user-controlled 평문의 CR/LF 제거. review IM-11 (R6 M2):
    /// audit log line 에 user 입력 (X-User-Identity, path, queryText) 직접 박제 시 CR/LF 가 log injection 야기.
    /// admin tool 의 log 파싱이 다음 line 을 잘못된 entry 로 인식. CR/LF/Tab 모두 ' ' 치환.
    let sanitizeForLog (s: string) : string =
        if isNull s then ""
        else s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
