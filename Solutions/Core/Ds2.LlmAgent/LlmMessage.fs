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
type LlmUserMessage = {
    Text: string
    Attachments: Attachment[]
}
with
    /// 텍스트 only message (회귀 호환 helper).
    static member OfText(text: string) =
        { Text = text; Attachments = [||] }

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
    /// 첨부 미지원 (텍스트 only).
    static member TextOnly =
        { ImageFormats = Set.empty
          SupportsPdfNative = false
          MaxImageBytes = None
          MaxPdfBytes = None
          MaxAttachmentCount = None }

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
