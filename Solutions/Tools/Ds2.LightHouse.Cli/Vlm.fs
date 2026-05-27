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
/// **정책 갱신 (사용자 결정)** — Phase 1 의 "API key 미박제 시 implicit noop fallback" 정책 폐기. 무인 batch
/// 가 image 가 있는 .xlsx/.pptx 색인 시 사용자 인지 없이 caption 누락되는 silent degradation 차단.
/// - `LIGHTHOUSE_VLM_API_KEY` 박제 → `CaptionGenerator.callAnthropic` (Anthropic vision API)
/// - 미박제 + `forceWithoutCaption=true` → `CaptionGenerator.noop` (사용자 명시 opt-out)
/// - 미박제 + `forceWithoutCaption=false` → **Error** (caller 가 fail-fast 박제)
/// - `LIGHTHOUSE_VLM_MODEL` env var 로 model override (default = `claude-sonnet-4-6`, D-2-1 정합)
/// - **cost gate 미적용** — cli path 는 자체 cost-aware orchestration caller 책임. UI 측의 `VisionCostGate` 와 격리.
[<RequireQualifiedAccess>]
module Vlm =

    /// **env var key SSOT (s6-r41)** — 자가 검열 s6-r40 Minor-2 정합. caller 가 Vlm.fs 단일이라 module 안 박제.
    [<Literal>]
    let EnvApiKey = "LIGHTHOUSE_VLM_API_KEY"
    [<Literal>]
    let private EnvModel = "LIGHTHOUSE_VLM_MODEL"

    /// process singleton HttpClient — socket exhaustion 차단 (per-image 신규 생성 회피).
    let private httpClient = new HttpClient(Timeout = TimeSpan.FromSeconds 60.0)

    /// env var 기반 captionGen builder. caller (Program.fs / Packager.fs) 가 `ct` + `sourceFolder` +
    /// `forceWithoutCaption` 주입.
    ///
    /// **P-R3a (todo-documents-based-gfm.md §6.1 N16) — sourceFolder 인자 추가**:
    /// 반환 closure 가 Anthropic API call 진입 *전* `CaptionCache.tryLookup` 박제 → 이전 색인 시점에
    /// 생성된 caption (sweep 박제분, `<sourceFolder>/.lighthouse-caption-cache.json`) 우선 사용. cache hit
    /// 시 `Captioned (text, model)` 반환 → API call skip. cache miss → 기존 callAnthropic 흐름. 사용자
    /// 결정 (1a 즉시) — `.lighthouse-kb/` wipe + 재색인 사이클의 VLM cost 누적 회피.
    ///
    /// 반환:
    /// - `Ok captionGen` — API key 박제 (cache 우선 + callAnthropic fallback) 또는 forceWithoutCaption=true (noop)
    /// - `Error msg` — API key 미박제 + force=false (사용자에게 명시 안내 의무)
    let buildCaptionGen
        (ct: CancellationToken)
        (sourceFolder: string)
        (forceWithoutCaption: bool)
        : Result<byte[] -> ImageFormat -> CaptionResult, string> =
        let apiKey = Environment.GetEnvironmentVariable EnvApiKey
        if String.IsNullOrWhiteSpace apiKey then
            if forceWithoutCaption then
                Ok (fun bytes fmt -> CaptionGenerator.noop bytes fmt)
            else
                // review m-3: `\` line continuation 의 leading whitespace leak 회피 — String.concat 박제.
                let msg =
                    [ sprintf "%s 환경 변수 미박제. 다음 중 하나 의무:" EnvApiKey
                      sprintf "  - %s=<Anthropic API key> 환경 변수 박제 후 재실행 (vision caption 활성)" EnvApiKey
                      "  - --force-without-image-caption flag 박제 (caption 명시 skip, 색인 자체만 진행)" ]
                    |> String.concat "\n"
                Error msg
        else
            let model =
                let m = Environment.GetEnvironmentVariable EnvModel
                if String.IsNullOrWhiteSpace m then "claude-sonnet-4-6" else m
            // P-R3a: cache lookup → hit 시 API call skip, miss 시 callAnthropic 진입. hash 산출은 lib SSOT
            // `ImageStore.computeSha256` 재사용 (ImageStore 의 hash 박제 정합 — drift 0).
            Ok (fun bytes fmt ->
                let hash = ImageStore.computeSha256 bytes
                match CaptionCache.tryLookup sourceFolder hash with
                | Some (text, cachedModel) ->
                    // cache hit — Anthropic call 진입 없이 즉시 Captioned. cachedModel 가 빈 string 이면 현재 model 박제.
                    let resolvedModel = if System.String.IsNullOrEmpty cachedModel then model else cachedModel
                    Captioned (text, resolvedModel)
                | None ->
                    CaptionGenerator.callAnthropic httpClient apiKey model bytes fmt ct)
