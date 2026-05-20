namespace Ds2.LightHouse.Cli

open System
open System.Net.Http
open System.Threading
open Ds2.LightHouse

/// Phase 2 task D-iii (s6-r20 --review M1) — cli 측 VLM captionGen builder SSOT.
///
/// `lighthouse-cli` 무인 batch 의 두 진입점 (Program.runIndex / Packager.runIngest) 이
/// 동일한 env var fallback 로직을 박제하던 중복 (17 line × 2) 을 본 module 로 통합.
///
/// **정책** (Promaker UI 측 `AttachmentIngestService.BuildCaptionGen` 과 분리 정합):
/// - `LIGHTHOUSE_VLM_API_KEY` 미박제 → `CaptionGenerator.noop` (Phase 1 회귀 0 + 무인 batch 의 implicit "VLM 비활성" 안전 default)
/// - `LIGHTHOUSE_VLM_API_KEY` 박제 → `CaptionGenerator.callAnthropic`
/// - `LIGHTHOUSE_VLM_MODEL` env var 로 model override 가능 (default = `claude-sonnet-4-6`, D-2-1 정합)
/// - **cost gate 미적용** — cli path 는 자체 cost-aware orchestration caller 책임. UI 측의 `VisionCostGate` 와 격리.
[<RequireQualifiedAccess>]
module Vlm =

    /// **env var key SSOT (s6-r41)** — 자가 검열 s6-r40 Minor-2 정합. caller 가 Vlm.fs 단일이라 module 안 박제.
    [<Literal>]
    let private EnvApiKey = "LIGHTHOUSE_VLM_API_KEY"
    [<Literal>]
    let private EnvModel = "LIGHTHOUSE_VLM_MODEL"

    /// process singleton HttpClient — socket exhaustion 차단 (per-image 신규 생성 회피).
    let private httpClient = new HttpClient(Timeout = TimeSpan.FromSeconds 60.0)

    /// env var 기반 captionGen builder. caller (Program.fs / Packager.fs) 가 `ct` 만 주입.
    let buildCaptionGen (ct: CancellationToken) : byte[] -> ImageFormat -> CaptionResult =
        let apiKey = Environment.GetEnvironmentVariable EnvApiKey
        if String.IsNullOrWhiteSpace apiKey then
            fun bytes fmt -> CaptionGenerator.noop bytes fmt
        else
            let model =
                let m = Environment.GetEnvironmentVariable EnvModel
                if String.IsNullOrWhiteSpace m then "claude-sonnet-4-6" else m
            fun bytes fmt -> CaptionGenerator.callAnthropic httpClient apiKey model bytes fmt ct
