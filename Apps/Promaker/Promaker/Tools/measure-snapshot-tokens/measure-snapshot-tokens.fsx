// LLM chat round-trip 최적화 §4.1 의 store-snapshot 추정 token 비용 (1.5 ~ 2.5K @ 10 system / 30 work) 을 실측.
//
// 본 script 는 doc 의 grammar 를 직접 생성 (Ds2.Core / Ds2.LlmAgent DLL 의존 X) → 표준 .NET 만으로 실행.
// 진짜 store 의 직렬화 결과와 1 byte 단위로 동일하지는 않지만 (escapeXml, sort 순서 등 미세 차이) doc 추정치
// 검증 용도로는 충분.
//
// 사용법:
//   dotnet fsi measure-snapshot-tokens.fsx                     // 기본 size mix
//   dotnet fsi measure-snapshot-tokens.fsx 10 30 3             // sys=10, work=30, callPerWork=3
//   $env:ANTHROPIC_API_KEY="sk-ant-..."; dotnet fsi ...        // 추가로 Anthropic count_tokens 실측
//
// 출력:
//   - char count
//   - UTF-8 byte count
//   - heuristic token (chars / 4) — GPT/Claude 영문 평균. structured (XML 등) 은 다소 over-estimate
//   - 옵션: Anthropic /v1/messages/count_tokens 실측 (claude-haiku-4-5)

open System
open System.Text
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

// ──────────────────────────────────────────────────────────────────────
// Synthetic snapshot 생성 — doc §4.1 grammar 직접 emit
// ──────────────────────────────────────────────────────────────────────

type Cfg = {
    SystemCount: int       // 1 project 안 system 수 (active 1 + passive N-1)
    FlowCount: int         // active system 안 flow 수 (= 1 권장 — modeling 관례)
    WorkPerFlow: int       // flow 안 work 수
    CallPerWork: int       // work 안 call 수 (chain)
    WorkArrowCount: int    // system 안 work-arrow 수 (소요 보통 work 수 - 1)
}

let buildSnapshot (cfg: Cfg) (revision: int) =
    let sb = StringBuilder()
    sb.AppendLine($"<store-snapshot revision=\"{revision}\">") |> ignore
    sb.AppendLine("projects:") |> ignore
    sb.AppendLine("  AutoLineProject:") |> ignore
    sb.AppendLine("    systems:") |> ignore

    // 1번째 system = active controller, 나머지는 passive cylinder/clamp/robot/device 순환
    sb.AppendLine("      MainCtrl (active)") |> ignore
    for i in 1 .. cfg.SystemCount - 1 do
        let kind = match i % 4 with
                   | 0 -> "/cylinder"
                   | 1 -> "/clamp"
                   | 2 -> "/robot"
                   | _ -> "/device"
        sb.AppendLine($"      Device{i:D2} (passive{kind}) {{Op1, Op2, Op3}}") |> ignore

    sb.AppendLine("    flows:") |> ignore
    for fi in 1 .. cfg.FlowCount do
        sb.AppendLine($"      Flow{fi:D2} @MainCtrl:") |> ignore
        sb.AppendLine("        works:") |> ignore
        for wi in 1 .. cfg.WorkPerFlow do
            let callDag =
                [ 1 .. cfg.CallPerWork ]
                |> List.map (fun ci -> $"Device{((ci - 1) % max 1 (cfg.SystemCount - 1)) + 1:D2}.Op{(ci % 3) + 1}")
                |> String.concat " → "
            sb.AppendLine($"          Step{wi:D2} [{callDag}]") |> ignore

    if cfg.WorkArrowCount > 0 then
        sb.AppendLine("    work-arrows:") |> ignore
        sb.AppendLine("      @MainCtrl:") |> ignore
        for i in 1 .. cfg.WorkArrowCount do
            let src = sprintf "Flow%02d.Step%02d" ((i - 1) / cfg.WorkPerFlow + 1) (((i - 1) % cfg.WorkPerFlow) + 1)
            let dst = sprintf "Flow%02d.Step%02d" ((i - 1) / cfg.WorkPerFlow + 1) (((i - 1) % cfg.WorkPerFlow) + 2)
            sb.AppendLine($"        {src} →S {dst}") |> ignore

    sb.Append("</store-snapshot>") |> ignore
    sb.ToString()

// ──────────────────────────────────────────────────────────────────────
// Anthropic /v1/messages/count_tokens (optional 실측)
// ──────────────────────────────────────────────────────────────────────

let countTokensAnthropic (apiKey: string) (text: string) : int option =
    try
        use http = new HttpClient()
        http.DefaultRequestHeaders.Add("x-api-key", apiKey)
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01")
        let payload =
            JsonSerializer.Serialize(
                {| model = "claude-haiku-4-5-20251001"
                   messages = [| {| role = "user"; content = text |} |] |})
        use req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages/count_tokens")
        req.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
        let resp = http.Send(req)
        let body = resp.Content.ReadAsStringAsync().Result
        if not resp.IsSuccessStatusCode then
            eprintfn "[count_tokens HTTP %d]: %s" (int resp.StatusCode) body
            None
        else
            use doc = JsonDocument.Parse(body)
            Some (doc.RootElement.GetProperty("input_tokens").GetInt32())
    with ex ->
        eprintfn "[count_tokens exception]: %s" ex.Message
        None

// ──────────────────────────────────────────────────────────────────────
// 측정 + 출력
// ──────────────────────────────────────────────────────────────────────

let measure (label: string) (cfg: Cfg) (apiKey: string option) =
    let s = buildSnapshot cfg 1
    let chars = s.Length
    let bytes = Encoding.UTF8.GetByteCount(s)
    let heuristicToken = (chars + 3) / 4  // chars / 4 (round up)
    printfn "─────────────────────────────────────────────────────────────"
    printfn "[%s]  sys=%d flow=%d work/flow=%d call/work=%d warrows=%d"
            label cfg.SystemCount cfg.FlowCount cfg.WorkPerFlow cfg.CallPerWork cfg.WorkArrowCount
    printfn "  chars        : %6d" chars
    printfn "  utf8 bytes   : %6d" bytes
    printfn "  heuristic tok: %6d   (chars / 4)" heuristicToken
    match apiKey with
    | Some key ->
        match countTokensAnthropic key s with
        | Some t -> printfn "  Anthropic tok: %6d   (count_tokens claude-haiku-4-5)" t
        | None   -> printfn "  Anthropic tok:    err (see stderr)"
    | None -> ()

let args = fsi.CommandLineArgs |> Array.skip 1

let parseArgs () =
    match args with
    | [| sys; work; call |] ->
        let s = Int32.Parse sys
        let w = Int32.Parse work
        let c = Int32.Parse call
        Some { SystemCount = s; FlowCount = 1; WorkPerFlow = w; CallPerWork = c; WorkArrowCount = max 0 (w - 1) }
    | _ -> None

let apiKey =
    match Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") with
    | null | "" -> None
    | k -> Some k

match parseArgs () with
| Some cfg ->
    measure "custom" cfg apiKey
| None ->
    // 기본 size mix — doc §4.1 추정 (10 system / 30 work / 100 call → 1.5~2.5K token) 검증
    measure "tiny    (2 sys / 2 work / 2 call)"     { SystemCount = 2;  FlowCount = 1; WorkPerFlow = 2;  CallPerWork = 2; WorkArrowCount = 1 }  apiKey
    measure "small   (5 sys / 10 work / 3 call)"    { SystemCount = 5;  FlowCount = 1; WorkPerFlow = 10; CallPerWork = 3; WorkArrowCount = 9 }  apiKey
    measure "medium  (10 sys / 30 work / 3 call)"   { SystemCount = 10; FlowCount = 1; WorkPerFlow = 30; CallPerWork = 3; WorkArrowCount = 29 } apiKey
    measure "large   (20 sys / 60 work / 5 call)"   { SystemCount = 20; FlowCount = 1; WorkPerFlow = 60; CallPerWork = 5; WorkArrowCount = 59 } apiKey
    measure "huge    (50 sys / 150 work / 5 call)"  { SystemCount = 50; FlowCount = 1; WorkPerFlow = 150;CallPerWork = 5; WorkArrowCount = 149 } apiKey

printfn ""
printfn "(Anthropic count_tokens 실측을 원하면 ANTHROPIC_API_KEY env 설정 후 재실행)"
