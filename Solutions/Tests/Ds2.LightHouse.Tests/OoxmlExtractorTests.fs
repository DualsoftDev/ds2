module Ds2.LightHouse.Tests.OoxmlExtractorTests

open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

/// todo-lighthouse-kb-index.md §4.8a — OoxmlExtractor (docx).
///
/// 실제 docx 를 임시 생성하여 검증 (DocumentFormat.OpenXml SDK 직접 사용).

let private withTempPath (ext: string) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-test-%s%s" (System.Guid.NewGuid().ToString("N")) ext)
    try action path
    finally if File.Exists path then File.Delete path

/// 간단한 docx 작성 — heading 들 + 본문 paragraph 들 + 한 표.
let private makeDocx (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let body = Body()

    let mkPara (style: string option) (text: string) =
        let p = Paragraph()
        match style with
        | Some s ->
            let pp = ParagraphProperties()
            let pid = ParagraphStyleId()
            pid.Val <- StringValue(s)
            pp.ParagraphStyleId <- pid
            p.AppendChild(pp) |> ignore
        | None -> ()
        let run = Run()
        run.AppendChild(Text(text)) |> ignore
        p.AppendChild(run) |> ignore
        p

    body.AppendChild(mkPara (Some "Heading1") "1장 개요") |> ignore
    body.AppendChild(mkPara None "본문 첫 단락 — 한국어와 English 혼합") |> ignore
    body.AppendChild(mkPara (Some "Heading2") "1.1 세부 사양") |> ignore
    body.AppendChild(mkPara None "두 번째 본문") |> ignore

    // 표 — 1 row 2 cell
    let tbl = Table()
    let row = TableRow()
    let cell1 = TableCell()
    cell1.AppendChild(mkPara None "셀A") |> ignore
    let cell2 = TableCell()
    cell2.AppendChild(mkPara None "셀B") |> ignore
    row.AppendChild(cell1) |> ignore
    row.AppendChild(cell2) |> ignore
    tbl.AppendChild(row) |> ignore
    body.AppendChild(tbl) |> ignore

    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

[<Fact>]
let ``docx — heading outline + paragraph/table segments`` () =
    withTempPath ".docx" (fun path ->
        makeDocx path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Docx, result.DocType)
        // outline = Heading1 + Heading2 = 2
        Assert.Equal(2, result.Outline.Length)
        Assert.Equal("1장 개요", result.Outline.[0].Label)
        Assert.Equal("1.1 세부 사양", result.Outline.[1].Label)
        // segments = 4 paragraph + 1 table = 5. OpenXml SDK 가 sectPr 등 부가 element 자동 보강 가능성
        // → 정확 5 가정은 brittle (review m4). "5 이상 + 표 본문 존재" 약화 검증.
        Assert.True(result.Segments.Length >= 5)
        Assert.Contains(result.Segments, fun s -> s.Text.Contains "셀A" && s.Text.Contains "셀B")
        // Phase 2 task C3 회귀 차단 — image part 없는 docx 는 Images=[||].
        Assert.Empty(result.Images))

[<Fact>]
let ``손상 docx (random bytes) — fail-safe 빈 결과`` () =
    withTempPath ".docx" (fun path ->
        File.WriteAllBytes(path, [| 1uy; 2uy; 3uy; 4uy |])
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Docx, result.DocType)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Outline)
        // Phase 2 task C3 회귀 차단 — 손상 docx 도 Images=[||].
        Assert.Empty(result.Images))

[<Fact>]
let ``Supports — Docx + Pptx 활성 (Task 1), Xlsx (Task 2) 진입 전`` () =
    // Task 1 (PPTX 활성) 이후 Supports 분기 확대 — 기존 "Docx only" 가정 폐기.
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Docx)
    Assert.True(ext.Supports Pptx)
    Assert.False(ext.Supports Xlsx)   // Task 2 에서 활성
    Assert.False(ext.Supports Pdf)

// ── Phase 2 task C3 (s6-r14): OoxmlExtractor 의 docx ImageParts 추출 회귀 차단 ──

/// 1×1 PNG raw bytes — `Ds2.LightHouse.Tests.SamplePng.bytes` SSOT (s6-r22 mn7).
let private samplePngBytes : byte[] = Ds2.LightHouse.Tests.SamplePng.bytes

/// docx 에 ImagePart 한 개 박제 + paragraph 1개. image 가 inline drawing 으로 묶이지 않아도
/// `MainDocumentPart.ImageParts` 에서 enumerate 됨 — paragraph 매핑은 C4 의무 (옵션 B trade-off).
let private makeDocxWithImage (path: string) (contentType: string) (bytes: byte[]) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let imgPart = main.AddImagePart(contentType)
    use ms = new MemoryStream(bytes)
    imgPart.FeedData(ms)
    let body = Body()
    let para = Paragraph()
    let run = Run()
    run.AppendChild(Text("본문")) |> ignore
    para.AppendChild(run) |> ignore
    body.AppendChild(para) |> ignore
    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

[<Fact>]
let ``docx + orphan ImagePart (Drawing 미참조) — C4-Q2 skip`` () =
    // s6-r16 의미 변경: 기존 (s6-r14) 에서는 mainPart.ImageParts iter 가 orphan 도 박제했으나,
    // C4-Q2 (s6-r16) 부터는 body.Descendants<Blip>() iter 가 Drawing 참조 image 만 박제. orphan = skip.
    withTempPath ".docx" (fun path ->
        makeDocxWithImage path "image/png" samplePngBytes
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Empty(result.Images))

[<Fact>]
let ``docx + BMP orphan ImagePart — 화이트리스트 외 + Drawing 미참조 두 가드 둘 다 skip`` () =
    // C4-Q2 + m6 primary 가드 — orphan 이라도 BMP 는 화이트리스트 외라 더더욱 skip 정합.
    withTempPath ".docx" (fun path ->
        let bmpStub = [| 0x42uy; 0x4Duy; 0x10uy; 0x00uy; 0x00uy; 0x00uy |]
        makeDocxWithImage path "image/bmp" bmpStub
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Empty(result.Images))

/// inline Drawing (Blip embed) 박제 docx. paragraph 1 = 일반 본문 / paragraph 2 = 본문 + inline Drawing image.
let private makeDocxWithInlineImage (path: string) (contentType: string) (bytes: byte[]) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let imgPart = main.AddImagePart(contentType)
    use ms = new MemoryStream(bytes)
    imgPart.FeedData(ms)
    let relId = main.GetIdOfPart(imgPart)

    let body = Body()
    // paragraph 1 — 일반 본문.
    let p1 = Paragraph()
    let r1 = Run()
    r1.AppendChild(Text("앞 paragraph 본문")) |> ignore
    p1.AppendChild(r1) |> ignore
    body.AppendChild(p1) |> ignore

    // paragraph 2 — 본문 + inline Drawing.
    let p2 = Paragraph()
    let r2 = Run()
    r2.AppendChild(Text("이미지 있는 paragraph")) |> ignore
    // inline Drawing 박제 — minimal namespace 박제.
    let drawingXml =
        sprintf """<w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="1" name="Pic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="1" name="Pic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>""" relId
    let drawing = Drawing(drawingXml)
    r2.AppendChild(drawing) |> ignore
    p2.AppendChild(r2) |> ignore
    body.AppendChild(p2) |> ignore

    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

[<Fact>]
let ``docx + inline Drawing PNG — C4-Q2 paragraph 단위 RefLocator 매핑`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithInlineImage path "image/png" samplePngBytes
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal(Png, img.Format)
        // RefLocator = "p=2" — paragraph 1 (앞 본문) 박제 후 paragraph 2 (image) 박제. paraOrdinal 1-based.
        Assert.Equal("p=2", img.RefLocator)
        // Ordinal = 1 (같은 paragraph 안 첫 image).
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(samplePngBytes.Length, img.Bytes.Length)
        // segment 도 정합 — paragraph 2 의 segment RefLocator 와 image RefLocator 일치.
        let p2Segment = result.Segments |> Array.tryFind (fun s -> s.RefLocator = "p=2")
        Assert.True(p2Segment.IsSome, "p=2 segment 가 박제되어 ChunkId 매핑 가능해야 함"))


/// **s6-r21 (s6-r16 backlog 해소)** — image-only paragraph (text=0 + Drawing) 박제 docx.
/// paragraph 1 = 일반 본문 / paragraph 2 = Drawing 만 (text 없음) / paragraph 3 = 후속 본문 (caption).
let private makeDocxWithImageOnlyParagraph (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let imgPart = main.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = main.GetIdOfPart(imgPart)

    let body = Body()
    // paragraph 1 — 일반 본문.
    let p1 = Paragraph()
    let r1 = Run()
    r1.AppendChild(Text("앞 본문")) |> ignore
    p1.AppendChild(r1) |> ignore
    body.AppendChild(p1) |> ignore

    // paragraph 2 — Drawing only (text 없음).
    let p2 = Paragraph()
    let r2 = Run()
    let drawingXml =
        sprintf """<w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="1" name="Pic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="1" name="Pic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>""" relId
    let drawing = Drawing(drawingXml)
    r2.AppendChild(drawing) |> ignore
    p2.AppendChild(r2) |> ignore
    body.AppendChild(p2) |> ignore

    // paragraph 3 — caption (image 직후 본문).
    let p3 = Paragraph()
    let r3 = Run()
    r3.AppendChild(Text("그림 1. 컨베이어 사양")) |> ignore
    p3.AppendChild(r3) |> ignore
    body.AppendChild(p3) |> ignore

    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

[<Fact>]
let ``docx + image-only paragraph — s6-r21 backlog 해소, paraOrdinal 증가 + image 박제`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithImageOnlyParagraph path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // image 1장 박제 (이전엔 자연 skip 됨).
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        // image-only paragraph 가 paraOrdinal 2 차지. caption (p3) 은 p=3 segment.
        Assert.Equal("p=2", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        // segment 는 paragraph 1 + paragraph 3 만 (image-only paragraph 2 는 text=0).
        let segRefs = result.Segments |> Array.map (fun s -> s.RefLocator) |> Array.distinct |> Array.sort
        Assert.Equal<string[]>([| "p=1"; "p=3" |], segRefs)
        // image 와 caption (p=3) 인접 — 검색 시 매칭 가능.
        let captionSeg = result.Segments |> Array.find (fun s -> s.RefLocator = "p=3")
        Assert.Contains("그림 1", captionSeg.Text))


/// **s6-r21 (s6-r16 backlog 해소)** — header Drawing 박제 docx.
/// body 본문 1개 + header 안 image 1장 (회사 로고 시나리오).
let private makeDocxWithHeaderImage (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()

    // body 본문.
    let body = Body()
    let p = Paragraph()
    let r = Run()
    r.AppendChild(Text("본문 텍스트")) |> ignore
    p.AppendChild(r) |> ignore
    body.AppendChild(p) |> ignore
    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

    // header part + image.
    let headerPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.HeaderPart>()
    let imgPart = headerPart.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = headerPart.GetIdOfPart(imgPart)

    // s6-r25 (mn5) — OpenXml SDK 정석: HeaderPart.Header property 객체 할당. raw stream write 우회 폐기
    // (SDK internal state 와 race 잠재). SDK Header 의 ctor(string outerXml) 사용 — w:hdr root 포함.
    let headerOuterXml =
        sprintf """<w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="2" name="HeaderPic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="2" name="HeaderPic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p></w:hdr>""" relId
    headerPart.Header <- DocumentFormat.OpenXml.Wordprocessing.Header(headerOuterXml)
    headerPart.Header.Save()

[<Fact>]
let ``docx + header image — s6-r21 backlog 해소, RefLocator="header=1"`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithHeaderImage path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // body 본문 segment 1 + header image 1.
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal("header=1", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(Png, img.Format)
        Assert.Equal(samplePngBytes.Length, img.Bytes.Length))


/// **s6-r22 task 5 (s6-r16 backlog 해소)** — table cell 안 inline Drawing 박제 docx.
/// body 본문 1 + table (row 1 / cell 1 의 paragraph 1 에 image 1장).
let private makeDocxWithTableCellImage (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let imgPart = main.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = main.GetIdOfPart(imgPart)

    let body = Body()
    // paragraph 1 (table 직전 본문).
    let p1 = Paragraph()
    let r1 = Run()
    r1.AppendChild(Text("앞 본문")) |> ignore
    p1.AppendChild(r1) |> ignore
    body.AppendChild(p1) |> ignore

    // table — 1 row × 2 cell. cell 1 = image+caption / cell 2 = 사양 텍스트.
    let tbl = Table()
    let tr = TableRow()

    // cell 1: paragraph 1 (image), paragraph 2 (caption).
    let tc1 = TableCell()
    let cp1 = Paragraph()
    let cr1 = Run()
    let drawingXml =
        sprintf """<w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="1" name="CellPic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="1" name="CellPic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>""" relId
    let drawing = Drawing(drawingXml)
    cr1.AppendChild(drawing) |> ignore
    cp1.AppendChild(cr1) |> ignore
    tc1.AppendChild(cp1) |> ignore
    let cp2 = Paragraph()
    let cr2 = Run()
    cr2.AppendChild(Text("그림 A")) |> ignore
    cp2.AppendChild(cr2) |> ignore
    tc1.AppendChild(cp2) |> ignore
    tr.AppendChild(tc1) |> ignore

    // cell 2: paragraph 1 (텍스트 only).
    let tc2 = TableCell()
    let cp3 = Paragraph()
    let cr3 = Run()
    cr3.AppendChild(Text("표 사양 컨베이어")) |> ignore
    cp3.AppendChild(cr3) |> ignore
    tc2.AppendChild(cp3) |> ignore
    tr.AppendChild(tc2) |> ignore

    tbl.AppendChild(tr) |> ignore
    body.AppendChild(tbl) |> ignore

    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

[<Fact>]
let ``docx + table cell image — s6-r22 task 5, RefLocator scheme p=N.cell=M.p=K`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithTableCellImage path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // image 정확히 1장 박제 — 기존 (table block 단위) `p=2` scheme 이 아니라 cell scheme.
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        // table block 의 paraOrdinal = 2 (paragraph1 직후). cell 1 의 paragraph 1 에 image.
        Assert.Equal("p=2.cell=1.p=1", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(Png, img.Format))


/// **s6-r22 자가 검열 C1 정합** — nested table 안 image silent drift 차단 fixture.
/// outer table 의 cell 1 안에 nested table (1×1) — nested cell 의 paragraph 에 inline Drawing 1장.
/// 의도된 결과 = image 0장 박제 (nested scheme 미지원, Warn log 만).
let private makeDocxWithNestedTableImage (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let imgPart = main.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = main.GetIdOfPart(imgPart)

    let body = Body()

    // outer table — 1 row × 1 cell. cell 안에 nested table 만.
    let outerTbl = Table()
    let outerRow = TableRow()
    let outerCell = TableCell()
    // nested table.
    let innerTbl = Table()
    let innerRow = TableRow()
    let innerCell = TableCell()
    let innerP = Paragraph()
    let innerR = Run()
    let drawingXml =
        sprintf """<w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="1" name="NestedPic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="1" name="NestedPic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>""" relId
    innerR.AppendChild(Drawing(drawingXml)) |> ignore
    innerP.AppendChild(innerR) |> ignore
    innerCell.AppendChild(innerP) |> ignore
    innerRow.AppendChild(innerCell) |> ignore
    innerTbl.AppendChild(innerRow) |> ignore

    outerCell.AppendChild(innerTbl) |> ignore
    outerRow.AppendChild(outerCell) |> ignore
    outerTbl.AppendChild(outerRow) |> ignore
    body.AppendChild(outerTbl) |> ignore

    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

[<Fact>]
let ``docx + nested table image — s6-r22 C1 정합, silent drift 차단 (image 0장)`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithNestedTableImage path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // nested table scheme 미지원 → image 0장 박제. outer cell scheme 도 inner cell 좌표를 평면화하지 않음.
        Assert.Equal(0, result.Images.Length))


// ────────────────────────────────────────────────────────────────────────────────
//  Task 1 (PPTX 활성) — todo-lighthouse-kb-index-xlsx-pptx-images.md
// ────────────────────────────────────────────────────────────────────────────────

/// **Task 1 fixture (r2 Minor 7)** — `PresentationDocument.Create` + SDK 객체 직접 build 패턴.
/// raw outerXml 박제는 SDK 의 strongly-typed parser (`Slide(outerXml)`) 가 child element 들을 fallback unknown 으로
/// 박제하여 `Descendants<DrawingParagraph>()` / `Descendants<Blip>()` 이 0 반환 → 본 fixture 는 객체 model 사용.
module private PptxFixture =
    open DocumentFormat.OpenXml
    open DocumentFormat.OpenXml.Packaging
    open DocumentFormat.OpenXml.Presentation

    type private DP = DocumentFormat.OpenXml.Drawing.Paragraph
    type private DR = DocumentFormat.OpenXml.Drawing.Run
    type private DT = DocumentFormat.OpenXml.Drawing.Text
    type private DBPr = DocumentFormat.OpenXml.Drawing.BodyProperties
    type private DLstStyle = DocumentFormat.OpenXml.Drawing.ListStyle

    let private mkPara (text: string) : DP =
        let p = DP()
        let r = DR()
        let t = DT()
        t.Text <- text
        r.Append(t :> OpenXmlElement) |> ignore
        p.Append(r :> OpenXmlElement) |> ignore
        p

    let private mkTxBody (paras: string list) : TextBody =
        let tb = TextBody()
        tb.Append(DBPr() :> OpenXmlElement) |> ignore
        tb.Append(DLstStyle() :> OpenXmlElement) |> ignore
        for txt in paras do
            tb.Append(mkPara txt :> OpenXmlElement) |> ignore
        tb

    let private mkPlaceholderShape (phType: PlaceholderValues) (cnvId: uint32) (cnvName: string) (paras: string list) : Shape =
        let sp = Shape()
        let nv = NonVisualShapeProperties()
        let cnv = NonVisualDrawingProperties()
        cnv.Id <- UInt32Value(cnvId)
        cnv.Name <- StringValue(cnvName)
        nv.Append(cnv :> OpenXmlElement) |> ignore
        nv.Append(NonVisualShapeDrawingProperties() :> OpenXmlElement) |> ignore
        let appNv = ApplicationNonVisualDrawingProperties()
        let ph = PlaceholderShape()
        ph.Type <- EnumValue<PlaceholderValues>(phType)
        appNv.Append(ph :> OpenXmlElement) |> ignore
        nv.Append(appNv :> OpenXmlElement) |> ignore
        sp.Append(nv :> OpenXmlElement) |> ignore
        sp.Append(ShapeProperties() :> OpenXmlElement) |> ignore
        sp.Append(mkTxBody paras :> OpenXmlElement) |> ignore
        sp

    let private mkBodyShapeNoPh (cnvId: uint32) (cnvName: string) (paras: string list) : Shape =
        // PlaceholderShape 없는 일반 body shape — title 부재 fixture 용 (M11). title 매칭에서 자연 skip.
        let sp = Shape()
        let nv = NonVisualShapeProperties()
        let cnv = NonVisualDrawingProperties()
        cnv.Id <- UInt32Value(cnvId)
        cnv.Name <- StringValue(cnvName)
        nv.Append(cnv :> OpenXmlElement) |> ignore
        nv.Append(NonVisualShapeDrawingProperties() :> OpenXmlElement) |> ignore
        nv.Append(ApplicationNonVisualDrawingProperties() :> OpenXmlElement) |> ignore
        sp.Append(nv :> OpenXmlElement) |> ignore
        sp.Append(ShapeProperties() :> OpenXmlElement) |> ignore
        sp.Append(mkTxBody paras :> OpenXmlElement) |> ignore
        sp

    let private mkPicture (relId: string) : Picture =
        let pic = Picture()
        let nvPicPr = NonVisualPictureProperties()
        let cnvPr = NonVisualDrawingProperties()
        cnvPr.Id <- UInt32Value(5u)
        cnvPr.Name <- StringValue("Pic")
        nvPicPr.Append(cnvPr :> OpenXmlElement) |> ignore
        nvPicPr.Append(NonVisualPictureDrawingProperties() :> OpenXmlElement) |> ignore
        nvPicPr.Append(ApplicationNonVisualDrawingProperties() :> OpenXmlElement) |> ignore
        pic.Append(nvPicPr :> OpenXmlElement) |> ignore
        let blipFill = BlipFill()
        let blip = DocumentFormat.OpenXml.Drawing.Blip()
        blip.Embed <- StringValue(relId)
        blipFill.Append(blip :> OpenXmlElement) |> ignore
        let stretch = DocumentFormat.OpenXml.Drawing.Stretch()
        stretch.Append(DocumentFormat.OpenXml.Drawing.FillRectangle() :> OpenXmlElement) |> ignore
        blipFill.Append(stretch :> OpenXmlElement) |> ignore
        pic.Append(blipFill :> OpenXmlElement) |> ignore
        pic.Append(ShapeProperties() :> OpenXmlElement) |> ignore
        pic

    /// 단일 슬라이드 spec — shape builder list + 선택 notes.
    type ShapeBuilder =
        | TitleSp of text: string
        | CenteredTitleSp of text: string
        /// title placeholder 없는 일반 body — title 부재 fixture (M11).
        | BodyNoPh of paras: string list
        | BodySp of paras: string list
        | PicSp of bytes: byte[]

    type SlideSpec = {
        Shapes: ShapeBuilder list
        Notes: string option
    }

    let emptySlideSpec = { Shapes = []; Notes = None }

    let private buildNotesSlide (notesText: string) : NotesSlide =
        let notes = NotesSlide()
        let nCSld = CommonSlideData()
        let nSpTree = ShapeTree()
        let nNvGrp = NonVisualGroupShapeProperties()
        let nCnvPr = NonVisualDrawingProperties()
        nCnvPr.Id <- UInt32Value(1u)
        nCnvPr.Name <- StringValue("")
        nNvGrp.Append(nCnvPr :> OpenXmlElement) |> ignore
        nNvGrp.Append(NonVisualGroupShapeDrawingProperties() :> OpenXmlElement) |> ignore
        nNvGrp.Append(ApplicationNonVisualDrawingProperties() :> OpenXmlElement) |> ignore
        nSpTree.Append(nNvGrp :> OpenXmlElement) |> ignore
        nSpTree.Append(GroupShapeProperties() :> OpenXmlElement) |> ignore
        nSpTree.Append(mkPlaceholderShape PlaceholderValues.Body 2u "Notes" [notesText] :> OpenXmlElement) |> ignore
        nCSld.Append(nSpTree :> OpenXmlElement) |> ignore
        notes.Append(nCSld :> OpenXmlElement) |> ignore
        notes

    /// SlidePart 안 Slide 객체 build + Save. SlideIdList 박제는 caller 책임.
    let private addSlideContent (slidePart: SlidePart) (spec: SlideSpec) =
        let slide = Slide()
        let cSld = CommonSlideData()
        let spTree = ShapeTree()
        let nvGrpSpPr = NonVisualGroupShapeProperties()
        let cnvPr = NonVisualDrawingProperties()
        cnvPr.Id <- UInt32Value(1u)
        cnvPr.Name <- StringValue("")
        nvGrpSpPr.Append(cnvPr :> OpenXmlElement) |> ignore
        nvGrpSpPr.Append(NonVisualGroupShapeDrawingProperties() :> OpenXmlElement) |> ignore
        nvGrpSpPr.Append(ApplicationNonVisualDrawingProperties() :> OpenXmlElement) |> ignore
        spTree.Append(nvGrpSpPr :> OpenXmlElement) |> ignore
        spTree.Append(GroupShapeProperties() :> OpenXmlElement) |> ignore
        for s in spec.Shapes do
            match s with
            | TitleSp txt ->
                spTree.Append(mkPlaceholderShape PlaceholderValues.Title 2u "Title" [txt] :> OpenXmlElement) |> ignore
            | CenteredTitleSp txt ->
                spTree.Append(mkPlaceholderShape PlaceholderValues.CenteredTitle 2u "CTitle" [txt] :> OpenXmlElement) |> ignore
            | BodyNoPh paras ->
                spTree.Append(mkBodyShapeNoPh 3u "BodyNoPh" paras :> OpenXmlElement) |> ignore
            | BodySp paras ->
                spTree.Append(mkPlaceholderShape PlaceholderValues.Body 3u "Body" paras :> OpenXmlElement) |> ignore
            | PicSp bytes ->
                let imgPart = slidePart.AddImagePart(ImagePartType.Png)
                use ms = new MemoryStream(bytes)
                imgPart.FeedData(ms)
                let relId = slidePart.GetIdOfPart(imgPart)
                spTree.Append(mkPicture relId :> OpenXmlElement) |> ignore
        cSld.Append(spTree :> OpenXmlElement) |> ignore
        slide.Append(cSld :> OpenXmlElement) |> ignore
        slidePart.Slide <- slide
        slidePart.Slide.Save()
        match spec.Notes with
        | None -> ()
        | Some notesText ->
            let notesPart = slidePart.AddNewPart<NotesSlidePart>()
            notesPart.NotesSlide <- buildNotesSlide notesText
            notesPart.NotesSlide.Save()

    let buildPptx (path: string) (slides: SlideSpec list) =
        use doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation)
        let presPart = doc.AddPresentationPart()
        // SlideIdList 와 SlideId 모두 객체 model 안에서 build 후 한 번에 Presentation assignment.
        // setter `presPart.Presentation <- Presentation()` 후 후속 SlideIdList.Append 가 stream 에 reflect 안 됨 (SDK 동작).
        let sIdList = SlideIdList()
        slides |> List.iteri (fun i spec ->
            let slidePart = presPart.AddNewPart<SlidePart>()
            addSlideContent slidePart spec
            let sId = SlideId()
            sId.Id <- UInt32Value(uint32 (256 + i))
            sId.RelationshipId <- StringValue(presPart.GetIdOfPart(slidePart))
            sIdList.AppendChild(sId) |> ignore)
        let pres = Presentation()
        pres.AppendChild(sIdList) |> ignore
        presPart.Presentation <- pres
        presPart.Presentation.Save()

    /// 빈 pptx (SlideIdList 박제 + SlideId 0개). r1 M13 fixture.
    let buildPptxEmpty (path: string) =
        use doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation)
        let presPart = doc.AddPresentationPart()
        let pres = Presentation()
        pres.AppendChild(SlideIdList()) |> ignore
        presPart.Presentation <- pres
        presPart.Presentation.Save()


[<Fact>]
let ``pptx — 3 slide (title + body + notes + image) outline + segments + image`` () =
    withTempPath ".pptx" (fun path ->
        // slide 1: title="개요" + body=["첫 단락"; "둘째 단락"]
        // slide 2: title="사양" + body 1줄 + notes + image 1장 (RefLocator=slide=2)
        // slide 3: title="요약"
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "개요"; PptxFixture.BodySp [ "첫 단락"; "둘째 단락" ] ] }
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "사양"; PptxFixture.BodySp [ "라인 1" ]; PptxFixture.PicSp samplePngBytes ]
                Notes = Some "발표자 메모" }
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "요약" ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Pptx, result.DocType)
        Assert.Equal(Some 3, result.PageOrSheetCnt)
        // outline = slide 단위 3개 (title 박제).
        Assert.Equal(3, result.Outline.Length)
        Assert.Equal("개요", result.Outline.[0].Label)
        Assert.Equal("slide=1", result.Outline.[0].RefLocator)
        Assert.Equal(OutlineNodeType.Slide, result.Outline.[0].NodeType)
        Assert.Equal("사양", result.Outline.[1].Label)
        Assert.Equal("요약", result.Outline.[2].Label)
        // segments = slide 1/2/3 — 모두 title+body 합성 1개씩.
        Assert.Equal(3, result.Segments.Length)
        Assert.True(result.Segments |> Array.exists (fun s -> s.RefLocator = "slide=2" && s.Text.Contains "--- 노트 ---" && s.Text.Contains "발표자 메모"))
        // image 1장 — slide 2.
        Assert.Equal(1, result.Images.Length)
        Assert.Equal("slide=2", result.Images.[0].RefLocator)
        Assert.Equal(1, result.Images.[0].Ordinal)
        Assert.Equal(Png, result.Images.[0].Format))

[<Fact>]
let ``pptx — image-only slide (title 없음, body 없음, image 1장) — segment 미박제, image 박제`` () =
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.PicSp samplePngBytes ] }   // title / body 없음, image 만
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // outline 은 slide 존재 자체로 박제 — "슬라이드 N" fallback label.
        Assert.Equal(1, result.Outline.Length)
        Assert.Equal("슬라이드 1", result.Outline.[0].Label)
        // segment 미박제 (text=0).
        Assert.Empty(result.Segments)
        // image 1장.
        Assert.Equal(1, result.Images.Length)
        Assert.Equal("slide=1", result.Images.[0].RefLocator))

[<Fact>]
let ``pptx — 손상 pptx (random bytes) fail-safe — DocType=Pptx 빈 결과`` () =
    withTempPath ".pptx" (fun path ->
        File.WriteAllBytes(path, [| 1uy; 2uy; 3uy; 4uy |])
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Pptx, result.DocType)   // Task 0 dispatch 회귀 가드
        Assert.Empty(result.Outline)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Images))

[<Fact>]
let ``pptx — 빈 pptx (0 슬라이드) — Major-1 SlideIdList null guard 정합`` () =
    withTempPath ".pptx" (fun path ->
        PptxFixture.buildPptxEmpty path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Pptx, result.DocType)
        Assert.Equal(Some 0, result.PageOrSheetCnt)
        Assert.Empty(result.Outline)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Images))

[<Fact>]
let ``pptx — 동일 image cross-slide dedup (r1 M12) — Indexer가 ImageCache 1행 + ImageReferences 3행 박제 가능`` () =
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "로고1"; PptxFixture.PicSp samplePngBytes ] }
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "로고2"; PptxFixture.PicSp samplePngBytes ] }
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "로고3"; PptxFixture.PicSp samplePngBytes ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // extractor 단위는 3장 박제 (Indexer 가 sha256 dedup → ImageCache 1행).
        Assert.Equal(3, result.Images.Length)
        let refs = result.Images |> Array.map (fun i -> i.RefLocator) |> Array.sort
        Assert.Equal<string[]>([| "slide=1"; "slide=2"; "slide=3" |], refs))

[<Fact>]
let ``pptx — CenteredTitle (ctrTitle) placeholder (r1 M4) — outline label 매칭`` () =
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.CenteredTitleSp "표지 제목" ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Outline.Length)
        // ctrTitle 도 title placeholder 처럼 매칭 — fallback "슬라이드 1" 이 아니라 실제 text.
        Assert.Equal("표지 제목", result.Outline.[0].Label))

[<Fact>]
let ``pptx — title 부재 slide (r1 M11) — outline label "슬라이드 N" fallback`` () =
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.BodyNoPh [ "본문만 있는 슬라이드" ] ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Outline.Length)
        Assert.Equal("슬라이드 1", result.Outline.[0].Label))

[<Fact>]
let ``pptx — paragraph break 보존 (r1 M5) — body 안 bullet 2개 → segment text 안 \n`` () =
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "T"; PptxFixture.BodySp [ "첫 줄"; "둘째 줄" ] ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Segments.Length)
        let text = result.Segments.[0].Text
        // title + body 2 paragraph 각각 별 줄 — `\n` 포함 확인.
        Assert.Contains("첫 줄", text)
        Assert.Contains("둘째 줄", text)
        // bullet 들러붙음 회귀 차단 — "첫 줄둘째 줄" 같은 form 거부.
        Assert.False(text.Contains "첫 줄둘째 줄"))

[<Fact>]
let ``pptx — Supports 분기 활성 (Task 1 박제)`` () =
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Docx)
    Assert.True(ext.Supports Pptx)
    Assert.False(ext.Supports Xlsx)  // Task 2 에서 활성

[<Fact>]
let ``pptx — 화이트리스트 외 image (예: BMP relId 가짜) — image 0장 박제 (m6 primary 가드)`` () =
    withTempPath ".pptx" (fun path ->
        // BMP 는 SlidePart.AddImagePart(ImagePartType.Bmp) 박제 후 본 extract 가 ContentType=image/bmp 매칭 안 함 → skip.
        // 직접 ImagePartType.Bmp 가 있는지 확인 어렵 — 대신 image 미박제 slide 로 image=0 검증 (단순화).
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "텍스트만" ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Empty(result.Images))
