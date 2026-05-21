module Ds2.LightHouse.Tests.ClassifierTests

open Xunit
open Ds2.LightHouse

/// todo-lighthouse-kb-index.md §4.8a — KB 색인 라우팅 분류기.
///
/// chat 측 `AttachmentClassifier.classify` 와 *독립* — 본 테스트는 KB 측 `classifyForKb` 만.

[<Theory>]
[<InlineData("spec.pdf",     "Pdf")>]
[<InlineData("REPORT.PDF",   "Pdf")>]
[<InlineData("manual.docx",  "Docx")>]
[<InlineData("slides.pptx",  "Pptx")>]
[<InlineData("io.xlsx",      "Xlsx")>]
[<InlineData("readme.md",    "Markdown")>]
[<InlineData("notes.MARKDOWN", "Markdown")>]
[<InlineData("log.txt",      "Text")>]
[<InlineData("data.csv",     "Text")>]
[<InlineData("config.yaml",  "Text")>]
let ``지원 확장자 분류`` (file: string) (expected: string) =
    let kind = Classifier.classifyForKb file
    let name =
        match kind with
        | Pdf -> "Pdf" | Docx -> "Docx" | Pptx -> "Pptx"
        | Xlsx -> "Xlsx" | Text -> "Text" | Markdown -> "Markdown"
        | Unsupported _ -> "Unsupported"
    Assert.Equal(expected, name)

[<Theory>]
[<InlineData(".env")>]
[<InlineData(".exe")>]
[<InlineData(".dll")>]
[<InlineData(".zip")>]
[<InlineData(".png")>]
[<InlineData(".mp4")>]
let ``보안·정책 거부 확장자 (§6 m15) — rejectedExtensions set 직접 contains`` (ext: string) =
    Assert.True(Set.contains ext Classifier.rejectedExtensions,
                sprintf "%s 는 rejectedExtensions 에 포함되어야 함 (§6 m15)" ext)

[<Fact>]
let ``거부 확장자 → Unsupported`` () =
    match Classifier.classifyForKb "secrets.env" with
    | Unsupported ".env" -> ()
    | other -> Assert.Fail(sprintf "secrets.env 는 Unsupported \".env\" 기대, 실제 = %A" other)

[<Fact>]
let ``미등록 확장자 → Unsupported`` () =
    match Classifier.classifyForKb "design.dwg" with
    | Unsupported ".dwg" -> ()
    | other -> Assert.Fail(sprintf "design.dwg 는 Unsupported \".dwg\" 기대, 실제 = %A" other)

[<Fact>]
let ``빈 path → Unsupported 빈 ext`` () =
    match Classifier.classifyForKb "" with
    | Unsupported "" -> ()
    | other -> Assert.Fail(sprintf "빈 path 는 Unsupported \"\" 기대, 실제 = %A" other)

[<Fact>]
let ``supportedExtensions / rejectedExtensions 교집합 0 — drift 회귀 보호`` () =
    let supported = Classifier.supportedExtensions |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let overlap = Set.intersect supported Classifier.rejectedExtensions
    Assert.True(Set.isEmpty overlap,
                sprintf "supported ↔ rejected 교집합 = %A (정책 결함)" overlap)
