module LlmUserMessageOpsTests

open System
open Xunit
open Ds2.LlmAgent

/// commit-6b — `LlmUserMessageOps.EnforceCapabilityOrFail` strict 모드 회귀.
/// silent drop 차단 — capability 미지원 첨부 wire 시도 시 invalidArg fail-fast.

[<Fact>]
let ``Image 첨부 + Capabilities.TextOnly → invalidArg`` () =
    let img = Image("a.png", [|0uy|], Png)
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
    let img = Image("a.png", [|1uy;2uy|], Png)
    match AttachmentInfo.tryGetImage img with
    | Some d ->
        Assert.Equal("a.png", d.Name)
        // rev 18 m3: Image case 가 ImageFormat 보유 → tryGetImage 가 Attachment.mimeOf 변환해 Mime field 채움.
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
    Assert.Equal(None, AttachmentInfo.tryGetPdf (Image("a.png", [||], Png)))

/// rev 18 m3 — ImageFormat ↔ mime 1:1 매핑 SSOT (`Attachment.mimeOf`) 회귀.
[<Fact>]
let ``Attachment.mimeOf — 4 case exhaustive 매핑`` () =
    Assert.Equal("image/png", Attachment.mimeOf Png)
    Assert.Equal("image/jpeg", Attachment.mimeOf Jpeg)
    Assert.Equal("image/gif", Attachment.mimeOf Gif)
    Assert.Equal("image/webp", Attachment.mimeOf Webp)

[<Fact>]
let ``AttachmentInfo.tryGetImageFormat — chip reevaluate 용 직접 노출`` () =
    Assert.Equal(Some Png, AttachmentInfo.tryGetImageFormat (Image("a.png", [|0uy|], Png)))
    Assert.Equal(Some Webp, AttachmentInfo.tryGetImageFormat (Image("b.webp", [|0uy|], Webp)))
    Assert.Equal(None, AttachmentInfo.tryGetImageFormat (Pdf("p.pdf", [||])))
    Assert.Equal(None, AttachmentInfo.tryGetImageFormat (TextFile("t.md", "x")))

/// Phase 3b (rev 15) — Capabilities schema 의 MaxPdfPages field 회귀.
/// optional `?maxPdfPages` 시그니처 + None / Some 분기.

[<Fact>]
let ``ImagesAndPdf without maxPdfPages → MaxPdfPages = None`` () =
    let caps = Capabilities.ImagesAndPdf(1024L, 1024L)
    Assert.Equal(None, caps.MaxPdfPages)

[<Fact>]
let ``ImagesAndPdf with maxPdfPages=100 → MaxPdfPages = Some 100`` () =
    let caps = Capabilities.ImagesAndPdf(1024L, 1024L, maxPdfPages = 100)
    Assert.Equal(Some 100, caps.MaxPdfPages)

[<Fact>]
let ``CapabilityPresets — Anthropic / OpenAI = 100p, Codex = N/A`` () =
    Assert.Equal(Some 100, CapabilityPresets.AnthropicWire.MaxPdfPages)
    Assert.True(CapabilityPresets.AnthropicWire.SupportsPdfNative)
    Assert.Equal(Some 100, CapabilityPresets.OpenAiApiWire.MaxPdfPages)
    Assert.True(CapabilityPresets.OpenAiApiWire.SupportsPdfNative)
    Assert.Equal(None, CapabilityPresets.CodexCliWire.MaxPdfPages)
    Assert.False(CapabilityPresets.CodexCliWire.SupportsPdfNative)

[<Fact>]
let ``ImagesOnly / TextOnly → MaxPdfPages = None`` () =
    Assert.Equal(None, (Capabilities.ImagesOnly 1024L).MaxPdfPages)
    Assert.Equal(None, Capabilities.TextOnly.MaxPdfPages)
