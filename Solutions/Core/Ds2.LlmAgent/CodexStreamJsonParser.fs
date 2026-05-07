namespace Ds2.LlmAgent

open System
open System.Text.Json

/// Codex CLI `--json` 라인 → `LlmEvent` 정규화 (Phase 2 C-3).
///
/// **사전 실증 4 결과**: 4종 packet
///   - `thread.started`  → SessionStarted (thread_id 를 session_id 로 사용)
///   - `turn.started`    → 무시 (Codex 만의 sentinel, Claude 는 명시 패킷 없음)
///   - `item.completed` (type=agent_message) → AssistantDelta (text 1회 — Codex 는 completion 후 단일 패킷)
///   - `turn.completed`  → SessionEnd (duration/cost 정보 부재 → placeholder 0 / "end_turn")
///
/// **Claude vs Codex 결정적 차이**:
///   - Claude `assistant` 는 토큰 단위 streaming delta 누적. Codex 는 단일 `item.completed` 1회 발사.
///   - 따라서 ChatPanel 의 AssistantDelta throttle 은 Codex 모드에선 noop (1회 패킷이라 즉시 출력).
///   - Codex 의 mcp_servers init 상태는 stdout 에 미노출 — SessionStarted.mcpServers 는 빈 list.
///     Promaker 측은 in-process Kestrel ready 신호를 별도로 확인 (HTTP GET / 등).
[<RequireQualifiedAccess>]
module CodexStreamJsonParser =

    /// 단일 JSONL 라인 최대 바이트 길이. Claude parser 와 동일 1MB cap.
    let MaxLineLength = 1024 * 1024

    /// JSON 트리 최대 depth. Claude parser 와 동일 32. Codex 패킷은 2~3 depth.
    let MaxJsonDepth = 32

    let private tryGetString (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private parseThreadStarted (root: JsonElement) : LlmEvent option =
        match tryGetString root "thread_id" with
        | None -> None
        | Some tid ->
            // model/tools/mcp_servers 정보는 thread.started 에 없음 — 호출자가 옵션으로 알고 있고,
            // mcp 상태는 Codex 가 미노출 (실증 4 결과).
            Some(SessionStarted(tid, "codex", [], []))

    let private parseItemCompleted (root: JsonElement) : LlmEvent seq =
        seq {
            match root.TryGetProperty("item") with
            | true, item ->
                match tryGetString item "type" with
                | Some "agent_message" ->
                    match tryGetString item "text" with
                    | Some t when t.Length > 0 -> yield AssistantDelta t
                    | _ -> ()
                | _ -> ()  // tool_call 등 다른 type 은 production 통합 후 검증 (C-3 후속)
            | _ -> ()
        }

    let private tryGetInt (el: JsonElement) (name: string) : int option =
        match el.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with true, n -> Some n | _ -> None
        | _ -> None

    let private parseTurnCompleted (root: JsonElement) : LlmEvent option =
        // duration_ms / total_cost_usd / stop_reason / permission_denials 정보 부재 → placeholder.
        // usage (input_tokens / cached_input_tokens / output_tokens / reasoning_output_tokens) 보존이 필요하면
        // LlmEvent 에 새 case 추가 — 현 단계는 SessionEnd 1회 발사로 충분 (turn 종료 신호).
        // forensic 보강: usage 통계는 LlmEvent 에는 포함 안 되지만 Log.provider.Debug 로 트레이스 — turn 별
        // token 사용량 / cache hit 율 / reasoning 비중 추적 가능.
        match root.TryGetProperty("usage") with
        | true, usage when usage.ValueKind = JsonValueKind.Object ->
            let input = tryGetInt usage "input_tokens" |> Option.defaultValue 0
            let cached = tryGetInt usage "cached_input_tokens" |> Option.defaultValue 0
            let output = tryGetInt usage "output_tokens" |> Option.defaultValue 0
            let reasoning = tryGetInt usage "reasoning_output_tokens" |> Option.defaultValue 0
            Log.provider.Debug(sprintf "Codex turn.completed usage: input=%d cached=%d output=%d reasoning=%d" input cached output reasoning)
        | _ -> ()
        Some(SessionEnd(0, 0m, false, "end_turn", 0))

    /// 단일 JSONL 라인을 파싱해서 0~N 개의 LlmEvent. 빈 라인 / 비-JSON 평문 / 알 수 없는 type → 빈 시퀀스.
    let parseLine (line: string) : LlmEvent seq =
        let trimmed = if isNull line then "" else line.Trim()
        if trimmed.Length = 0 || not (trimmed.StartsWith("{")) then Seq.empty
        elif trimmed.Length > MaxLineLength then
            Log.provider.Warn(sprintf "CodexStreamJsonParser: line dropped — length %d > MaxLineLength %d" trimmed.Length MaxLineLength)
            Seq.empty
        else
            let opts = JsonDocumentOptions(MaxDepth = MaxJsonDepth)
            let mutable doc : JsonDocument = null
            try
                doc <- JsonDocument.Parse(trimmed, opts)
            with ex ->
                Log.provider.Warn(sprintf "CodexStreamJsonParser: JSON parse failed (%s) — line skipped (len=%d, head=%s)"
                                    (ex.GetType().Name) trimmed.Length
                                    (if trimmed.Length > 80 then trimmed.Substring(0, 80) + "..." else trimmed))
            if isNull doc then Seq.empty
            else
                use d = doc
                let root = d.RootElement
                let typ = tryGetString root "type" |> Option.defaultValue ""
                let result =
                    match typ with
                    | "thread.started" -> parseThreadStarted root |> Option.toList
                    | "item.completed" -> parseItemCompleted root |> List.ofSeq
                    | "turn.completed" -> parseTurnCompleted root |> Option.toList
                    | "turn.started"   -> []  // Codex sentinel — 명시적 무시
                    | _ -> []
                result :> seq<_>
