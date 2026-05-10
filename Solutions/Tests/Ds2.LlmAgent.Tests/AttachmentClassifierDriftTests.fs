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
let ``review M5 — 보안·정책 critical 거부 확장자 set 직접 contains assertion`` () =
    // classify sample 검사만으로는 rejectedExtensions set 에서 silent 삭제될 위험.
    // 보안 critical 항목 (XSS/실행/대용량/HEIC 등 미지원) 을 set 자체에 직접 contains 으로 강제.
    let critical = [
        ".svg"   // XSS / XXE
        ".exe"; ".dll"; ".msi"; ".bin"; ".scr"  // 실행 / 바이너리
        ".zip"; ".7z"; ".rar"; ".tar"; ".gz"     // 압축
        ".mp4"; ".mp3"                            // 미디어
        ".bmp"; ".tiff"; ".heic"                  // 미지원 이미지 포맷 (대용량 / Apple)
    ]
    for ext in critical do
        Assert.True(
            Set.contains ext AttachmentClassifier.rejectedExtensions,
            sprintf "rejectedExtensions 에서 %s 가 누락됨 — 보안·정책 critical drift" ext)

[<Fact>]
let ``review M5 — 텍스트 화이트리스트 critical 항목 set 직접 contains`` () =
    // 핵심 코드/문서 확장자 silent 삭제 방어. classify sample 보강.
    let critical = [
        ".txt"; ".md"; ".log"
        ".json"; ".xml"; ".yaml"
        ".fs"; ".fsi"; ".cs"
        ".ts"; ".js"; ".py"
        ".sql"; ".sh"; ".ps1"
    ]
    for ext in critical do
        Assert.True(
            Set.contains ext AttachmentClassifier.textExtensions,
            sprintf "textExtensions 에서 %s 가 누락됨" ext)

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

[<Fact>]
let ``invalid UTF-8 + invalid CP949 — UTF-8 replacement fallback (review m7)`` () =
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance)
    // 0xC3 0x28 = UTF-8 lead byte 후 invalid continuation. CP949 도 0xC3 0x28 (ASCII 0x28 두번째) 라 invalid.
    // → UTF-8 replacement fallback (ConfidenceHigh = false).
    let bytes = [| 0xC3uy; 0x28uy; 0x41uy; 0x42uy |]
    let detect = AttachmentClassifier.detectEncoding bytes
    Assert.Equal(System.Text.Encoding.UTF8.WebName, detect.Encoding.WebName)
    Assert.False(detect.ConfidenceHigh)

[<Fact>]
let ``CP949 fallback — 한국어 Windows 환경 .txt`` () =
    // CodePagesEncodingProvider 가 등록 안 된 단위 테스트 환경에서도 1회 등록 (Promaker.App 와 동일 패턴).
    // 등록은 idempotent — 중복 호출 무해.
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance)

    // 한국어 "안녕하세요" 를 CP949 (Windows-949, code page 949) 로 인코딩 → invalid UTF-8 sequence 생성.
    let cp949 = System.Text.Encoding.GetEncoding(949)
    let bytes = cp949.GetBytes("안녕하세요")
    let detect = AttachmentClassifier.detectEncoding bytes
    // strict UTF-8 fail → CP949 strict 통과 → CP949 반환 (UTF-16 LE 로 떨어지지 않음).
    Assert.Equal(949, detect.Encoding.CodePage)
    Assert.False(detect.ConfidenceHigh)
