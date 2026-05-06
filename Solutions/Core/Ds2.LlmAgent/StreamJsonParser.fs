namespace Ds2.LlmAgent

open System
open System.Text.Json

/// Claude CLI `--output-format stream-json` 라인 → `LlmEvent` 정규화.
///
/// 단일 라인이 0~N 개 LlmEvent 를 만든다 (assistant 의 content 배열이 thinking + text + tool_use 혼재 가능).
/// JSON parse 실패 / 알 수 없는 type 은 silently skip 하고 raw 라인은 호출자가 RawStream logger 로 기록.
[<RequireQualifiedAccess>]
module StreamJsonParser =

    /// 단일 stream-json 라인 최대 바이트 길이 (UTF-16 char 단위).
    /// Claude CLI stdout 은 신뢰된 input 이지만 비정상 / 깨진 stream 으로부터의 OOM 1차 방어.
    /// 1MB 면 정상 assistant turn 응답 수 배 여유.
    let MaxLineLength = 1024 * 1024

    /// JSON 트리 최대 depth. System.Text.Json 의 default 64 보다 보수적으로 32 채택.
    /// Claude CLI 패킷 (system/init / assistant.message.content[].* / result) 은 4~5 depth.
    let MaxJsonDepth = 32

    let private tryGetString (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private tryGetInt (el: JsonElement) (name: string) : int option =
        match el.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with true, n -> Some n | _ -> None
        | _ -> None

    let private tryGetDecimal (el: JsonElement) (name: string) : decimal option =
        match el.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetDecimal() with true, n -> Some n | _ -> None
        | _ -> None

    let private tryGetBool (el: JsonElement) (name: string) : bool option =
        match el.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False ->
            Some(v.GetBoolean())
        | _ -> None

    let private parseInit (root: JsonElement) : LlmEvent option =
        match tryGetString root "session_id" with
        | None -> None
        | Some sid ->
            let model = tryGetString root "model" |> Option.defaultValue "unknown"
            let tools =
                match root.TryGetProperty("tools") with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    [ for t in v.EnumerateArray() do
                        if t.ValueKind = JsonValueKind.String then yield t.GetString() ]
                | _ -> []
            let mcpServers =
                match root.TryGetProperty("mcp_servers") with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    [ for s in v.EnumerateArray() do
                        match tryGetString s "name", tryGetString s "status" with
                        | Some n, Some st -> yield { Name = n; Status = st }
                        | _ -> () ]
                | _ -> []
            Some(SessionStarted(sid, model, tools, mcpServers))

    let private parseAssistantContent (content: JsonElement) : LlmEvent seq =
        seq {
            if content.ValueKind = JsonValueKind.Array then
                for item in content.EnumerateArray() do
                    match tryGetString item "type" with
                    | Some "text" ->
                        match tryGetString item "text" with
                        | Some t when t.Length > 0 -> yield AssistantDelta t
                        | _ -> ()
                    | Some "thinking" ->
                        match tryGetString item "thinking" with
                        | Some t when t.Length > 0 -> yield Thinking t
                        | _ -> ()
                    | Some "tool_use" ->
                        match tryGetString item "id", tryGetString item "name" with
                        | Some id, Some name ->
                            let input =
                                match item.TryGetProperty("input") with
                                | true, v -> v.Clone()
                                | _ -> JsonDocument.Parse("{}").RootElement.Clone()
                            yield ToolUse(id, name, input)
                        | _ -> ()
                    | _ -> ()
        }

    let private parseAssistant (root: JsonElement) : LlmEvent seq =
        match root.TryGetProperty("message") with
        | true, msg ->
            match msg.TryGetProperty("content") with
            | true, content -> parseAssistantContent content
            | _ -> Seq.empty
        | _ -> Seq.empty

    let private parseToolResultContent (content: JsonElement) : string =
        if content.ValueKind = JsonValueKind.String then content.GetString()
        elif content.ValueKind = JsonValueKind.Array then
            let parts =
                [ for item in content.EnumerateArray() do
                    if item.ValueKind = JsonValueKind.Object then
                        match tryGetString item "type" with
                        | Some "text" ->
                            match tryGetString item "text" with
                            | Some s -> yield s
                            | None -> ()
                        | _ -> yield item.GetRawText()
                    else
                        yield item.GetRawText() ]
            String.Join("\n", parts)
        else
            content.GetRawText()

    let private parseUserMessage (root: JsonElement) : LlmEvent seq =
        seq {
            match root.TryGetProperty("message") with
            | true, msg ->
                match msg.TryGetProperty("content") with
                | true, content when content.ValueKind = JsonValueKind.Array ->
                    for item in content.EnumerateArray() do
                        match tryGetString item "type" with
                        | Some "tool_result" ->
                            let toolUseId = tryGetString item "tool_use_id" |> Option.defaultValue ""
                            let isError = tryGetBool item "is_error" |> Option.defaultValue false
                            let body =
                                match item.TryGetProperty("content") with
                                | true, c -> parseToolResultContent c
                                | _ -> ""
                            yield ToolResult(toolUseId, isError, body)
                        | _ -> ()
                | _ -> ()
            | _ -> ()
        }

    let private parseRateLimit (root: JsonElement) : LlmEvent option =
        // 주의: 다른 패킷은 snake_case (session_id / is_error 등) 인데
        // rate_limit_info 객체 *내부* 필드만 camelCase (resetsAt / rateLimitType / isUsingOverage).
        // 실증 결과 그대로 매칭. CLI 변경 시 본 함수만 갱신.
        match root.TryGetProperty("rate_limit_info") with
        | true, info ->
            let status = tryGetString info "status" |> Option.defaultValue "unknown"
            let resetsAt =
                match info.TryGetProperty("resetsAt") with
                | true, v when v.ValueKind = JsonValueKind.Number ->
                    match v.TryGetInt64() with true, n -> n | _ -> 0L
                | _ -> 0L
            Some(RateLimitEvent(resetsAt, status))
        | _ -> None

    let private parseResult (root: JsonElement) : LlmEvent option =
        let durationMs = tryGetInt root "duration_ms" |> Option.defaultValue 0
        let costUsd = tryGetDecimal root "total_cost_usd" |> Option.defaultValue 0m
        let isError = tryGetBool root "is_error" |> Option.defaultValue false
        let stopReason = tryGetString root "stop_reason" |> Option.defaultValue "unknown"
        let denials =
            match root.TryGetProperty("permission_denials") with
            | true, v when v.ValueKind = JsonValueKind.Array -> v.GetArrayLength()
            | _ -> 0
        Some(SessionEnd(durationMs, costUsd, isError, stopReason, denials))

    /// 단일 stream-json 라인을 파싱해서 0~N 개의 LlmEvent 를 만든다.
    /// 라인이 빈 문자열, 비-JSON 평문 (e.g. shell hook 메시지), 알 수 없는 type 이면 빈 시퀀스.
    /// MaxLineLength / MaxJsonDepth 초과 시 Log.Warn + skip (m5/m10 — silent skip 의 forensic 단서).
    let parseLine (line: string) : LlmEvent seq =
        let trimmed = if isNull line then "" else line.Trim()
        if trimmed.Length = 0 || not (trimmed.StartsWith("{")) then Seq.empty
        elif trimmed.Length > MaxLineLength then
            Log.provider.Warn(sprintf "StreamJsonParser: line dropped — length %d > MaxLineLength %d" trimmed.Length MaxLineLength)
            Seq.empty
        else
            let opts = JsonDocumentOptions(MaxDepth = MaxJsonDepth)
            let mutable doc : JsonDocument = null
            try
                doc <- JsonDocument.Parse(trimmed, opts)
            with ex ->
                Log.provider.Warn(sprintf "StreamJsonParser: JSON parse failed (%s) — line skipped (len=%d, head=%s)"
                                    (ex.GetType().Name) trimmed.Length
                                    (if trimmed.Length > 80 then trimmed.Substring(0, 80) + "..." else trimmed))
            if isNull doc then Seq.empty
            else
                use d = doc
                let root = d.RootElement
                let typ = tryGetString root "type" |> Option.defaultValue ""
                let result =
                    match typ with
                    | "system" ->
                        match tryGetString root "subtype" with
                        | Some "init" -> parseInit root |> Option.toList
                        | _ -> []
                    | "assistant" -> parseAssistant root |> List.ofSeq
                    | "user" -> parseUserMessage root |> List.ofSeq
                    | "rate_limit_event" -> parseRateLimit root |> Option.toList
                    | "result" -> parseResult root |> Option.toList
                    | _ -> []
                result :> seq<_>
