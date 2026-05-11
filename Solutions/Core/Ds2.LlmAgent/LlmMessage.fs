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
///
/// rev 18 (2026-05-09) 일원화 (m3): `Image` case 의 mime: string → format: ImageFormat 로 교체.
/// 4 mime 의 free string 표현 (`"image/png"` 등 마법 문자열) 을 화이트리스트 enum 으로 좁혀
/// `_ -> ".bin"` 같은 dead fallback 제거 + AttachmentClassifier / Capabilities 와의 SSOT 일원화.
/// mime 문자열은 wire 시점에 `Attachment.mimeOf format` 으로 1:1 변환.
type Attachment =
    /// 이미지 첨부. format ∈ {Png, Jpeg, Gif, Webp} — mime 변환은 <see cref="Attachment.mimeOf"/>.
    | Image of name: string * bytes: byte[] * format: ImageFormat
    /// PDF 첨부 (Phase 3b). Anthropic / Claude CLI 만 native 지원.
    | Pdf of name: string * bytes: byte[]
    /// 텍스트/코드 첨부 — prompt injection 방어 위해 fenced wrapper 로 inline (정책 15).
    | TextFile of name: string * content: string

/// <see cref="Attachment.Image"/> 의 ImageFormat ↔ mime / 확장자 1:1 매핑 (rev 18 m3 일원화).
/// Anthropic / OpenAI / Claude CLI multipart content block 의 `media_type` 필드 형식과 동일.
/// rev 18 review 후속: `extOf` 추가 — Codex CLI 임시 파일 spool / chip notice 사용자 표시 등 SSOT 확장.
///
/// `[<CompilationRepresentation(ModuleSuffix)>]` = type `Attachment` 와 동명 module 충돌 회피.
/// F# 측은 `Attachment.mimeOf fmt` 그대로 호출, C# 측은 `Ds2.LlmAgent.AttachmentModule.mimeOf(fmt)` 로 접근.
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Attachment =
    /// ImageFormat → mime ("image/png" 등). 4 case exhaustive — 신규 case 추가 시 컴파일러가 강제.
    let mimeOf (fmt: ImageFormat) : string =
        match fmt with
        | Png -> "image/png"
        | Jpeg -> "image/jpeg"
        | Gif -> "image/gif"
        | Webp -> "image/webp"

    /// ImageFormat → 파일 확장자 (".png" 등). 4 case exhaustive.
    /// `mimeOf` 와 동일 SSOT — Codex CLI 임시 파일 spool (`CodexCliProvider.fs`) / C# chip notice
    /// 사용자 표시 (`LlmChatViewModel.Attachments.cs ExtOf`) 양쪽이 본 helper 위임.
    let extOf (fmt: ImageFormat) : string =
        match fmt with
        | Png -> ".png"
        | Jpeg -> ".jpg"
        | Gif -> ".gif"
        | Webp -> ".webp"

/// `ILlmProvider.Send` 의 user message 표현. 텍스트 + 첨부.
///
/// 컬렉션은 `Attachment[]` (불변 array) — C# producer (LlmChatViewModel 등) 가 FSharpList 변환 부담 없이 사용.
/// review m11: C# 측에서 `Attachments=null` 로 record 만드는 경로 차단 — `Create` factory 사용 권장.
type LlmUserMessage = {
    Text: string
    Attachments: Attachment[]
    /// round-trip §C1 (doc: Apps/Promaker/Docs/todo-promaker-llm-roundtrip-optimization.md) —
    /// store snapshot block (있으면 `<store-snapshot revision="N"> ... </store-snapshot>` envelope).
    /// **history 누적 회피 정책**: in-process IChatClient provider (ApiChatProvider) 는 본 prefix 를 본 turn 호출
    /// 시점에만 multi-content 로 분리해 prepend — `_history` 의 user message 본문에는 들어가지 않음. CLI provider
    /// (Claude/Codex) 는 prompt 텍스트 앞에 단순 prepend (CLI 자체가 history 관리하므로 분리 불가능).
    /// None / `""` 면 미첨부.
    SnapshotPrefix: string option
}
with
    /// 텍스트 only message (회귀 호환 helper).
    static member OfText(text: string) =
        { Text = text; Attachments = [||]; SnapshotPrefix = None }

    /// review m11 — null Attachments 정규화 factory. C# `new LlmUserMessage(text, null)` 같은 경로 방어.
    static member Create(text: string, attachments: Attachment[]) =
        { Text = text
          Attachments = if isNull attachments then [||] else attachments
          SnapshotPrefix = None }

    /// round-trip §C1 — snapshot prefix 와 함께 생성. `snapshotPrefix` 가 null/empty 면 `Create` 와 동일.
    static member CreateWithSnapshot(text: string, attachments: Attachment[], snapshotPrefix: string) =
        { Text = text
          Attachments = if isNull attachments then [||] else attachments
          SnapshotPrefix = if System.String.IsNullOrEmpty snapshotPrefix then None else Some snapshotPrefix }

    /// C# interop helper — `SnapshotPrefix` 가 None 이면 null, Some s 면 s. C# 측은
    /// `msg.SnapshotPrefixOrNull` 로 nullable string 처럼 사용 (FSharpOption boxing 회피).
    member this.SnapshotPrefixOrNull =
        match this.SnapshotPrefix with
        | Some s -> s
        | None -> null

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
    /// 단일 PDF 최대 페이지 수 (정책 11 — Anthropic 200K context = 100p / 그 외 600p, OpenAI = 100p).
    /// None = 페이지 cap 미적용. Phase 3b (rev 15 / 2026-05-09) 추가.
    MaxPdfPages: int option
    /// turn 당 첨부 개수 cap (None = 정책 6 의 기본값 10 사용).
    MaxAttachmentCount: int option
    /// round-trip §J2 — provider 가 Anthropic-compatible `cache_control: ephemeral` 부착을
    /// raw wire 까지 통과시키는지 여부. true 면 ApiChatProvider 가 system / sticky snapshot block 에
    /// `WithCacheControl` 부여 (다른 어댑터는 silent ignore). label 문자열 비교 대신 capability 비트로
    /// 분기하여 Bedrock 등 Anthropic 호환 endpoint 추가 시 silent miss 방지.
    SupportsAnthropicCacheControl: bool
}
with
    /// 첨부 미지원 (텍스트 only). review m4: `static member val` 로 1회 평가 (매 접근마다 record alloc 회피).
    static member val TextOnly =
        { ImageFormats = Set.empty
          SupportsPdfNative = false
          MaxImageBytes = None
          MaxPdfBytes = None
          MaxPdfPages = None
          MaxAttachmentCount = None
          SupportsAnthropicCacheControl = false } with get

    /// 이미지 4종 (png/jpg/gif/webp) + PDF 미지원. Codex CLI / vision 지원 Ollama 모델용.
    static member ImagesOnly(maxImageBytes: int64) =
        { ImageFormats = Set.ofList [Png; Jpeg; Gif; Webp]
          SupportsPdfNative = false
          MaxImageBytes = Some maxImageBytes
          MaxPdfBytes = None
          MaxPdfPages = None
          MaxAttachmentCount = None
          SupportsAnthropicCacheControl = false }

    /// 이미지 4종 + PDF native. Claude CLI (`--input-format stream-json`) / Anthropic API / OpenAI API.
    /// `?maxPdfPages` = 페이지 cap (Phase 3b). 미지정 시 페이지 cap 미적용 (None).
    static member ImagesAndPdf(maxImageBytes: int64, maxPdfBytes: int64, ?maxPdfPages: int) =
        { ImageFormats = Set.ofList [Png; Jpeg; Gif; Webp]
          SupportsPdfNative = true
          MaxImageBytes = Some maxImageBytes
          MaxPdfBytes = Some maxPdfBytes
          MaxPdfPages = maxPdfPages
          MaxAttachmentCount = None
          SupportsAnthropicCacheControl = false }

/// provider 별 wire 한도 SSOT (review 2차 M1). 4 호출처의 byte literal 중복 제거.
/// 변경 시 이 한 곳만 수정 — Claude/Codex CLI provider + ApiProviderFactory (Anthropic/OpenAI) 위임.
module CapabilityPresets =
    let private MB = 1024L * 1024L

    /// turn 당 첨부 개수 cap 의 SSOT (정책 6, review m1). VM 측 `LlmChatViewModel.MaxAttachmentCount`
    /// 가 본 literal 을 참조하도록 향후 갱신 예정.
    [<Literal>]
    let DefaultMaxAttachmentCount = 10

    /// Anthropic API / Claude CLI (`--input-format stream-json`) — 5MB image / 32MB PDF.
    /// 페이지 cap = 100p (200K context Opus 4.7 / Sonnet 4.6 기준 보수, 정책 11 외부 검증).
    /// round-trip §J2 — `SupportsAnthropicCacheControl = true` 만 본 preset 한정. Anthropic 호환 endpoint
    /// (Bedrock 등) 추가 시 동일 비트로 새 preset 정의.
    let AnthropicWire =
        { Capabilities.ImagesAndPdf(5L * MB, 32L * MB, maxPdfPages = 100) with
            SupportsAnthropicCacheControl = true }

    /// OpenAI API — 20MB image / 32MB PDF / 100p (Phase 3a-pre S-4 spike rev 15 — V-1 RESOLVED).
    /// Chat Completions API 의 `type:"file"` content block + Microsoft.Extensions.AI.OpenAI 10.5.2
    /// `application/pdf` → `ChatMessageContentPart.CreateFilePart(...)` 자동 wire.
    let OpenAiApiWire = Capabilities.ImagesAndPdf(20L * MB, 32L * MB, maxPdfPages = 100)

    /// Codex CLI (`-i/--image <FILE>...`) — OpenAI 와 동일 image 한도. PDF 미지원.
    let CodexCliWire = Capabilities.ImagesOnly(20L * MB)

/// 이미지 첨부 decompose helper (commit-6b). C# DataContent / Anthropic content block 으로 wire 시 사용.
type ImageAttachmentData = { Name: string; Bytes: byte[]; Mime: string }

/// PDF 첨부 decompose helper (commit-6b). C# DataContent / Anthropic document content block 으로 wire 시 사용.
type PdfAttachmentData = { Name: string; Bytes: byte[] }

/// 첨부 metadata 추출 helper (commit-6 m2). F# DU C# interop 에서 multi-field named 의 명칭 모호함 회피.
module AttachmentInfo =
    /// 이미지 ImageFormat — 이미지 case 가 아니면 None. rev 18 m3: chip reevaluate 시 mime → ImageFormat 역추론
    /// 회피 위해 직접 format 노출. C# 측에서 <c>caps.ImageFormats.Contains(fmt)</c> 비교에 사용.
    /// rev 18 review 후속: `tryGetImageMime` 폐기 (외부 호출자 0건) — mime 필요 시 `tryGetImage.Mime` 사용.
    let tryGetImageFormat (att: Attachment) : ImageFormat option =
        match att with
        | Image (_, _, fmt) -> Some fmt
        | _ -> None

    /// commit-6b: 이미지 첨부 → record. 이미지 case 가 아니면 None.
    /// C# 측에서 `Attachment.Image` 중첩 클래스의 field 명 (F# 컴파일러 버전별) 의존 회피.
    /// rev 18 m3: format → mime 변환은 본 helper 가 책임 — 호출자 (ApiChatProvider) 의 Mime field 의존 호환 유지.
    let tryGetImage (att: Attachment) : ImageAttachmentData option =
        match att with
        | Image (n, b, fmt) -> Some { Name = n; Bytes = b; Mime = Attachment.mimeOf fmt }
        | _ -> None

    /// commit-6b: PDF 첨부 → record. PDF case 가 아니면 None.
    let tryGetPdf (att: Attachment) : PdfAttachmentData option =
        match att with
        | Pdf (n, b) -> Some { Name = n; Bytes = b }
        | _ -> None

    /// 첨부 공통 이름 — chip filename, 임시 파일 spool naming 등.
    let attachmentName (att: Attachment) : string =
        match att with
        | Image (n, _, _) -> n
        | Pdf (n, _) -> n
        | TextFile (n, _) -> n

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

    /// 본문에 등장하는 backtick run 의 longest length. fence escalate 산정용.
    let private longestBacktickRun (content: string) : int =
        let mutable longest = 0
        let mutable cur = 0
        for ch in content do
            if ch = '`' then
                cur <- cur + 1
                if cur > longest then longest <- cur
            else
                cur <- 0
        longest

    /// 텍스트 첨부를 prompt 본문에 inject 할 fenced code block (정책 15 prompt injection 방어).
    /// 본문 안 backtick longest run + 1 만큼 fence 길이 escalate (5-backtick 이상 PDF/markdown 도 안전).
    /// 이미지/PDF 는 multimodal content block 으로 직접 wire — placeholder 만 반환.
    let toInlineString (att: Attachment) : string =
        match att with
        | TextFile (name, content) ->
            let lang = langTokenOf name
            let fenceLen = max 3 (longestBacktickRun content + 1)
            let fence = String.replicate fenceLen "`"
            sprintf "%s%s filename=\"%s\"\n%s\n%s" fence lang name content fence
        | Image (name, _, _)
        | Pdf (name, _) ->
            sprintf "[binary attachment: %s — multimodal content block 으로 전달]" name

    /// history summary 형식 (정책 17). bytes 미누적 — 다음 turn 의 multi-turn history 에 메타만 보관.
    let summarize (att: Attachment) : string =
        match att with
        | Image (name, bytes, _) ->
            sprintf "[image: %s %s]" name (formatBytes (bytes.LongLength))
        | Pdf (name, bytes) ->
            sprintf "[pdf: %s %s]" name (formatBytes (bytes.LongLength))
        | TextFile (name, content) ->
            let bytes = int64 (System.Text.Encoding.UTF8.GetByteCount content)
            sprintf "[text: %s %s]" name (formatBytes bytes)

/// review 2차 M4 — provider Send 진입 시 capability 미지원 첨부 silent drop 방어.
/// commit-6b (이미지/PDF wire 활성) 부터 strict 모드 — UI 1차 검증 후 race / provider 전환
/// 누락이 발생하면 fail-fast 가 silent drop 보다 안전 (CLAUDE.md "복잡한 예외처리보다 간단한 fail-safe 우선").
module LlmUserMessageOps =
    /// commit-6b strict 모드 — capability 미지원 첨부 발견 시 invalidArg throw.
    /// provider wire 직전 호출 → silent drop 차단. UI 측 `ReevaluateAttachmentsForProvider` (provider 전환 시
    /// 강제 제거) + 첨부 추가 시 capability 검증을 race 로 빠져나간 첨부가 도달하면 fail-fast.
    /// rev 20 (F3 외부 review): format/native 외 **size cap** 도 검증 — 10MB OpenAI 이미지 → Claude (5MB)
    /// 전환 후 send 시 silent 통과하던 회귀 차단.
    let EnforceCapabilityOrFail (caps: Capabilities) (msg: LlmUserMessage) : unit =
        if isNull (box msg.Attachments) then ()
        else
            for att in msg.Attachments do
                match att with
                | Image (name, bytes, fmt) ->
                    if caps.ImageFormats.IsEmpty then
                        invalidArg "msg" (sprintf "이미지 첨부 미지원 provider 에 wire 시도: %s (%s)" name (Attachment.mimeOf fmt))
                    elif not (caps.ImageFormats.Contains fmt) then
                        // rev 18 m3: format 별 세분 검증 추가 — capability 가 일부 format 만 지원하는 provider 대비
                        // (현재는 4종 일괄 지원/미지원이라 dead path 지만 향후 모델별 capability 변동 시 fail-fast).
                        invalidArg "msg" (sprintf "이미지 포맷 미지원 provider 에 wire 시도: %s (%s)" name (Attachment.mimeOf fmt))
                    else
                        match caps.MaxImageBytes with
                        | Some cap when bytes.LongLength > cap ->
                            invalidArg "msg"
                                (sprintf "이미지 크기 cap 초과: %s (%s > %s)"
                                    name
                                    (AttachmentRendering.formatBytes (bytes.LongLength))
                                    (AttachmentRendering.formatBytes cap))
                        | _ -> ()
                | Pdf (name, bytes) ->
                    if not caps.SupportsPdfNative then
                        invalidArg "msg" (sprintf "PDF 첨부 미지원 provider 에 wire 시도: %s" name)
                    else
                        match caps.MaxPdfBytes with
                        | Some cap when bytes.LongLength > cap ->
                            invalidArg "msg"
                                (sprintf "PDF 크기 cap 초과: %s (%s > %s)"
                                    name
                                    (AttachmentRendering.formatBytes (bytes.LongLength))
                                    (AttachmentRendering.formatBytes cap))
                        | _ -> ()
                | TextFile _ -> ()  // 텍스트는 inline — capability 무관
