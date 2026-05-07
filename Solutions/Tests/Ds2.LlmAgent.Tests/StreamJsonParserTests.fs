module StreamJsonParserTests

open Xunit
open Ds2.LlmAgent

/// Phase 2 후속 — Claude CLI `--output-format stream-json` 패킷 → LlmEvent 매핑 회귀.
/// `CodexStreamJsonParserTests` 와 대칭. CLI 출력 형식 변경 시 본 테스트가 즉시 회귀 검출.
/// 검증 사실 표 1d-3 / Pass B m5/m10 (MaxLineLength / MaxJsonDepth cap) 의 자동화 net.

let private parseAll (line: string) : LlmEvent list =
    StreamJsonParser.parseLine line |> List.ofSeq

// ─── happy path: 5종 패킷 ──────────────────────────────────────────────

[<Fact>]
let ``system/init 은 SessionStarted (session_id / model / tools / mcp_servers 추출)`` () =
    let line = """{"type":"system","subtype":"init","session_id":"sess-abc","model":"claude-opus-4","tools":["Read","Write"],"mcp_servers":[{"name":"promaker","status":"connected"}]}"""
    match parseAll line with
    | [ SessionStarted (sid, model, tools, mcpServers) ] ->
        Assert.Equal("sess-abc", sid)
        Assert.Equal("claude-opus-4", model)
        Assert.Equal<string list>([ "Read"; "Write" ], tools)
        Assert.Equal(1, mcpServers.Length)
        Assert.Equal("promaker", mcpServers.[0].Name)
        Assert.Equal("connected", mcpServers.[0].Status)
    | other -> Assert.Fail(sprintf "Expected single SessionStarted, got: %A" other)

[<Fact>]
let ``system/init 에 session_id 없으면 빈 결과`` () =
    let line = """{"type":"system","subtype":"init","model":"claude-opus-4"}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``system 의 subtype 이 init 이 아니면 빈 결과`` () =
    let line = """{"type":"system","subtype":"info","msg":"hello"}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``assistant content 의 text item 은 AssistantDelta 1회`` () =
    let line = """{"type":"assistant","message":{"content":[{"type":"text","text":"hello"}]}}"""
    match parseAll line with
    | [ AssistantDelta t ] -> Assert.Equal("hello", t)
    | other -> Assert.Fail(sprintf "Expected AssistantDelta, got: %A" other)

[<Fact>]
let ``assistant content 의 thinking item 은 Thinking 1회`` () =
    let line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"reasoning..."}]}}"""
    match parseAll line with
    | [ Thinking t ] -> Assert.Equal("reasoning...", t)
    | other -> Assert.Fail(sprintf "Expected Thinking, got: %A" other)

[<Fact>]
let ``assistant content 의 tool_use 는 ToolUse (id / name / input)`` () =
    let line = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_1","name":"add_system","input":{"name":"S1"}}]}}"""
    match parseAll line with
    | [ ToolUse (id, name, input) ] ->
        Assert.Equal("tu_1", id)
        Assert.Equal("add_system", name)
        Assert.Equal("S1", input.GetProperty("name").GetString())
    | other -> Assert.Fail(sprintf "Expected ToolUse, got: %A" other)

[<Fact>]
let ``assistant content 의 tool_use 에 input 부재 시 ToolUse 빈 객체`` () =
    let line = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_1","name":"list_systems"}]}}"""
    match parseAll line with
    | [ ToolUse (id, name, input) ] ->
        Assert.Equal("tu_1", id)
        Assert.Equal("list_systems", name)
        Assert.Equal(System.Text.Json.JsonValueKind.Object, input.ValueKind)
    | other -> Assert.Fail(sprintf "Expected ToolUse, got: %A" other)

[<Fact>]
let ``assistant content 의 빈 text 는 yield 안 함 (placeholder skip)`` () =
    let line = """{"type":"assistant","message":{"content":[{"type":"text","text":""}]}}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``assistant content 의 mixed (thinking + text + tool_use) 은 3 event 순서 보존`` () =
    let line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"r"},{"type":"text","text":"t"},{"type":"tool_use","id":"u1","name":"n","input":{}}]}}"""
    match parseAll line with
    | [ Thinking "r"; AssistantDelta "t"; ToolUse ("u1", "n", _) ] -> ()
    | other -> Assert.Fail(sprintf "Expected Thinking/AssistantDelta/ToolUse 순서, got: %A" other)

[<Fact>]
let ``user.tool_result (string content) 은 ToolResult 매핑`` () =
    let line = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_1","is_error":false,"content":"ok"}]}}"""
    match parseAll line with
    | [ ToolResult (toolUseId, isError, body) ] ->
        Assert.Equal("tu_1", toolUseId)
        Assert.False(isError)
        Assert.Equal("ok", body)
    | other -> Assert.Fail(sprintf "Expected ToolResult, got: %A" other)

[<Fact>]
let ``user.tool_result 의 array content (text item 다수) 는 줄바꿈 join`` () =
    let line = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_2","is_error":false,"content":[{"type":"text","text":"line1"},{"type":"text","text":"line2"}]}]}}"""
    match parseAll line with
    | [ ToolResult (_, _, body) ] -> Assert.Equal("line1\nline2", body)
    | other -> Assert.Fail(sprintf "Expected ToolResult, got: %A" other)

[<Fact>]
let ``user.tool_result 의 is_error true 보존`` () =
    let line = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_3","is_error":true,"content":"VALIDATION_ERROR: ..."}]}}"""
    match parseAll line with
    | [ ToolResult (_, isError, _) ] -> Assert.True(isError)
    | other -> Assert.Fail(sprintf "Expected ToolResult, got: %A" other)

[<Fact>]
let ``rate_limit_event 는 RateLimitEvent (resetsAt / status)`` () =
    let line = """{"type":"rate_limit_event","rate_limit_info":{"resetsAt":1234567890,"status":"approaching_limit"}}"""
    match parseAll line with
    | [ RateLimitEvent (resetsAt, status) ] ->
        Assert.Equal(1234567890L, resetsAt)
        Assert.Equal("approaching_limit", status)
    | other -> Assert.Fail(sprintf "Expected RateLimitEvent, got: %A" other)

[<Fact>]
let ``result 는 SessionEnd (duration_ms / total_cost_usd / is_error / stop_reason / permission_denials)`` () =
    let line = """{"type":"result","duration_ms":1500,"total_cost_usd":0.0123,"is_error":false,"stop_reason":"end_turn","permission_denials":[]}"""
    match parseAll line with
    | [ SessionEnd (durationMs, costUsd, isError, stopReason, denials) ] ->
        Assert.Equal(1500, durationMs)
        Assert.Equal(0.0123m, costUsd)
        Assert.False(isError)
        Assert.Equal("end_turn", stopReason)
        Assert.Equal(0, denials)
    | other -> Assert.Fail(sprintf "Expected SessionEnd, got: %A" other)

[<Fact>]
let ``result 의 permission_denials 배열 길이 보존`` () =
    let line = """{"type":"result","duration_ms":100,"total_cost_usd":0,"is_error":true,"stop_reason":"max_turns","permission_denials":[{"tool":"Bash"},{"tool":"Write"}]}"""
    match parseAll line with
    | [ SessionEnd (_, _, isError, _, denials) ] ->
        Assert.True(isError)
        Assert.Equal(2, denials)
    | other -> Assert.Fail(sprintf "Expected SessionEnd, got: %A" other)

// ─── empty / unknown / malformed ───────────────────────────────────────

[<Fact>]
let ``빈 라인 / null / 공백 / 비-JSON 평문은 빈 결과`` () =
    Assert.Empty(parseAll "")
    Assert.Empty(parseAll null)
    Assert.Empty(parseAll "   ")
    Assert.Empty(parseAll "Reading additional input from stdin...")

[<Fact>]
let ``알 수 없는 type 은 빈 결과`` () =
    let line = """{"type":"some.future.event","data":42}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``malformed JSON 은 빈 결과 (Log.Warn 경유 silent skip)`` () =
    let line = """{"type":"system","subtype":"init","session_id":"abc-"""
    Assert.Empty(parseAll line)

// ─── cap (Pass B m5/m10) ──────────────────────────────────────────────

[<Fact>]
let ``MaxLineLength 초과 라인은 빈 결과 (1MB 이상 line 차단 — m5)`` () =
    let huge = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + System.String('x', StreamJsonParser.MaxLineLength) + "\"}"
    Assert.Empty(parseAll huge)

[<Fact>]
let ``MaxLineLength 경계 (1MB 이하) 는 정상 처리`` () =
    // 정상 길이 라인은 cap 에 안 걸림.
    let line = """{"type":"system","subtype":"init","session_id":"normal-line"}"""
    Assert.True(line.Length < StreamJsonParser.MaxLineLength)
    match parseAll line with
    | [ SessionStarted ("normal-line", _, _, _) ] -> ()
    | other -> Assert.Fail(sprintf "Expected SessionStarted, got: %A" other)

[<Fact>]
let ``MaxJsonDepth 초과 JSON 은 빈 결과 (depth 33 차단 — m10)`` () =
    // depth 33 만들기: open '{' 33 번 nested. JsonDocument.Parse 가 InvalidOperationException 으로 거부.
    let mutable s = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"x\""
    for _ in 1 .. (StreamJsonParser.MaxJsonDepth + 1) do
        s <- s + ",\"a\":{"
    s <- s + "\"k\":1"
    for _ in 1 .. (StreamJsonParser.MaxJsonDepth + 1) do
        s <- s + "}"
    s <- s + "}"
    Assert.Empty(parseAll s)

[<Fact>]
let ``MaxJsonDepth 경계 (depth 32 이하) 는 정상 처리`` () =
    // depth 32 안쪽으로 살짝 — 값 1 + 객체 N 중첩. Top-level (system/init 객체) = 1, 그 아래 1 nested 만.
    let line = """{"type":"system","subtype":"init","session_id":"depth-ok","extra":{"a":{"b":1}}}"""
    match parseAll line with
    | [ SessionStarted ("depth-ok", _, _, _) ] -> ()
    | other -> Assert.Fail(sprintf "Expected SessionStarted, got: %A" other)
