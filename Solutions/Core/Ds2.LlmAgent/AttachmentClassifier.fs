namespace Ds2.LlmAgent

open System
open System.IO
open System.Text

/// 첨부물 분류 결과 (정책 19 SSOT). UI Drop/Paste handler 는 본 분류만으로 의사결정.
type Classification =
    /// 이미지 첨부 가능. 매핑된 ImageFormat 동반.
    | AcceptImage of ImageFormat
    /// 텍스트/코드 첨부 가능 (fenced code block 으로 inline wrap 예정 — 정책 15).
    | AcceptText
    /// PDF 첨부 가능 (provider capability 별도 검증 필요 — 정책 11).
    | AcceptPdf
    /// 명시적으로 거부한 확장자 (e.g. .exe / .svg / .mp4 / .zip / .bmp). 거부 사유 안내용.
    /// `ext` 는 항상 소문자 + leading dot 포함 (`extOf` 의 `ToLowerInvariant` 결과).
    | RejectExtension of ext: string
    /// 화이트리스트에도 거부 list 에도 없는 알 수 없는 확장자 / 확장자 없음.
    | RejectUnknown

/// 첨부 파일 분류 + 텍스트 인코딩 추정 (정책 19 SSOT).
///
/// rev 4 (2026-05-09 / commit-3): UI 호출자 미존재 (dead code 의도). UI commit (commit-4..N) 부터 호출 개시.
module AttachmentClassifier =

    /// 이미지 확장자 → ImageFormat 매핑. SSOT — 새 image 포맷 추가 시 본 함수 + ImageFormat enum 동시 갱신.
    /// drift 검출은 `AttachmentClassifierDriftTests` 가 enum 4 case 전수 검사.
    let private imageFormatOf (ext: string) : ImageFormat option =
        match ext with
        | ".png" -> Some Png
        | ".jpg" | ".jpeg" -> Some Jpeg
        | ".gif" -> Some Gif
        | ".webp" -> Some Webp
        | _ -> None

    /// 텍스트/코드 확장자 화이트리스트 (소문자, leading dot 포함).
    /// 새 언어/포맷 추가 시 본 set 만 갱신 — UI 측 hardcoded list 없음.
    let textExtensions : Set<string> =
        Set.ofList [
            // 문서 / 데이터
            ".txt"; ".md"; ".log"; ".csv"; ".tsv"
            ".json"; ".xml"; ".yaml"; ".yml"; ".ini"; ".toml"
            // F# / .NET
            ".fs"; ".fsi"; ".fsx"
            ".cs"; ".csx"; ".vb"
            // JS / TS
            ".ts"; ".tsx"; ".js"; ".jsx"; ".mjs"; ".cjs"
            // 기타 언어
            ".py"; ".rb"; ".go"; ".rs"; ".java"; ".kt"; ".swift"; ".scala"; ".clj"
            ".c"; ".h"; ".cpp"; ".hpp"; ".cc"
            ".sql"
            // 셸 / 스크립트
            ".sh"; ".ps1"; ".bat"; ".cmd"
            // 웹
            ".html"; ".htm"; ".css"; ".scss"; ".less"
            // 설정
            ".gitignore"; ".gitattributes"; ".editorconfig"
        ]

    /// 명시 거부 확장자 — 실행파일 / 미디어 / 압축 / SVG (XSS/XXE) / BMP·TIFF (대용량 비효율) / 비밀정보.
    let rejectedExtensions : Set<string> =
        Set.ofList [
            // 비밀정보 — `.env` 는 OPENAI_API_KEY / DATABASE_URL 등 평문 비밀을 통상 보관.
            // consent 다이얼로그 ("API 키 / 비밀번호 ... 는 전송되지 않습니다") 와의 정합성 위해 차단.
            ".env"
            // 실행 / 바이너리
            ".exe"; ".dll"; ".msi"; ".bin"; ".so"; ".dylib"; ".com"; ".scr"
            // 압축
            ".zip"; ".7z"; ".rar"; ".tar"; ".gz"; ".bz2"; ".xz"; ".tgz"
            // 비디오
            ".mp4"; ".mov"; ".avi"; ".mkv"; ".webm"; ".flv"; ".wmv"
            // 오디오
            ".mp3"; ".wav"; ".flac"; ".ogg"; ".m4a"; ".aac"
            // 거부된 이미지 포맷 (Anthropic / OpenAI 미지원 또는 보안 이슈)
            ".bmp"; ".tiff"; ".tif"; ".svg"; ".ico"; ".heic"; ".heif"
        ]

    /// 확장자 없는 파일명 화이트리스트 (소문자) — Dockerfile / Makefile / .editorconfig 등.
    /// review m2: "license.txt" 등 .txt 확장자가 있는 항목은 dead entry (`String.IsNullOrEmpty ext` 분기 미진입) → 제거.
    let extensionlessTextNames : Set<string> =
        Set.ofList [
            "dockerfile"; "containerfile"
            "makefile"; "rakefile"; "gemfile"; "procfile"; "vagrantfile"
            "license"; "readme"; "changelog"; "authors"; "contributors"
            "copying"; "notice"; "version"
        ]

    /// path 의 확장자 (소문자, leading dot 포함). 없으면 빈 문자열.
    let private extOf (path: string) =
        Path.GetExtension(path).ToLowerInvariant()

    /// path 의 파일명 (소문자, 확장자 포함).
    let private nameOf (path: string) =
        Path.GetFileName(path).ToLowerInvariant()

    /// 파일 path 분류. UI Drop/Paste 는 본 함수의 결과만으로 의사결정.
    let classify (path: string) : Classification =
        if String.IsNullOrWhiteSpace path then RejectUnknown
        else
            let ext = extOf path
            match imageFormatOf ext with
            | Some fmt -> AcceptImage fmt
            | None ->
                if ext = ".pdf" then AcceptPdf
                elif Set.contains ext textExtensions then AcceptText
                elif Set.contains ext rejectedExtensions then RejectExtension ext
                elif String.IsNullOrEmpty ext then
                    if Set.contains (nameOf path) extensionlessTextNames then AcceptText
                    else RejectUnknown
                else RejectUnknown

    /// 텍스트 인코딩 추정 결과. ConfidenceHigh = BOM 또는 strict UTF-8 통과.
    /// 정책 §3.6 — UTF-8 → CP949 → UTF-16 fallback. CP949 는 .NET Core 환경에서
    /// `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 사전 등록 필요 — Promaker `App.OnStartup` 에서 1회.
    /// 미등록 환경 (CodePages 미참조) 은 ArgumentException → UTF-16 LE 로 fallback.
    type TextEncodingDetect = {
        Encoding: Encoding
        ConfidenceHigh: bool
    }

    /// CP949 (Windows-949) lazy probe. CodePagesEncodingProvider 등록 안 된 환경이면 None.
    /// 명칭 "cp949" 는 .NET 미인식 — code page 번호 949 사용.
    /// `EncoderExceptionFallback` 인자는 GetEncoding signature 필수 (decode 흐름에 무영향, encoder fallback 정책).
    let private tryCp949 () : Encoding option =
        try Some (Encoding.GetEncoding(949, EncoderExceptionFallback(), DecoderExceptionFallback()))
        with :? System.ArgumentException -> None

    /// bytes 가 strict 모드의 enc 로 디코딩 가능한지. invalid sequence 발견 시 false.
    let private isStrictDecodable (enc: Encoding) (bytes: byte[]) : bool =
        try
            enc.GetCharCount(bytes, 0, bytes.Length) |> ignore
            true
        with :? DecoderFallbackException -> false

    /// bytes 의 텍스트 인코딩 추정. BOM 우선, 다음 strict UTF-8, 다음 strict CP949 (한국어 환경), 마지막 UTF-8 replacement.
    /// review 2차 M3: 모든 strict 시도 실패 시 fallback 을 UTF-16 LE → UTF-8 replacement 로 변경.
    /// ASCII 부분은 살아남고 비-UTF-8 부분만 U+FFFD 로 대체 → 사용자 측 mojibake 인지 가능 + LLM 입력 손상 최소화.
    let detectEncoding (bytes: byte[]) : TextEncodingDetect =
        if isNull bytes then
            { Encoding = Encoding.UTF8; ConfidenceHigh = false }
        elif bytes.Length >= 3 && bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy && bytes.[2] = 0xBFuy then
            { Encoding = Encoding.UTF8; ConfidenceHigh = true }
        elif bytes.Length >= 4 && bytes.[0] = 0xFFuy && bytes.[1] = 0xFEuy && bytes.[2] = 0x00uy && bytes.[3] = 0x00uy then
            { Encoding = Encoding.UTF32; ConfidenceHigh = true }
        elif bytes.Length >= 2 && bytes.[0] = 0xFFuy && bytes.[1] = 0xFEuy then
            { Encoding = Encoding.Unicode; ConfidenceHigh = true }
        elif bytes.Length >= 2 && bytes.[0] = 0xFEuy && bytes.[1] = 0xFFuy then
            { Encoding = Encoding.BigEndianUnicode; ConfidenceHigh = true }
        else
            let utf8Strict = Encoding.GetEncoding("utf-8", EncoderExceptionFallback(), DecoderExceptionFallback())
            if isStrictDecodable utf8Strict bytes then
                { Encoding = Encoding.UTF8; ConfidenceHigh = false }
            else
                match tryCp949 () with
                | Some cp949 when isStrictDecodable cp949 bytes ->
                    // 한국어 Windows 의 .txt / .log 흔한 케이스. confidence low — invalid UTF-8 가 우연히 CP949 로
                    // 통과한 random binary 도 가능. UI 측 안내 문구로 보강.
                    { Encoding = cp949; ConfidenceHigh = false }
                | _ ->
                    // 모든 strict 시도 실패 → fallback. UTF-8 replacement 로 ASCII 부분만 살리고 나머지는 U+FFFD.
                    Log.provider.Warn("AttachmentClassifier.detectEncoding — strict UTF-8/CP949 모두 실패, UTF-8 replacement fallback")
                    { Encoding = Encoding.UTF8; ConfidenceHigh = false }
