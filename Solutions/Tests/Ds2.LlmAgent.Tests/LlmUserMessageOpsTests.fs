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

/// rev 20 (F3 외부 review) — EnforceCapabilityOrFail 의 size cap 검증 회귀.
/// 10MB OpenAI 이미지 → Claude (5MB) 전환 시 silent 통과 회귀 차단.

[<Fact>]
let ``Image 첨부 size cap 초과 → invalidArg`` () =
    // Claude 5MB cap. 6MB image → fail.
    let bytes6mb = Array.zeroCreate<byte> (6 * 1024 * 1024)
    let img = Image("big.png", bytes6mb, Png)
    let msg = LlmUserMessage.Create("review", [| img |])
    let ex = Assert.Throws<ArgumentException>(fun () ->
        LlmUserMessageOps.EnforceCapabilityOrFail CapabilityPresets.AnthropicWire msg)
    Assert.Contains("이미지 크기 cap 초과", ex.Message)
    Assert.Contains("big.png", ex.Message)

[<Fact>]
let ``Image 첨부 size cap 경계 (정확히 cap) → 통과`` () =
    let cap = 5L * 1024L * 1024L
    let bytes5mb = Array.zeroCreate<byte> (int cap)
    let img = Image("edge.png", bytes5mb, Png)
    let msg = LlmUserMessage.Create("review", [| img |])
    LlmUserMessageOps.EnforceCapabilityOrFail CapabilityPresets.AnthropicWire msg

[<Fact>]
let ``PDF 첨부 size cap 초과 → invalidArg`` () =
    // Anthropic 32MB cap. 33MB → fail.
    let bytes33mb = Array.zeroCreate<byte> (33 * 1024 * 1024)
    let pdf = Pdf("big.pdf", bytes33mb)
    let msg = LlmUserMessage.Create("review", [| pdf |])
    let ex = Assert.Throws<ArgumentException>(fun () ->
        LlmUserMessageOps.EnforceCapabilityOrFail CapabilityPresets.AnthropicWire msg)
    Assert.Contains("PDF 크기 cap 초과", ex.Message)

[<Fact>]
let ``ImagesOnly + MaxImageBytes None → size 무검사 (cap 미설정 시 통과)`` () =
    // ImagesOnly factory 는 MaxImageBytes 명시. 그러나 cap = None 인 case 도 record 직접 만들면 가능.
    let caps =
        { ImageFormats = Set.ofList [Png]
          SupportsPdfNative = false
          MaxImageBytes = None
          MaxPdfBytes = None
          MaxPdfPages = None
          MaxAttachmentCount = None }
    let bytes = Array.zeroCreate<byte> (100 * 1024 * 1024)  // 100MB — cap None 이면 통과
    let img = Image("huge.png", bytes, Png)
    let msg = LlmUserMessage.Create("x", [| img |])
    LlmUserMessageOps.EnforceCapabilityOrFail caps msg

/// rev 20 (F2 외부 review) — Codex 32K 한도 mitigation 회귀.
/// CodexCliArgs.measureArgsBytes + CodexArgsByteCap 보수 추정 일관성.

[<Fact>]
let ``CodexCliArgs.measureArgsBytes — empty → 0`` () =
    Assert.Equal(0, CodexCliArgs.measureArgsBytes [])

[<Fact>]
let ``CodexCliArgs.measureArgsBytes — ASCII tokens 합 + token-당 separator 3`` () =
    // ["abc"; "de"] → 3 + 2 + 2*3 = 11
    Assert.Equal(11, CodexCliArgs.measureArgsBytes [ "abc"; "de" ])

[<Fact>]
let ``CodexCliArgs.measureArgsBytes — 한국어 multi-byte UTF-8`` () =
    // "가" UTF-8 = 3 byte. ["가나"] → 6 + 1*3 = 9
    Assert.Equal(9, CodexCliArgs.measureArgsBytes [ "가나" ])

[<Fact>]
let ``CodexCliArgs.measureArgsBytes — null token 안전 무시`` () =
    Assert.Equal(3, CodexCliArgs.measureArgsBytes [ null ])  // 0 byte + 1 separator overhead

[<Fact>]
let ``CodexCliArgs.CodexArgsByteCap = 24576 (24K 보수 cap)`` () =
    Assert.Equal(24576, CodexCliArgs.CodexArgsByteCap)
