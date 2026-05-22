module Ds2.LightHouse.Tests.SummaryBuilderTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-H1 (todo-lighthouse-index-summary.md §11)** — SummaryBuilder 단위 fact.

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-sb-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private writeFile (dir: string) (name: string) (body: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, body, Encoding.UTF8)
    path

let private extractors () : IExtractor list = [
    new TextExtractor() :> IExtractor
    new PdfExtractor() :> IExtractor
    new OoxmlExtractor() :> IExtractor
    new ImageExtractor() :> IExtractor
]

let private noProgress (_: IngestProgress) = ()

let private indexAndBuild (dir: string) : SummaryBuilder.DocSummary array =
    let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
    use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) true
    SummaryBuilder.build conn


[<Fact>]
let ``빈 collection — DocSummary 0 + summary.md header 만`` () =
    withTempDir (fun dir ->
        let summaries = indexAndBuild dir
        Assert.Empty(summaries)
        let outPath = SummaryBuilder.write dir summaries
        Assert.True(File.Exists outPath)
        let body = File.ReadAllText outPath
        Assert.Contains("# Collection Summary", body)
        Assert.Contains("docs: 0", body)
        Assert.Contains("| 원본 | text dump | 요약 |", body))


[<Fact>]
let ``단일 txt — 첫 sentence 박제 (의미 단위 boundary)`` () =
    withTempDir (fun dir ->
        // 마침표 boundary 가 MinSentenceChars (40) 이후 위치하므로 cut 박제.
        writeFile dir "spec.txt" "이 문서는 컨베이어 시스템의 사양을 정의합니다. 모터 정격 12A. 보호 등급 IP65." |> ignore
        let summaries = indexAndBuild dir
        Assert.Single(summaries) |> ignore
        let s = summaries.[0]
        Assert.Equal("spec.txt", Path.GetFileName s.OriginalPath)
        // 첫 sentence boundary 박제 — "이 문서는 컨베이어 시스템의 사양을 정의합니다." (마침표 포함)
        Assert.Contains("컨베이어 시스템", s.Summary)
        Assert.Contains("정의합니다.", s.Summary))


[<Fact>]
let ``firstSentence — newline 정규화 (PDF 짧은 줄 단위 layout 보호)`` () =
    withTempDir (fun dir ->
        // 짧은 token 의 newline 분할 — newline 을 boundary 로 치면 "자동화기술실" 만 박제되는 결함.
        // 정규화 후 sentence boundary (마침표) 까지 cascade.
        writeFile dir "title.txt"
            "자동화기술실\n설비제어기술2팀\n2026년\n광명2 전동화공장 제어시스템 설명회입니다."
            |> ignore
        let summaries = indexAndBuild dir
        Assert.Single(summaries) |> ignore
        let s = summaries.[0]
        // newline → space 정규화 후 의미 단위 sentence 박제
        Assert.Contains("광명2 전동화공장", s.Summary)
        Assert.DoesNotContain("\n", s.Summary))


[<Fact>]
let ``firstSentence — digit guard (날짜/번호의 마침표는 boundary 아님)`` () =
    withTempDir (fun dir ->
        // "2022. 12. 15" 의 마침표 가 MinSentenceChars 이후 첫 후보면 잘못 cut 됨 (정상 의미 미박제).
        // digit guard 가 cascade → 다음 정상 boundary ("정의합니다.") 까지 박제.
        writeFile dir "report.txt"
            "보고서 발행일 2022. 12. 15. 이 문서는 산업 자동화 시스템의 표준 사양을 정의합니다."
            |> ignore
        let summaries = indexAndBuild dir
        Assert.Single(summaries) |> ignore
        let s = summaries.[0]
        Assert.Contains("산업 자동화", s.Summary)
        Assert.Contains("정의합니다.", s.Summary))


[<Fact>]
let ``MaxSentenceChars truncate — boundary 미발견 시 cut`` () =
    withTempDir (fun dir ->
        // 마침표 없는 매우 긴 token 의 stream — MaxSentenceChars (120) 까지 truncate.
        let long = String.replicate 30 "가나다라 "
        writeFile dir "long.txt" long |> ignore
        let summaries = indexAndBuild dir
        Assert.Single(summaries) |> ignore
        let s = summaries.[0]
        Assert.True(s.Summary.Length <= SummaryBuilder.MaxSentenceChars,
            sprintf "MaxSentenceChars (%d) 초과 — len=%d" SummaryBuilder.MaxSentenceChars s.Summary.Length))


[<Fact>]
let ``write — markdown table escape (pipe + newline)`` () =
    withTempDir (fun dir ->
        // 본문에 `|` 와 newline 포함 — escape 의무.
        writeFile dir "pipe.txt" "이 문서는 a|b|c 형식의 파이프 표기와\n여러 줄 사양을 정의합니다." |> ignore
        let summaries = indexAndBuild dir
        let outPath = SummaryBuilder.write dir summaries
        let body = File.ReadAllText outPath
        // `|` 가 cell 안에서 `\|` 로 escape (markdown 표 깨짐 차단)
        Assert.Contains("a\\|b\\|c", body)
        // newline 은 single space 로 치환 (table cell single line invariant)
        Assert.DoesNotContain("이 문서는 a\\|b\\|c 형식의 파이프 표기와\n", body))
