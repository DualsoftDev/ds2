module CodexStreamJsonParserTests

open Xunit
open Ds2.LlmAgent

/// Phase 2 C-3 — Codex CLI `--json` 패킷 → LlmEvent 매핑 회귀.
/// Sample 라인은 사전 실증 4 (C-2) 의 실제 호출 결과.
/// CLI 출력 형식 변경 시 본 테스트가 즉시 회귀 검출.

let private parseAll (line: string) : LlmEvent list =
    CodexStreamJsonParser.parseLine line |> List.ofSeq

[<Fact>]
let ``thread.started 는 SessionStarted 로 매핑 (thread_id=session_id, model=codex)`` () =
    let line = """{"type":"thread.started","thread_id":"019dfd28-9f3c-7400-8551-68487d5b38b6"}"""
    match parseAll line with
    | [ SessionStarted (sid, model, tools, mcpServers) ] ->
        Assert.Equal("019dfd28-9f3c-7400-8551-68487d5b38b6", sid)
        Assert.Equal("codex", model)
        Assert.Empty(tools)
        Assert.Empty(mcpServers)
    | other -> Assert.Fail(sprintf "Expected single SessionStarted, got: %A" other)

[<Fact>]
let ``thread.started 에 thread_id 없으면 빈 결과`` () =
    let line = """{"type":"thread.started"}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``turn.started 는 sentinel — 무시 (빈 결과)`` () =
    let line = """{"type":"turn.started"}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``item.completed agent_message 는 AssistantDelta 1회`` () =
    let line = """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"pong"}}"""
    match parseAll line with
    | [ AssistantDelta t ] -> Assert.Equal("pong", t)
    | other -> Assert.Fail(sprintf "Expected single AssistantDelta, got: %A" other)

[<Fact>]
let ``item.completed agent_message 의 text 안에 줄바꿈 보존`` () =
    let line = """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"1\n2\n3\n4\n5"}}"""
    match parseAll line with
    | [ AssistantDelta t ] -> Assert.Equal("1\n2\n3\n4\n5", t)
    | other -> Assert.Fail(sprintf "Expected single AssistantDelta, got: %A" other)

[<Fact>]
let ``item.completed text 가 빈 문자열이면 빈 결과 (placeholder skip)`` () =
    let line = """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":""}}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``item.completed type 이 agent_message 가 아니면 빈 결과 (tool_call 등 후속)`` () =
    let line = """{"type":"item.completed","item":{"id":"item_0","type":"tool_call","name":"foo"}}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``turn.completed 는 SessionEnd 1회 (placeholder duration / cost / stop_reason)`` () =
    let line = """{"type":"turn.completed","usage":{"input_tokens":12894,"cached_input_tokens":12672,"output_tokens":136,"reasoning_output_tokens":121}}"""
    match parseAll line with
    | [ SessionEnd (durationMs, costUsd, isError, stopReason, denials) ] ->
        Assert.Equal(0, durationMs)
        Assert.Equal(0m, costUsd)
        Assert.False(isError)
        Assert.Equal("end_turn", stopReason)
        Assert.Equal(0, denials)
    | other -> Assert.Fail(sprintf "Expected single SessionEnd, got: %A" other)

[<Fact>]
let ``알 수 없는 type 은 빈 결과`` () =
    let line = """{"type":"some.future.event","data":42}"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``빈 라인 / null / 비-JSON 평문은 빈 결과`` () =
    Assert.Empty(parseAll "")
    Assert.Empty(parseAll null)
    Assert.Empty(parseAll "   ")
    Assert.Empty(parseAll "Reading additional input from stdin...")

[<Fact>]
let ``malformed JSON 은 빈 결과 (Log.Warn 경유 silent skip)`` () =
    let line = """{"type":"thread.started","thread_id":"abc-"""
    Assert.Empty(parseAll line)

[<Fact>]
let ``MaxLineLength 초과 라인은 빈 결과 (1MB 이상 line 차단)`` () =
    let huge = "{\"type\":\"thread.started\",\"thread_id\":\"" + System.String('x', CodexStreamJsonParser.MaxLineLength) + "\"}"
    Assert.Empty(parseAll huge)

[<Fact>]
let ``실증 4 의 4-packet sequence 가 SessionStarted / AssistantDelta / SessionEnd 로 매핑`` () =
    let lines = [
        """{"type":"thread.started","thread_id":"019dfd28-f3a2-70e0-90c7-26b2ebe9ea53"}"""
        """{"type":"turn.started"}"""
        """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"hello"}}"""
        """{"type":"turn.completed","usage":{"input_tokens":12904,"cached_input_tokens":9600,"output_tokens":136,"reasoning_output_tokens":121}}"""
    ]
    let events = lines |> List.collect parseAll
    Assert.Equal(3, events.Length)  // turn.started 는 sentinel skip
    match events with
    | [ SessionStarted _; AssistantDelta "hello"; SessionEnd _ ] -> ()
    | other -> Assert.Fail(sprintf "Expected SessionStarted/AssistantDelta/SessionEnd, got: %A" other)
