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

/// OOXML extractor — DocumentFormat.OpenXml 3.5.1 기반 (todo-lighthouse-kb-index.md §4.3 / xlsx-pptx-images r2).
///
/// **Phase 1 활성**: docx — heading 깊이 (Heading1~Heading6) 를 outline 으로, paragraph + table 의 InnerText 를 segment 로.
///
/// **Phase 2 활성 (todo-lighthouse-kb-index-xlsx-pptx-images.md Task 0~2)**:
///   - Task 0 (본 turn): `Extract` 진정한 dispatch + `ExtractWithFailSafe` wrapper + `ImagePartToFormat` helper +
///     closure 4종 (`ExtractImagesAtRefLocator` / `CollectValidBlips` / `ExtractImagesFromBlips` /
///     `ExtractImagesFromOpenXmlPart`) static 승격. DOCX 동작 회귀 0. PPTX/XLSX 진입 직전 정리.
///   - Task 1: `ExtractPptx` 신설 — slide outline + paragraph break + speaker notes + 내부 이미지.
///   - Task 2: `ExtractXlsx` 신설 — sheet outline + sparse cell row + SST + 내부 이미지.
///
/// **Task 0 — Extract dispatch 신설 (r1 Critical-1 해소)**: 기존 `Extract` 가 `ExtractDocx` 직호출이라 향후
/// Pptx/Xlsx 활성 시 dispatch 분기 누락 위험. `Classifier.classifyForKb` 기반 분기 + `ExtractWithFailSafe`
/// (5 종 catch incl. `System.Xml.XmlException` for OpenXml lazy deferred parsing — r2 m4) wrapper 가
/// `DocType` 인자를 받아 정확한 빈 record 박제. 기존 4 catch arm 의 `DocType=Docx` hardcode 회귀.
///
/// **closure 4종 static 승격 (R3 M6 해소)**: `ExtractDocx` body 안 `images: ResizeArray<ExtractedImage>` 를
/// closure 로 capture 하던 helper 4종 → `images` + `path` 를 인자로 받는 static member. PPTX/XLSX 의
/// `ExtractPptx` / `ExtractXlsx` 에서 동일 helper 재사용. closure 와 동일하게 paragraph hot path 의 `Blips` cache
/// 도 같이 노출.
///
/// **`ImagePartToFormat` (Task 0)**: 기존 `OoxmlExtractor.fs:115-118` + `:174-177` 2회 중복 mapping 단일화.
/// lowercase 가정 (OpenXml SDK 규약). 외부에서 손으로 작성된 ContentTypes.xml 의 mixed case (`image/PNG` 등) 는
/// 자연 skip — Phase 2 차단 사유 0, case-insensitive 매칭은 backlog.
///
/// **DOCX 원래 박제 (변경 없음)**:
/// - Body 의 paragraph/table iter 안 `Descendants<Blip>()` → `Blip.Embed` (relationship id) → ImagePart 매핑
///   → ExtractedImage 박제. ContentType 화이트리스트 4 종 (PNG/JPEG/GIF/WEBP) 외 (EMF/WMF/BMP/TIFF 등) 자연 skip.
/// - RefLocator scheme (s6-r16 C4-Q2): docx 도 PdfExtractor scheme 통일 → `"p=%d"` (paragraph ordinal 1-based)
///   + `Ordinal = 1..N` (같은 paragraph 안 image 순번). ChunkId 매핑 활성화.
/// - orphan ImagePart skip — Drawing element 미참조 ImagePart 는 박제 안 함.
/// - image-only paragraph (s6-r21) — text=0 + Drawing 만 있는 paragraph 도 `isText || hasImg` 단일 분기로
///   image 박제 + paraOrdinal 증가. ChunkId 매핑은 None (segment 없는 paragraph).
/// - table cell scheme — `extractImagesFromTable` 분기. RefLocator scheme = `p=<tbl-paraOrd>.cell=<cellOrd>.p=<paraInCellOrd>`.
///   nested table 안 image 는 Warn 후 silent drift 차단 (backlog).
/// - header/footer Drawing (s6-r21) — `MainDocumentPart.HeaderParts` / `FooterParts` 의 RootElement 안 image 박제.
///   RefLocator scheme = `header=%d` / `footer=%d`. comments/footnotes/endnotes 는 backlog.
///
/// **fail-safe** (§3.16 / §6.5): 손상 ooxml 류는 log + 빈 결과. cancel 은 reraise.
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

    /// **Task 0 (Critical-1 + R3 M6 해소)** — ContentType (lowercase) → ImageFormat 화이트리스트 매핑.
    /// body / header / footer / pptx slide / xlsx drawing 의 image 박제가 본 helper 단일 진입점 사용.
    /// None = 화이트리스트 외 (EMF/WMF/BMP/TIFF / 대문자 mime) → 자연 skip (m6 primary 가드).
    static member private ImagePartToFormat (contentType: string) : ImageFormat option =
        match contentType with
        | "image/png"  -> Some Png
        | "image/jpeg" -> Some Jpeg
        | "image/gif"  -> Some Gif
        | "image/webp" -> Some Webp
        | _ -> None

    /// **Task 0 (R3 M6 closure 4종 승격, s6-r23 m2 통합)** — `Descendants<Blip>()` 1회 enumerate +
    /// relId → ImagePart 매핑 → byte[] → ExtractedImage 박제. body / header / footer / pptx slide /
    /// xlsx drawing 의 동일 패턴 단일 진입점. images + path 인자.
    /// `Ordinal` 은 호출 1회 안에서 1부터 (`Models.fs §108` 의 "같은 RefLocator 안 N번째" SSOT).
    /// per-image fail-safe (M2 결론) — decode exception → log + skip. 다른 image 진행 차단 안 함.
    static member private ExtractImagesAtRefLocator
        (container: OpenXmlElement)
        (resolveImagePart: string -> ImagePart option)
        (location: string)
        (refLocator: string)
        (images: ResizeArray<ExtractedImage>)
        (path: string) : unit =
        let mutable imgOrdInBlock = 1
        for blip in container.Descendants<Blip>() do
            if not (isNull blip.Embed) && blip.Embed.HasValue then
                let relId = blip.Embed.Value
                match resolveImagePart relId with
                | None -> ()   // 외부 image / hyperlink / 손상 relId — 자연 skip.
                | Some imgPart ->
                    try
                        match OoxmlExtractor.ImagePartToFormat imgPart.ContentType with
                        | None -> ()   // 화이트리스트 외 — m6 primary 가드.
                        | Some fmt ->
                            use stream = imgPart.GetStream()
                            use ms = new MemoryStream()
                            stream.CopyTo(ms)
                            let bytes = ms.ToArray()
                            if bytes.Length > 0 then
                                images.Add {
                                    Bytes = bytes
                                    Format = fmt
                                    Width = None
                                    Height = None
                                    RefLocator = refLocator
                                    Ordinal = imgOrdInBlock
                                }
                                imgOrdInBlock <- imgOrdInBlock + 1
                    with ex ->
                        Log.lighthouse.Warn(
                            sprintf "OoxmlExtractor: %s image 추출 실패 (ref=%s try-ord=%d relId=%s) — path=%s, ex=%s"
                                location refLocator imgOrdInBlock relId path ex.Message)

    /// **Task 0 (s6-r44 L-Maj-6 정정 박제)** — block 의 valid Blip 1회 enumerate cache.
    /// paragraph hot path 의 `hasInlineDrawing` + image extract 양쪽 박제하던 2회 deep enumerate 회피.
    /// caller (paragraph loop) 가 본 cache 박제 후 `ExtractImagesFromBlips` 호출.
    static member private CollectValidBlips (block: OpenXmlElement) : Blip ResizeArray =
        let arr = ResizeArray<Blip>()
        for blip in block.Descendants<Blip>() do
            if not (isNull blip.Embed) && blip.Embed.HasValue then
                arr.Add blip
        arr

    /// **Task 0 (R3 M6 closure 승격)** — `ExtractImagesAtRefLocator` 의 cached variant.
    /// caller 가 사전 enumerate 한 valid Blip ResizeArray 박제 (`CollectValidBlips` 결과 가정).
    /// ContentType 화이트리스트 + per-image fail-safe + Ordinal 박제 동일.
    static member private ExtractImagesFromBlips
        (blips: Blip ResizeArray)
        (resolveImagePart: string -> ImagePart option)
        (location: string)
        (refLocator: string)
        (images: ResizeArray<ExtractedImage>)
        (path: string) : unit =
        let mutable imgOrdInBlock = 1
        for blip in blips do
            let relId = blip.Embed.Value
            match resolveImagePart relId with
            | None -> ()
            | Some imgPart ->
                try
                    match OoxmlExtractor.ImagePartToFormat imgPart.ContentType with
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
                                Width = None
                                Height = None
                                RefLocator = refLocator
                                Ordinal = imgOrdInBlock
                            }
                            imgOrdInBlock <- imgOrdInBlock + 1
                with ex ->
                    Log.lighthouse.Warn(
                        sprintf "OoxmlExtractor: %s image 추출 실패 (ref=%s try-ord=%d relId=%s) — path=%s, ex=%s"
                            location refLocator imgOrdInBlock relId path ex.Message)

    /// **Task 0 (R3 M6 closure 승격)** — OpenXmlPart (HeaderPart / FooterPart 등) 의 RootElement enumerate.
    /// `part.Parts` 에서 ImagePart 만 골라 relId map 빌드 → `ExtractImagesAtRefLocator` 위임.
    static member private ExtractImagesFromOpenXmlPart
        (part: OpenXmlPart)
        (location: string)
        (refLocator: string)
        (images: ResizeArray<ExtractedImage>)
        (path: string) : unit =
        if not (isNull part.RootElement) then
            let imgMap =
                part.Parts
                |> Seq.choose (fun ip -> match ip.OpenXmlPart with :? ImagePart as p -> Some (part.GetIdOfPart(p), p) | _ -> None)
                |> Map.ofSeq
            let resolve relId = Map.tryFind relId imgMap
            OoxmlExtractor.ExtractImagesAtRefLocator part.RootElement resolve location refLocator images path

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

            // C4-Q2 (s6-r16): ImagePart 의 relationship id → ImagePart map.
            let imgPartByRelId =
                mainPart.ImageParts
                |> Seq.map (fun ip -> mainPart.GetIdOfPart(ip), ip)
                |> Map.ofSeq

            // body 측 resolver — mainPart 안 ImagePart map.
            let resolveBodyImagePart relId = Map.tryFind relId imgPartByRelId

            // paragraph 의 inline Drawing 박제 (paraOrd 기반 RefLocator `p=%d`). cached path.
            let extractImagesFromBlock (blips: Blip ResizeArray) (paraOrd: int) =
                OoxmlExtractor.ExtractImagesFromBlips blips resolveBodyImagePart "body" (sprintf "p=%d" paraOrd) images path

            // table cell 단위 매핑. RefLocator scheme = `p=<table-paraOrd>.cell=<cellOrd>.p=<paraInCellOrd>`.
            // nested table 안 image 는 silent drift 차단 (backlog).
            let extractImagesFromTable (tbl: Table) (paraOrd: int) =
                let mutable cellOrd = 1
                for row in tbl.Elements<TableRow>() do
                    for cell in row.Elements<TableCell>() do
                        let mutable paraInCell = 1
                        for cellPara in cell.Elements<Paragraph>() do
                            let refLoc = sprintf "p=%d.cell=%d.p=%d" paraOrd cellOrd paraInCell
                            OoxmlExtractor.ExtractImagesAtRefLocator cellPara resolveBodyImagePart "table-cell" refLoc images path
                            paraInCell <- paraInCell + 1
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
            for elem in body.ChildElements do
                ct.ThrowIfCancellationRequested()
                match elem with
                | :? Paragraph as p ->
                    let styleId = OoxmlExtractor.GetStyleId p
                    let text =
                        let raw = p.InnerText
                        if isNull raw then "" else raw.Trim()
                    let isText = text.Length > 0
                    // s6-r44: paragraph 의 Blip 1회 enumerate + cache (L-Maj-6 정정).
                    let pBlips = OoxmlExtractor.CollectValidBlips p
                    let hasImg = pBlips.Count > 0
                    if isText || hasImg then
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
                        if hasImg then
                            extractImagesFromBlock pBlips (paraOrdinal + 1)
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
                        // s6-r22 task 5: table cell 단위 매핑 — `extractImagesFromTable` 분기 분리.
                        extractImagesFromTable tbl (paraOrdinal + 1)
                        paraOrdinal <- paraOrdinal + 1
                | _ -> ()

            // s6-r21: header / footer Drawing 커버. RefLocator scheme = `header=%d` / `footer=%d`.
            // s6-r23 m2 / s6-r24 m1: log prefix `"header"` / `"footer"` 박제.
            let mutable headerOrd = 1
            for hp in mainPart.HeaderParts do
                OoxmlExtractor.ExtractImagesFromOpenXmlPart hp "header" (sprintf "header=%d" headerOrd) images path
                headerOrd <- headerOrd + 1
            let mutable footerOrd = 1
            for fp in mainPart.FooterParts do
                OoxmlExtractor.ExtractImagesFromOpenXmlPart fp "footer" (sprintf "footer=%d" footerOrd) images path
                footerOrd <- footerOrd + 1

            {
                DocType = Docx
                PageOrSheetCnt = None
                Title = title
                Outline = outline.ToArray()
                Segments = segments.ToArray()
                Images = images.ToArray()
            }

    /// **Task 0 (Critical-1, r2 m4 XmlException 추가)** — 외부 환경 fail-safe (§6.5) 5 종 통합 wrapper.
    ///   - FileFormatException: zip header 등 OOXML 패키지 구조 깨짐
    ///   - OpenXmlPackageException: 패키지 일관성 위반
    ///   - InvalidDataException: 손상된 압축 stream
    ///   - IOException: 파일 접근 실패 (lock / 권한)
    ///   - System.Xml.XmlException: OpenXml lazy deferred parsing 시점 발생 가능 (r2 m4)
    /// 빈 record 의 DocType 은 `docType` 인자로 정확 박제 (기존 4 arm 의 `DocType=Docx` hardcode 회귀).
    /// 그 외 (NullReferenceException 등 코드 버그) 는 reraise — 디버깅 가시성 보존.
    static member private ExtractWithFailSafe
        (docType: FileKind)
        (path: string)
        (action: unit -> ExtractedDocument) : ExtractedDocument =
        let emptyResult () =
            { DocType = docType; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
        try action ()
        with
        | :? FileFormatException as ex ->
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A 패키지 손상 — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()
        | :? OpenXmlPackageException as ex ->
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A OpenXml 패키지 일관성 위반 — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()
        | :? InvalidDataException as ex ->
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A 손상 압축 stream — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()
        | :? IOException as ex ->
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A 파일 접근 실패 — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()
        | :? System.Xml.XmlException as ex ->
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A XML 파싱 실패 (lazy deferred) — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()

    interface IExtractor with
        member _.Supports kind =
            match kind with
            | Docx -> true
            | _ -> false  // Pptx / Xlsx Phase 2 task — Task 1/2 진입 시 활성

        member _.Extract (path, ct) =
            ct.ThrowIfCancellationRequested()
            // Task 0 Critical-1 — 진정한 dispatch. 현 시점 Supports=Docx only 라 routeExtractor 가 본 extractor 로
            // Pptx/Xlsx 라우팅 안 함 → `| _ ->` 분기는 미진입 상태. Task 1/2 에서 Supports 분기 확대 + ExtractPptx/
            // ExtractXlsx 신설하면 자연스럽게 활성.
            let kind = Classifier.classifyForKb path
            OoxmlExtractor.ExtractWithFailSafe kind path (fun () ->
                match kind with
                | Docx -> OoxmlExtractor.ExtractDocx path ct
                | _ ->
                    failwith (sprintf "OoxmlExtractor.Extract: Supports invariant 위반 — kind=%A path=%s" kind path))

        member _.Dispose () = ()
