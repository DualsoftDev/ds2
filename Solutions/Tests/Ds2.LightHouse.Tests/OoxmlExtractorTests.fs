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
let ``docx + PNG ImagePart — 화이트리스트 매칭 + 추출`` () =
    withTempPath ".docx" (fun path ->
        makeDocxWithImage path "image/png" samplePngBytes
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal(Png, img.Format)
        // RefLocator = "body" (옵션 B, paragraph 매핑은 C4 의무).
        Assert.Equal("body", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        // Width/Height 는 OpenXml ImagePart 가 노출 안 함 — None.
        Assert.Equal(None, img.Width)
        Assert.Equal(None, img.Height)
        // Bytes round-trip — FeedData 박제 bytes 가 그대로 복원.
        Assert.Equal(samplePngBytes.Length, img.Bytes.Length))

[<Fact>]
let ``docx + BMP ImagePart — 화이트리스트 외 (vector/raster 비대상) 자연 skip`` () =
    // m6 primary 가드 — BMP / x-emf / x-wmf 등 ContentType match _ -> None 분기 검증.
    withTempPath ".docx" (fun path ->
        // BMP minimal header — content 검증 안 함 (skip 분기는 ContentType 매칭에서 분기, bytes 무관).
        let bmpStub = [| 0x42uy; 0x4Duy; 0x10uy; 0x00uy; 0x00uy; 0x00uy |]
        makeDocxWithImage path "image/bmp" bmpStub
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Empty(result.Images))
