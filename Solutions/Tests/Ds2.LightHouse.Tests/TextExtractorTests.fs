module Ds2.LightHouse.Tests.TextExtractorTests

open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// done-lighthouse-kb-index.md §4.8a — TextExtractor (txt/md).

let private withTempFile (ext: string) (bytes: byte[]) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-test-%s%s" (System.Guid.NewGuid().ToString("N")) ext)
    File.WriteAllBytes(path, bytes)
    try action path
    finally if File.Exists path then File.Delete path

[<Fact>]
let ``txt — UTF-8 단락 분리`` () =
    let body = "첫 단락\n\n두 번째 단락\n\n세 번째"
    let bytes = Encoding.UTF8.GetBytes body
    withTempFile ".txt" bytes (fun path ->
        use ext = new TextExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Text, result.DocType)
        Assert.Equal(3, result.Segments.Length)
        Assert.Equal("첫 단락", result.Segments.[0].Text)
        Assert.Equal("p=1", result.Segments.[0].RefLocator)
        Assert.Equal("p=3", result.Segments.[2].RefLocator)
        Assert.Empty(result.Outline))

[<Fact>]
let ``txt — CP949 인코딩`` () =
    let cp949 = Encoding.GetEncoding(949)
    let body = "한글 CP949 본문"
    let bytes = cp949.GetBytes body
    withTempFile ".txt" bytes (fun path ->
        use ext = new TextExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Segments) |> ignore
        Assert.Equal(body, result.Segments.[0].Text))

[<Fact>]
let ``md — heading flat outline 추출`` () =
    let body = "# 1장\n\n본문\n\n## 1.1 소제목\n\n중첩 본문\n\n### 1.1.1 더 깊은\n\n끝"
    let bytes = Encoding.UTF8.GetBytes body
    withTempFile ".md" bytes (fun path ->
        use ext = new TextExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(Markdown, result.DocType)
        Assert.Equal(3, result.Outline.Length)
        Assert.Equal("1장", result.Outline.[0].Label)
        Assert.Equal("1.1 소제목", result.Outline.[1].Label)
        Assert.Equal("1.1.1 더 깊은", result.Outline.[2].Label)
        for n in result.Outline do
            Assert.Equal(OutlineNodeType.Heading, n.NodeType))

[<Fact>]
let ``빈 txt → segments 0`` () =
    withTempFile ".txt" [||] (fun path ->
        use ext = new TextExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Empty(result.Segments)
        Assert.Empty(result.Outline))

[<Fact>]
let ``Supports — Text / Markdown only`` () =
    use ext = new TextExtractor() :> IExtractor
    Assert.True(ext.Supports Text)
    Assert.True(ext.Supports Markdown)
    Assert.False(ext.Supports Pdf)
    Assert.False(ext.Supports Docx)
    Assert.False(ext.Supports (Unsupported ".dwg"))

[<Fact>]
let ``UTF-8 BOM 첫 char strip — parent r10 lib fix F3 회귀 보호 (review IM-8)`` () =
    // BOM (EF BB BF) + 한국어 본문. Encoding.UTF8.GetString 이 BOM 을 U+FEFF 로 보존 → leading char strip 필수.
    let bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]
    let body = Encoding.UTF8.GetBytes "본문 첫 줄\n\n둘째 단락"
    let bytes = Array.append bom body
    withTempFile ".txt" bytes (fun path ->
        use ext = new TextExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.NotEmpty(result.Segments)
        let firstChar = result.Segments.[0].Text.[0]
        // R2/R4 m1 권장: invisible char literal 회피 → '﻿' escape 사용.
        Assert.NotEqual('﻿', firstChar)
        Assert.Equal('본', firstChar))
