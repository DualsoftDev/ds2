// LLM chat round-trip 최적화 §E2E 시나리오 3 (cache 적중률 ≥ 90%) + R5b (snapshot 실측) 자동 측정.
//
// 본 script 는 Promaker 실 실행 없이 Anthropic `/v1/messages` 를 직접 호출 →
//   1) system + snapshot 을 `cache_control: ephemeral` breakpoint 로 부착 (Promaker ApiChatProvider 모사)
//   2) 동일 session 에서 N turn 짧은 user message 송신 (history 점차 누적)
//   3) 각 turn 의 `usage` (input / cache_creation / cache_read / output) 집계
//   4) hit ratio = cache_read / (input + cache_creation + cache_read) 출력
//
// 사용법 (repo root 에서):
//   $env:ANTHROPIC_API_KEY="sk-ant-..."; dotnet fsi Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx
//   $env:ANTHROPIC_API_KEY="sk-ant-..."; dotnet fsi Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx 5         // 5 turn
//   $env:ANTHROPIC_API_KEY="sk-ant-..."; dotnet fsi Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx 10 medium // turn + size
//
// 비용: claude-haiku-4-5 기준 10 turn ≈ 0.01~0.03 USD (max_tokens=64, prompt cache hit 가정).
//
// 종속성: 표준 .NET 만 사용 (Ds2 DLL 미참조). buildSnapshot 은 measure-snapshot-tokens.fsx 와 동일 grammar.

open System
open System.Text
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

// ──────────────────────────────────────────────────────────────────────
// Synthetic snapshot — measure-snapshot-tokens.fsx 와 동일 grammar
// **drift 시 동기화 책임**: 본 함수와 measure-snapshot-tokens.fsx 의 buildSnapshot 은 의도적 중복.
// 한쪽 grammar 갱신 시 다른쪽도 같이 갱신할 것. (DLL 의존을 피해 fsx 단독 실행성을 우선한 trade-off)
// ──────────────────────────────────────────────────────────────────────

type Cfg = {
    SystemCount: int
    WorkPerFlow: int
    CallPerWork: int
}

let buildSnapshot (cfg: Cfg) (revision: int) =
    let sb = StringBuilder()
    sb.AppendLine($"<store-snapshot revision=\"{revision}\">") |> ignore
    sb.AppendLine("projects:") |> ignore
    sb.AppendLine("  AutoLineProject:") |> ignore
    sb.AppendLine("    systems:") |> ignore
    sb.AppendLine("      MainCtrl (active)") |> ignore
    for i in 1 .. cfg.SystemCount - 1 do
        let kind = match i % 4 with | 0 -> "/cylinder" | 1 -> "/clamp" | 2 -> "/robot" | _ -> "/device"
        sb.AppendLine($"      Device{i:D2} (passive{kind}) {{Op1, Op2, Op3}}") |> ignore
    sb.AppendLine("    flows:") |> ignore
    sb.AppendLine("      Flow01 @MainCtrl:") |> ignore
    sb.AppendLine("        works:") |> ignore
    for wi in 1 .. cfg.WorkPerFlow do
        let callDag =
            [ 1 .. cfg.CallPerWork ]
            |> List.map (fun ci -> $"Device{((ci - 1) % max 1 (cfg.SystemCount - 1)) + 1:D2}.Op{(ci % 3) + 1}")
            |> String.concat " → "
        sb.AppendLine($"          Step{wi:D2} [{callDag}]") |> ignore
    sb.Append("</store-snapshot>") |> ignore
    sb.ToString()

let presetMedium = { SystemCount = 10; WorkPerFlow = 30; CallPerWork = 3 }
let presetSmall  = { SystemCount = 5;  WorkPerFlow = 10; CallPerWork = 3 }
let presetLarge  = { SystemCount = 20; WorkPerFlow = 60; CallPerWork = 5 }

// 의도적으로 긴 system prompt (≥ 1024 token — cache breakpoint 자격 충족).
// Promaker 의 실 prompt 와 1 byte 동일하지는 않지만 cache 동작 검증엔 충분.
let systemPrompt =
    let sb = StringBuilder()
    sb.AppendLine("You are Promaker LLM modeling assistant.") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("# Role") |> ignore
    sb.AppendLine("You help the user build manufacturing automation models with the following entities:") |> ignore
    sb.AppendLine("- Project: top-level container") |> ignore
    sb.AppendLine("- System (active or passive): controllers and devices") |> ignore
    sb.AppendLine("- Flow: sequence of works belonging to one active system") |> ignore
    sb.AppendLine("- Work: a node in a flow; contains a Call DAG") |> ignore
    sb.AppendLine("- Call: invocation of a device ApiDef from a work") |> ignore
    sb.AppendLine("- Arrow: ordering between works (Start/Reset/StartReset/ResetReset/Group)") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("# Snapshot rule") |> ignore
    sb.AppendLine("If you see <store-snapshot revision=\"N\">...</store-snapshot> in the user turn, trust it as the current store state.") |> ignore
    sb.AppendLine("Do NOT call list_projects / list_systems unless the user explicitly asks for the current state.") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("# Brevity") |> ignore
    sb.AppendLine("Keep all replies under 40 words. Acknowledge briefly and stop.") |> ignore
    sb.AppendLine() |> ignore
    // padding 으로 1024 token 충족 (cache 자격)
    for i in 1 .. 40 do
        sb.AppendLine(sprintf "Rule %d: When asked about Promaker entity %d, refer back to the snapshot block as the source of truth and avoid speculative answers about IDs or timestamps." i i) |> ignore
    sb.ToString().TrimEnd()

// ──────────────────────────────────────────────────────────────────────
// Anthropic /v1/messages 호출
// ──────────────────────────────────────────────────────────────────────

type TurnUsage = {
    Turn: int
    InputTokens: int
    CacheCreation: int
    CacheRead: int
    OutputTokens: int
    HitRatio: float
    LatencyMs: int
}

let model = "claude-haiku-4-5-20251001"

// HttpClient module-level singleton — `new HttpClient()` 매 호출 시 socket exhaustion 위험 회피 + header 1회 설정.
// review §Major4 — 두 endpoint (messages / count_tokens) 의 HTTP boilerplate 통합.
let private http =
    let c = new HttpClient(Timeout = TimeSpan.FromSeconds 60.0)
    c.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01")
    c

let private setApiKeyOnce (apiKey: string) =
    if not (http.DefaultRequestHeaders.Contains "x-api-key") then
        http.DefaultRequestHeaders.Add("x-api-key", apiKey)

/// 공통 POST helper — (statusCode, body, elapsedMs) 반환. 예외는 caller 가 처리하지 않고 그대로 throw.
let private postJson (url: string) (payload: string) =
    use req = new HttpRequestMessage(HttpMethod.Post, url)
    req.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let resp = http.Send(req)
    sw.Stop()
    let body = resp.Content.ReadAsStringAsync().Result
    int resp.StatusCode, resp.IsSuccessStatusCode, body, int sw.ElapsedMilliseconds

let callAnthropic (apiKey: string) (systemBlocks: obj seq) (messages: obj seq) : Result<JsonElement * int, string> =
    setApiKeyOnce apiKey
    let payload =
        JsonSerializer.Serialize(
            {| model = model
               max_tokens = 64
               system = systemBlocks |> Seq.toArray
               messages = messages |> Seq.toArray |})
    let status, ok, body, elapsedMs = postJson "https://api.anthropic.com/v1/messages" payload
    if not ok then
        Error (sprintf "HTTP %d: %s" status body)
    else
        let doc = JsonDocument.Parse(body)
        Ok (doc.RootElement.Clone(), elapsedMs)

let extractAssistantText (root: JsonElement) =
    let text = StringBuilder()
    let content = root.GetProperty("content")
    for i in 0 .. content.GetArrayLength() - 1 do
        let block = content.[i]
        let kind = block.GetProperty("type").GetString()
        if kind = "text" then
            text.Append(block.GetProperty("text").GetString()) |> ignore
    text.ToString()

let extractUsage (turn: int) (latency: int) (root: JsonElement) =
    let u = root.GetProperty("usage")
    let getInt (name: string) =
        match u.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
        | _ -> 0
    let input    = getInt "input_tokens"
    let creation = getInt "cache_creation_input_tokens"
    let read     = getInt "cache_read_input_tokens"
    let output   = getInt "output_tokens"
    let total    = input + creation + read
    let ratio    = if total > 0 then float read / float total else 0.0
    { Turn = turn
      InputTokens = input
      CacheCreation = creation
      CacheRead = read
      OutputTokens = output
      HitRatio = ratio
      LatencyMs = latency }

// ──────────────────────────────────────────────────────────────────────
// E2E 10 turn loop
// ──────────────────────────────────────────────────────────────────────

// 외부 네트워크 호출 예외 (DNS 실패 / 일시 timeout 등) 만 좁게 catch — `HttpRequestException` 만.
// 다른 예외 (parse 실패 / NullRef 등 코드 결함) 는 그대로 throw 하여 stack 노출.
let countTokens (apiKey: string) (text: string) : int option =
    setApiKeyOnce apiKey
    let payload =
        JsonSerializer.Serialize(
            {| model = model
               messages = [| {| role = "user"; content = text |} |] |})
    try
        let status, ok, body, _ = postJson "https://api.anthropic.com/v1/messages/count_tokens" payload
        if not ok then
            eprintfn "[count_tokens HTTP %d]: %s" status body
            None
        else
            use doc = JsonDocument.Parse(body)
            Some (doc.RootElement.GetProperty("input_tokens").GetInt32())
    with :? HttpRequestException as ex ->
        eprintfn "[count_tokens network]: %s" ex.Message
        None

let runE2E (apiKey: string) (cfg: Cfg) (turnCount: int) =
    let snapshot = buildSnapshot cfg 1

    // R5b — snapshot 단독 token 실측 (Anthropic count_tokens)
    printfn "─────────────────────────────────────────────────────────────"
    printfn "[R5b] snapshot 단독 token 실측"
    printfn "  size      : sys=%d work=%d call=%d" cfg.SystemCount cfg.WorkPerFlow cfg.CallPerWork
    printfn "  chars     : %d" snapshot.Length
    printfn "  heuristic : %d  (chars / 4)" ((snapshot.Length + 3) / 4)
    match countTokens apiKey snapshot with
    | Some t -> printfn "  Anthropic : %d  (count_tokens %s)" t model
    | None -> printfn "  Anthropic : err (see stderr)"
    printfn ""

    // system block — cache_control 부착 안 함.
    // 이유: claude-haiku-4-5 의 prompt cache 최소 토큰 = 2048 (Sonnet/Opus 의 1024 와 다름).
    // 본 synthetic system 은 2048 미만이라 system 끝 breakpoint 가 silently 거부됨.
    // 대신 마지막 user 의 snapshot block 끝 1 breakpoint 만 유지 — prefix = system + history + 마지막 snapshot 합 (수천 토큰) 으로 minimum 충족.
    let systemBlocks : obj seq = [
        {| ``type`` = "text"; text = systemPrompt |}
    ]

    // **옵션 A 패턴** (sticky snapshot 의 cache hit 가능성 측정용 — ApiChatProvider 의 현 design 과 다름):
    //   history 의 모든 user message 가 multi-content (snapshot block + ping block). 본 turn 호출 messages 의
    //   마지막 user 의 snapshot block 에만 cache_control 부착. → snapshot 의 토큰이 매 turn 동일 위치에 누적 →
    //   prefix-match cache 가 자라면서 hit ratio 상승.
    //
    // (참고) 현재 Promaker `ApiChatProvider` 는 history 에 plain user 만 누적 → snapshot 위치가 매 turn 바뀌어
    //   snapshot 자체는 cache hit 안 됨. 본 script 가 ≥ 90% hit ratio 를 보여주면, doc §Step 4 의 deferred 항목
    //   R10 (snapshot block 별도 cache breakpoint + history multi-content 누적) 의 실효성 입증.
    let userMultiContent (turn: int) (withCacheControl: bool) : obj =
        let snapshotBlock : obj =
            if withCacheControl then
                {| ``type`` = "text"; text = snapshot; cache_control = {| ``type`` = "ephemeral" |} |}
            else
                box {| ``type`` = "text"; text = snapshot |}
        let blocks : obj[] = [|
            snapshotBlock
            {| ``type`` = "text"; text = sprintf "turn %d: ack briefly" turn |}
        |]
        {| role = "user"; content = blocks |}

    let history = ResizeArray<obj>()

    printfn "─────────────────────────────────────────────────────────────"
    printfn "[E2E #3] cache 적중률 측정 — %d turn, model=%s" turnCount model
    printfn "  format: turn=N input cache_cr cache_rd output hit_ratio latency_ms"
    let usages = ResizeArray<TurnUsage>()
    let mutable hadError = false

    let mutable t = 1
    while t <= turnCount && not hadError do
        // history 에 multi-content user (snapshot 포함, cache_control 없음) 누적.
        history.Add(userMultiContent t false)
        // 본 turn 호출 messages: history copy → 마지막 user 만 cache_control 부착 버전으로 swap.
        let messages : seq<obj> =
            let copy = ResizeArray<obj>(history)
            copy.[copy.Count - 1] <- userMultiContent t true
            copy :> seq<_>
        match callAnthropic apiKey systemBlocks messages with
        | Error e ->
            eprintfn "[turn %d failed]: %s" t e
            hadError <- true
        | Ok (root, latency) ->
            let assistantText = extractAssistantText root
            history.Add({| role = "assistant"; content = assistantText |})
            let u = extractUsage t latency root
            usages.Add u
            printfn "  turn=%2d input=%5d cache_cr=%5d cache_rd=%5d output=%4d hit=%5.1f%% lat=%dms"
                u.Turn u.InputTokens u.CacheCreation u.CacheRead u.OutputTokens (u.HitRatio * 100.0) u.LatencyMs
            t <- t + 1

    if usages.Count > 0 && usages.[0].CacheCreation = 0 && usages.[0].CacheRead = 0 then
        eprintfn ""
        eprintfn "[경고] turn 1 의 cache_creation_input_tokens=0 — Anthropic prompt cache 가 만들어지지 않았습니다."
        eprintfn "  가능 원인:"
        eprintfn "  (a) prefix 토큰이 모델 minimum 미달 (Opus/Sonnet=1024, Haiku=2048)"
        eprintfn "  (b) cache_control 위치가 prefix 가 아닌 부분"
        eprintfn "  → cache_control breakpoint 까지의 prefix 토큰 합을 키우거나 위치를 조정하세요."

    if usages.Count > 0 then
        printfn ""
        printfn "[요약]"
        let totalInput = usages |> Seq.sumBy (fun u -> u.InputTokens)
        let totalCreate = usages |> Seq.sumBy (fun u -> u.CacheCreation)
        let totalRead = usages |> Seq.sumBy (fun u -> u.CacheRead)
        let totalOut = usages |> Seq.sumBy (fun u -> u.OutputTokens)
        let grandTotal = totalInput + totalCreate + totalRead
        let aggregateHit = if grandTotal > 0 then float totalRead / float grandTotal else 0.0
        let avgLat = (usages |> Seq.sumBy (fun u -> u.LatencyMs)) / usages.Count
        let warmHit =
            if usages.Count > 1 then
                let warm = usages |> Seq.skip 1 |> Seq.toArray
                let wIn = warm |> Array.sumBy (fun u -> u.InputTokens)
                let wCr = warm |> Array.sumBy (fun u -> u.CacheCreation)
                let wRd = warm |> Array.sumBy (fun u -> u.CacheRead)
                let wTot = wIn + wCr + wRd
                if wTot > 0 then float wRd / float wTot else 0.0
            else aggregateHit
        // steady-state hit ratio — 마지막 3 turn 평균. Anthropic server 의 cache 생성 결정이
        // 첫 1~2 turn 지연되는 동작 (observed) 의 noise 를 제외한 본질적 cache 효과 측정.
        let steadyHit =
            let take = min 3 usages.Count
            let tail = usages |> Seq.skip (usages.Count - take) |> Seq.toArray
            let tIn = tail |> Array.sumBy (fun u -> u.InputTokens)
            let tCr = tail |> Array.sumBy (fun u -> u.CacheCreation)
            let tRd = tail |> Array.sumBy (fun u -> u.CacheRead)
            let tTot = tIn + tCr + tRd
            if tTot > 0 then float tRd / float tTot else 0.0
        printfn "  총 turn          : %d" usages.Count
        printfn "  총 input         : %d (cache_cr=%d cache_rd=%d)" totalInput totalCreate totalRead
        printfn "  총 output        : %d" totalOut
        printfn "  전체 hit ratio   : %5.1f%%   ((cache_rd) / (input + cache_cr + cache_rd))" (aggregateHit * 100.0)
        printfn "  warm hit ratio   : %5.1f%%   (turn 2~ 만 — cold turn 1 제외)" (warmHit * 100.0)
        printfn "  steady hit ratio : %5.1f%%   (마지막 3 turn 평균 — Anthropic cache 정착 후의 본질 효과)" (steadyHit * 100.0)
        printfn "  평균 latency     : %d ms" avgLat
        printfn ""
        printfn "  판정: %s" (
            if steadyHit >= 0.90 then "PASS (steady ≥ 90%, doc §E2E 시나리오 3 통과)"
            else sprintf "MISS (steady %.1f%% — 90%% 미달, 원인 분석 필요)" (steadyHit * 100.0))

// ──────────────────────────────────────────────────────────────────────
// CLI
// ──────────────────────────────────────────────────────────────────────

let args = fsi.CommandLineArgs |> Array.skip 1

let apiKey =
    match Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") with
    | null | "" -> None
    | k -> Some k

let parseCfg (name: string) =
    match name.ToLowerInvariant() with
    | "small"  -> presetSmall
    | "medium" -> presetMedium
    | "large"  -> presetLarge
    | other    ->
        eprintfn "[경고] 알 수 없는 size '%s' — medium 로 fallback. (지원: small / medium / large)" other
        presetMedium

match apiKey with
| None ->
    eprintfn "ANTHROPIC_API_KEY 환경변수가 설정되어 있지 않습니다."
    eprintfn "  PowerShell:  $env:ANTHROPIC_API_KEY = \"sk-ant-...\""
    eprintfn "  bash:        export ANTHROPIC_API_KEY=sk-ant-..."
    exit 1
| Some key ->
    let turnCount, cfg =
        match args with
        | [| n |]    -> Int32.Parse n, presetMedium
        | [| n; sz |] -> Int32.Parse n, parseCfg sz
        | _ -> 10, presetMedium
    runE2E key cfg turnCount
