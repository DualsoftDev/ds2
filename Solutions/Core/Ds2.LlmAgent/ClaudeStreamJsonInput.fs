namespace Ds2.LlmAgent

open System
open System.IO
open System.Text
open System.Text.Json

/// Claude CLI `--input-format stream-json` stdin 인코더 (commit-6b).
///
/// 첨부가 있는 turn 을 위해 user message 를 Anthropic API 의 multipart content block 형식으로 wire.
/// 첨부 없을 때는 기존 `--input-format text` 경로 (raw stdin) 를 그대로 사용 — 본 module 미경유.
///
/// **wire 형식** (단일 라인, JSON Lines):
/// ```
/// {"type":"user","message":{"role":"user","content":[{"type":"text","text":"..."},
///   {"type":"image","source":{"type":"base64","media_type":"image/png","data":"<base64>"}},
///   {"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"<base64>"}}]}}
/// ```
///
/// spike S-1 (`done-llm-chat-attachment.md`) 결과에 따른 채널. `Stdin = Some encoded` 으로
/// `CliProcessHost.runStream` 에 전달, CLI 측이 한 줄씩 user turn 으로 인식.
[<RequireQualifiedAccess>]
module ClaudeStreamJsonInput =

    /// 단일 user turn 을 JSON Lines 한 줄로 인코딩. **trailing `\n` 포함** —
    /// Claude CLI 의 stream-json input 은 line-based parser ("Error parsing streaming input line"
    /// 회귀: envelope 끝에 `\n` 없으면 partial line 으로 인식, parse 실패 후 process exit 1).
    /// 첨부는 Image / Pdf 만 wire — TextFile 은 이미 fenced wrapper 로 prompt 본문에 inline 됨 (정책 15).
    let encode (prompt: string) (attachments: Attachment seq) : string =
        use buffer = new MemoryStream()
        // indented=false (단일 라인) + skipValidation=false (안정성).
        let opts = JsonWriterOptions(Indented = false)
        do
            use writer = new Utf8JsonWriter(buffer, opts)
            writer.WriteStartObject()
            writer.WriteString("type", "user")
            writer.WriteStartObject("message")
            writer.WriteString("role", "user")
            writer.WriteStartArray("content")

            // text content 는 항상 첫 블록 (Claude API 권장 — 텍스트 instruction 이 image 보다 먼저).
            // 빈 prompt 도 명시적 빈 text block 으로 wire (LLM 이 "본 turn 은 첨부 검토만" 추론 가능).
            writer.WriteStartObject()
            writer.WriteString("type", "text")
            writer.WriteString("text", prompt)
            writer.WriteEndObject()

            for att in attachments do
                match att with
                | Image (_, bytes, fmt) ->
                    writer.WriteStartObject()
                    writer.WriteString("type", "image")
                    writer.WriteStartObject("source")
                    writer.WriteString("type", "base64")
                    // rev 18 m3: ImageFormat → mime 1:1 변환은 Attachment.mimeOf SSOT.
                    writer.WriteString("media_type", Attachment.mimeOf fmt)
                    // Convert.ToBase64String 1회 — Anthropic API 와 동일 inline base64.
                    writer.WriteString("data", Convert.ToBase64String(bytes))
                    writer.WriteEndObject()
                    writer.WriteEndObject()
                | Pdf (_, bytes) ->
                    writer.WriteStartObject()
                    writer.WriteString("type", "document")
                    writer.WriteStartObject("source")
                    writer.WriteString("type", "base64")
                    writer.WriteString("media_type", "application/pdf")
                    writer.WriteString("data", Convert.ToBase64String(bytes))
                    writer.WriteEndObject()
                    writer.WriteEndObject()
                | TextFile _ ->
                    // TextFile 은 prompt 본문에 fenced wrapper 로 inline — 본 wire path 미경유.
                    // 여기 도달하면 호출자 (LlmChatViewModel) 가 nonText filter 를 빠뜨린 case 라 fail-fast.
                    invalidOp "TextFile 첨부는 prompt 본문에 inline — stream-json content block 으로 wire 금지."

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.Flush()
        // Claude CLI line-based parser 의 line 종료 — `\n` 누락 시 "Error parsing streaming input line" + exit 1.
        Encoding.UTF8.GetString(buffer.ToArray()) + "\n"
