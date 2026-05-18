module Ds2.LightHouse.Tests.OoxmlExtractorTests

open System.IO
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

/// 1×1 PNG raw bytes (IndexerTests.samplePngBytes 의 mirror — Tests common module 도입은 backlog).
let private samplePngBytes : byte[] =
    [|
        0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy
        0x00uy; 0x00uy; 0x00uy; 0x0Duy; 0x49uy; 0x48uy; 0x44uy; 0x52uy
        0x00uy; 0x00uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x00uy; 0x01uy
        0x08uy; 0x06uy; 0x00uy; 0x00uy; 0x00uy
        0x1Fuy; 0x15uy; 0xC4uy; 0x89uy
        0x00uy; 0x00uy; 0x00uy; 0x0Auy; 0x49uy; 0x44uy; 0x41uy; 0x54uy
        0x78uy; 0x9Cuy; 0x63uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x05uy; 0x00uy; 0x01uy
        0x0Duy; 0x0Auy; 0x2Duy; 0xB4uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x49uy; 0x45uy; 0x4Euy; 0x44uy
        0xAEuy; 0x42uy; 0x60uy; 0x82uy
    |]

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
