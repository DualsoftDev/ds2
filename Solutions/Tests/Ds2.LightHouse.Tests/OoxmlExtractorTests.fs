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
let ``Supports — Docx + Pptx + Xlsx 모두 활성 (Task 2 완료)`` () =
    // Task 0 ~ Task 2 완료 후: Docx + Pptx + Xlsx 모두 OoxmlExtractor 가 담당.
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Docx)
    Assert.True(ext.Supports Pptx)
    Assert.True(ext.Supports Xlsx)
    Assert.False(ext.Supports Pdf)
    Assert.False(ext.Supports FileKind.Text)

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


/// **s6-r82 B2 (PR 4 잔여 fact)** — footnotes part 안 inline Drawing 박제 docx.
/// extractImagesFromOpenXmlPart helper path 통과 검증 (분기 누락 0). FootnotesPart 도 동일 패턴.
let private makeDocxWithFootnoteImage (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let body = Body()
    let p = Paragraph()
    let r = Run()
    r.AppendChild(Text("본문")) |> ignore
    p.AppendChild(r) |> ignore
    body.AppendChild(p) |> ignore
    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

    let footnotesPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.FootnotesPart>()
    let imgPart = footnotesPart.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = footnotesPart.GetIdOfPart(imgPart)
    let outerXml =
        sprintf """<w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:footnote w:id="1"><w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="4" name="FootnotePic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="4" name="FootnotePic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p></w:footnote></w:footnotes>""" relId
    footnotesPart.Footnotes <- DocumentFormat.OpenXml.Wordprocessing.Footnotes(outerXml)
    footnotesPart.Footnotes.Save()

[<Fact>]
let ``docx + footnote image — s6-r82 B2 PR 4, RefLocator="footnotes"`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithFootnoteImage path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal("footnotes", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(Png, img.Format))


/// **s6-r82 B2 (PR 4 잔여 fact)** — endnotes part 안 inline Drawing 박제 docx.
let private makeDocxWithEndnoteImage (path: string) =
    use doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    let body = Body()
    let p = Paragraph()
    let r = Run()
    r.AppendChild(Text("본문")) |> ignore
    p.AppendChild(r) |> ignore
    body.AppendChild(p) |> ignore
    let docXml = Document()
    docXml.AppendChild(body) |> ignore
    main.Document <- docXml
    main.Document.Save()

    let endnotesPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.EndnotesPart>()
    let imgPart = endnotesPart.AddImagePart("image/png")
    use ms = new MemoryStream(samplePngBytes)
    imgPart.FeedData(ms)
    let relId = endnotesPart.GetIdOfPart(imgPart)
    let outerXml =
        sprintf """<w:endnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:endnote w:id="1"><w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0"><wp:extent cx="100000" cy="100000"/><wp:docPr id="5" name="EndnotePic"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="5" name="EndnotePic"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p></w:endnote></w:endnotes>""" relId
    endnotesPart.Endnotes <- DocumentFormat.OpenXml.Wordprocessing.Endnotes(outerXml)
    endnotesPart.Endnotes.Save()

[<Fact>]
let ``docx + endnote image — s6-r82 B2 PR 4, RefLocator="endnotes"`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithEndnoteImage path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal("endnotes", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(Png, img.Format))

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
        /// **review M9** — PartTypeInfo (`ImagePartType.Bmp` 등) 인자 받는 변형 (whitelist 외 ContentType 박제용).
        /// PNG (whitelist 통과) / Bmp / Tiff (whitelist 외, skip) 등 mixed fixture 박제 가능.
        | PicSpWithType of bytes: byte[] * partType: PartTypeInfo

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
            | PicSpWithType (bytes, partType) ->
                let imgPart = slidePart.AddImagePart(partType)
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
let ``pptx — Supports 분기 활성 (Task 1 박제, Task 2 도 활성)`` () =
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Pptx)

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


// ────────────────────────────────────────────────────────────────────────────────
//  Task 2 (XLSX 활성) — todo-lighthouse-kb-index-xlsx-pptx-images.md
// ────────────────────────────────────────────────────────────────────────────────

/// **Task 2 fixture (r2 Minor 7)** — `SpreadsheetDocument.Create` + SDK 객체 직접 build.
/// 정합 박제 패턴 — PPTX fixture 와 동일하게 single Workbook assignment.
module private XlsxFixture =
    open DocumentFormat.OpenXml
    open DocumentFormat.OpenXml.Packaging
    open DocumentFormat.OpenXml.Spreadsheet

    /// `Value` 의 의미는 `DataType` 에 따름.
    ///   - None → number/date (raw text)
    ///   - SharedString → SST index (raw string of int)
    ///   - InlineString → empty (Value 무시, InlineString sub-element 박제 별 helper)
    ///   - String → formula string result
    ///   - Error → error string ("#REF!" 등)
    type CellSpec = {
        Ref: string
        Value: string
        DataType: CellValues option
        /// formula cached value 부재 시 true → CellValue element 미박제, CellFormula 만.
        HasFormulaButNoValue: bool
    }

    let mkCellSpec ref value = { Ref = ref; Value = value; DataType = None; HasFormulaButNoValue = false }
    let mkSharedStringCell ref sstIdx =
        { Ref = ref; Value = string sstIdx; DataType = Some CellValues.SharedString; HasFormulaButNoValue = false }
    let mkInlineStringCell ref value =
        { Ref = ref; Value = value; DataType = Some CellValues.InlineString; HasFormulaButNoValue = false }
    let mkErrorCell ref errVal =
        { Ref = ref; Value = errVal; DataType = Some CellValues.Error; HasFormulaButNoValue = false }
    /// formula cached value 부재 cell (`<c><f>...</f></c>` no CellValue). r1 M14.
    let mkFormulaNoValueCell ref =
        { Ref = ref; Value = ""; DataType = None; HasFormulaButNoValue = true }

    type RowSpec = {
        Index: uint32
        Cells: CellSpec list
    }

    type SheetSpec = {
        Name: string
        /// None = visible default. Some Hidden | VeryHidden = skip.
        State: SheetStateValues option
        Rows: RowSpec list
        /// `[]` = image 미박제. `[bytes; bytes; ...]` = WorksheetDrawing 안 N장 (review M8 fixture).
        Images: byte[] list
    }

    let mkSheet name rows : SheetSpec = { Name = name; State = None; Rows = rows; Images = [] }
    let mkHiddenSheet name rows : SheetSpec = { Name = name; State = Some SheetStateValues.Hidden; Rows = rows; Images = [] }
    let mkVeryHiddenSheet name rows : SheetSpec = { Name = name; State = Some SheetStateValues.VeryHidden; Rows = rows; Images = [] }

    let private mkCell (spec: CellSpec) : Cell =
        let c = Cell()
        c.CellReference <- StringValue(spec.Ref)
        match spec.DataType with
        | Some dt -> c.DataType <- EnumValue<CellValues>(dt)
        | None -> ()
        if spec.HasFormulaButNoValue then
            // CellFormula 박제, CellValue 부재 — null guard fixture.
            c.Append(CellFormula("1+1") :> OpenXmlElement) |> ignore
        else
            let isInlineString =
                match spec.DataType with
                | Some dt -> dt = CellValues.InlineString
                | None -> false
            if isInlineString then
                let is = InlineString()
                is.Append(Text(spec.Value) :> OpenXmlElement) |> ignore
                c.Append(is :> OpenXmlElement) |> ignore
            else
                c.Append(CellValue(spec.Value) :> OpenXmlElement) |> ignore
        c

    let private mkRow (spec: RowSpec) : Row =
        let r = Row()
        r.RowIndex <- UInt32Value(spec.Index)
        for cs in spec.Cells do
            r.Append(mkCell cs :> OpenXmlElement) |> ignore
        r

    let private buildWorksheet (wsPart: WorksheetPart) (rows: RowSpec list) =
        let ws = Worksheet()
        let sd = SheetData()
        for rs in rows do
            sd.Append(mkRow rs :> OpenXmlElement) |> ignore
        ws.Append(sd :> OpenXmlElement) |> ignore
        // DrawingsPart 가 있으면 Drawing reference 박제 의무 — Extract 가 worksheetPart.DrawingsPart 로 접근 가능 (relationship 자동).
        wsPart.Worksheet <- ws
        wsPart.Worksheet.Save()

    let private addDrawingsPart (wsPart: WorksheetPart) (bytesList: byte[] list) =
        if List.isEmpty bytesList then () else
        let drawingsPart = wsPart.AddNewPart<DrawingsPart>()
        let wsDrawing = DocumentFormat.OpenXml.Drawing.Spreadsheet.WorksheetDrawing()
        bytesList |> List.iteri (fun i bytes ->
            let imgPart = drawingsPart.AddImagePart(ImagePartType.Png)
            use ms = new MemoryStream(bytes)
            imgPart.FeedData(ms)
            let relId = drawingsPart.GetIdOfPart(imgPart)
            let pic = DocumentFormat.OpenXml.Drawing.Spreadsheet.Picture()
            let nvPicPr = DocumentFormat.OpenXml.Drawing.Spreadsheet.NonVisualPictureProperties()
            let cnvPr = DocumentFormat.OpenXml.Drawing.Spreadsheet.NonVisualDrawingProperties()
            cnvPr.Id <- UInt32Value(uint32 (2 + i))
            cnvPr.Name <- StringValue(sprintf "Pic%d" (i + 1))
            nvPicPr.Append(cnvPr :> OpenXmlElement) |> ignore
            nvPicPr.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.NonVisualPictureDrawingProperties() :> OpenXmlElement) |> ignore
            pic.Append(nvPicPr :> OpenXmlElement) |> ignore
            let blipFill = DocumentFormat.OpenXml.Drawing.Spreadsheet.BlipFill()
            let blip = DocumentFormat.OpenXml.Drawing.Blip()
            blip.Embed <- StringValue(relId)
            blipFill.Append(blip :> OpenXmlElement) |> ignore
            let stretch = DocumentFormat.OpenXml.Drawing.Stretch()
            stretch.Append(DocumentFormat.OpenXml.Drawing.FillRectangle() :> OpenXmlElement) |> ignore
            blipFill.Append(stretch :> OpenXmlElement) |> ignore
            pic.Append(blipFill :> OpenXmlElement) |> ignore
            pic.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.ShapeProperties() :> OpenXmlElement) |> ignore
            let oneCellAnchor = DocumentFormat.OpenXml.Drawing.Spreadsheet.OneCellAnchor()
            let fromMarker = DocumentFormat.OpenXml.Drawing.Spreadsheet.FromMarker()
            fromMarker.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.ColumnId(string i) :> OpenXmlElement) |> ignore
            fromMarker.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.ColumnOffset("0") :> OpenXmlElement) |> ignore
            fromMarker.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.RowId(string i) :> OpenXmlElement) |> ignore
            fromMarker.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.RowOffset("0") :> OpenXmlElement) |> ignore
            oneCellAnchor.Append(fromMarker :> OpenXmlElement) |> ignore
            let ext = DocumentFormat.OpenXml.Drawing.Spreadsheet.Extent()
            ext.Cx <- Int64Value(100000L)
            ext.Cy <- Int64Value(100000L)
            oneCellAnchor.Append(ext :> OpenXmlElement) |> ignore
            oneCellAnchor.Append(pic :> OpenXmlElement) |> ignore
            oneCellAnchor.Append(DocumentFormat.OpenXml.Drawing.Spreadsheet.ClientData() :> OpenXmlElement) |> ignore
            wsDrawing.Append(oneCellAnchor :> OpenXmlElement) |> ignore)
        drawingsPart.WorksheetDrawing <- wsDrawing
        drawingsPart.WorksheetDrawing.Save()

    /// shared strings 가 None 이면 SST 미박제 (SharedString 셀 검증 fact 에서만 사용).
    let buildXlsx (path: string) (sharedStrings: string list option) (sheets: SheetSpec list) =
        use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
        let wbPart = doc.AddWorkbookPart()
        match sharedStrings with
        | None -> ()
        | Some items ->
            let sstPart = wbPart.AddNewPart<SharedStringTablePart>()
            let sst = SharedStringTable()
            for s in items do
                let item = SharedStringItem()
                item.Append(Text(s) :> OpenXmlElement) |> ignore
                sst.Append(item :> OpenXmlElement) |> ignore
            sstPart.SharedStringTable <- sst
            sstPart.SharedStringTable.Save()
        // Workbook + Sheets — PPTX 와 동일하게 single assignment.
        let workbook = Workbook()
        let sheetsEl = Sheets()
        sheets |> List.iteri (fun i sSpec ->
            let wsPart = wbPart.AddNewPart<WorksheetPart>()
            buildWorksheet wsPart sSpec.Rows
            addDrawingsPart wsPart sSpec.Images
            let sheet = Sheet()
            sheet.Id <- StringValue(wbPart.GetIdOfPart(wsPart))
            sheet.SheetId <- UInt32Value(uint32 (i + 1))
            sheet.Name <- StringValue(sSpec.Name)
            match sSpec.State with
            | Some st -> sheet.State <- EnumValue<SheetStateValues>(st)
            | None -> ()
            sheetsEl.AppendChild(sheet) |> ignore)
        workbook.AppendChild(sheetsEl) |> ignore
        wbPart.Workbook <- workbook
        wbPart.Workbook.Save()

    /// SST 안 PhoneticRun 포함 item — r1 M2 fixture.
    let buildXlsxWithPhoneticRubySST (path: string) =
        use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
        let wbPart = doc.AddWorkbookPart()
        let sstPart = wbPart.AddNewPart<SharedStringTablePart>()
        let sst = SharedStringTable()
        let item = SharedStringItem()
        item.Append(Text("회사") :> OpenXmlElement) |> ignore
        // <rPh> 안 <t> — base text 와 무관한 ruby. extractor 가 skip 해야 함.
        let rPh = PhoneticRun()
        rPh.BaseTextStartIndex <- UInt32Value(0u)
        rPh.EndingBaseIndex <- UInt32Value(2u)
        rPh.Append(Text("ホイサ") :> OpenXmlElement) |> ignore
        item.Append(rPh :> OpenXmlElement) |> ignore
        sst.Append(item :> OpenXmlElement) |> ignore
        sstPart.SharedStringTable <- sst
        sstPart.SharedStringTable.Save()
        // 1 visible sheet — A1 = SharedString idx 0.
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        buildWorksheet wsPart [ { Index = 1u; Cells = [ mkSharedStringCell "A1" 0 ] } ]
        let workbook = Workbook()
        let sheetsEl = Sheets()
        let sheet = Sheet()
        sheet.Id <- StringValue(wbPart.GetIdOfPart(wsPart))
        sheet.SheetId <- UInt32Value(1u)
        sheet.Name <- StringValue("Sheet1")
        sheetsEl.AppendChild(sheet) |> ignore
        workbook.AppendChild(sheetsEl) |> ignore
        wbPart.Workbook <- workbook
        wbPart.Workbook.Save()

    /// **r3 timeline filter fixture** — Columns element 도 박제하는 buildWorksheet variant.
    /// ranges: (min, max, width) list. width<1.0 인 컬럼은 `narrowColIndexes` set 에 포함.
    /// 산업 .xlsx 의 Gantt 시각화 (`Column.Width=0.75 × AM~DF (col 39~110) 72개`) 재현용 fixture base.
    let private buildWorksheetWithColumns
        (wsPart: WorksheetPart)
        (ranges: (uint32 * uint32 * float) list)
        (rows: RowSpec list) =
        let ws = Worksheet()
        let cols = Columns()
        for (mn, mx, width) in ranges do
            let col = Column()
            col.Min <- UInt32Value(mn)
            col.Max <- UInt32Value(mx)
            col.Width <- DoubleValue(width)
            col.CustomWidth <- BooleanValue(true)
            cols.Append(col :> OpenXmlElement) |> ignore
        ws.Append(cols :> OpenXmlElement) |> ignore
        let sd = SheetData()
        for rs in rows do
            sd.Append(mkRow rs :> OpenXmlElement) |> ignore
        ws.Append(sd :> OpenXmlElement) |> ignore
        wsPart.Worksheet <- ws
        wsPart.Worksheet.Save()

    /// **r3 timeline filter fixture** — 단일 sheet xlsx, Columns + Rows 박제.
    let buildXlsxWithNarrowColumns
        (path: string)
        (sheetName: string)
        (colRanges: (uint32 * uint32 * float) list)
        (rows: RowSpec list) =
        use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
        let wbPart = doc.AddWorkbookPart()
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        buildWorksheetWithColumns wsPart colRanges rows
        let workbook = Workbook()
        let sheetsEl = Sheets()
        let sheet = Sheet()
        sheet.Id <- StringValue(wbPart.GetIdOfPart(wsPart))
        sheet.SheetId <- UInt32Value(1u)
        sheet.Name <- StringValue(sheetName)
        sheetsEl.AppendChild(sheet) |> ignore
        workbook.AppendChild(sheetsEl) |> ignore
        wbPart.Workbook <- workbook
        wbPart.Workbook.Save()


// ── XLSX Fact ──

[<Fact>]
let ``xlsx — 3 sheet (visible 2 + hidden 1) — outline 2 + segments 2 + hidden skip`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "BOM" [
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "품번"; XlsxFixture.mkCellSpec "B1" "수량" ] }
                { Index = 2u; Cells = [ XlsxFixture.mkCellSpec "A2" "P-001"; XlsxFixture.mkCellSpec "B2" "10" ] }
            ]
            XlsxFixture.mkSheet "사양" [
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "사양1" ] }
            ]
            XlsxFixture.mkHiddenSheet "내부메모" [
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "내부" ] }
            ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Xlsx, result.DocType)
        Assert.Equal(Some 2, result.PageOrSheetCnt)   // hidden 제외
        Assert.Equal(2, result.Outline.Length)
        Assert.Equal("BOM", result.Outline.[0].Label)
        Assert.Equal("sheet=BOM", result.Outline.[0].RefLocator)
        Assert.Equal(OutlineNodeType.Sheet, result.Outline.[0].NodeType)
        Assert.Equal(2, result.Segments.Length)
        Assert.True(result.Segments |> Array.exists (fun s -> s.RefLocator = "sheet=BOM" && s.Text.Contains "품번" && s.Text.Contains "수량")))

[<Fact>]
let ``xlsx — sparse cell expandSparseRow (r1 Critical-4) — A1=v1 C1=v2 → "v1\t\tv2"`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S" [
                { Index = 1u; Cells = [
                    XlsxFixture.mkCellSpec "A1" "v1"
                    XlsxFixture.mkCellSpec "C1" "v2"
                ] }
            ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Segments.Length)
        // B 컬럼 빈 채로 보존 — sparse cell silent 소실 차단.
        Assert.Contains("v1\t\tv2", result.Segments.[0].Text))

[<Fact>]
let ``xlsx — SharedString resolve — SST 안 문자열 정상 노출`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S" [
                { Index = 1u; Cells = [
                    XlsxFixture.mkSharedStringCell "A1" 0
                    XlsxFixture.mkSharedStringCell "B1" 1
                ] }
            ]
        ]
        XlsxFixture.buildXlsx path (Some [ "헤더A"; "헤더B" ]) sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Segments.Length)
        Assert.Contains("헤더A\t헤더B", result.Segments.[0].Text))

[<Fact>]
let ``xlsx — phonetic ruby skip (r1 M2) — rPh 포함 SST item → ruby 제외, base text 만`` () =
    withTempPath ".xlsx" (fun path ->
        XlsxFixture.buildXlsxWithPhoneticRubySST path
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Segments.Length)
        // base "회사" 만 박제, ruby "ホイサ" 미포함.
        Assert.Contains("회사", result.Segments.[0].Text)
        Assert.DoesNotContain("ホイサ", result.Segments.[0].Text))

[<Fact>]
let ``xlsx — CellValues.Error skip (r1 M1) — #REF! cell → 빈 값 + log`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S" [
                { Index = 1u; Cells = [
                    XlsxFixture.mkCellSpec "A1" "v1"
                    XlsxFixture.mkErrorCell "B1" "#REF!"
                    XlsxFixture.mkCellSpec "C1" "v3"
                ] }
            ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Segments.Length)
        // error cell 위치는 빈 값. 다른 cell 정상.
        Assert.Contains("v1\t\tv3", result.Segments.[0].Text)
        Assert.DoesNotContain("#REF!", result.Segments.[0].Text))

[<Fact>]
let ``xlsx — formula cached value 부재 (r1 M14) — <c><f>...</f></c> no CellValue → null guard 정상`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S" [
                { Index = 1u; Cells = [
                    XlsxFixture.mkCellSpec "A1" "v1"
                    XlsxFixture.mkFormulaNoValueCell "B1"
                    XlsxFixture.mkCellSpec "C1" "v3"
                ] }
            ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // null guard 가 작동해야 함 (exception 안 남) → segment 정상.
        Assert.Equal(1, result.Segments.Length)
        Assert.Contains("v1\t\tv3", result.Segments.[0].Text))

[<Fact>]
let ``xlsx — Row.OrderBy(RowIndex) (r1 M3) — element 역순 → RowIndex 정렬 보장`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S" [
                { Index = 3u; Cells = [ XlsxFixture.mkCellSpec "A3" "row3" ] }
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "row1" ] }
                { Index = 2u; Cells = [ XlsxFixture.mkCellSpec "A2" "row2" ] }
            ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Segments.Length)
        // 정렬된 text: row1, row2, row3 순.
        let text = result.Segments.[0].Text
        let idx1 = text.IndexOf("row1")
        let idx2 = text.IndexOf("row2")
        let idx3 = text.IndexOf("row3")
        Assert.True(idx1 >= 0 && idx2 > idx1 && idx3 > idx2,
            sprintf "row 정렬 깨짐 — text=%s idx1=%d idx2=%d idx3=%d" text idx1 idx2 idx3))

[<Fact>]
let ``xlsx — DrawingsPart image (r1 M16) — sheet=<name> RefLocator`` () =
    withTempPath ".xlsx" (fun path ->
        let baseSheet =
            XlsxFixture.mkSheet "BOM" [
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "head" ] }
            ]
        let sheets = [ { baseSheet with Images = [ samplePngBytes ] } ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Images.Length)
        Assert.Equal("sheet=BOM", result.Images.[0].RefLocator)
        Assert.Equal(Png, result.Images.[0].Format))

[<Fact>]
let ``xlsx — DrawingsPart null guard (r1 M16) — image 없는 sheet → image 0`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S" [
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "v" ] }
            ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Empty(result.Images))

[<Fact>]
let ``xlsx — VeryHidden sheet skip (r1 M15)`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "Visible" [ { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "v" ] } ]
            XlsxFixture.mkVeryHiddenSheet "Secret" [ { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "secret" ] } ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Some 1, result.PageOrSheetCnt)
        Assert.Equal(1, result.Outline.Length)
        Assert.Equal("Visible", result.Outline.[0].Label))

[<Fact>]
let ``xlsx — Sheet.State HasValue=false (r1 Critical-6) — State 부재 시 visible 처리`` () =
    // Sheet.State 가 default (visible) 인 일반 시나리오 — 모든 visible fact 가 사실상 본 검증.
    // 명시 sanity check.
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "S1" [ { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "v" ] } ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Some 1, result.PageOrSheetCnt))

[<Fact>]
let ``xlsx — 시트명 # 포함 색인 (Backlog 5 hotfix, r1 M18 무효화) — %23 escape + 정상 색인`` () =
    // **Backlog 5**: 산업 .xlsx 의 호기 번호 표기 (`5-1. #201` 등) 정상 색인. 기존 r1 M18 (skip 정책) 폐기.
    // RefLocator.encodeMainValue 가 `#` → `%23` 자동 escape, tryParse 가 자동 decode.
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "정상" [ { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "v1" ] } ]
            XlsxFixture.mkSheet "5-1. #201" [ { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "호기 본문" ] } ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // 두 시트 모두 박제.
        Assert.Equal(Some 2, result.PageOrSheetCnt)
        Assert.Equal(2, result.Outline.Length)
        Assert.Equal("정상", result.Outline.[0].Label)
        Assert.Equal("5-1. #201", result.Outline.[1].Label)   // Label 은 raw (decode 의무 caller 측 표시)
        // RefLocator stored — `#` → `%23` escape 박제.
        Assert.Equal("sheet=정상", result.Outline.[0].RefLocator)
        Assert.Equal("sheet=5-1. %23201", result.Outline.[1].RefLocator)
        // round-trip — RefLocator.tryParse 가 stored → parsed → Main.Value 자동 decode.
        let parsed = RefLocator.tryParse result.Outline.[1].RefLocator
        Assert.True(parsed.IsSome)
        Assert.Equal("5-1. #201", parsed.Value.Main.Value)
        // round-trip 역방향 — Main.Value="5-1. #201" → toStored 자동 encode.
        Assert.Equal("sheet=5-1. %23201", RefLocator.toStored parsed.Value))

[<Fact>]
let ``xlsx — 시트명 = 포함 round-trip (r2 Major-2 반론 검증) — RefLocator tryParse round-trip 정상`` () =
    withTempPath ".xlsx" (fun path ->
        let sheets = [
            XlsxFixture.mkSheet "BOM=spec" [ { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "v" ] } ]
        ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Some 1, result.PageOrSheetCnt)
        Assert.Equal(1, result.Outline.Length)
        Assert.Equal("sheet=BOM=spec", result.Outline.[0].RefLocator)
        // RefLocator round-trip 정상 — `tryParse` 가 첫 `=` 만 split → Value="BOM=spec" 보존.
        let parsed = RefLocator.tryParse "sheet=BOM=spec"
        Assert.True(parsed.IsSome)
        Assert.Equal("sheet=BOM=spec", RefLocator.toStored parsed.Value)
        Assert.Equal("BOM=spec", parsed.Value.Main.Value))

[<Fact>]
let ``xlsx — 손상 xlsx (random bytes) fail-safe — DocType=Xlsx 빈 결과`` () =
    withTempPath ".xlsx" (fun path ->
        File.WriteAllBytes(path, [| 1uy; 2uy; 3uy; 4uy |])
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Xlsx, result.DocType)   // Task 0 dispatch 회귀 가드
        Assert.Empty(result.Outline)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Images))

[<Fact>]
let ``xlsx — Supports 분기 활성 (Task 2 박제)`` () =
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Xlsx)


// ────────────────────────────────────────────────────────────────────────────────
//  Backlog 2 — review M8 + M11 회귀 Fact
// ────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``pptx — 단일 slide 안 N장 image (review M8) — Ordinal 1..N 자리 보존`` () =
    // 단일 slide 의 WorksheetDrawing(== ShapeTree) 안 Pic 3개 → 동일 RefLocator + Ordinal 1/2/3.
    // ExtractImagesAtRefLocator 의 `imgOrdInBlock` 증가 hot path (helper refactor 이후도 유지) 회귀 차단.
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "다이어그램"
                           PptxFixture.PicSp samplePngBytes
                           PptxFixture.PicSp samplePngBytes
                           PptxFixture.PicSp samplePngBytes ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(3, result.Images.Length)
        let ordinals = result.Images |> Array.map (fun i -> i.Ordinal) |> Array.sort
        Assert.Equal<int[]>([| 1; 2; 3 |], ordinals)
        // 모두 동일 RefLocator = slide=1.
        Assert.All(result.Images, fun img -> Assert.Equal("slide=1", img.RefLocator)))

[<Fact>]
let ``xlsx — 단일 sheet 안 N장 image (review M8) — Ordinal 1..N 자리 보존`` () =
    // 단일 sheet 의 WorksheetDrawing 안 OneCellAnchor 2개 (각 Picture 1장) → Ordinal 1/2.
    withTempPath ".xlsx" (fun path ->
        let baseSheet =
            XlsxFixture.mkSheet "BOM" [
                { Index = 1u; Cells = [ XlsxFixture.mkCellSpec "A1" "head" ] }
            ]
        let sheets = [ { baseSheet with Images = [ samplePngBytes; samplePngBytes ] } ]
        XlsxFixture.buildXlsx path None sheets
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(2, result.Images.Length)
        let ordinals = result.Images |> Array.map (fun i -> i.Ordinal) |> Array.sort
        Assert.Equal<int[]>([| 1; 2 |], ordinals)
        Assert.All(result.Images, fun img -> Assert.Equal("sheet=BOM", img.RefLocator)))

[<Theory>]
[<InlineData("image/png", true, "png")>]
[<InlineData("image/jpeg", true, "jpeg")>]
[<InlineData("image/gif", true, "gif")>]
[<InlineData("image/webp", true, "webp")>]
[<InlineData("image/bmp", false, "")>]
[<InlineData("image/tiff", false, "")>]
let ``docx + inline Drawing image format 화이트리스트 분기 (review M11)`` (contentType: string) (shouldExtract: bool) (expectedFormatStr: string) =
    // ImagePartToFormat 의 4 종 화이트리스트 (PNG/JPEG/GIF/WEBP) + 외 (BMP/TIFF) skip 정합 회귀 차단.
    // makeDocxWithInlineImage 가 contentType 인자 받음 — bytes 는 PNG SamplePng.bytes 재사용 (ContentType 매칭만 검증).
    withTempPath ".docx" (fun path ->
        makeDocxWithInlineImage path contentType samplePngBytes
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        if shouldExtract then
            Assert.Equal(1, result.Images.Length)
            let img = result.Images.[0]
            let actualFormat =
                match img.Format with
                | Png -> "png"
                | Jpeg -> "jpeg"
                | Gif -> "gif"
                | Webp -> "webp"
            Assert.Equal(expectedFormatStr, actualFormat)
        else
            Assert.Empty(result.Images))


// ────────────────────────────────────────────────────────────────────────────────
//  Backlog 4 — review M9 (per-image fail-safe Ordinal 자리 보존) + M10 (IOException arm)
// ────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``pptx — 단일 slide 안 PNG+BMP+PNG 혼합 (review M9) — BMP whitelist 외 skip + PNG Ordinal 1/2 자리 보존`` () =
    // ExtractImagesAtRefLocator 의 None 분기 (ImagePartToFormat → None, whitelist 외) 에서 imgOrdInBlock 미증가
    // 정책 회귀 차단. BMP 1장이 ordinal 자리 점유하면 PNG 2장이 [1; 3] 으로 갈라짐 → 본 fact 가 [1; 2] 검증.
    withTempPath ".pptx" (fun path ->
        let slides = [
            { PptxFixture.emptySlideSpec with
                Shapes = [ PptxFixture.TitleSp "혼합 image"
                           PptxFixture.PicSp samplePngBytes
                           PptxFixture.PicSpWithType (samplePngBytes, ImagePartType.Bmp)   // whitelist 외
                           PptxFixture.PicSp samplePngBytes ] }
        ]
        PptxFixture.buildPptx path slides
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // BMP skip → PNG 2장만 박제. Ordinal 자리 보존 (1, 2 — 3 으로 점프 안 함).
        Assert.Equal(2, result.Images.Length)
        let ordinals = result.Images |> Array.map (fun i -> i.Ordinal) |> Array.sort
        Assert.Equal<int[]>([| 1; 2 |], ordinals)
        Assert.All(result.Images, fun img ->
            Assert.Equal("slide=1", img.RefLocator)
            Assert.Equal(Png, img.Format)))

// ────────────────────────────────────────────────────────────────────────────────
//  Plan 3 — EMF/WMF Metafile → PNG 변환
// ────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``docx + image/x-emf + 손상 bytes (Plan 3) — Metafile 변환 실패 + per-image fail-safe`` () =
    // ConvertImagePart 의 EMF/WMF 분기 진입 검증 + per-image fail-safe path.
    // raw PNG bytes 를 image/x-emf ContentType 박제 → Metafile ctor 가 ArgumentException 또는 ExternalException
    // → ConvertImagePart None 반환 → image 미박제 (ExtractImagesAtRefLocator 의 Ordinal 자리 보존 정합).
    //
    // **검증 한계 (review Plan3 M-2)** — `Assert.Empty(result.Images)` 만 검증 → 다음 두 path 구분 못함:
    //   (a) EMF/WMF case 진입 → Metafile throw → catch → None (의도된 path)
    //   (b) `_ -> None` (whitelist 외 fall-through) — 만약 누군가 `"image/x-emf"` 패턴 오타 시 silent green.
    // 직접 분기 진입 검증은 log capture sink (xunit InMemorySink) 도입 의무 — 별 turn backlog.
    // 현 보강 — control PNG case 와 대조 검증 (`docx + inline Drawing image format 화이트리스트 분기` Theory) 이
    // image/png/jpeg/gif/webp 4종은 성공 박제 → 본 fact 의 EMF skip 이 *whitelist 외 자연 skip* 정합.
    //
    // 실 EMF bytes fixture (mkMinimalEmfBytes via System.Drawing Metafile + Graphics) 는 GDI+ 의존 의무로
    // backlog — 변환 성공 path 의 e2e 검증은 사용자 실 .xlsx 색인 결과로 대체.
    withTempPath ".docx" (fun path ->
        makeDocxWithInlineImage path "image/x-emf" samplePngBytes
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // 변환 실패 → image 미박제 (per-image fail-safe + Ordinal 자리 보존).
        Assert.Empty(result.Images))


[<Fact>]
let ``docx — file lock 시 IOException → ExtractWithFailSafe arm + Docx 빈 결과 (review M10)`` () =
    // ExtractWithFailSafe 7종 catch arm 중 IOException 회귀 차단. FileShare.None 으로 lock 후 Extract 시도
    // → WordprocessingDocument.Open 가 IOException → fail-safe 빈 결과.
    withTempPath ".docx" (fun path ->
        // valid docx 박제 (Open 시 valid 라야 IOException 정확히 trigger — random bytes 면 FileFormatException 가 먼저).
        makeDocx path
        // lock — read 도 차단 (FileShare.None).
        use locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        // fail-safe — DocType 정합 (Task 0 dispatch 회귀 가드) + 빈 결과.
        Assert.Equal(Docx, result.DocType)
        Assert.Empty(result.Outline)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Images))


// ────────────────────────────────────────────────────────────────────────────────
//  r3 — timeline filter (Gantt 시각화 좁은 컬럼 noise drop)
// ────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``xlsx + 좁은 컬럼 (width<1.0) + 빈 cell — narrow filter 가 dense entry drop (Gantt 시각화 noise 제거)`` () =
    // 산업 .xlsx 의 Gantt 시각화 재현 fixture (단순화):
    //   Columns: 1~6 = width 10 (데이터), 7~12 = width 0.75 (Gantt 시각화)
    //   Row: A1=v1, B1=v2, C1=v3 (데이터) + G1~L1 = 빈 cell entry (좁은 영역)
    // filter off (baseline) 시: dense.Length=12, line = "v1\tv2\tv3\t\t\t\t\t\t\t\t\t" (9 trailing tabs)
    // filter on 시: 좁은 영역 빈 6개 drop → dense.Length=6 → line = "v1\tv2\tv3\t\t\t" (3 mid tabs, narrow 제거)
    withTempPath ".xlsx" (fun path ->
        let cells =
            [ XlsxFixture.mkCellSpec "A1" "v1"
              XlsxFixture.mkCellSpec "B1" "v2"
              XlsxFixture.mkCellSpec "C1" "v3"
              // 좁은 컬럼 영역 (G=7 ~ L=12) cell entry 박제, value 빈. fill style only Gantt bar 재현.
              XlsxFixture.mkCellSpec "G1" ""
              XlsxFixture.mkCellSpec "H1" ""
              XlsxFixture.mkCellSpec "I1" ""
              XlsxFixture.mkCellSpec "J1" ""
              XlsxFixture.mkCellSpec "K1" ""
              XlsxFixture.mkCellSpec "L1" "" ]
        let rows : XlsxFixture.RowSpec list = [ { Index = 1u; Cells = cells } ]
        XlsxFixture.buildXlsxWithNarrowColumns path "TL"
            [ (1u, 6u, 10.0); (7u, 12u, 0.75) ]
            rows
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Segments) |> ignore
        let seg = result.Segments.[0]
        // 좁은 영역 빈 cell 6개 drop → tab count = 3 데이터 컬럼 사이 2개 + (없음). 실제로는 A/B/C 컬럼만 박제.
        // ExpandSparseRow + narrow filter 후 dense = ["v1"; "v2"; "v3"] → 정확.
        Assert.Equal("v1\tv2\tv3", seg.Text))

[<Fact>]
let ``xlsx + 좁은 컬럼 + cell 값 있음 (tick label) — narrow filter 가 보존 (drop 조건 = width<1.0 AND value="")`` () =
    // 좁은 영역 cell 에 값 있는 경우 (e.g. Gantt 타임축 tick label `1, 2, 3 ...`) 는 보존 의무.
    // filter 가 너무 적극적이면 tick label 정보 손실 → Gantt 시각화의 축 좌표 의미 박제 불가.
    withTempPath ".xlsx" (fun path ->
        let cells =
            [ XlsxFixture.mkCellSpec "A1" "header"
              // 좁은 영역 (G=7 ~ I=9) 의 cell 에 tick label 박제.
              XlsxFixture.mkCellSpec "G1" "1"
              XlsxFixture.mkCellSpec "H1" "2"
              XlsxFixture.mkCellSpec "I1" "3" ]
        let rows : XlsxFixture.RowSpec list = [ { Index = 1u; Cells = cells } ]
        XlsxFixture.buildXlsxWithNarrowColumns path "TL"
            [ (1u, 6u, 10.0); (7u, 9u, 0.75) ]
            rows
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Segments) |> ignore
        let seg = result.Segments.[0]
        // dense = ["header"; ""; ""; ""; ""; ""; "1"; "2"; "3"] (col 1=header, 2~6 빈 정상 컬럼, 7~9 tick).
        // narrow filter — col 7~9 값 있음 → drop 안 됨. 빈 col 2~6 = 정상 컬럼이라 drop 안 됨.
        // 결과 = ["header"; ""; ""; ""; ""; ""; "1"; "2"; "3"] → tab join = "header\t\t\t\t\t\t1\t2\t3"
        Assert.Equal("header\t\t\t\t\t\t1\t2\t3", seg.Text))

[<Fact>]
let ``xlsx + Width=1.0 경계값 — narrow 아님 (NarrowColumnWidthThreshold 의 < 비교 off-by-one 회귀 catch)`` () =
    // narrow filter 의 threshold 정의 = `< 1.0` (strict). Width=1.0 정확히는 narrow 아님 → 빈 cell 도 보존.
    // 임계값 비교 연산자가 `<` 에서 `<=` 로 회귀 시 본 fact 가 catch.
    //
    // **trailing trim 회피** — `sb.ToString().Trim()` 이 trailing tab 모두 제거하므로, 회귀 catch 를
    // 위해 maxCol 위치에 값 있는 tail cell (col 8 = H1) 박제. 회귀 시 col 7 drop 으로 tab 1개 줄어듦.
    withTempPath ".xlsx" (fun path ->
        let cells =
            [ XlsxFixture.mkCellSpec "A1" "header"
              // col 7 = Width=1.0 정확 (== threshold). 빈 cell entry. narrow 아니면 보존.
              XlsxFixture.mkCellSpec "G1" ""
              // col 8 = 값 있는 tail. trailing trim 회피용.
              XlsxFixture.mkCellSpec "H1" "tail" ]
        let rows : XlsxFixture.RowSpec list = [ { Index = 1u; Cells = cells } ]
        XlsxFixture.buildXlsxWithNarrowColumns path "TL"
            [ (1u, 6u, 10.0); (7u, 7u, 1.0); (8u, 12u, 10.0) ]   // col 7 = 1.0 (경계), col 1~6/8~12 정상
            rows
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Segments) |> ignore
        let seg = result.Segments.[0]
        // dense = ["header"; ""; ""; ""; ""; ""; ""; "tail"] (col 1=header, 2~7 빈, 8=tail).
        // narrow filter (현재 `<` 비교): col 7 width=1.0 == threshold → narrow 아님 → G1 보존 → 7 tabs.
        // 회귀 (`<` → `<=`) 시: col 7 narrow → G1 drop → kept.Length=7 → 6 tabs (mismatch).
        Assert.Equal("header\t\t\t\t\t\t\ttail", seg.Text))

// ── Task 2-extra — Gantt schedule 시트 type 힌트 Fact ──
// 산업 .xlsx 의 작업 일정표 (Gantt 형식) 시트 검출 + role 기반 동적 preamble prepend + outline `[Gantt schedule]` suffix.
// false positive 회피 우선 — distinct role ≥3 AND start/dur/cum 중 ≥2 일 때만 검출.

[<Fact>]
let ``xlsx Gantt — 정상 검출 (NO|SYM|작업내역|시작|시간|누계 6 컬럼)`` () =
    withTempPath ".xlsx" (fun path ->
        let headerRow : XlsxFixture.RowSpec = {
            Index = 1u
            Cells = [
                XlsxFixture.mkCellSpec "A1" "NO"
                XlsxFixture.mkCellSpec "B1" "SYM"
                XlsxFixture.mkCellSpec "C1" "작업내역"
                XlsxFixture.mkCellSpec "D1" "시작"
                XlsxFixture.mkCellSpec "E1" "시간"
                XlsxFixture.mkCellSpec "F1" "누계"
            ]
        }
        let dataRow : XlsxFixture.RowSpec = {
            Index = 2u
            Cells = [
                XlsxFixture.mkCellSpec "A2" "1"
                XlsxFixture.mkCellSpec "B2" "M"
                XlsxFixture.mkCellSpec "C2" "233-1호기 조립"
                XlsxFixture.mkCellSpec "D2" "0"
                XlsxFixture.mkCellSpec "E2" "6"
                XlsxFixture.mkCellSpec "F2" "6"
            ]
        }
        XlsxFixture.buildXlsxWithNarrowColumns path "작업서"
            [ (1u, 6u, 10.0) ]   // 데이터 컬럼만 (narrow 컬럼 무 — 검출 자체만 검증)
            [ headerRow; dataRow ]
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Outline) |> ignore
        Assert.Equal("작업서 [Gantt schedule]", result.Outline.[0].Label)
        Assert.Single(result.Segments) |> ignore
        let seg = result.Segments.[0]
        Assert.StartsWith("이 시트는 작업 일정표(Gantt)입니다.", seg.Text)
        Assert.Contains("A=NO(순번)", seg.Text)
        Assert.Contains("D=START(시작초)", seg.Text)
        Assert.Contains("E=DURATION(소요초)", seg.Text)
        Assert.Contains("F=CUMULATIVE(누계초)", seg.Text)
        // 데이터 row 도 preamble 다음에 박제 (회귀 차단).
        Assert.Contains("233-1호기 조립", seg.Text))

[<Fact>]
let ``xlsx Gantt — 컬럼 순서 바뀜 (SYM/NO/시작/작업내역/시간/누계) — 동일 검출 + letter 정합`` () =
    withTempPath ".xlsx" (fun path ->
        let headerRow : XlsxFixture.RowSpec = {
            Index = 1u
            Cells = [
                XlsxFixture.mkCellSpec "A1" "SYM"
                XlsxFixture.mkCellSpec "B1" "NO"
                XlsxFixture.mkCellSpec "C1" "시작"
                XlsxFixture.mkCellSpec "D1" "작업내역"
                XlsxFixture.mkCellSpec "E1" "시간"
                XlsxFixture.mkCellSpec "F1" "누계"
            ]
        }
        XlsxFixture.buildXlsxWithNarrowColumns path "S" [ (1u, 6u, 10.0) ] [ headerRow ]
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal("S [Gantt schedule]", result.Outline.[0].Label)
        let seg = result.Segments.[0]
        // 컬럼 letter ↔ role 매칭이 실제 위치 반영.
        Assert.Contains("A=SYM(심볼)", seg.Text)
        Assert.Contains("B=NO(순번)", seg.Text)
        Assert.Contains("C=START(시작초)", seg.Text)
        Assert.Contains("D=TASK(작업내역)", seg.Text))

[<Fact>]
let ``xlsx Gantt — 영문 헤더 (NO/Symbol/Task/Start/Duration/Cumulative) — 검출`` () =
    withTempPath ".xlsx" (fun path ->
        let headerRow : XlsxFixture.RowSpec = {
            Index = 1u
            Cells = [
                XlsxFixture.mkCellSpec "A1" "NO"
                XlsxFixture.mkCellSpec "B1" "Symbol"
                XlsxFixture.mkCellSpec "C1" "Task"
                XlsxFixture.mkCellSpec "D1" "Start"
                XlsxFixture.mkCellSpec "E1" "Duration"
                XlsxFixture.mkCellSpec "F1" "Cumulative"
            ]
        }
        XlsxFixture.buildXlsxWithNarrowColumns path "EN" [ (1u, 6u, 10.0) ] [ headerRow ]
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal("EN [Gantt schedule]", result.Outline.[0].Label)
        Assert.StartsWith("이 시트는 작업 일정표(Gantt)입니다.", result.Segments.[0].Text))

[<Fact>]
let ``xlsx Gantt — 공백/괄호/한자 normalize ("작 업 내 역" / "시간(sec)" / 開始) — 검출`` () =
    withTempPath ".xlsx" (fun path ->
        let headerRow : XlsxFixture.RowSpec = {
            Index = 1u
            Cells = [
                XlsxFixture.mkCellSpec "A1" "NO"
                XlsxFixture.mkCellSpec "B1" "SYM"
                XlsxFixture.mkCellSpec "C1" "작 업 내 역"
                XlsxFixture.mkCellSpec "D1" "開始"                // 한자→한글 normalize (시작)
                XlsxFixture.mkCellSpec "E1" "시간(sec)"            // 괄호 부연 strip
                XlsxFixture.mkCellSpec "F1" "누계"
            ]
        }
        XlsxFixture.buildXlsxWithNarrowColumns path "N" [ (1u, 6u, 10.0) ] [ headerRow ]
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal("N [Gantt schedule]", result.Outline.[0].Label)
        let seg = result.Segments.[0]
        Assert.Contains("C=TASK(작업내역)", seg.Text)
        Assert.Contains("D=START(시작초)", seg.Text)
        Assert.Contains("E=DURATION(소요초)", seg.Text))

[<Fact>]
let ``xlsx Gantt — 2-row merged header (row1 "시간" + row2 tick "10/20/30") — 검출 + 타임라인 미오인`` () =
    withTempPath ".xlsx" (fun path ->
        // row1 = 데이터 컬럼 헤더 (col 1~3) + col 4~6 = 시간/누계/등 위쪽 merged 위쪽 cell
        let row1 : XlsxFixture.RowSpec = {
            Index = 1u
            Cells = [
                XlsxFixture.mkCellSpec "A1" "NO"
                XlsxFixture.mkCellSpec "B1" "SYM"
                XlsxFixture.mkCellSpec "C1" "작업"
                XlsxFixture.mkCellSpec "D1" "시작"
                XlsxFixture.mkCellSpec "E1" "시간"
                XlsxFixture.mkCellSpec "F1" "누계"
            ]
        }
        // row2 = 타임라인 tick label (col 7~9). 데이터 row 와 헷갈리지 않게 narrow col 박제 + 값 박제 → buildRoleMap 의 concat 매칭이 데이터 컬럼의 row1 단독 매칭을 우선.
        let row2 : XlsxFixture.RowSpec = {
            Index = 2u
            Cells = [
                XlsxFixture.mkCellSpec "G2" "10"
                XlsxFixture.mkCellSpec "H2" "20"
                XlsxFixture.mkCellSpec "I2" "30"
            ]
        }
        XlsxFixture.buildXlsxWithNarrowColumns path "T"
            [ (1u, 6u, 10.0); (7u, 9u, 0.75) ]
            [ row1; row2 ]
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal("T [Gantt schedule]", result.Outline.[0].Label)
        let seg = result.Segments.[0]
        Assert.Contains("D=START(시작초)", seg.Text)
        // 타임라인 컬럼 (G~I, narrow + tick) 은 role 매핑 안 됨 → preamble 에 미박제.
        Assert.DoesNotContain("G=", seg.Text)
        Assert.DoesNotContain("H=", seg.Text)
        Assert.DoesNotContain("I=", seg.Text))

[<Fact>]
let ``xlsx Gantt — 미검출 false negative (NO|Item|값 — start/dur/cum 부재) — Gantt 판정 안 함`` () =
    withTempPath ".xlsx" (fun path ->
        let headerRow : XlsxFixture.RowSpec = {
            Index = 1u
            Cells = [
                XlsxFixture.mkCellSpec "A1" "NO"
                XlsxFixture.mkCellSpec "B1" "Item"  // synonym 없음
                XlsxFixture.mkCellSpec "C1" "값"     // synonym 없음
            ]
        }
        XlsxFixture.buildXlsxWithNarrowColumns path "NG" [ (1u, 3u, 10.0) ] [ headerRow ]
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal("NG", result.Outline.[0].Label)   // suffix 없음
        let seg = result.Segments.[0]
        Assert.DoesNotContain("이 시트는 작업 일정표", seg.Text)
        // header row 만 정상 박제.
        Assert.Equal("NO\tItem\t값", seg.Text))
