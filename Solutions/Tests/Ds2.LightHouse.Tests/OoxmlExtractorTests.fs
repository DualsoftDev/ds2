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
        Assert.Contains(result.Segments, fun s -> s.Text.Contains "셀A" && s.Text.Contains "셀B"))

[<Fact>]
let ``손상 docx (random bytes) — fail-safe 빈 결과`` () =
    withTempPath ".docx" (fun path ->
        File.WriteAllBytes(path, [| 1uy; 2uy; 3uy; 4uy |])
        use ext = new OoxmlExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Docx, result.DocType)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Outline))

[<Fact>]
let ``Supports — Docx only (Pptx/Xlsx Phase 2)`` () =
    use ext = new OoxmlExtractor() :> IExtractor
    Assert.True(ext.Supports Docx)
    Assert.False(ext.Supports Pptx)
    Assert.False(ext.Supports Xlsx)
    Assert.False(ext.Supports Pdf)
