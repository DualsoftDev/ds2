namespace Ds2.LightHouse

/// 첨부 가능한 이미지 포맷. 화이트리스트 기반 (4 case exhaustive).
///
/// todo-lighthouse-kb-index.md §3.0/§3.4 — chat image drop (Ds2.LlmAgent.AttachmentClassifier) 와
/// KB ingest (Ds2.LightHouse.classifyForKb, Phase 2) 두 경로가 공통으로 사용하는 base enum.
///
/// 신규 case 추가 시 강제 갱신 대상:
///   ① `Ds2.LlmAgent.Attachment.mimeOf` / `extOf` — exhaustive match 이므로 **컴파일러 강제** (FS0025).
///   ② `Ds2.LlmAgent.AttachmentClassifier.imageFormatOf` — `| _ -> None` wildcard 가 있어 *컴파일 강제 안 됨*.
///     대신 `AttachmentClassifierDriftTests` 의 reflection 기반 `FSharpType.GetUnionCases` count 가 런타임 보호.
///   ③ `Provider Capabilities` 의 `[Png; Jpeg; Gif; Webp]` literal — drift test 안내 메시지가 강제 알림.
/// 즉 새 case 추가 시 (i) 빌드 실패하는 부분 고치고, (ii) `AttachmentClassifierDriftTests.fs` 의 cases list +
/// classifier 의 imageFormatOf 도 직접 갱신해야 한다 (review M3 정정 박제).
type ImageFormat =
    | Png
    | Jpeg
    | Gif
    | Webp
