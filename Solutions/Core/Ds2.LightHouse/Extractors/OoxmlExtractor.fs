namespace Ds2.LightHouse.Extractors

open System
open System.IO
open System.Threading
open Ds2.LightHouse
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

/// OOXML extractor — DocumentFormat.OpenXml 3.5.1 기반 (todo-lighthouse-kb-index.md §4.3).
///
/// Phase 1 활성: docx 만. heading 깊이 (Heading1~Heading6) 를 outline 으로, paragraph + table 의 InnerText 를 segment 로.
/// pptx / xlsx 는 Phase 2 — `Supports` false 반환하여 Indexer routing 에서 자연스럽게 누락 → 다른 extractor 로 fallback.
///
/// **Phase 2 task C3 (s6-r14)**: `MainDocumentPart.ImageParts` 순회 + `ContentType` 화이트리스트 매칭
/// (image/png / image/jpeg / image/gif / image/webp). EMF/WMF/x-emf/x-wmf/BMP/TIFF 등 vector/비대상 raster 는
/// 자연 skip (m6 primary 가드). per-image try/catch fail-safe (M2 결론).
///
/// ContentType **lowercase 가정** (OpenXml SDK 규약 — Image*ContentType 상수가 항상 lowercase). 외부에서 손으로
/// 작성된 ContentTypes.xml 의 mixed case (`image/PNG` 등) 는 매칭 안 됨 → 자연 skip 처리. 본 phase 차단 사유 0,
/// case-insensitive 매칭은 backlog (필요 시 별 turn).
///
/// RefLocator scheme (s6-r14 M-r13-1 옵션 B 결정): docx 는 page 개념 없음 →
/// `RefLocator = "body"` (전체 docx grouping) + `Ordinal = 1..N` (전체 docx 안 image 순번).
/// paragraph 매핑 (image 가 inline 박힌 paragraph 의 RefLocator 통일) 은 C4 (ChunkId linking) 진입 시 강화 —
/// 본 phase 의 단순화 trade-off (옵션 B 채택 시 명시 의무).
///
/// **MainDocumentPart 한정** — header/footer/comments/footnotes/endnotes 의 ImageParts 는 미커버 (각 part 가
/// 독립 ImageParts 보유). 일반 산업 docx 에서 image 는 본문 (Body) 에 집중되어 본 phase 의 단순화 정합. 별 turn
/// (C4 후 또는 별 task) 의무 박제.
///
/// fail-safe (§3.16 / §6.5): 손상 docx (FileFormatException / OpenXmlPackageException) 는 log + 빈 결과. cancel 은 reraise.
type OoxmlExtractor() =

    /// Word 의 paragraph style id (예: "Heading1", "Heading2", ..., "Heading6").
    /// `Heading` prefix 만 대소문자 무시 일치 — 한국어 문서에서도 ParagraphStyleId 는 영문 keyword.
    static member private IsHeadingStyle (styleId: string) : bool =
        not (String.IsNullOrEmpty styleId)
        && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)

    static member private GetStyleId (p: Paragraph) : string =
        let pp = p.ParagraphProperties
        if isNull pp then "" else
            let pid = pp.ParagraphStyleId
            if isNull pid || isNull pid.Val then ""
            else pid.Val.Value

    static member private ExtractDocx (path: string) (ct: CancellationToken) : ExtractedDocument =
        ct.ThrowIfCancellationRequested()
        use doc = WordprocessingDocument.Open(path, false)
        let mainPart = doc.MainDocumentPart
        if isNull mainPart || isNull mainPart.Document || isNull mainPart.Document.Body then
            { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
        else
            let body = mainPart.Document.Body
            // Title 추출은 Phase 1 skip — OpenXml 3.x 의 `PackageProperties` 가 experimental (FS0044/FS0057).
            // Models.fs 의 ExtractedDocument.Title 주석대로 Indexer 가 filename fallback. Phase 2 진입 시 새 API 검토.
            let title = None

            let outline = ResizeArray<ExtractedOutlineNode>()
            let segments = ResizeArray<ExtractedSegment>()
            let mutable paraOrdinal = 0

            for elem in body.ChildElements do
                ct.ThrowIfCancellationRequested()
                match elem with
                | :? Paragraph as p ->
                    let styleId = OoxmlExtractor.GetStyleId p
                    let text =
                        let raw = p.InnerText
                        if isNull raw then "" else raw.Trim()
                    if text.Length > 0 then
                        // heading 우선 처리 — outline 등록 + 이후 segment 들이 본 outline 에 link.
                        if OoxmlExtractor.IsHeadingStyle styleId then
                            outline.Add {
                                ParentIndex = None
                                Ordinal = outline.Count
                                NodeType = OutlineNodeType.Heading
                                Label = text
                                RefLocator = sprintf "p=%d" (paraOrdinal + 1)
                            }
                        let outlineIdx = if outline.Count > 0 then Some (outline.Count - 1) else None
                        segments.Add {
                            OutlineIndex = outlineIdx
                            RefLocator = sprintf "p=%d" (paraOrdinal + 1)
                            Text = text
                        }
                        paraOrdinal <- paraOrdinal + 1
                | :? Table as tbl ->
                    let text =
                        let raw = tbl.InnerText
                        if isNull raw then "" else raw.Trim()
                    if text.Length > 0 then
                        let outlineIdx = if outline.Count > 0 then Some (outline.Count - 1) else None
                        segments.Add {
                            OutlineIndex = outlineIdx
                            RefLocator = sprintf "p=%d" (paraOrdinal + 1)
                            Text = text
                        }
                        paraOrdinal <- paraOrdinal + 1
                | _ -> ()

            // Phase 2 task C3 (s6-r14): docx ImageParts 박제.
            // MainDocumentPart.ImageParts = IEnumerable<ImagePart>. ContentType 화이트리스트 매칭 →
            // 화이트리스트 외 (EMF/WMF/x-emf/x-wmf 등 vector) 는 자연 skip (m6 primary 가드).
            // 본 phase 는 RefLocator = "body" (옵션 B, paragraph 매핑은 C4 의무).
            let images = ResizeArray<ExtractedImage>()
            let mutable imgOrd = 1
            for imgPart in mainPart.ImageParts do
                try
                    let fmtOpt =
                        match imgPart.ContentType with
                        | "image/png"  -> Some Png
                        | "image/jpeg" -> Some Jpeg
                        | "image/gif"  -> Some Gif
                        | "image/webp" -> Some Webp
                        | _ -> None
                    match fmtOpt with
                    | None -> ()
                    | Some fmt ->
                        use stream = imgPart.GetStream()
                        use ms = new MemoryStream()
                        stream.CopyTo(ms)
                        let bytes = ms.ToArray()
                        if bytes.Length > 0 then
                            images.Add {
                                Bytes = bytes
                                Format = fmt
                                // OpenXml ImagePart 는 pixel dim 노출 안 함 — header parse 별 task.
                                Width = None
                                Height = None
                                RefLocator = "body"
                                Ordinal = imgOrd
                            }
                            imgOrd <- imgOrd + 1
                with ex ->
                    // M2 per-image fail-safe — decode exception → log + skip, ordinal 증가 안 함.
                    Log.lighthouse.Warn(
                        sprintf "OoxmlExtractor: ImagePart 추출 실패 (try-ord=%d) — path=%s, ex=%s"
                            imgOrd path ex.Message)

            {
                DocType = Docx
                PageOrSheetCnt = None
                Title = title
                Outline = outline.ToArray()
                Segments = segments.ToArray()
                Images = images.ToArray()
            }

    interface IExtractor with
        member _.Supports kind =
            match kind with
            | Docx -> true
            | _ -> false  // Pptx / Xlsx 는 Phase 2 — routing 에서 자연 누락

        member _.Extract (path, ct) =
            ct.ThrowIfCancellationRequested()
            // 외부 환경 fail-safe (§6.5, review M3): 손상 docx 류만 한정 catch.
            //   - FileFormatException: zip header 등 OOXML 패키지 구조 깨짐
            //   - OpenXmlPackageException: 패키지 일관성 위반
            //   - InvalidDataException: 손상된 압축 stream
            //   - IOException: 파일 접근 실패 (lock / 권한)
            // 그 외 (NullReferenceException 등 코드 버그) 는 fail-fast — 디버깅 가시성 보존.
            try
                OoxmlExtractor.ExtractDocx path ct
            with
            | :? FileFormatException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: docx 패키지 손상 — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
            | :? OpenXmlPackageException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: OpenXml 패키지 일관성 위반 — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
            | :? InvalidDataException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: 손상 압축 stream — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
            | :? IOException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: 파일 접근 실패 — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }

        member _.Dispose () = ()
