module LlmUserMessageOpsTests

open System
open Xunit
open Ds2.LlmAgent

/// commit-6b — `LlmUserMessageOps.EnforceCapabilityOrFail` strict 모드 회귀.
/// silent drop 차단 — capability 미지원 첨부 wire 시도 시 invalidArg fail-fast.

[<Fact>]
let ``Image 첨부 + Capabilities.TextOnly → invalidArg`` () =
    let img = Image("a.png", [|0uy|], "image/png")
    let msg = LlmUserMessage.Create("hi", [| img |])
    let ex = Assert.Throws<ArgumentException>(fun () ->
        LlmUserMessageOps.EnforceCapabilityOrFail Capabilities.TextOnly msg)
    Assert.Contains("이미지", ex.Message)

[<Fact>]
let ``PDF 첨부 + Capabilities.ImagesOnly → invalidArg`` () =
    let pdf = Pdf("doc.pdf", [|0uy|])
    let msg = LlmUserMessage.Create("review", [| pdf |])
    let ex = Assert.Throws<ArgumentException>(fun () ->
        LlmUserMessageOps.EnforceCapabilityOrFail (Capabilities.ImagesOnly 1024L) msg)
    Assert.Contains("PDF", ex.Message)

[<Fact>]
let ``PDF 첨부 + Capabilities.ImagesAndPdf → 통과`` () =
    let pdf = Pdf("doc.pdf", [|0uy|])
    let msg = LlmUserMessage.Create("review", [| pdf |])
    LlmUserMessageOps.EnforceCapabilityOrFail (Capabilities.ImagesAndPdf(1024L, 1024L)) msg

[<Fact>]
let ``TextFile 첨부는 capability 무관 통과`` () =
    let txt = TextFile("notes.md", "# hi")
    let msg = LlmUserMessage.Create("x", [| txt |])
    LlmUserMessageOps.EnforceCapabilityOrFail Capabilities.TextOnly msg

[<Fact>]
let ``OfText (첨부 0개) 는 모든 capability 에서 통과`` () =
    let msg = LlmUserMessage.OfText "hello"
    LlmUserMessageOps.EnforceCapabilityOrFail Capabilities.TextOnly msg
    LlmUserMessageOps.EnforceCapabilityOrFail (Capabilities.ImagesOnly 1024L) msg
    LlmUserMessageOps.EnforceCapabilityOrFail (Capabilities.ImagesAndPdf(1024L, 1024L)) msg

[<Fact>]
let ``AttachmentInfo.tryGetImage 은 Image 만 Some, 그 외 None`` () =
    let img = Image("a.png", [|1uy;2uy|], "image/png")
    match AttachmentInfo.tryGetImage img with
    | Some d ->
        Assert.Equal("a.png", d.Name)
        Assert.Equal("image/png", d.Mime)
        Assert.Equal(2, d.Bytes.Length)
    | None -> failwith "Image 인데 None"
    Assert.Equal(None, AttachmentInfo.tryGetImage (Pdf("p.pdf", [||])))
    Assert.Equal(None, AttachmentInfo.tryGetImage (TextFile("t.md", "x")))

[<Fact>]
let ``AttachmentInfo.tryGetPdf 은 Pdf 만 Some, 그 외 None`` () =
    let pdf = Pdf("doc.pdf", [|0x25uy;0x50uy|])
    match AttachmentInfo.tryGetPdf pdf with
    | Some d ->
        Assert.Equal("doc.pdf", d.Name)
        Assert.Equal(2, d.Bytes.Length)
    | None -> failwith "Pdf 인데 None"
    Assert.Equal(None, AttachmentInfo.tryGetPdf (Image("a.png", [||], "image/png")))
