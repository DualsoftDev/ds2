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
            { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||] }
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

            {
                DocType = Docx
                PageOrSheetCnt = None
                Title = title
                Outline = outline.ToArray()
                Segments = segments.ToArray()
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
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||] }
            | :? OpenXmlPackageException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: OpenXml 패키지 일관성 위반 — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||] }
            | :? InvalidDataException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: 손상 압축 stream — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||] }
            | :? IOException as ex ->
                Log.lighthouse.Warn(sprintf "OoxmlExtractor: 파일 접근 실패 — path=%s, ex=%s" path ex.Message)
                { DocType = Docx; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||] }

        member _.Dispose () = ()
