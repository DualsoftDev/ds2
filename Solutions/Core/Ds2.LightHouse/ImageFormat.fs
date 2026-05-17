namespace Ds2.LightHouse

/// 첨부 가능한 이미지 포맷. 화이트리스트 기반 (4 case exhaustive).
///
/// todo-lighthouse-kb-index.md §3.0/§3.4 — chat image drop (Ds2.LlmAgent.AttachmentClassifier) 와
/// KB ingest (Ds2.LightHouse.classifyForKb, Phase 2) 두 경로가 공통으로 사용하는 base enum.
/// 신규 case 추가 시 `Ds2.LlmAgent.Attachment.mimeOf` / `extOf` / `AttachmentClassifier.imageFormatOf`
/// 등 매핑 함수가 컴파일러로 강제됨 (`AttachmentClassifierDriftTests` 의 reflection case count 도).
type ImageFormat =
    | Png
    | Jpeg
    | Gif
    | Webp
