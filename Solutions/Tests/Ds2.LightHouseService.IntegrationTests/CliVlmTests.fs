module Ds2.LightHouseService.IntegrationTests.CliVlmTests

open System
open System.Threading
open Xunit
open Ds2.LightHouse

/// **s6-r24 작업 1 cli VLM e2e fact** — `Ds2.LightHouse.Cli.Vlm.buildCaptionGen` 의 env var 분기 SSOT 회귀 차단.
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
    member _.``LIGHTHOUSE_VLM_API_KEY 미박제 → noop closure (SkippedCaption "no caption gen")`` () =
        withEnvKey None (fun () ->
            let gen = Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None
            // noop = CaptionGenerator.noop = `fun _ _ -> SkippedCaption "no caption gen"`.
            let result = gen [| 0x89uy |] Png
            match result with
            | CaptionResult.SkippedCaption r ->
                Assert.Contains("no caption gen", r)
            | other ->
                Assert.Fail (sprintf "noop SkippedCaption 기대, 실제 %A" other))

    [<Fact>]
    member _.``LIGHTHOUSE_VLM_API_KEY 빈 문자열 → noop closure (whitespace 가드)`` () =
        withEnvKey (Some "   ") (fun () ->
            let gen = Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None
            // whitespace 만 박제 → IsNullOrWhiteSpace true → noop 분기.
            let result = gen [| 0x89uy |] Png
            match result with
            | CaptionResult.SkippedCaption r ->
                Assert.Contains("no caption gen", r)
            | other ->
                Assert.Fail (sprintf "whitespace 가드 noop 기대, 실제 %A" other))

    [<Fact>]
    member _.``LIGHTHOUSE_VLM_API_KEY 박제 → noop 가 아닌 callAnthropic 분기 (fake key 로 FailedCaption / 응답 결함)`` () =
        // fake key 박제 — callAnthropic 진입. 외부 네트워크 실 호출이라 (a) 4xx (b) network 실패 (c) timeout
        // 어느 경우든 noop "no caption gen" 분기 아닌 다른 결과 박제. SSOT = noop 분기 미진입 검증.
        // 본 fact 는 cli e2e 환경에서 external network 도달 시점에 actually FailedCaption 분기. fail-safe 보장됨.
        withEnvKey (Some "sk-ant-fake-key-for-test") (fun () ->
            let gen = Ds2.LightHouse.Cli.Vlm.buildCaptionGen CancellationToken.None
            let result = gen [| 0x89uy |] Png
            // 핵심 SSOT = "no caption gen" 미반환 (noop 미진입). FailedCaption / 다른 SkippedCaption (예: empty bytes
            // 검증 통과 후 4xx) 어느 case 든 OK — env var 분기 정합 만 박제.
            match result with
            | CaptionResult.SkippedCaption r when r = "no caption gen" ->
                Assert.Fail "env var 박제했으나 noop 분기 진입 — buildCaptionGen 의 분기 SSOT 결함"
            | _ -> ())
