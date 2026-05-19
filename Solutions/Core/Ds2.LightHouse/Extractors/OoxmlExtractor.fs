namespace Ds2.LightHouse.Extractors

open System
open System.IO
open System.Threading
open Ds2.LightHouse
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

/// Wordprocessing.ParagraphProperties 가 Drawing.ParagraphProperties 와 동명 → fully qualified 로만 사용.
type private Blip = DocumentFormat.OpenXml.Drawing.Blip

/// OOXML extractor — DocumentFormat.OpenXml 3.5.1 기반 (todo-lighthouse-kb-index.md §4.3).
///
/// Phase 1 활성: docx 만. heading 깊이 (Heading1~Heading6) 를 outline 으로, paragraph + table 의 InnerText 를 segment 로.
/// pptx / xlsx 는 Phase 2 — `Supports` false 반환하여 Indexer routing 에서 자연스럽게 누락 → 다른 extractor 로 fallback.
///
/// **Phase 2 task C3 (s6-r14) + C4-Q2 강화 (s6-r16)**: `Body` 의 paragraph/table iter 안에서 `Descendants<Blip>()`
/// 분석 → `Blip.Embed` (relationship id) → ImagePart 매핑 → ExtractedImage 박제. `ContentType` 화이트리스트
/// (image/png / image/jpeg / image/gif / image/webp) 외 (EMF/WMF/x-emf/x-wmf/BMP/TIFF 등) 는 자연 skip
/// (m6 primary 가드). per-image try/catch fail-safe (M2 결론).
///
/// ContentType **lowercase 가정** (OpenXml SDK 규약 — Image*ContentType 상수가 항상 lowercase). 외부에서 손으로
/// 작성된 ContentTypes.xml 의 mixed case (`image/PNG` 등) 는 매칭 안 됨 → 자연 skip 처리. 본 phase 차단 사유 0,
/// case-insensitive 매칭은 backlog (필요 시 별 turn).
///
/// RefLocator scheme (s6-r16 C4-Q2 옵션 B 강화): docx 도 PdfExtractor scheme 통일 → `"p=%d"` (paragraph ordinal
/// 1-based, segment 와 동일 scheme) + `Ordinal = 1..N` (같은 paragraph 안 image 순번). ChunkId 매핑 활성화
/// (refToChunkId map 의 `"p=%d"` key 와 정합) — C4 의 image-chunk linking 완비.
///
/// **orphan ImagePart skip** — Drawing element 가 미참조하는 ImagePart (Word 가 unused 로 남긴 자취) 는 박제 안 함.
/// `Body.Descendants<Blip>()` 만 enumerate 하므로 자연 skip — UI 비표시 image 의 KB 색인 가치 낮음 정합.
///
/// **image-only paragraph** (s6-r21, s6-r22 mn1 통합) — paragraph 가 text=0 + Drawing 만 있는 경우
/// `isText || hasImg` 단일 분기에서 segment 는 미박제하나 paraOrdinal 증가 + image 박제. 산업 매뉴얼 docx 의
/// caption 직전 image-only paragraph 시나리오 정합. ChunkId 매핑은 None (segment 없는 paragraph 는 chunks 도 없음).
///
/// **table cell scheme 확장** (s6-r22 task 5, s6-r16 backlog 해소) — table 의 Drawing 추출은
/// `extractImagesFromTable` 분기로 분리. RefLocator scheme = `p=&lt;tableParaOrd&gt;.cell=&lt;cellOrd&gt;.p=&lt;paraInCellOrd&gt;`
/// (모두 1-based). cellOrd = `tbl.Descendants&lt;TableCell&gt;()` row-major. paraInCellOrd = 각 cell 안 Paragraph 순번.
/// nested table (cell 안 table) 의 cell 좌표 평면화 / table 직속 Drawing (OOXML 규약상 빈도 0) 은 미지원 backlog.
///
/// **MainDocumentPart Body 한정** — header/footer/comments/footnotes/endnotes 안의 Drawing 은 미커버 (Body iter
/// 가 본문 한정). 일반 산업 docx 에서 image 는 본문에 집중되어 본 phase 의 단순화 정합. 별 turn (또는 별 task) 의무 박제.
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
            let images = ResizeArray<ExtractedImage>()
            let mutable paraOrdinal = 0

            // C4-Q2 (s6-r16): ImagePart 의 relationship id → ImagePart map. body.Descendants<Blip>() 의 Embed
            // (relId) 와 매칭. orphan ImagePart (Drawing 미참조) 는 본 map 에 등록되나 enumerate 안 됨 → 자연 skip.
            let imgPartByRelId =
                mainPart.ImageParts
                |> Seq.map (fun ip -> mainPart.GetIdOfPart(ip), ip)
                |> Map.ofSeq

            // **s6-r23 m2 helper 통합 (s6-r22 자가 검열 m2 적용)** — body / header / footer 의 동일 패턴
            // (Blip enumerate → relId → ImagePart 매핑 → ContentType 화이트리스트 → byte[] 추출 → ExtractedImage 박제)
            // 을 단일 `extractImagesAtRefLocator` 로 통합.
            //
            // signature: `(container, resolveImagePart, refLocator)`.
            //  - `container` = enumerate 대상 OpenXmlElement (paragraph / table cell / part RootElement).
            //  - `resolveImagePart` = relId → ImagePart option (body 는 `imgPartByRelId` map, header/footer 는 별 map).
            //  - `refLocator` = caller 가 결정한 RefLocator (`p=%d` / `p=%d.cell=%d.p=%d` / `header=%d` / `footer=%d`).
            //
            // Ordinal 은 caller 호출 1회 안에서 1부터 (`Models.fs §108` 의 "같은 RefLocator 안 N번째" SSOT).
            // M2 per-image fail-safe — decode exception → log + skip. 다른 image 진행 차단 안 함.
            let extractImagesAtRefLocator
                (container: OpenXmlElement)
                (resolveImagePart: string -> ImagePart option)
                (refLocator: string) =
                let mutable imgOrdInBlock = 1
                for blip in container.Descendants<Blip>() do
                    if not (isNull blip.Embed) && blip.Embed.HasValue then
                        let relId = blip.Embed.Value
                        match resolveImagePart relId with
                        | None -> ()   // 외부 image / hyperlink / 손상 relId — 자연 skip.
                        | Some imgPart ->
                            try
                                let fmtOpt =
                                    match imgPart.ContentType with
                                    | "image/png"  -> Some Png
                                    | "image/jpeg" -> Some Jpeg
                                    | "image/gif"  -> Some Gif
                                    | "image/webp" -> Some Webp
                                    | _ -> None
                                match fmtOpt with
                                | None -> ()   // 화이트리스트 외 (EMF/WMF/BMP 등) — m6 primary 가드.
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
                                            RefLocator = refLocator
                                            Ordinal = imgOrdInBlock
                                        }
                                        imgOrdInBlock <- imgOrdInBlock + 1
                            with ex ->
                                // M2 per-image fail-safe — decode exception → log + skip.
                                Log.lighthouse.Warn(
                                    sprintf "OoxmlExtractor: Drawing image 추출 실패 (ref=%s try-ord=%d relId=%s) — path=%s, ex=%s"
                                        refLocator imgOrdInBlock relId path ex.Message)

            // body 측 resolver — mainPart 안 ImagePart map.
            let resolveBodyImagePart relId = Map.tryFind relId imgPartByRelId

            // helper: Paragraph 의 inline Drawing 박제 (paraOrd 기반 RefLocator `p=%d`).
            let extractImagesFromBlock (block: OpenXmlElement) (paraOrd: int) =
                extractImagesAtRefLocator block resolveBodyImagePart (sprintf "p=%d" paraOrd)

            // **s6-r22 task 5 (s6-r16 backlog 해소)** — table cell 단위 매핑.
            // RefLocator scheme = `p=<table-paraOrd>.cell=<cellOrd>.p=<paraInCellOrd>` (모두 1-based).
            // cellOrd = outer table 의 row → cell 순서 (row-major, direct children 만). paraInCellOrd = 각 cell 안
            // 직속 Paragraph 순번.
            //
            // **nested table 명시 skip (s6-r22 자가 검열 C1 정합)** — `tbl.Descendants<TableCell>()` 대신
            // `tbl.Elements<TableRow>() → r.Elements<TableCell>()` 로 outer cell 한정 enumerate. nested table
            // (cell 안 table) 의 inner cell 은 본 scheme 미지원 → 자연 skip + Warn log. silent RefLocator drift 차단.
            // nested table 안 image 누락은 산업 docx 빈도 매우 낮음 — 별 turn backlog.
            //
            // table 직속 Drawing (cell 외부) 은 OOXML 규약상 빈도 0 (Drawing 은 paragraph 안 인라인 강제) — 별 분기 미박제.
            let extractImagesFromTable (tbl: Table) (paraOrd: int) =
                let mutable cellOrd = 1
                for row in tbl.Elements<TableRow>() do
                    for cell in row.Elements<TableCell>() do
                        let mutable paraInCell = 1
                        for cellPara in cell.Elements<Paragraph>() do
                            let refLoc = sprintf "p=%d.cell=%d.p=%d" paraOrd cellOrd paraInCell
                            extractImagesAtRefLocator cellPara resolveBodyImagePart refLoc
                            paraInCell <- paraInCell + 1
                        // nested table 안 image silent drift 차단 — outer cell 의 직속 Table 자식 enumerate 후 Warn.
                        for nestedTbl in cell.Elements<Table>() do
                            let nestedImgCount =
                                nestedTbl.Descendants<Blip>()
                                |> Seq.filter (fun b -> not (isNull b.Embed) && b.Embed.HasValue)
                                |> Seq.length
                            if nestedImgCount > 0 then
                                Log.lighthouse.Warn(
                                    sprintf "OoxmlExtractor: nested table 안 image %d 장 skip (RefLocator scheme 미지원, backlog) — path=%s, outer p=%d.cell=%d"
                                        nestedImgCount path paraOrd cellOrd)
                        cellOrd <- cellOrd + 1

            // helper: block 안 Drawing.Blip 존재 여부 — image-only 분리 분기 검사.
            let hasInlineDrawing (block: OpenXmlElement) : bool =
                block.Descendants<Blip>() |> Seq.exists (fun b -> not (isNull b.Embed) && b.Embed.HasValue)

            // s6-r22 mn1: paragraph/table 분기 통합 — `isText || hasImg` 단일 분기.
            // text 가 0 이어도 inline Drawing 있으면 image 박제 + paraOrdinal 증가 (s6-r21 image-only paragraph).
            // segment 는 isText 일 때만 박제 (text=0 chunk 는 의미 없음).
            for elem in body.ChildElements do
                ct.ThrowIfCancellationRequested()
                match elem with
                | :? Paragraph as p ->
                    let styleId = OoxmlExtractor.GetStyleId p
                    let text =
                        let raw = p.InnerText
                        if isNull raw then "" else raw.Trim()
                    let isText = text.Length > 0
                    let hasImg = hasInlineDrawing p
                    if isText || hasImg then
                        // heading 처리는 text 있을 때만 의미 — image-only paragraph 는 outline 비등록.
                        if isText && OoxmlExtractor.IsHeadingStyle styleId then
                            outline.Add {
                                ParentIndex = None
                                Ordinal = outline.Count
                                NodeType = OutlineNodeType.Heading
                                Label = text
                                RefLocator = sprintf "p=%d" (paraOrdinal + 1)
                            }
                        if isText then
                            let outlineIdx = if outline.Count > 0 then Some (outline.Count - 1) else None
                            segments.Add {
                                OutlineIndex = outlineIdx
                                RefLocator = sprintf "p=%d" (paraOrdinal + 1)
                                Text = text
                            }
                        // C4-Q2: 같은 paragraph 안의 inline Drawing → 같은 RefLocator 의 image 박제.
                        // image-only paragraph (s6-r21) 도 동일 분기 — ChunkId 매핑은 None (segment 없음).
                        extractImagesFromBlock p (paraOrdinal + 1)
                        paraOrdinal <- paraOrdinal + 1
                | :? Table as tbl ->
                    let text =
                        let raw = tbl.InnerText
                        if isNull raw then "" else raw.Trim()
                    let isText = text.Length > 0
                    let hasImg = hasInlineDrawing tbl
                    if isText || hasImg then
                        if isText then
                            let outlineIdx = if outline.Count > 0 then Some (outline.Count - 1) else None
                            segments.Add {
                                OutlineIndex = outlineIdx
                                RefLocator = sprintf "p=%d" (paraOrdinal + 1)
                                Text = text
                            }
                        // s6-r22 task 5: table cell 단위 매핑 — `extractImagesFromTable` 분기 분리. cell 안 image
                        // 는 `p=%d.cell=%d.p=%d` scheme. table 직속 (cell 외) image 는 기존 `p=%d` scheme 유지.
                        extractImagesFromTable tbl (paraOrdinal + 1)
                        paraOrdinal <- paraOrdinal + 1
                | _ -> ()

            // **s6-r21 (s6-r16 backlog 해소)** — header / footer Drawing 커버.
            // 일반 산업 docx 의 로고/머리말 image. RefLocator scheme = `header=%d` / `footer=%d` (1-based,
            // mainPart.HeaderParts / FooterParts 순서). chunks 와의 매칭은 미보장 (header text 가 chunk 화 안 됨)
            // — image-only 시나리오와 동일 정합 (ChunkId None).
            //
            // **comments / footnotes / endnotes Drawing**: 산업 docx 에서 빈도 매우 낮고 ContentType 분포도
            // 다양 (메타 image 류) — 본 phase scope 외, 별 turn 박제. (HeaderParts/FooterParts 만 진입.)
            // **s6-r23 m2** — header/footer 의 ImagePart resolver 만 다르고 enumerate/박제 로직은 body 와 동일.
            // `extractImagesAtRefLocator` 의 resolver argument 로 통합.
            let extractImagesFromOpenXmlPart (part: OpenXmlPart) (refLocator: string) =
                if not (isNull part.RootElement) then
                    let imgMap =
                        part.Parts
                        |> Seq.choose (fun ip -> match ip.OpenXmlPart with :? ImagePart as p -> Some (part.GetIdOfPart(p), p) | _ -> None)
                        |> Map.ofSeq
                    let resolve relId = Map.tryFind relId imgMap
                    extractImagesAtRefLocator part.RootElement resolve refLocator

            let mutable headerOrd = 1
            for hp in mainPart.HeaderParts do
                extractImagesFromOpenXmlPart hp (sprintf "header=%d" headerOrd)
                headerOrd <- headerOrd + 1
            let mutable footerOrd = 1
            for fp in mainPart.FooterParts do
                extractImagesFromOpenXmlPart fp (sprintf "footer=%d" footerOrd)
                footerOrd <- footerOrd + 1

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
