module Ds2.LightHouse.Tests.ImageExtractorTests

open System.IO
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

/// todo-lighthouse-kb-index-xlsx-pptx-images.md Task 7 (r6) — standalone image 색인 활성.
///
/// 활성 6 종 (Classifier SSOT):
///   - PNG / JPEG / GIF / WEBP : raw 보존, magic byte 검증
///   - EMF / WMF                : Metafile→PNG 변환 (System.Drawing.Common, Windows 의존)
///
/// per-image fail-safe — magic byte mismatch / 0 byte / 변환 실패 모두 빈 `Images` 반환.

let private withTempPath (ext: string) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-imgtest-%s%s" (System.Guid.NewGuid().ToString("N")) ext)
    try action path
    finally if File.Exists path then File.Delete path

/// minimal valid JPEG / GIF / BMP-not-supported bytes 를 System.Drawing.Bitmap 으로 생성 (Windows 의존).
let private makeBitmapBytes (fmt: System.Drawing.Imaging.ImageFormat) : byte[] =
    use bmp = new System.Drawing.Bitmap(1, 1)
    use ms = new MemoryStream()
    bmp.Save(ms, fmt)
    ms.ToArray()

[<Fact>]
let ``ImageExtractor.Supports — FileKind.Image 만 true`` () =
    use ext = new ImageExtractor() :> IExtractor
    Assert.True(ext.Supports FileKind.Image)
    Assert.False(ext.Supports Pdf)
    Assert.False(ext.Supports Docx)
    Assert.False(ext.Supports Pptx)
    Assert.False(ext.Supports Xlsx)
    Assert.False(ext.Supports Text)
    Assert.False(ext.Supports Markdown)
    Assert.False(ext.Supports (Unsupported ".bmp"))

[<Fact>]
let ``ImageExtractor — 정상 PNG (1x1 SamplePng) — Format=Png + Width/Height + RefLocator + Title`` () =
    withTempPath ".png" (fun path ->
        File.WriteAllBytes(path, SamplePng.bytes)
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(FileKind.Image, result.DocType)
        Assert.Equal(None, result.PageOrSheetCnt)
        Assert.True(result.Title.IsSome)
        Assert.Empty(result.Outline)
        Assert.Empty(result.Segments)
        Assert.Single(result.Images) |> ignore
        let img = result.Images.[0]
        Assert.Equal(Png, img.Format)
        Assert.Equal(Some 1, img.Width)
        Assert.Equal(Some 1, img.Height)
        Assert.Equal("image=1", img.RefLocator)
        Assert.Equal(1, img.Ordinal)
        Assert.Equal(SamplePng.bytes.Length, img.Bytes.Length))

[<Fact>]
let ``ImageExtractor — 정상 JPEG (1x1 Bitmap save) — Format=Jpeg + magic byte 통과`` () =
    let jpegBytes = makeBitmapBytes System.Drawing.Imaging.ImageFormat.Jpeg
    withTempPath ".jpg" (fun path ->
        File.WriteAllBytes(path, jpegBytes)
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Images) |> ignore
        let img = result.Images.[0]
        Assert.Equal(Jpeg, img.Format)
        // Bitmap save 결과 = 원본 + magic byte 통과. Width/Height parse 도 정상.
        Assert.Equal(Some 1, img.Width)
        Assert.Equal(Some 1, img.Height)
        Assert.Equal("image=1", img.RefLocator))

[<Fact>]
let ``ImageExtractor — 정상 GIF (1x1 Bitmap save) — Format=Gif`` () =
    let gifBytes = makeBitmapBytes System.Drawing.Imaging.ImageFormat.Gif
    withTempPath ".gif" (fun path ->
        File.WriteAllBytes(path, gifBytes)
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Images) |> ignore
        Assert.Equal(Gif, result.Images.[0].Format))

[<Fact>]
let ``ImageExtractor — 정상 WEBP (raw magic byte) — Format=Webp + Bytes 보존`` () =
    // System.Drawing.Bitmap.Save 는 WEBP 미지원 (Windows native). raw RIFF+WEBP header bytes 만 박제.
    // header 만 있는 invalid WEBP → magic byte 통과 + Width/Height=None + Bytes=원본 박제.
    let webpHeader = [|
        0x52uy; 0x49uy; 0x46uy; 0x46uy   // "RIFF"
        0x24uy; 0x00uy; 0x00uy; 0x00uy   // file size (dummy)
        0x57uy; 0x45uy; 0x42uy; 0x50uy   // "WEBP"
        // 이후 lossy/lossless chunk 박제 — invalid 도 magic byte 검증만 통과 의도.
        0x56uy; 0x50uy; 0x38uy; 0x4Cuy   // "VP8L" chunk header (lossless dummy)
        0x00uy; 0x00uy; 0x00uy; 0x00uy
    |]
    withTempPath ".webp" (fun path ->
        File.WriteAllBytes(path, webpHeader)
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Single(result.Images) |> ignore
        let img = result.Images.[0]
        Assert.Equal(Webp, img.Format)
        Assert.Equal(webpHeader.Length, img.Bytes.Length))

[<Fact>]
let ``ImageExtractor — magic byte mismatch (.png + JPEG bytes) — Warn + 빈 Images`` () =
    // 확장자 .png 인데 실제 JPEG bytes → VerifyMagicBytes 실패 → 빈 Images + Log.Warn.
    let jpegBytes = makeBitmapBytes System.Drawing.Imaging.ImageFormat.Jpeg
    withTempPath ".png" (fun path ->
        File.WriteAllBytes(path, jpegBytes)
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(FileKind.Image, result.DocType)
        Assert.Empty(result.Images))

[<Fact>]
let ``ImageExtractor — 0 byte 파일 — 빈 Images`` () =
    withTempPath ".png" (fun path ->
        File.WriteAllBytes(path, [||])
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(FileKind.Image, result.DocType)
        Assert.Empty(result.Images))

[<Fact>]
let ``ImageExtractor — EMF invalid bytes — 변환 실패 fail-safe + 빈 Images`` () =
    // Metafile constructor 가 invalid bytes 에 ArgumentException 발생 → catch + 빈 Images.
    withTempPath ".emf" (fun path ->
        File.WriteAllBytes(path, [| 0x00uy; 0x01uy; 0x02uy; 0x03uy |])
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        Assert.Equal(FileKind.Image, result.DocType)
        Assert.Empty(result.Images))

[<Fact>]
let ``ImageExtractor — Title = filename without extension`` () =
    withTempPath ".png" (fun path ->
        File.WriteAllBytes(path, SamplePng.bytes)
        use ext = new ImageExtractor() :> IExtractor
        let result = ext.Extract(path, CancellationToken.None)
        let expected = Path.GetFileNameWithoutExtension path
        Assert.Equal(Some expected, result.Title))
