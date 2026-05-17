namespace Ds2.LightHouse

/// Ds2.LightHouse 의 log4net logger 정의.
///
/// KB ingest / TextEncoding 진단용. Promaker 측 `log4net.config` 의
/// `<logger name="Ds2.LightHouse.*">` 설정이 적용됨. 별도 설정 없으면 root 정책 따름.
[<RequireQualifiedAccess>]
module internal Log =

    /// 공통 LightHouse logger — Extractor / Chunker / Classifier 등 KB ingest 파이프라인 진단.
    let lighthouse = log4net.LogManager.GetLogger("Ds2.LightHouse")

    /// TextEncoding 전용 — chat / KB 두 경로 공유 SSOT. detectEncoding 의 strict fallback 추적.
    let textEncoding = log4net.LogManager.GetLogger("Ds2.LightHouse.TextEncoding")
