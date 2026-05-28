module Ds2.LightHouseService.IntegrationTests.CliVlmTests

open System
open System.Threading
open Xunit
open Ds2.LightHouse

/// **s6-r24 작업 1 cli VLM e2e fact** — `Ds2.LightHouse.Cli.Vlm.buildCaptionGen` 의 env var 분기 SSOT 회귀 차단.
///
/// **사용자 결정 (CLI API key 필수 변경)** — buildCaptionGen 의 signature 변경: `Result<captionGen, errorMsg>` 반환.
/// API key 미박제 + force=false → `Error msg` (caller fail-fast 의무).
/// API key 미박제 + force=true → `Ok noop` (사용자 명시 opt-out).
/// API key 박제 + force=무관 → `Ok callAnthropic`.
///
/// scope: `LIGHTHOUSE_VLM_API_KEY` 분기 검증 만 (callAnthropic 의 실 HTTP 호출은 `httpClient` private singleton
/// 으로 mock 불가 — env var 분기 자체의 SSOT 박제로 회귀 detection 충분).
///
/// 본 fact 는 process-wide env var 변경 → xUnit parallel collision 차단 위해 `[<Collection>]` 직렬화.
[<Collection("EnvVarSerialized")>]
type CliVlmTests() =

    let envKey = "LIGHTHOUSE_VLM_API_KEY"

    /// env var 의 caller 측 snapshot 저장 후 fact 안에서 임의 값 박제. finally 에서 원래 값 복원.
    let withEnvKey (value: string option) (action: unit -> 'r) : 'r =
        let original = Environment.GetEnvironmentVariable envKey
        let target = match value with Some v -> v | None -> null
        Environment.SetEnvironmentVariable(envKey, target)
        try action ()
        finally Environment.SetEnvironmentVariable(envKey, original)

    [<Fact>]
    member _.``API key 미박제 + force=false → Error (CLI fail-fast)`` () =
        withEnvKey None (fun () ->
            match Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None (System.IO.Path.GetTempPath()) false with
            | Error msg ->
                // 사용자 안내 msg 에 env var 명 + flag 명 포함 의무.
                Assert.Contains("LIGHTHOUSE_VLM_API_KEY", msg)
                Assert.Contains("--force-without-image-caption", msg)
            | Ok _ ->
                Assert.Fail "API key 미박제 + force=false 는 Error 의무 — Ok 반환은 silent degradation 회귀")

    [<Fact>]
    member _.``API key 빈 whitespace + force=false → Error (whitespace 가드)`` () =
        withEnvKey (Some "   ") (fun () ->
            match Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None (System.IO.Path.GetTempPath()) false with
            | Error msg ->
                Assert.Contains("LIGHTHOUSE_VLM_API_KEY", msg)
            | Ok _ ->
                Assert.Fail "whitespace 만 박제는 IsNullOrWhiteSpace true → Error 의무")

    [<Fact>]
    member _.``API key 미박제 + force=true → Ok noop closure (SkippedCaption "no caption gen")`` () =
        withEnvKey None (fun () ->
            match Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None (System.IO.Path.GetTempPath()) true with
            | Ok gen ->
                let result = gen [| 0x89uy |] Png
                match result with
                | CaptionResult.SkippedCaption r ->
                    Assert.Contains("no caption gen", r)
                | other ->
                    Assert.Fail (sprintf "noop SkippedCaption 기대, 실제 %A" other)
            | Error msg ->
                Assert.Fail (sprintf "force=true 시 Ok noop 의무, Error: %s" msg))

    [<Fact>]
    member _.``API key 박제 + force=무관 → Ok callAnthropic 분기 (no caption gen 미반환)`` () =
        // fake key 박제 — callAnthropic 진입. 외부 네트워크 실 호출이라 (a) 4xx (b) network 실패 (c) timeout
        // 어느 경우든 noop "no caption gen" 분기 아닌 다른 결과 박제. SSOT = noop 분기 미진입 검증.
        withEnvKey (Some "sk-ant-fake-key-for-test") (fun () ->
            match Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None (System.IO.Path.GetTempPath()) false with
            | Ok gen ->
                let result = gen [| 0x89uy |] Png
                match result with
                | CaptionResult.SkippedCaption r when r = "no caption gen" ->
                    Assert.Fail "env var 박제했으나 noop 분기 진입 — buildCaptionGen 의 분기 SSOT 결함"
                | _ -> ()
            | Error msg ->
                Assert.Fail (sprintf "API key 박제 시 Ok callAnthropic 의무, Error: %s" msg))

    [<Fact>]
    member _.``API key 박제 + force=true → Ok callAnthropic (force flag 가 API key 보다 후순위)`` () =
        // API key 박제 + force=true → API key 우선 활성 (force flag 는 미박제 상태에서만 의미).
        withEnvKey (Some "sk-ant-fake-key-for-test") (fun () ->
            match Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None (System.IO.Path.GetTempPath()) true with
            | Ok gen ->
                let result = gen [| 0x89uy |] Png
                match result with
                | CaptionResult.SkippedCaption r when r = "no caption gen" ->
                    Assert.Fail "API key 박제 시 force=true 가 noop 우선으로 떨어지면 silent degradation"
                | _ -> ()
            | Error msg ->
                Assert.Fail (sprintf "API key 박제 시 Ok callAnthropic 의무, Error: %s" msg))
