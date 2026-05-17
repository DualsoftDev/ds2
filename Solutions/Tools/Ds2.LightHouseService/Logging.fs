namespace Ds2.LightHouseService

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
