namespace Ds2.LightHouse

open System
open System.IO

/// KB 색인 라우팅 분류기 (todo-lighthouse-kb-index.md §3.3 / §3.11).
///
/// chat 측 `Ds2.LlmAgent.AttachmentClassifier.classify` 와 **독립** — 출력 DU 도 다름 (§3.0 / §3.11).
/// 입력만 path 로 공통, 의사결정·확장자 set·rejected list 모두 별개.
///
/// 보안 정책 (§6 m15): `.env` 등 비밀정보 확장자는 색인 거부 (`Unsupported` 반환).
/// chat 측 정책 19 의 `rejectedExtensions` 와 의미 일치하나 set 은 독립 — chat/KB 경로 분리 SSOT.
[<RequireQualifiedAccess>]
module Classifier =

    /// 확장자 → FileKind 매핑 (소문자 + leading dot).
    ///
    /// Phase 1 활성: Pdf / Docx / Text / Markdown. Phase 2 활성 case 추가 시 본 map 만 갱신.
    let supportedExtensions : Map<string, FileKind> =
        Map.ofList [
            ".pdf",       Pdf
            ".docx",      Docx
            ".pptx",      Pptx
            ".xlsx",      Xlsx
            ".txt",       Text
            ".log",       Text
            ".csv",       Text
            ".tsv",       Text
            ".json",      Text
            ".xml",       Text
            ".yaml",      Text
            ".yml",       Text
            ".md",        Markdown
            ".markdown",  Markdown
        ]

    /// 명시 색인 거부 확장자 — 비밀정보 / 실행파일 / 미디어 / 압축 (§6 m15).
    ///
    /// chat 측 `Ds2.LlmAgent.AttachmentClassifier.rejectedExtensions` 와 의미 일치하나 set 은 독립.
    /// `.env` 차단 사유: KB 가 LLM provider 로 송신될 수 있어 사용자 비밀 누출 방지 (consent 다이얼로그 정합).
    let rejectedExtensions : Set<string> =
        Set.ofList [
            ".env"
            ".exe"; ".dll"; ".msi"; ".bin"; ".so"; ".dylib"; ".com"; ".scr"
            ".zip"; ".7z"; ".rar"; ".tar"; ".gz"; ".bz2"; ".xz"; ".tgz"
            ".mp4"; ".mov"; ".avi"; ".mkv"; ".webm"; ".flv"; ".wmv"
            ".mp3"; ".wav"; ".flac"; ".ogg"; ".m4a"; ".aac"
            ".bmp"; ".tiff"; ".tif"; ".svg"; ".ico"; ".heic"; ".heif"
            ".png"; ".jpg"; ".jpeg"; ".gif"; ".webp"
        ]

    let private extOf (path: string) : string =
        Path.GetExtension(path).ToLowerInvariant()

    /// 파일 path → KB 색인 분류.
    ///
    /// 우선순위: ① rejected → `Unsupported ext` (보안 차단 의미)
    ///          ② supported map → 해당 FileKind
    ///          ③ 그 외 → `Unsupported ext` (단순 미지원)
    ///
    /// rejected 와 단순 unsupported 는 출력이 동일 (Unsupported) — Indexer 가 `rejectedExtensions` 를
    /// 별도 검사하여 reject 사유를 log 에 박는다 (사용자 안내용).
    let classifyForKb (path: string) : FileKind =
        if String.IsNullOrWhiteSpace path then Unsupported ""
        else
            let ext = extOf path
            if Set.contains ext rejectedExtensions then Unsupported ext
            else
                match Map.tryFind ext supportedExtensions with
                | Some kind -> kind
                | None -> Unsupported ext
