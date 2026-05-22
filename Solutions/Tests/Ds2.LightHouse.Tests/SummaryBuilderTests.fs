module Ds2.LightHouse.Tests.SummaryBuilderTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-H1/H2 (todo-lighthouse-index-summary.md §11)** — SummaryBuilder 단위 fact.
/// r5+: PR-H1 zero-cost fallback (firstSentence) 폐기 후 placeholder 박제 design 정합.

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
let ``SummaryText NULL — PendingPlaceholder 박제 (PR-H1 fallback 폐기 후)`` () =
    withTempDir (fun dir ->
        writeFile dir "spec.txt" "이 문서는 컨베이어 시스템의 사양을 정의합니다. 모터 정격 12A." |> ignore
        let summaries = indexAndBuild dir
        Assert.Single(summaries) |> ignore
        let s = summaries.[0]
        Assert.Equal("spec.txt", Path.GetFileName s.OriginalPath)
        // subagent batch (summary-update) 미진행 → SummaryText NULL → placeholder 박제
        Assert.Equal(SummaryBuilder.PendingPlaceholder, s.Summary))


[<Fact>]
let ``SummaryText 우선 분기 — DB stored summary 가 placeholder 보다 우선 (PR-H2)`` () =
    withTempDir (fun dir ->
        writeFile dir "spec.txt" "이 문서는 컨베이어 시스템의 사양을 정의합니다. 모터 정격 12A." |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        // subagent 가 박제한 summary 시뮬레이션 — SummaryStore.updateSummaryBatch 호출 후 build 검증.
        let stored = "***REDACTED***2 전동화공장 제어시스템 (PLC, RAPIENET, HMI 표준 사양)"
        use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) false
        let n = SummaryStore.updateSummaryBatch conn [ (1L, stored) ]
        Assert.Equal(1, n)
        conn.Dispose()
        use conn2 = SqliteStore.openConnection (SqliteStore.dbPath dir) true
        let summaries = SummaryBuilder.build conn2
        Assert.Single(summaries) |> ignore
        Assert.Equal(stored, summaries.[0].Summary))


[<Fact>]
let ``write — markdown table escape (pipe + newline 박제된 summary 본문)`` () =
    withTempDir (fun dir ->
        writeFile dir "pipe.txt" "본문은 무관 — DB 박제된 summary 만 검증." |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        // 박제된 summary 안 `|` / newline 포함 — escape 의무.
        let stored = "a|b|c 형식의 파이프 표기와\n여러 줄 사양"
        use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) false
        let n = SummaryStore.updateSummaryBatch conn [ (1L, stored) ]
        Assert.Equal(1, n)
        conn.Dispose()
        use conn2 = SqliteStore.openConnection (SqliteStore.dbPath dir) true
        let summaries = SummaryBuilder.build conn2
        let outPath = SummaryBuilder.write dir summaries
        let body = File.ReadAllText outPath
        // `|` 가 cell 안에서 `\|` 로 escape (markdown 표 깨짐 차단)
        Assert.Contains("a\\|b\\|c", body)
        // newline 은 single space 로 치환 (table cell single line invariant)
        Assert.DoesNotContain("a\\|b\\|c 형식의 파이프 표기와\n", body))
