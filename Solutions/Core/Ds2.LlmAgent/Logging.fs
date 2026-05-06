namespace Ds2.LlmAgent

/// Ds2.LlmAgent 의 log4net logger 정의.
///
/// Provider 동작 로그는 일반 logger (Ds2.LlmAgent.*).
/// Claude CLI 의 raw stream-json 라인은 별도 logger (Promaker.LlmAgent.RawStream) — default OFF, verbose 진단용.
[<RequireQualifiedAccess>]
module internal Log =

    let provider = log4net.LogManager.GetLogger("Ds2.LlmAgent.Provider")

    /// raw stream-json 라인 (Claude CLI stdout) 진단용. Promaker 의 log4net.config 에서
    /// `<logger name="Promaker.LlmAgent.RawStream"><level value="OFF"/></logger>` 가 default.
    let rawStream = log4net.LogManager.GetLogger("Promaker.LlmAgent.RawStream")
