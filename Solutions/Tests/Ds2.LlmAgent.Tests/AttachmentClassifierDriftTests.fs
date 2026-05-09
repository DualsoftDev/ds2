module AttachmentClassifierDriftTests

open Xunit
open Ds2.LlmAgent
open Microsoft.FSharp.Reflection

/// 정책 19 SSOT — `AttachmentClassifier` 의 화이트리스트가 `ImageFormat` enum 과 1:1 매칭하는지,
/// 그리고 핵심 분류 동작이 회귀 없이 유지되는지 검증.
///
/// drift 시 LLM Chat 의 첨부 UI 가 silent 하게 잘못된 분류 (e.g. .png 가 RejectUnknown) 로 굳을 수 있어
/// build-time 에 잡는다. 새 image 포맷 추가 시 본 test 의 `cases` list + classifier 동시 갱신 필요.

[<Fact>]
let ``이미지 확장자 4종 정상 매핑`` () =
    Assert.Equal(AcceptImage Png, AttachmentClassifier.classify "x.png")
    Assert.Equal(AcceptImage Jpeg, AttachmentClassifier.classify "x.jpg")
    Assert.Equal(AcceptImage Jpeg, AttachmentClassifier.classify "x.JPEG")
    Assert.Equal(AcceptImage Gif, AttachmentClassifier.classify "x.gif")
    Assert.Equal(AcceptImage Webp, AttachmentClassifier.classify "x.webp")

[<Fact>]
let ``PDF accept`` () =
    Assert.Equal(AcceptPdf, AttachmentClassifier.classify "spec.pdf")
    Assert.Equal(AcceptPdf, AttachmentClassifier.classify "REPORT.PDF")

[<Fact>]
let ``텍스트 / 코드 화이트리스트 sample`` () =
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.txt")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.md")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.fs")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.cs")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.json")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.yaml")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "x.sql")

[<Fact>]
let ``명시 거부 — SVG / 실행파일 / 미디어 / 압축 / BMP·TIFF`` () =
    Assert.Equal(RejectExtension ".svg", AttachmentClassifier.classify "x.svg")
    Assert.Equal(RejectExtension ".exe", AttachmentClassifier.classify "x.exe")
    Assert.Equal(RejectExtension ".dll", AttachmentClassifier.classify "x.dll")
    Assert.Equal(RejectExtension ".mp4", AttachmentClassifier.classify "x.mp4")
    Assert.Equal(RejectExtension ".mp3", AttachmentClassifier.classify "x.mp3")
    Assert.Equal(RejectExtension ".zip", AttachmentClassifier.classify "x.zip")
    Assert.Equal(RejectExtension ".bmp", AttachmentClassifier.classify "x.bmp")
    Assert.Equal(RejectExtension ".tiff", AttachmentClassifier.classify "x.tiff")

[<Fact>]
let ``확장자 없는 파일 — Dockerfile / Makefile / .editorconfig`` () =
    Assert.Equal(AcceptText, AttachmentClassifier.classify "Dockerfile")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "DOCKERFILE")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "Makefile")
    Assert.Equal(AcceptText, AttachmentClassifier.classify ".editorconfig")
    Assert.Equal(AcceptText, AttachmentClassifier.classify "LICENSE")

[<Fact>]
let ``알 수 없는 확장자 / 빈 path — RejectUnknown`` () =
    Assert.Equal(RejectUnknown, AttachmentClassifier.classify "x.xyz")
    Assert.Equal(RejectUnknown, AttachmentClassifier.classify "noextension")
    Assert.Equal(RejectUnknown, AttachmentClassifier.classify "")
    Assert.Equal(RejectUnknown, AttachmentClassifier.classify "   ")

[<Fact>]
let ``ImageFormat enum 4 case 모두 classifier 가 매핑 + reflection 기반 case 수 drift`` () =
    // ImageFormat 에 새 case 추가 시 본 list 도 동시 갱신 — drift 시 본 test 가 fail.
    let cases = [
        Png, ".png"
        Jpeg, ".jpg"
        Gif, ".gif"
        Webp, ".webp"
    ]
    for (fmt, ext) in cases do
        Assert.Equal(AcceptImage fmt, AttachmentClassifier.classify (sprintf "x%s" ext))

    // M1 보강 — reflection 기반 case count assert. ImageFormat 에 새 case 추가되면 length 불일치로 fail
    // → cases list 갱신 + classifier 의 imageFormatOf 갱신 + ApiProviderFactory / Provider Capabilities 의
    //   `[Png; Jpeg; Gif; Webp]` literal 도 동시 갱신해야 함을 강제 알림.
    let unionCases = FSharpType.GetUnionCases(typeof<ImageFormat>)
    Assert.Equal(cases.Length, unionCases.Length)

[<Fact>]
let ``BOM 인코딩 추정 — UTF-8 / UTF-16 LE / UTF-16 BE / UTF-32`` () =
    let bomUtf8 = [| 0xEFuy; 0xBBuy; 0xBFuy; 0x41uy |]
    let bomUtf16Le = [| 0xFFuy; 0xFEuy; 0x41uy; 0x00uy |]
    let bomUtf16Be = [| 0xFEuy; 0xFFuy; 0x00uy; 0x41uy |]
    let bomUtf32Le = [| 0xFFuy; 0xFEuy; 0x00uy; 0x00uy |]

    Assert.Equal(System.Text.Encoding.UTF8.WebName, (AttachmentClassifier.detectEncoding bomUtf8).Encoding.WebName)
    Assert.True((AttachmentClassifier.detectEncoding bomUtf8).ConfidenceHigh)
    Assert.Equal(System.Text.Encoding.Unicode.WebName, (AttachmentClassifier.detectEncoding bomUtf16Le).Encoding.WebName)
    Assert.Equal(System.Text.Encoding.BigEndianUnicode.WebName, (AttachmentClassifier.detectEncoding bomUtf16Be).Encoding.WebName)
    Assert.Equal(System.Text.Encoding.UTF32.WebName, (AttachmentClassifier.detectEncoding bomUtf32Le).Encoding.WebName)

[<Fact>]
let ``strict UTF-8 통과 — BOM 없는 영어 텍스트`` () =
    let bytes = System.Text.Encoding.UTF8.GetBytes("hello world")
    let detect = AttachmentClassifier.detectEncoding bytes
    Assert.Equal(System.Text.Encoding.UTF8.WebName, detect.Encoding.WebName)
    Assert.False(detect.ConfidenceHigh)  // BOM 없으므로 strict 통과해도 confidence low
