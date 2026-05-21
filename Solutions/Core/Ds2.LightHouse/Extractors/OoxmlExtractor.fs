namespace Ds2.LightHouse.Extractors

open System
open System.IO
open System.Text
open System.Threading
open Ds2.LightHouse
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

/// Wordprocessing.ParagraphProperties 가 Drawing.ParagraphProperties 와 동명 → fully qualified 로만 사용.
type private Blip = DocumentFormat.OpenXml.Drawing.Blip

/// **Task 1 (r2 Minor 5)** — DrawingML 의 `Paragraph` / `Text` 가 Wordprocessing 측과 동명 → file top alias.
/// PPTX 의 SlideText / NotesSlide 에서 paragraph break 보존 enumerate 시 사용.
type private DrawingParagraph = DocumentFormat.OpenXml.Drawing.Paragraph
type private DrawingText = DocumentFormat.OpenXml.Drawing.Text

/// **Backlog 3 (review M7)** — XLSX namespace alias. `ExtractXlsx` / `IngestXlsxSheet` 가 매번 fully
/// qualified 박제하던 verbosity 감소. Wordprocessing 측 동명 type 과 충돌 회피 위해 `Xl` prefix.
type private XlCell = DocumentFormat.OpenXml.Spreadsheet.Cell
type private XlRow = DocumentFormat.OpenXml.Spreadsheet.Row
type private XlSheet = DocumentFormat.OpenXml.Spreadsheet.Sheet
type private XlCellValues = DocumentFormat.OpenXml.Spreadsheet.CellValues
type private XlSheetStateValues = DocumentFormat.OpenXml.Spreadsheet.SheetStateValues
type private XlSharedStringItem = DocumentFormat.OpenXml.Spreadsheet.SharedStringItem
type private XlPhoneticRun = DocumentFormat.OpenXml.Spreadsheet.PhoneticRun
type private XlColumns = DocumentFormat.OpenXml.Spreadsheet.Columns
type private XlColumn = DocumentFormat.OpenXml.Spreadsheet.Column
/// Spreadsheet.Text 는 Wordprocessing.Text 와 동명 → alias 로 명시 한정.
type private XlText = DocumentFormat.OpenXml.Spreadsheet.Text

/// **Task 2-extra (Gantt schedule 시트 힌트)** — 산업 .xlsx 의 작업 일정표 (Gantt) 시트 검출용 8 role.
/// header 매칭 (`XlsxSheetRoles.tryMatch`) 시 column index ↔ role 박제 후 segment 머리에 동적 preamble prepend.
/// LLM 이 row tab-join 데이터를 "이 컬럼은 START(초)" 등으로 정확 해석.
type private XlsxSheetRole =
    | RNo
    | RSym
    | RTask
    | RStart
    | RDuration
    | RCumulative
    | RScore
    | RGrade

/// XlsxSheetRole synonym set (한글/영문/약어). `normalizeHeader` 결과 (공백/괄호 strip + 소문자 + 한자→한글) 와 정확 매칭.
/// 신규 alias 는 본 map 만 갱신. 사용자 정의 alias 의 config 주입은 Phase 4 backlog.
module private XlsxSheetRoles =
    let synonyms : Map<XlsxSheetRole, Set<string>> =
        Map.ofList [
            RNo,         Set.ofList ["no"; "no."; "번호"; "순번"; "순서"; "#"; "step"; "idx"; "seq"]
            RSym,        Set.ofList ["sym"; "symbol"; "기호"; "심볼"; "약호"; "code"]
            RTask,       Set.ofList ["작업내역"; "작업내용"; "작업"; "task"; "work"; "공정"; "내역"; "description"; "desc"; "단계"]
            RStart,      Set.ofList ["시작"; "시작시간"; "시작초"; "개시"; "start"; "starttime"; "from"; "begin"]
            RDuration,   Set.ofList ["시간"; "소요"; "소요시간"; "소요초"; "duration"; "dur"; "span"; "length"]
            RCumulative, Set.ofList ["누계"; "누적"; "누적시간"; "종료"; "종료시간"; "cum"; "cumulative"; "to"; "end"; "total"]
            RScore,      Set.ofList ["용접점수"; "점수"; "score"; "points"]
            RGrade,      Set.ofList ["용접등급"; "등급"; "grade"; "rank"; "class"]
        ]

    /// normalize header text → match key. 공백/탭/줄바꿈/전각공백 제거 + 괄호 안 부연 (`(sec)` 등) strip +
    /// 소문자 + 소수 대표 한자→한글 (산업 .xlsx 의 일본/중국 표기 흡수 — synonym alias 폭증 회피).
    let normalizeHeader (s: string) : string =
        if System.String.IsNullOrEmpty s then ""
        else
            let stripped = System.Text.RegularExpressions.Regex.Replace(s, @"\([^)]*\)", "")
            let noSpace = stripped |> String.filter (fun c -> not (System.Char.IsWhiteSpace c))
            let lower = noSpace.ToLowerInvariant()
            lower
                .Replace("時間", "시간").Replace("開始", "시작")
                .Replace("作業", "작업").Replace("番號", "번호")

    /// normalized key → 매칭되는 단일 role 반환 (없으면 None). 정확 매칭만.
    let tryMatch (normalized: string) : XlsxSheetRole option =
        if System.String.IsNullOrEmpty normalized then None
        else
            synonyms
            |> Map.toSeq
            |> Seq.tryPick (fun (role, syns) -> if Set.contains normalized syns then Some role else None)

    /// header row 후보의 column-wise role match — 각 컬럼의 row1 단독 매칭 우선, 실패 시 row1+row2 concat 매칭
    /// (2-row merged header — 위쪽 `시간` + 아래쪽 `10,20,30` 패턴 흡수). 1-based column → role.
    let buildRoleMap (rows: string[] list) : Map<int, XlsxSheetRole> =
        if List.isEmpty rows then Map.empty
        else
            let row1 = List.head rows
            let row2 =
                match rows with
                | _ :: r2 :: _ -> r2
                | _ -> [||]
            let maxCol = max row1.Length row2.Length
            let mutable acc = Map.empty
            for i in 0 .. maxCol - 1 do
                let v1 = if i < row1.Length then row1.[i] else ""
                let n1 = normalizeHeader v1
                match tryMatch n1 with
                | Some r -> acc <- Map.add (i + 1) r acc
                | None ->
                    let v2 = if i < row2.Length then row2.[i] else ""
                    let n2 = normalizeHeader v2
                    if n1.Length > 0 || n2.Length > 0 then
                        match tryMatch (n1 + n2) with
                        | Some r -> acc <- Map.add (i + 1) r acc
                        | None -> ()
            acc

    /// Gantt schedule 시트 판정 — distinct role ≥3 AND (RStart/RDuration/RCumulative) 중 ≥2.
    /// false negative 보다 false positive 회피 우선 (단순 NO/Item 표는 Gantt 판정 안 함).
    let isGanttSchedule (roleMap: Map<int, XlsxSheetRole>) : bool =
        let roles = roleMap |> Map.toSeq |> Seq.map snd |> Set.ofSeq
        if Set.count roles < 3 then false
        else
            let timeRoles = [ RStart; RDuration; RCumulative ]
            (timeRoles |> List.filter (fun r -> Set.contains r roles) |> List.length) >= 2

    /// 1-based column index → Excel column letter ("A" / "AA" / ...).
    let columnIndexToLetter (idx: int) : string =
        if idx <= 0 then ""
        else
            let sb = StringBuilder()
            let mutable n = idx
            while n > 0 do
                let r = (n - 1) % 26
                sb.Insert(0, char (int 'A' + r)) |> ignore
                n <- (n - 1) / 26
            sb.ToString()

    /// role → preamble label.
    let roleLabel (role: XlsxSheetRole) : string =
        match role with
        | RNo -> "NO(순번)"
        | RSym -> "SYM(심볼)"
        | RTask -> "TASK(작업내역)"
        | RStart -> "START(시작초)"
        | RDuration -> "DURATION(소요초)"
        | RCumulative -> "CUMULATIVE(누계초)"
        | RScore -> "SCORE(점수)"
        | RGrade -> "GRADE(등급)"

    /// role 기반 동적 안내문 — segment 머리에 prepend (Gantt 검출 시).
    let buildPreamble (roleMap: Map<int, XlsxSheetRole>) : string =
        let parts =
            roleMap
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (col, role) ->
                sprintf "%s=%s" (columnIndexToLetter col) (roleLabel role))
        sprintf "이 시트는 작업 일정표(Gantt)입니다. 컬럼 의미: %s. 좁은 컬럼들은 Gantt 시각화 막대(데이터 없음)."
            (String.concat ", " parts)

    /// 첫 N 개 dense row 중 Gantt 검출 시도 — 각 row 를 (단독 / 다음-row 와 concat) 패턴으로 buildRoleMap 호출.
    /// 첫 matching attempt 채택. 산업 .xlsx 의 header 가 row 1~3 사이에 박힌 패턴 흡수.
    let tryDetect (denseRows: string[] seq) : Map<int, XlsxSheetRole> option =
        let candidates =
            denseRows
            |> Seq.truncate 5
            |> Seq.toList
        let attempts = seq {
            for i in 0 .. candidates.Length - 1 do
                yield [ candidates.[i] ]
                if i + 1 < candidates.Length then
                    yield [ candidates.[i]; candidates.[i + 1] ]
        }
        attempts
        |> Seq.map buildRoleMap
        |> Seq.tryFind isGanttSchedule

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
/// **`ConvertImagePart` (Task 0 + Plan 3)**: 기존 `OoxmlExtractor.fs:115-118` + `:174-177` 2회 중복 mapping 단일화.
/// lowercase 가정 (OpenXml SDK 규약). 외부에서 손으로 작성된 ContentTypes.xml 의 mixed case (`image/PNG` 등) 는
/// 자연 skip — case-insensitive 매칭은 backlog. **Plan 3 (Backlog 6) 박제**: EMF/WMF metafile (image/x-emf,
/// image/emf, image/x-wmf, image/wmf) 도 System.Drawing.Imaging.Metafile → PNG 변환 후 ExtractedImage 박제.
/// 옛 `ImagePartToFormat: string -> ImageFormat option` 폐기, 신규 `ConvertImagePart: ImagePart -> ...` SSOT.
///
/// **DOCX 원래 박제 (변경 없음 — Plan 3 후 EMF/WMF 변환 활성)**:
/// - Body 의 paragraph/table iter 안 `Descendants<Blip>()` → `Blip.Embed` (relationship id) → ImagePart 매핑
///   → ExtractedImage 박제. ContentType 화이트리스트 4 종 (PNG/JPEG/GIF/WEBP) raw + EMF/WMF Metafile→PNG 변환,
///   그 외 (BMP/TIFF / 대문자 mime) 자연 skip.
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
/// **table cell scheme 확장** (s6-r22 task 5, s6-r16 backlog 해소) — table 의 Drawing 추출은
/// `extractImagesFromTable` 분기로 분리. RefLocator scheme = `p=&lt;tableParaOrd&gt;.cell=&lt;cellOrd&gt;.p=&lt;paraInCellOrd&gt;`
/// (모두 1-based). cellOrd = `tbl.Descendants&lt;TableCell&gt;()` row-major. paraInCellOrd = 각 cell 안 Paragraph 순번.
/// nested table (cell 안 table) 의 cell 좌표 평면화 / table 직속 Drawing (OOXML 규약상 빈도 0) 은 미지원 backlog.
///
/// **MainDocumentPart Body / Header / Footer / Comments / Footnotes / Endnotes 박제** — 본문 + header/footer 는
/// s6-r21 부터 박제, comments / footnotes / endnotes 는 **s6-r79 (B2, external review backlog)** 추가 박제.
/// 각 part 의 RootElement 의 Descendants&lt;Blip&gt;() enumerate + relId → ImagePart map. part 가 null 인 docx 도 안전.
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

    /// **Plan 3 — EMF/WMF 비례 축소 변환**. SSOT 는 `MetafileConverter.convertToPng` 으로 분리 (Task 7).
    /// caller 가 PlatformNotSupportedException / OutOfMemoryException / ExternalException 등 catch 의무.
    /// 반환: PNG byte array. raw EMF stream 은 처리 중 dispose.
    static member private ConvertMetafileToPng (stream: Stream) : byte[] =
        MetafileConverter.convertToPng stream MetafileConverter.DefaultMaxDim

    /// **Task 0 (Critical-1 + R3 M6) + Plan 3 (EMF/WMF 변환)** — ImagePart → (bytes, format) 변환.
    /// PNG/JPEG/GIF/WEBP 화이트리스트 → raw bytes + 원본 format.
    /// EMF/WMF (image/x-emf, image/emf, image/x-wmf, image/wmf) → System.Drawing Metafile → PNG bytes + Png.
    /// 그 외 (BMP/TIFF / 대문자 mime) → None 자연 skip (m6 primary 가드).
    /// per-image fail-safe — 변환 / stream read 실패 시 None (log + 다음 image 진행).
    /// location/path 인자는 log 진단용 (caller 측 context).
    static member private ConvertImagePart (imgPart: ImagePart) (location: string) (path: string) : (byte[] * ImageFormat) option =
        match imgPart.ContentType with
        | "image/png" | "image/jpeg" | "image/gif" | "image/webp" as ct ->
            try
                use stream = imgPart.GetStream()
                use ms = new MemoryStream()
                stream.CopyTo(ms)
                let bytes = ms.ToArray()
                let fmt =
                    match ct with
                    | "image/png"  -> Png
                    | "image/jpeg" -> Jpeg
                    | "image/gif"  -> Gif
                    | _ -> Webp
                if bytes.Length > 0 then Some (bytes, fmt) else None
            with ex ->
                Log.lighthouse.Warn(
                    sprintf "OoxmlExtractor: %s image stream read 실패 (skip) — ct=%s path=%s, ex=%s: %s"
                        location ct path (ex.GetType().Name) ex.Message)
                None
        | "image/x-emf" | "image/emf" | "image/x-wmf" | "image/wmf" as ct ->
            try
                use stream = imgPart.GetStream()
                let pngBytes = OoxmlExtractor.ConvertMetafileToPng stream
                if pngBytes.Length > 0 then Some (pngBytes, Png) else None
            with
            | :? System.PlatformNotSupportedException as ex ->
                // Linux / macOS — System.Drawing.Imaging.Metafile 미지원. 자연 skip.
                Log.lighthouse.Warn(
                    sprintf "OoxmlExtractor: %s Metafile 변환 미지원 platform — ct=%s path=%s, ex=%s"
                        location ct path ex.Message)
                None
            | ex ->
                // OutOfMemoryException / ExternalException (GDI+) / ArgumentException 등 — per-image fail-safe.
                Log.lighthouse.Warn(
                    sprintf "OoxmlExtractor: %s Metafile 변환 실패 (skip) — ct=%s path=%s, ex=%s: %s"
                        location ct path (ex.GetType().Name) ex.Message)
                None
        | _ -> None

    /// **review M1 helper consolidation** — `blip.Embed` non-null + HasValue 유효성 가드.
    /// `EnumerateValidBlips` / `CollectValidBlips` / `ExtractImagesAtRefLocator` 가 본 가드 단일 사용.
    static member private IsValidEmbeddedBlip (blip: Blip) : bool =
        not (isNull blip.Embed) && blip.Embed.HasValue

    /// **review M1** — container 의 valid Blip 만 lazy enumerate (single-shot caller).
    /// caller (DOCX table cell / header/footer / PPTX slide / XLSX drawing) 가 본 helper 호출 후
    /// `ExtractImagesAtRefLocator` 에 직접 전달.
    static member private EnumerateValidBlips (container: OpenXmlElement) : Blip seq =
        container.Descendants<Blip>() |> Seq.filter OoxmlExtractor.IsValidEmbeddedBlip

    /// **Task 0 (s6-r44 L-Maj-6)** — paragraph hot path 의 cache. `EnumerateValidBlips` 의 ResizeArray variant.
    /// `Count > 0` 확인 + 박제 시 enumerate 두 단계에서 deep descendants 1회만 호출 보장 (lazy seq 면 2회).
    static member private CollectValidBlips (block: OpenXmlElement) : Blip ResizeArray =
        let arr = ResizeArray<Blip>()
        for blip in OoxmlExtractor.EnumerateValidBlips block do
            arr.Add blip
        arr

    /// **review M4 helper consolidation** — relId → ImagePart map resolver.
    /// DOCX body (mainPart) / DOCX header-footer (part.Parts choose) / PPTX slide / XLSX drawing 의
    /// 동일 3줄 패턴 단일 진입점. parent 의 `GetIdOfPart` 로 relId 산출 + Map lookup.
    static member private BuildImagePartResolver
        (parent: OpenXmlPart) (imageParts: ImagePart seq) : string -> ImagePart option =
        let m =
            imageParts
            |> Seq.map (fun ip -> parent.GetIdOfPart(ip), ip)
            |> Map.ofSeq
        fun relId -> Map.tryFind relId m

    /// **review M5 helper consolidation** — DrawingML paragraph break 보존 enumerate.
    /// PPTX 의 title shape / slide body / notes 3 caller 의 동일 패턴 단일 진입점 + trim.
    /// `\n` 명시 삽입 (r1 M5 — InnerText 직접 사용 시 bullet 들러붙음).
    static member private CollectDrawingText (container: OpenXmlElement) : string =
        let sb = StringBuilder()
        for paragraph in container.Descendants<DrawingParagraph>() do
            let paraText =
                paragraph.Descendants<DrawingText>()
                |> Seq.map (fun t -> t.Text)
                |> String.concat ""
            if paraText.Length > 0 then
                sb.AppendLine(paraText) |> ignore
        sb.ToString().Trim()

    /// **Task 0 + review M1 + Plan 3 통합** — `Blip seq` 단일 진입점. body / header / footer / pptx slide /
    /// xlsx drawing 의 image 박제 동일 처리. caller 는 `EnumerateValidBlips` (single-shot) 또는
    /// `CollectValidBlips` (paragraph cache) 결과 전달.
    /// `Ordinal` 은 호출 1회 안에서 1부터 (`Models.fs §108` 의 "같은 RefLocator 안 N번째" SSOT).
    /// per-image fail-safe — `ConvertImagePart` 가 None 반환 시 자연 skip (Ordinal 미증가, review M9 정합).
    /// EMF/WMF metafile → PNG 변환은 `ConvertImagePart` 내부 박제 (Plan 3).
    static member private ExtractImagesAtRefLocator
        (validBlips: Blip seq)
        (resolveImagePart: string -> ImagePart option)
        (location: string)
        (refLocator: string)
        (images: ResizeArray<ExtractedImage>)
        (path: string) : unit =
        let mutable imgOrdInBlock = 1
        for blip in validBlips do
            let relId = blip.Embed.Value
            match resolveImagePart relId with
            | None -> ()   // 외부 image / hyperlink / 손상 relId — 자연 skip.
            | Some imgPart ->
                match OoxmlExtractor.ConvertImagePart imgPart location path with
                | None ->
                    // 화이트리스트 외 (BMP/TIFF) 또는 변환 실패 (PlatformNotSupported / GDI+) — 자연 skip.
                    // `ConvertImagePart` 내부에서 log 박제됨, 본 layer 추가 log 없음.
                    ()
                | Some (bytes, fmt) ->
                    images.Add {
                        Bytes = bytes
                        Format = fmt
                        Width = None
                        Height = None
                        RefLocator = refLocator
                        Ordinal = imgOrdInBlock
                    }
                    imgOrdInBlock <- imgOrdInBlock + 1

    /// **Task 0 + review M4 통합** — OpenXmlPart (HeaderPart / FooterPart 등) 의 RootElement enumerate.
    /// `part.Parts` 에서 ImagePart 만 추출 → `BuildImagePartResolver` + `ExtractImagesAtRefLocator` 위임.
    static member private ExtractImagesFromOpenXmlPart
        (part: OpenXmlPart)
        (location: string)
        (refLocator: string)
        (images: ResizeArray<ExtractedImage>)
        (path: string) : unit =
        if not (isNull part.RootElement) then
            let imageParts =
                part.Parts
                |> Seq.choose (fun ip -> match ip.OpenXmlPart with :? ImagePart as p -> Some p | _ -> None)
            let resolve = OoxmlExtractor.BuildImagePartResolver part imageParts
            let validBlips = OoxmlExtractor.EnumerateValidBlips part.RootElement
            OoxmlExtractor.ExtractImagesAtRefLocator validBlips resolve location refLocator images path

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

            // body 측 resolver — mainPart 안 ImagePart map (review M4 단일 helper).
            let resolveBodyImagePart = OoxmlExtractor.BuildImagePartResolver mainPart mainPart.ImageParts

            // paragraph 의 inline Drawing 박제 (paraOrd 기반 RefLocator `p=%d`). cached path.
            let extractImagesFromBlock (blips: Blip ResizeArray) (paraOrd: int) =
                OoxmlExtractor.ExtractImagesAtRefLocator (blips :> seq<Blip>) resolveBodyImagePart "body" (sprintf "p=%d" paraOrd) images path

            // table cell 단위 매핑. RefLocator scheme = `p=<table-paraOrd>.cell=<cellOrd>.p=<paraInCellOrd>`.
            // nested table 안 image 는 silent drift 차단 (backlog).
            let extractImagesFromTable (tbl: Table) (paraOrd: int) =
                let mutable cellOrd = 1
                for row in tbl.Elements<TableRow>() do
                    for cell in row.Elements<TableCell>() do
                        let mutable paraInCell = 1
                        for cellPara in cell.Elements<Paragraph>() do
                            let refLoc = sprintf "p=%d.cell=%d.p=%d" paraOrd cellOrd paraInCell
                            OoxmlExtractor.ExtractImagesAtRefLocator
                                (OoxmlExtractor.EnumerateValidBlips cellPara)
                                resolveBodyImagePart "table-cell" refLoc images path
                            paraInCell <- paraInCell + 1
                        for nestedTbl in cell.Elements<Table>() do
                            let nestedImgCount =
                                OoxmlExtractor.EnumerateValidBlips nestedTbl |> Seq.length
                            if nestedImgCount > 0 then
                                Log.lighthouse.Warn(
                                    sprintf "OoxmlExtractor: nested table 안 image %d 장 skip (RefLocator scheme 미지원, backlog) — path=%s, outer p=%d.cell=%d"
                                        nestedImgCount path paraOrd cellOrd)
                        cellOrd <- cellOrd + 1

            // helper: block 안 Drawing.Blip 존재 여부 — image-only 분리 분기 검사.
            let hasInlineDrawing (block: OpenXmlElement) : bool =
                OoxmlExtractor.EnumerateValidBlips block |> Seq.isEmpty |> not

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
            // s6-r79 (B2): comments / footnotes / endnotes part singleton — RefLocator = `comments` / `footnotes` / `endnotes`.
            let mutable headerOrd = 1
            for hp in mainPart.HeaderParts do
                OoxmlExtractor.ExtractImagesFromOpenXmlPart hp "header" (sprintf "header=%d" headerOrd) images path
                headerOrd <- headerOrd + 1
            let mutable footerOrd = 1
            for fp in mainPart.FooterParts do
                OoxmlExtractor.ExtractImagesFromOpenXmlPart fp "footer" (sprintf "footer=%d" footerOrd) images path
                footerOrd <- footerOrd + 1

            // s6-r79 (B2) — comments / footnotes / endnotes part 각 singleton. merge xlsx ← light-house: closure→static 갱신.
            if not (isNull mainPart.WordprocessingCommentsPart) then
                OoxmlExtractor.ExtractImagesFromOpenXmlPart mainPart.WordprocessingCommentsPart "comments" "comments" images path
            if not (isNull mainPart.FootnotesPart) then
                OoxmlExtractor.ExtractImagesFromOpenXmlPart mainPart.FootnotesPart "footnotes" "footnotes" images path
            if not (isNull mainPart.EndnotesPart) then
                OoxmlExtractor.ExtractImagesFromOpenXmlPart mainPart.EndnotesPart "endnotes" "endnotes" images path

            {
                DocType = Docx
                PageOrSheetCnt = None
                Title = title
                Outline = outline.ToArray()
                Segments = segments.ToArray()
                Images = images.ToArray()
            }

    /// **Task 0 (Critical-1, r2 m4 XmlException 추가, review M6 KeyNotFound/ArgumentOutOfRange 추가)** —
    /// 외부 환경 fail-safe (§6.5) 7 종 통합 wrapper.
    ///   - FileFormatException: zip header 등 OOXML 패키지 구조 깨짐
    ///   - OpenXmlPackageException: 패키지 일관성 위반
    ///   - InvalidDataException: 손상된 압축 stream
    ///   - IOException: 파일 접근 실패 (lock / 권한)
    ///   - System.Xml.XmlException: OpenXml lazy deferred parsing 시점 발생 가능 (r2 m4)
    ///   - KeyNotFoundException: presentationPart.GetPartById(relId) 의 dangling relId — slideId/sheetId
    ///     참조 표는 있는데 매핑 part 부재 (review M6). 손상 ooxml 1건이 전체 색인 abort 회피.
    ///   - ArgumentOutOfRangeException: SDK 내부 index 범위 위반 — 손상 pptx/xlsx 의 SDK fallback path (review M6).
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
        | :? System.Collections.Generic.KeyNotFoundException as ex ->
            // review M6 — GetPartById(relId) 의 dangling relId 차단. PPTX slideId / XLSX sheet.Id 의
            // 참조표는 있으나 매핑 part 부재 시 SDK 가 throw. 손상 ooxml 1건이 전체 색인 abort 회피.
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A relationship 매핑 실패 — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()
        | :? System.ArgumentOutOfRangeException as ex ->
            // review M6 — SDK 내부 index 범위 위반 (손상 pptx/xlsx 의 SDK fallback path).
            Log.lighthouse.Warn(sprintf "OoxmlExtractor: %A 인덱스 범위 위반 — path=%s, ex=%s" docType path ex.Message)
            emptyResult ()

    /// **Task 1 — PPTX 활성**. (todo-lighthouse-kb-index-xlsx-pptx-images.md Task 1)
    ///
    /// 활성 박제:
    /// - SlideIdList SSOT 순회 (r1 Critical-5, MS Learn 공식) — `presentationPart.SlideParts` 직접 enumerate
    ///   금지 (zip relationship 순서라 reorder/insert 시 정렬 어긋남).
    /// - SlideIdList null guard (r2 Major-1) — 빈/손상 pptx 의 NRE 차단 (ExtractWithFailSafe 의 5 catch 가
    ///   NRE 안 잡음).
    /// - slideId.RelationshipId null guard (r2 Minor 3) — 손상 pptx 의 NRE 차단.
    /// - title + CenteredTitle placeholder 모두 매칭 (r1 M4, ECMA-376) — `<ph type="title">` 와 `<ph type="ctrTitle">`.
    ///   EnumValue 직접 비교 (`PlaceholderValues.Title` / `PlaceholderValues.CenteredTitle`).
    /// - title 부재 슬라이드 fallback = "슬라이드 N" literal (r1 M11).
    /// - paragraph break 보존 (r1 M5) — `Slide.InnerText` 직접 사용 금지 (bullet 들러붙음).
    ///   `Descendants<DrawingParagraph>()` enumerate + `\n` 명시 삽입.
    /// - speaker notes 합성 (r1 M10) — body + `--- 노트 ---` marker + notes 단일 segment. RefLocator = `slide=N`.
    /// - image — `slide=N` RefLocator + Ordinal 1..M. `SlidePart.ImageParts` 의 relId map resolver.
    /// - slide loop `ct.ThrowIfCancellationRequested()` (r2 Minor 6) — 100+ slide deck cancel 응답성.
    /// - PageOrSheetCnt = 전체 슬라이드 수 (빈 pptx = Some 0).
    ///
    /// 명시 skip:
    /// - SlideMaster / SlideLayout placeholder text — `SlidePart` 직속 enumerate 만, master/layout 진입 안 함.
    /// - comments / notes master — Phase 3 backlog.
    /// - Title (`PackageProperties`) — DOCX 와 동일 OpenXml 3.x experimental 회피, None 박제 (r1 M7).
    static member private ExtractPptx (path: string) (ct: CancellationToken) : ExtractedDocument =
        ct.ThrowIfCancellationRequested()
        use doc = PresentationDocument.Open(path, false)
        let presentationPart = doc.PresentationPart
        if isNull presentationPart || isNull presentationPart.Presentation then
            { DocType = Pptx; PageOrSheetCnt = Some 0; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
        else
            let pres = presentationPart.Presentation
            let outline = ResizeArray<ExtractedOutlineNode>()
            let segments = ResizeArray<ExtractedSegment>()
            let images = ResizeArray<ExtractedImage>()
            // r2 Major-1: SlideIdList null guard. 빈/손상 pptx 의 NRE 차단.
            let slideIds : seq<DocumentFormat.OpenXml.Presentation.SlideId> =
                if isNull pres.SlideIdList then Seq.empty
                else pres.SlideIdList.Elements<DocumentFormat.OpenXml.Presentation.SlideId>()
            // **review M3 fix** — slideNo (SlideIdList ordinal, slide=N 박제 정합 유지) 와
            // ingestedSlideCnt (정상 ingest 카운트, PageOrSheetCnt = outline.Length 일치) 분리. XLSX
            // 의 `visibleSheetCount` 패턴과 통일. broken slide 1건이 정상 N 의 PageOrSheetCnt 를
            // N+1 로 오염하던 회귀 차단.
            let mutable slideNo = 1
            let mutable ingestedSlideCnt = 0
            for slideId in slideIds do
                ct.ThrowIfCancellationRequested()   // r2 Minor 6
                // r2 Minor 3: 손상 pptx 의 RelationshipId null guard.
                if isNull slideId.RelationshipId || not slideId.RelationshipId.HasValue then
                    Log.lighthouse.Warn(
                        sprintf "ExtractPptx: slideId(no=%d) RelationshipId null — path=%s" slideNo path)
                else
                    let relId = slideId.RelationshipId.Value
                    match presentationPart.GetPartById(relId) with
                    | :? SlidePart as slidePart ->
                        OoxmlExtractor.IngestPptxSlide path slidePart slideNo outline segments images
                        ingestedSlideCnt <- ingestedSlideCnt + 1
                    | other ->
                        Log.lighthouse.Warn(
                            sprintf "ExtractPptx: relId=%s 가 SlidePart 아님 (%A) — path=%s" relId (other.GetType().Name) path)
                slideNo <- slideNo + 1
            {
                DocType = Pptx
                PageOrSheetCnt = Some ingestedSlideCnt
                Title = None
                Outline = outline.ToArray()
                Segments = segments.ToArray()
                Images = images.ToArray()
            }

    /// **Task 1** — 단일 슬라이드 ingest helper.
    /// outline (slide 라벨) + segment (body + notes 합성) + image 박제 후 caller 가 slideNo 증가.
    ///
    /// **review M2 — title 중복 박제는 의도된 동작** (xlsx-pptx-images r2 line 192-208 의사코드 SSOT).
    /// title placeholder shape 의 paragraph 가 `slide.Descendants<DrawingParagraph>()` 박제 시 다시 enumerate
    /// 되어 outline.Label + segment.Text 양쪽에 동일 텍스트 등장. segment 단위 "한 슬라이드 1개" 정합 +
    /// title 분리 enumerate 시 ShapeTree placeholder 매칭 로직 중복 회피 목적. LLM citation 결과의 noise 가능성은
    /// Phase 3 backlog (실측 후 strict matching 진입).
    static member private IngestPptxSlide
        (path: string)
        (slidePart: SlidePart)
        (slideNo: int)
        (outline: ResizeArray<ExtractedOutlineNode>)
        (segments: ResizeArray<ExtractedSegment>)
        (images: ResizeArray<ExtractedImage>) : unit =
        let slide = slidePart.Slide
        let refLoc = sprintf "slide=%d" slideNo
        // ── Title placeholder (title + ctrTitle) ──
        let titleLabel =
            let fallback = sprintf "슬라이드 %d" slideNo
            if isNull slide || isNull slide.CommonSlideData || isNull slide.CommonSlideData.ShapeTree then fallback
            else
                let titleShape =
                    slide.CommonSlideData.ShapeTree.Elements<DocumentFormat.OpenXml.Presentation.Shape>()
                    |> Seq.tryFind (fun shape ->
                        let nv = shape.NonVisualShapeProperties
                        if isNull nv then false
                        else
                            let appNv = nv.ApplicationNonVisualDrawingProperties
                            if isNull appNv then false
                            else
                                let ph = appNv.PlaceholderShape
                                if isNull ph || isNull ph.Type || not ph.Type.HasValue then false
                                else
                                    let t = ph.Type.Value
                                    t = DocumentFormat.OpenXml.Presentation.PlaceholderValues.Title
                                    || t = DocumentFormat.OpenXml.Presentation.PlaceholderValues.CenteredTitle)
                match titleShape with
                | None -> fallback
                | Some sh ->
                    // review M5 — CollectDrawingText 단일 helper.
                    let s = OoxmlExtractor.CollectDrawingText sh
                    if s.Length = 0 then fallback else s
        outline.Add {
            ParentIndex = None
            Ordinal = outline.Count
            NodeType = OutlineNodeType.Slide
            Label = titleLabel
            RefLocator = refLoc
        }
        let outlineIdx = outline.Count - 1
        // ── Segment text — slide 전체 paragraph break 보존 + speaker notes 합성 (r1 M5 + M10) ──
        // review M5 — title/body/notes 3 caller 가 동일 CollectDrawingText 사용.
        let bodyText = if isNull slide then "" else OoxmlExtractor.CollectDrawingText slide
        let notesText =
            if not (isNull slidePart.NotesSlidePart) && not (isNull slidePart.NotesSlidePart.NotesSlide) then
                OoxmlExtractor.CollectDrawingText slidePart.NotesSlidePart.NotesSlide
            else ""
        let combined =
            if notesText.Length > 0 then
                if bodyText.Length > 0 then
                    bodyText + Environment.NewLine + "--- 노트 ---" + Environment.NewLine + notesText
                else
                    "--- 노트 ---" + Environment.NewLine + notesText
            else bodyText
        if combined.Length > 0 then
            segments.Add {
                OutlineIndex = Some outlineIdx
                RefLocator = refLoc
                Text = combined
            }
        // ── Image — SlidePart 안 Blip enumerate (review M4 단일 resolver) ──
        if not (isNull slide) then
            let resolve = OoxmlExtractor.BuildImagePartResolver slidePart slidePart.ImageParts
            let validBlips = OoxmlExtractor.EnumerateValidBlips slide
            OoxmlExtractor.ExtractImagesAtRefLocator validBlips resolve "slide" refLoc images path

    /// **Task 2 — XLSX 활성**. (todo-lighthouse-kb-index-xlsx-pptx-images.md Task 2)
    ///
    /// 활성 박제 (사용자 결정 "최대한 간편하게"):
    /// - SharedStringTable 사전 로드 — phonetic ruby `<rPh>` 제외 (r1 M2 + r2 Minor 1).
    /// - Sheet.State enum 비교 — Hidden / VeryHidden skip (r1 Critical-6). visible 만 진입.
    /// - sparse cell `expandSparseRow` helper (r1 Critical-4) — column letter parse + gap "" fill +
    ///   MaxXlsxColumnsPerRow=1024 cap (r2 Minor 2) + cell.CellReference null guard (r2 Minor 4).
    /// - Row.OrderBy(RowIndex) (r1 M3) — element 순서 미보장 → RowIndex sort.
    /// - Cell.DataType 6 분기 (r1 M1 Str/Error 추가) — Number/Date/Boolean (null) / SharedString / InlineString /
    ///   String (formula string result) / Error (`#REF!` 등 skip + log).
    /// - 시트명 `#` escape (r1 M18) — fragment separator 충돌 → Warn + sheet skip. `=` / `.` 안전 (r2 Major-2 반론).
    /// - image — `worksheetPart.DrawingsPart` null guard (r1 M16) + `sheet=<name>` RefLocator.
    /// - PageOrSheetCnt = visible sheet 수 (hidden 제외, r1 m5).
    ///
    /// 명시 skip (사용자 결정):
    /// - CellFormula — element 무시, CellValue cached 만.
    /// - MergeCells — top-left cell 만 자연 노출.
    /// - 빈 행 — 모든 cell 빈 문자열이면 행 skip.
    /// - 좌표 RefLocator (`sheet=BOM!A1:D40`) — Phase 3 backlog (sheet 단위만).
    /// - Defined Name / Pivot Table / Chart — Phase 3 backlog.
    /// **Backlog 3** — SharedStringTable 사전 로드 (r1 M2 + r2 Minor 1). `<rPh>` (PhoneticRun) ruby 제외.
    static member private LoadSharedStrings (workbookPart: WorkbookPart) : string array =
        let sst = workbookPart.SharedStringTablePart
        if isNull sst || isNull sst.SharedStringTable then [||]
        else
            sst.SharedStringTable.Elements<XlSharedStringItem>()
            |> Seq.map (fun item ->
                item.Descendants<XlText>()
                |> Seq.filter (fun t -> t.Ancestors<XlPhoneticRun>() |> Seq.isEmpty)
                |> Seq.map (fun t -> t.Text)
                |> String.concat "")
            |> Array.ofSeq

    /// **Backlog 3 (r1 Critical-6)** — Sheet.State enum 비교 (Hidden / VeryHidden).
    /// `State.Value.ToString() = "visible"` 비교 금지 — locale/대소문자 변동 risk.
    static member private IsHiddenSheet (sheet: XlSheet) : bool =
        not (isNull sheet.State)
        && sheet.State.HasValue
        && (sheet.State.Value = XlSheetStateValues.Hidden
            || sheet.State.Value = XlSheetStateValues.VeryHidden)

    /// **Backlog 3 (r1 Critical-4)** — Excel column letter ("A" / "AA" / "AB" / ...) → 1-based index.
    static member private ColumnLetterToIndex (letter: string) : int =
        let mutable result = 0
        for c in letter.ToUpperInvariant() do
            result <- result * 26 + (int c - int 'A' + 1)
        result

    /// **Backlog 3** — CellReference ("B12" / "AA3") → column letter prefix.
    static member private CellRefToColumnLetter (cellRef: string) : string =
        String(cellRef.ToCharArray() |> Array.takeWhile System.Char.IsLetter)

    /// **Backlog 3 (r1 M1 + r2 Minor 4 null guard)** — Cell.DataType 6 분기 셀 값 해결.
    static member private ResolveCellValue
        (cell: XlCell) (sstItems: string array) (path: string) : string =
        if isNull cell.CellValue then
            // r2 Minor: formula cached value 부재 — null guard.
            if not (isNull cell.DataType) && cell.DataType.HasValue
               && cell.DataType.Value = XlCellValues.InlineString then
                if isNull cell.InlineString then "" else cell.InlineString.InnerText
            else ""
        else
            if isNull cell.DataType || not cell.DataType.HasValue then
                cell.CellValue.Text  // Number / Date / Boolean (cached value)
            else
                match cell.DataType.Value with
                | dt when dt = XlCellValues.SharedString ->
                    match System.Int32.TryParse cell.CellValue.Text with
                    | true, idx when idx >= 0 && idx < sstItems.Length -> sstItems.[idx]
                    | _ -> ""
                | dt when dt = XlCellValues.InlineString ->
                    if isNull cell.InlineString then "" else cell.InlineString.InnerText
                | dt when dt = XlCellValues.String ->
                    cell.CellValue.Text  // formula string result
                | dt when dt = XlCellValues.Error ->
                    Log.lighthouse.Debug(
                        sprintf "ExtractXlsx: error cell skip — ref=%s value=%s path=%s"
                            (if isNull cell.CellReference || not cell.CellReference.HasValue then "<null>" else cell.CellReference.Value)
                            cell.CellValue.Text path)
                    ""
                | _ -> cell.CellValue.Text

    /// MaxXlsxColumnsPerRow — sparse cell row 의 worst-case (A1 + ZZZ1 = 18278) 폭주 차단 hard cap (r2 Minor 2).
    static member private MaxXlsxColumnsPerRow = 1024

    /// **r3 timeline filter** — Gantt 시각화용 좁은 컬럼 판정 threshold (Excel 기본 컬럼 폭 11 대비 1.0 미만).
    /// 산업 .xlsx (4-1.SV_SIDE 등) 의 Gantt 시각화는 `Column.Width=0.75 × 70+` 컬럼 + cell value 없음 + fill style only.
    /// 이 셀들은 dense array 의 trailing `""` 가 되어 tab join noise 가 됨. threshold 미만 + 빈 값 셀은 drop.
    /// tick label (`1, 2, 3 …`) 처럼 값 있는 좁은 셀은 보존 (filter 는 width<threshold AND value="" 동시 충족).
    static member private NarrowColumnWidthThreshold = 1.0

    /// **r3 timeline filter** — `worksheet.Columns` 의 `Column.Width < threshold` 인 컬럼 1-based index set 빌드.
    /// `Column` element 는 `Min ~ Max` range 형태로 1~ 다수 컬럼 폭 박제 가능. Min/Max HasValue false 면 skip.
    /// `Columns` element 부재 (열 기본 폭만 사용) 시 빈 set 반환 — 자연 무 영향.
    static member private NarrowColIndexes (worksheet: DocumentFormat.OpenXml.Spreadsheet.Worksheet) : Set<int> =
        if isNull worksheet then Set.empty
        else
            let cols = worksheet.GetFirstChild<XlColumns>()
            if isNull cols then Set.empty
            else
                cols.Elements<XlColumn>()
                |> Seq.filter (fun c ->
                    not (isNull c.Width) && c.Width.HasValue
                    && c.Width.Value < OoxmlExtractor.NarrowColumnWidthThreshold
                    && not (isNull c.Min) && c.Min.HasValue
                    && not (isNull c.Max) && c.Max.HasValue)
                |> Seq.collect (fun c -> seq { int c.Min.Value .. int c.Max.Value })
                |> Set.ofSeq

    /// **Backlog 3 (r1 Critical-4)** — sparse cell 들을 dense array 로 확장. gap "" 채움.
    /// `<row>` 가 값 있는 cell 만 child 라 A1+C1 row 는 `[A1; C1]` 2개 반환 → "A1값\tC1값" 으로 컬럼 alignment 깨짐 (B silent 소실).
    /// MaxXlsxColumnsPerRow cap (r2 Minor 2) + CellReference null guard (r2 Minor 4) + colIdx >=0 guard (review m1).
    static member private ExpandSparseRow
        (cells: XlCell seq) (sstItems: string array) (sheetName: string) (path: string) : string[] =
        let cellList =
            cells
            |> Seq.filter (fun c ->
                if isNull c.CellReference || not c.CellReference.HasValue then
                    Log.lighthouse.Warn(
                        sprintf "ExtractXlsx: CellReference null skip — sheet=%s path=%s" sheetName path)
                    false
                else true)
            |> Seq.toList
        if List.isEmpty cellList then [||]
        else
            let maxCol =
                cellList
                |> List.map (fun c -> OoxmlExtractor.ColumnLetterToIndex (OoxmlExtractor.CellRefToColumnLetter c.CellReference.Value))
                |> List.max
            let cappedMax =
                if maxCol > OoxmlExtractor.MaxXlsxColumnsPerRow then
                    Log.lighthouse.Warn(
                        sprintf "ExtractXlsx: row column cap 초과 (maxCol=%d > %d) — sheet=%s path=%s, cap 적용"
                            maxCol OoxmlExtractor.MaxXlsxColumnsPerRow sheetName path)
                    OoxmlExtractor.MaxXlsxColumnsPerRow
                else maxCol
            let result = Array.create cappedMax ""
            for cell in cellList do
                let colIdx = OoxmlExtractor.ColumnLetterToIndex (OoxmlExtractor.CellRefToColumnLetter cell.CellReference.Value) - 1
                if colIdx >= 0 && colIdx < cappedMax then
                    result.[colIdx] <- OoxmlExtractor.ResolveCellValue cell sstItems path
                elif colIdx < 0 then
                    Log.lighthouse.Warn(
                        sprintf "ExtractXlsx: 손상 cellRef letter prefix skip — ref=%s sheet=%s path=%s"
                            cell.CellReference.Value sheetName path)
            result

    /// **Backlog 3 (review M7) — 단일 visible sheet ingest helper. PPTX 의 `IngestPptxSlide` 대칭.**
    /// caller (ExtractXlsx) 가 hidden 가드 통과 후 호출. outline + segment + image 박제.
    /// **Backlog 5 (시트명 `#` hotfix)** — 시트명 `#` 는 RefLocator.encodeMainValue 가 `%23` 으로 자동 escape.
    /// caller 의 `#` skip 정책 폐기 — 산업 .xlsx 의 호기/부품 번호 표기 (`5-1. #201`) 정합.
    static member private IngestXlsxSheet
        (path: string)
        (workbookPart: WorkbookPart)
        (sheet: XlSheet)
        (sheetName: string)
        (sstItems: string array)
        (outline: ResizeArray<ExtractedOutlineNode>)
        (segments: ResizeArray<ExtractedSegment>)
        (images: ResizeArray<ExtractedImage>)
        (ct: CancellationToken) : unit =
        let refLoc = sprintf "sheet=%s" (RefLocator.encodeMainValue sheetName)
        // r1 M17: outline 박제 — segment 미박제 (빈 시트) 라도 시트 존재 자체가 정보.
        outline.Add {
            ParentIndex = None
            Ordinal = outline.Count
            NodeType = OutlineNodeType.Sheet
            Label = sheetName
            RefLocator = refLoc
        }
        let outlineIdx = outline.Count - 1
        if not (isNull sheet.Id) && sheet.Id.HasValue then
            let relId = sheet.Id.Value
            match workbookPart.GetPartById(relId) with
            | :? WorksheetPart as worksheetPart ->
                // r1 M3: Row.OrderBy(RowIndex) — element 순서 미보장.
                let sortedRows =
                    worksheetPart.Worksheet.Descendants<XlRow>()
                    |> Seq.sortBy (fun r ->
                        if isNull r.RowIndex || not r.RowIndex.HasValue then 0u
                        else r.RowIndex.Value)
                // **r3 timeline filter** — Gantt 시각화용 좁은 컬럼 index set. 본 sheet 1회 빌드 + row 루프 재사용.
                let narrowSet = OoxmlExtractor.NarrowColIndexes worksheetPart.Worksheet
                // **Task 2-extra** — Gantt 검출 위해 dense row 를 미리 모은다 (pass 1).
                // 회귀 0 의무: 단일 시트의 row 들을 메모리에 모았다가 sb 빌드 (기존 streaming → 두-패스).
                // 산업 .xlsx 시트 row 폭 통상 < 10K row 라 메모리 영향 무시.
                let denseRows = ResizeArray<string[]>()
                for row in sortedRows do
                    ct.ThrowIfCancellationRequested()
                    let dense =
                        OoxmlExtractor.ExpandSparseRow
                            (row.Elements<XlCell>()) sstItems sheetName path
                    // **r3 timeline filter** — 좁은 컬럼이 빈 값이면 drop, 값 있는 (tick label) 셀은 보존.
                    // narrowSet 가 빈 set (=`Columns` 부재 또는 모든 컬럼 width >= 1.0) 이면 자연 무 영향.
                    let kept =
                        if Set.isEmpty narrowSet then dense
                        else
                            dense
                            |> Array.mapi (fun i v ->
                                let colIdx = i + 1
                                if Set.contains colIdx narrowSet && v = "" then None else Some v)
                            |> Array.choose id
                    denseRows.Add kept
                // **Task 2-extra** — Gantt schedule 시트 검출. 검출 시 outline label `[Gantt schedule]` suffix +
                // segment 머리에 role 기반 preamble prepend.
                let ganttRoleMap = XlsxSheetRoles.tryDetect (denseRows :> seq<_>)
                match ganttRoleMap with
                | Some roleMap ->
                    let prev = outline.[outlineIdx]
                    outline.[outlineIdx] <- { prev with Label = sprintf "%s [Gantt schedule]" prev.Label }
                    Log.lighthouse.Debug(
                        sprintf "ExtractXlsx: Gantt schedule 검출 — sheet=%s roles=%d path=%s"
                            sheetName (Map.count roleMap) path)
                | None -> ()
                // pass 2: sb 빌드 (preamble prepend → row tab-join).
                let sb = StringBuilder()
                match ganttRoleMap with
                | Some roleMap ->
                    sb.AppendLine(XlsxSheetRoles.buildPreamble roleMap) |> ignore
                | None -> ()
                for kept in denseRows do
                    let line = String.Join("\t", kept)
                    if line.Trim().Length > 0 then
                        sb.AppendLine(line) |> ignore
                let segText = sb.ToString().Trim()
                if segText.Length > 0 then
                    segments.Add {
                        OutlineIndex = Some outlineIdx
                        RefLocator = refLoc
                        Text = segText
                    }
                // r1 M16: DrawingsPart null guard. review M4 — BuildImagePartResolver 단일 helper.
                if not (isNull worksheetPart.DrawingsPart) then
                    let drawingsPart = worksheetPart.DrawingsPart
                    if not (isNull drawingsPart.WorksheetDrawing) then
                        let resolve = OoxmlExtractor.BuildImagePartResolver drawingsPart drawingsPart.ImageParts
                        let validBlips = OoxmlExtractor.EnumerateValidBlips drawingsPart.WorksheetDrawing
                        OoxmlExtractor.ExtractImagesAtRefLocator validBlips resolve "sheet" refLoc images path
            | other ->
                Log.lighthouse.Warn(
                    sprintf "ExtractXlsx: sheet relId=%s 가 WorksheetPart 아님 (%A) — name=%s path=%s"
                        relId (other.GetType().Name) sheetName path)
        else
            Log.lighthouse.Warn(
                sprintf "ExtractXlsx: sheet.Id null — name=%s path=%s" sheetName path)

    static member private ExtractXlsx (path: string) (ct: CancellationToken) : ExtractedDocument =
        ct.ThrowIfCancellationRequested()
        use doc = SpreadsheetDocument.Open(path, false)
        let workbookPart = doc.WorkbookPart
        if isNull workbookPart || isNull workbookPart.Workbook || isNull workbookPart.Workbook.Sheets then
            { DocType = Xlsx; PageOrSheetCnt = Some 0; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
        else
            let outline = ResizeArray<ExtractedOutlineNode>()
            let segments = ResizeArray<ExtractedSegment>()
            let images = ResizeArray<ExtractedImage>()
            let sstItems = OoxmlExtractor.LoadSharedStrings workbookPart
            let mutable visibleSheetCount = 0
            for sheet in workbookPart.Workbook.Sheets.Elements<XlSheet>() do
                ct.ThrowIfCancellationRequested()
                if OoxmlExtractor.IsHiddenSheet sheet then
                    Log.lighthouse.Debug(
                        sprintf "ExtractXlsx: hidden sheet skip — name=%s path=%s"
                            (if isNull sheet.Name || not sheet.Name.HasValue then "<null>" else sheet.Name.Value) path)
                else
                    // **Backlog 5 (시트명 `#` hotfix)** — 시트명 `#` skip 폐기. RefLocator.encodeMainValue 가 `%23` 자동 escape →
                    // 산업 .xlsx 의 호기 번호 표기 (`5-1. #201` 등) 정상 색인. 기존 r1 M18 정책 무효화.
                    let sheetName =
                        if isNull sheet.Name || not sheet.Name.HasValue then "" else sheet.Name.Value
                    visibleSheetCount <- visibleSheetCount + 1
                    OoxmlExtractor.IngestXlsxSheet path workbookPart sheet sheetName sstItems outline segments images ct
            {
                DocType = Xlsx
                PageOrSheetCnt = Some visibleSheetCount
                Title = None
                Outline = outline.ToArray()
                Segments = segments.ToArray()
                Images = images.ToArray()
            }

    interface IExtractor with
        member _.Supports kind =
            match kind with
            | Docx | Pptx | Xlsx -> true
            | _ -> false

        member _.Extract (path, ct) =
            ct.ThrowIfCancellationRequested()
            // Task 0 Critical-1 — 진정한 dispatch.
            let kind = Classifier.classifyForKb path
            OoxmlExtractor.ExtractWithFailSafe kind path (fun () ->
                match kind with
                | Docx -> OoxmlExtractor.ExtractDocx path ct
                | Pptx -> OoxmlExtractor.ExtractPptx path ct
                | Xlsx -> OoxmlExtractor.ExtractXlsx path ct
                | _ ->
                    failwith (sprintf "OoxmlExtractor.Extract: Supports invariant 위반 — kind=%A path=%s" kind path))

        member _.Dispose () = ()
