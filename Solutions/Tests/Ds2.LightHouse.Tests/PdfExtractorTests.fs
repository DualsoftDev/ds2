module Ds2.LightHouse.Tests.PdfExtractorTests

open System.IO
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

/// todo-lighthouse-kb-index.md §4.8a — PdfExtractor fail-safe.
///
/// 정상 PDF fixture 는 본 lib 가 생성 능력 없음 (PdfPig 는 read-only). Phase 2 진입 시
/// `Solutions/Tests/Ds2.LightHouse.Tests/Fixtures/*.pdf` 추가 권장. Phase 1 은 fail-safe 검증 우선.

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
