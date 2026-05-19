module Ds2.LightHouse.Tests.PdfExtractorTests

open System.IO
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open UglyToad.PdfPig.Core
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Writer

/// todo-lighthouse-kb-index.md §4.8a — PdfExtractor fail-safe.
///
/// Phase 1 = fail-safe 검증 우선 (정상 PDF fixture 없이도 회귀 차단).
/// **s6-r23 묶음 3** — `PdfDocumentBuilder` (PdfPig 0.1.14) 로 정상 PDF fixture 박제 가능 (AddPage + AddPng + AddText).
/// 페이지 안 image 추출 회귀 차단 fact 박제 — PdfExtractor 의 `page.GetImages()` + `IPdfImage.TryGetPng` 정합.

let private withTempPath (ext: string) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-test-%s%s" (System.Guid.NewGuid().ToString("N")) ext)
    try action path
    finally if File.Exists path then File.Delete path

[<Fact>]
let ``손상 PDF (random bytes) — fail-safe 빈 결과`` () =
    withTempPath ".pdf" (fun path ->
        File.WriteAllBytes(path, [| 1uy; 2uy; 3uy; 4uy; 5uy |])
        use ext = new PdfExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Pdf, result.DocType)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Outline)
        // Phase 2 task C2 회귀 차단 — 손상 PDF (Open 실패 → 빈 doc) 도 Images=[||].
        Assert.Empty(result.Images))

[<Fact>]
let ``빈 파일 — fail-safe 빈 결과`` () =
    withTempPath ".pdf" (fun path ->
        File.WriteAllBytes(path, [||])
        use ext = new PdfExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Pdf, result.DocType)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Images))

[<Fact>]
let ``Supports — Pdf only`` () =
    use ext = new PdfExtractor() :> IExtractor
    Assert.True(ext.Supports Pdf)
    Assert.False(ext.Supports Docx)
    Assert.False(ext.Supports Text)


// ─── s6-r23 묶음 3 — PdfDocumentBuilder fixture + 실 image 추출 회귀 차단 ───────────────

/// 1 페이지 PDF 박제 — `bodyText` (영문 — Standard14 Helvetica ASCII only) + PNG image N장.
/// 반환 = PDF byte[] (caller 가 임시 파일로 write 후 Extract 검증).
let private buildPdfWithImages (bodyText: string) (imageCount: int) : byte[] =
    let builder = new PdfDocumentBuilder()
    let page = builder.AddPage(595.0, 842.0)   // A4 portrait
    let helvetica = builder.AddStandard14Font(Standard14Font.Helvetica)
    // 본문 text — extractor 의 `page.Text` 가 추출하도록 영문 ASCII (Standard14 한국어 미지원).
    page.AddText(bodyText, 12.0, PdfPoint(50.0, 700.0), helvetica) |> ignore
    // image N장 — 같은 PNG bytes 박제 (PdfPig 가 page 안에 deduplicate 안 함, 새 image object 로 처리).
    let pngBytes = SamplePng.bytes
    for i in 1 .. imageCount do
        let yOffset = 600.0 - float (i - 1) * 110.0
        let rect = PdfRectangle(50.0, yOffset, 150.0, yOffset + 100.0)
        page.AddPng(pngBytes, rect) |> ignore
    builder.Build()

let private withTempPdf (pdfBytes: byte[]) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-test-%s.pdf" (System.Guid.NewGuid().ToString("N")))
    File.WriteAllBytes(path, pdfBytes)
    try action path
    finally if File.Exists path then File.Delete path

[<Fact>]
let ``정상 PDF (PdfDocumentBuilder fixture) + image 1장 — 추출 회귀 차단 (s6-r23 묶음 3)`` () =
    let pdfBytes = buildPdfWithImages "PdfPig fixture body conveyor specification" 1
    withTempPdf pdfBytes (fun path ->
        use ext = new PdfExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Pdf, result.DocType)
        Assert.Equal(Some 1, result.PageOrSheetCnt)
        // segment — 본문 영문 text. PdfPig 의 text 추출이 layout 으로 인해 spacing 변화 가능 → Contains 로 박제.
        Assert.True(result.Segments.Length >= 1, "최소 1 segment 박제 의무")
        let firstSeg = result.Segments.[0]
        Assert.Equal("p=1", firstSeg.RefLocator)
        Assert.Contains("conveyor", firstSeg.Text)
        // image — TryGetPng 성공 분기 (PdfPig 가 본 builder 산물에 대해 안정적으로 PNG 추출).
        Assert.Equal(1, result.Images.Length)
        let img = result.Images.[0]
        Assert.Equal(Png, img.Format)
        Assert.Equal("p=1", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.True(img.Bytes.Length > 0, "image bytes 비어있으면 안 됨"))

[<Fact>]
let ``정상 PDF + image 3장 — Ordinal 1..3 + 동일 page RefLocator 박제 (s6-r23 묶음 3)`` () =
    let pdfBytes = buildPdfWithImages "Page 1 body text" 3
    withTempPdf pdfBytes (fun path ->
        use ext = new PdfExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(3, result.Images.Length)
        // 모두 page 1 — RefLocator "p=1" 동일, Ordinal 1..3 (page 안 image 순번 SSOT).
        let ordinals =
            result.Images
            |> Array.filter (fun i -> i.RefLocator = "p=1")
            |> Array.map (fun i -> i.Ordinal)
            |> Array.sort
        Assert.Equal<int[]>([| 1; 2; 3 |], ordinals))
