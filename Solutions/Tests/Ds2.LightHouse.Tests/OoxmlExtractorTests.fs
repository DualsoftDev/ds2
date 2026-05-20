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
let ``Supports — Docx only (Pptx/Xlsx Phase 2)`` () =
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Docx)
    Assert.False(ext.Supports Pptx)
    Assert.False(ext.Supports Xlsx)
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


/// **s6-r79 B2 (external review backlog)** — comments part 안 inline Drawing 박제 docx.
/// body 본문 1 + WordprocessingCommentsPart 안 image 1장 (도해/보충 시나리오).
let private makeDocxWithCommentImage (path: string) =
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

    // comments part + image.
    let commentsPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.WordprocessingCommentsPart>()
    let imgPart = commentsPart.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = commentsPart.GetIdOfPart(imgPart)

    // OpenXml SDK 정석 (s6-r25 mn5 패턴 동일) — Comments ctor(string outerXml) + Save.
    let commentsOuterXml =
        sprintf """<w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:comment w:id="0" w:author="kwak" w:date="2026-05-21T00:00:00Z" w:initials="K"><w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="3" name="CommentPic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="3" name="CommentPic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p></w:comment></w:comments>""" relId
    commentsPart.Comments <- DocumentFormat.OpenXml.Wordprocessing.Comments(commentsOuterXml)
    commentsPart.Comments.Save()

[<Fact>]
let ``docx + comments image — s6-r79 B2, RefLocator="comments"`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithCommentImage path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal("comments", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(Png, img.Format)
        Assert.Equal(samplePngBytes.Length, img.Bytes.Length))
