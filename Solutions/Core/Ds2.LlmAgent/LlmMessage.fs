namespace Ds2.LlmAgent

/// 첨부 가능한 이미지 포맷. 화이트리스트 기반.
type ImageFormat =
    | Png
    | Jpeg
    | Gif
    | Webp

/// LLM 에 전달되는 사용자 첨부물.
///
/// rev 4 (2026-05-09) 결정: spike S-1/S-2/S-3 결과 단일 `Image` case 로 모든 provider 커버.
/// Codex CLI (`-i/--image <FILE>...`) 어댑터는 bytes → 임시 파일 spool 후 path 전달 책임 보유.
/// Claude CLI 는 `--input-format stream-json` 으로 base64 inline. Anthropic / OpenAI / Ollama API 도 SDK content block 으로 inline.
type Attachment =
    /// 이미지 첨부. mime = "image/png" | "image/jpeg" | "image/gif" | "image/webp".
    | Image of name: string * bytes: byte[] * mime: string
    /// PDF 첨부 (Phase 3b). Anthropic / Claude CLI 만 native 지원.
    | Pdf of name: string * bytes: byte[]
    /// 텍스트/코드 첨부 — prompt injection 방어 위해 fenced wrapper 로 inline (정책 15).
    | TextFile of name: string * content: string

/// `ILlmProvider.Send` 의 user message 표현. 텍스트 + 첨부.
///
/// 컬렉션은 `Attachment[]` (불변 array) — C# producer (LlmChatViewModel 등) 가 FSharpList 변환 부담 없이 사용.
/// review m11: C# 측에서 `Attachments=null` 로 record 만드는 경로 차단 — `Create` factory 사용 권장.
type LlmUserMessage = {
    Text: string
    Attachments: Attachment[]
}
with
    /// 텍스트 only message (회귀 호환 helper).
    static member OfText(text: string) =
        { Text = text; Attachments = [||] }

    /// review m11 — null Attachments 정규화 factory. C# `new LlmUserMessage(text, null)` 같은 경로 방어.
    static member Create(text: string, attachments: Attachment[]) =
        { Text = text
          Attachments = if isNull attachments then [||] else attachments }

/// provider 별 multimodal 능력. `ILlmProvider.Capabilities` 노출.
///
/// Claude CLI / Codex CLI 는 `EnsureCli` 시점 정적 확정. Ollama 는 모델별 동적 (`/api/show`) 갱신.
type Capabilities = {
    /// 지원하는 이미지 포맷. 비어있으면 이미지 첨부 자체 미지원.
    ImageFormats: Set<ImageFormat>
    /// PDF native 지원 여부. true 면 자체 채널 wire, false 면 정책 8 (즉시 거부) 흐름.
    SupportsPdfNative: bool
    /// 단일 이미지 최대 byte (None = provider 자체 한도 — UI 가 보수적 cap 사용).
    MaxImageBytes: int64 option
    /// 단일 PDF 최대 byte (None = provider 자체 한도).
    MaxPdfBytes: int64 option
    /// turn 당 첨부 개수 cap (None = 정책 6 의 기본값 10 사용).
    MaxAttachmentCount: int option
}
with
    /// 첨부 미지원 (텍스트 only). review m4: `static member val` 로 1회 평가 (매 접근마다 record alloc 회피).
    static member val TextOnly =
        { ImageFormats = Set.empty
          SupportsPdfNative = false
          MaxImageBytes = None
          MaxPdfBytes = None
          MaxAttachmentCount = None } with get

    /// 이미지 4종 (png/jpg/gif/webp) + PDF 미지원. Codex CLI / OpenAI API / vision 지원 Ollama 모델용.
    static member ImagesOnly(maxImageBytes: int64) =
        { ImageFormats = Set.ofList [Png; Jpeg; Gif; Webp]
          SupportsPdfNative = false
          MaxImageBytes = Some maxImageBytes
          MaxPdfBytes = None
          MaxAttachmentCount = None }

    /// 이미지 4종 + PDF native. Claude CLI (`--input-format stream-json`) / Anthropic API 용.
    static member ImagesAndPdf(maxImageBytes: int64, maxPdfBytes: int64) =
        { ImageFormats = Set.ofList [Png; Jpeg; Gif; Webp]
          SupportsPdfNative = true
          MaxImageBytes = Some maxImageBytes
          MaxPdfBytes = Some maxPdfBytes
          MaxAttachmentCount = None }

/// provider 별 wire 한도 SSOT (review 2차 M1). 4 호출처의 byte literal 중복 제거.
/// 변경 시 이 한 곳만 수정 — Claude/Codex CLI provider + ApiProviderFactory (Anthropic/OpenAI) 위임.
module CapabilityPresets =
    let private MB = 1024L * 1024L

    /// turn 당 첨부 개수 cap 의 SSOT (정책 6, review m1). VM 측 `LlmChatViewModel.MaxAttachmentCount`
    /// 가 본 literal 을 참조하도록 향후 갱신 예정.
    [<Literal>]
    let DefaultMaxAttachmentCount = 10

    /// Anthropic API / Claude CLI (`--input-format stream-json`) — 5MB image / 32MB PDF (외부 검증 통과).
    let AnthropicWire = Capabilities.ImagesAndPdf(5L * MB, 32L * MB)

    /// OpenAI API — 20MB image, PDF placeholder (V-1 미해결, S-4 spike 결과 의존).
    let OpenAiApiWire = Capabilities.ImagesOnly(20L * MB)

    /// Codex CLI (`-i/--image <FILE>...`) — OpenAI 와 동일 한도. 의미 명료를 위해 별도 alias.
    let CodexCliWire = Capabilities.ImagesOnly(20L * MB)

/// 첨부 metadata 추출 helper (commit-6 m2). F# DU C# interop 에서 multi-field named 의 명칭 모호함 회피.
module AttachmentInfo =
    /// 이미지 mime — 이미지 case 가 아니면 None.
    let tryGetImageMime (att: Attachment) : string option =
        match att with
        | Image (_, _, mime) -> Some mime
        | _ -> None

/// 텍스트 첨부 inline wrapper (정책 15) + history summary (정책 17). commit-6 (rev 11).
/// provider 가 본 helper 만으로 첨부 → prompt 본문 / history summary 변환 — provider 별 중복 로직 회피.
module AttachmentRendering =

    /// byte 크기 표기 ("B" / "KB" / "MB"). chip / summary 공통.
    let formatBytes (n: int64) : string =
        if n < 1024L then sprintf "%dB" n
        elif n < 1024L * 1024L then sprintf "%.1fKB" (float n / 1024.0)
        else sprintf "%.2fMB" (float n / 1024.0 / 1024.0)

    /// 확장자 → fenced code block lang token (정책 15). 화이트리스트 외 확장자는 그대로 사용.
    let langTokenOf (filename: string) : string =
        let ext = System.IO.Path.GetExtension(filename).ToLowerInvariant().TrimStart('.')
        match ext with
        | "fs" | "fsi" | "fsx" -> "fsharp"
        | "cs" | "csx" -> "csharp"
        | "ts" | "tsx" -> "typescript"
        | "js" | "jsx" | "mjs" | "cjs" -> "javascript"
        | "py" -> "python"
        | "rb" -> "ruby"
        | "go" -> "go"
        | "rs" -> "rust"
        | "java" -> "java"
        | "kt" -> "kotlin"
        | "sql" -> "sql"
        | "ps1" -> "powershell"
        | "sh" -> "bash"
        | "bat" | "cmd" -> "batch"
        | "html" | "htm" -> "html"
        | "css" -> "css"
        | "scss" -> "scss"
        | "json" -> "json"
        | "xml" -> "xml"
        | "yaml" | "yml" -> "yaml"
        | "md" -> "markdown"
        | "txt" | "log" -> ""
        | other -> other

    /// 텍스트 첨부를 prompt 본문에 inject 할 fenced code block (정책 15 prompt injection 방어).
    /// 본문에 ``` fence 충돌 시 4-backtick 으로 escalate.
    /// 이미지/PDF 는 multimodal content block 으로 직접 wire — placeholder 만 반환.
    let toInlineString (att: Attachment) : string =
        match att with
        | TextFile (name, content) ->
            let lang = langTokenOf name
            let fence = if content.Contains("```") then "````" else "```"
            sprintf "%s%s filename=\"%s\"\n%s\n%s" fence lang name content fence
        | Image (name, _, _)
        | Pdf (name, _) ->
            sprintf "[binary attachment: %s — multimodal content block 으로 전달]" name

    /// history summary 형식 (정책 17). bytes 미누적 — 다음 turn 의 multi-turn history 에 메타만 보관.
    let summarize (att: Attachment) : string =
        match att with
        | Image (name, bytes, _) ->
            sprintf "[image: %s %s]" name (formatBytes (int64 bytes.Length))
        | Pdf (name, bytes) ->
            sprintf "[pdf: %s %s]" name (formatBytes (int64 bytes.Length))
        | TextFile (name, content) ->
            let bytes = int64 (System.Text.Encoding.UTF8.GetByteCount content)
            sprintf "[text: %s %s]" name (formatBytes bytes)

/// review 2차 M4 — provider Send 진입 시 capability 미지원 첨부 silent drop 방어.
/// commit-4 단계 (chip UI 활성 / provider wire 미구현) = warn only. commit-6 wire 진입 시 strict invalidArg 로 전환 예정.
/// UI 측 1차 검증 (`LlmChatViewModel.AddPathsAsync`) 후에도 race / provider 전환 누락 시 본 helper 가 2차 안전망.
module LlmUserMessageOps =
    /// 미지원 첨부에 대해 log warn 만. 본 함수는 의도적으로 throw 하지 않음 — commit-4 dead code 단계 호환.
    let WarnUnsupportedAttachments (caps: Capabilities) (msg: LlmUserMessage) : unit =
        if isNull (box msg.Attachments) then ()
        else
            for att in msg.Attachments do
                match att with
                | Image (name, _, _) ->
                    if caps.ImageFormats.IsEmpty then
                        Log.provider.Warn(sprintf "이미지 첨부 무시 — provider capability 미지원: %s" name)
                | Pdf (name, _) ->
                    if not caps.SupportsPdfNative then
                        Log.provider.Warn(sprintf "PDF 첨부 무시 — provider capability 미지원: %s" name)
                | TextFile _ -> ()  // 텍스트는 inline wrapper (commit-6) — capability 무관
