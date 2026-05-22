module Ds2.LightHouse.Tests.SummaryStoreTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Microsoft.Data.Sqlite
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-H2 (todo-lighthouse-index-summary.md §11)** — SummaryStore 단위 fact (Step 2b subagent path).

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-ss-%s" (Guid.NewGuid().ToString("N")))
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

let private indexThen (dir: string) (action: SqliteConnection -> 'r) : 'r =
    let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
    use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) false
    action conn


[<Fact>]
let ``listPendingSummaries — 신규 색인 직후 모든 doc 박제 (SummaryText IS NULL)`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "A 문서 본문" |> ignore
        writeFile dir "b.txt" "B 문서 본문" |> ignore
        indexThen dir (fun conn ->
            let pending = SummaryStore.listPendingSummaries conn |> Seq.toArray
            Assert.Equal(2, pending.Length)
            // TextDumpPath 패턴 — SummaryBuilder.sanitizedTextDumpRel 재사용 정합
            Assert.All(pending, fun p ->
                Assert.StartsWith("text/", p.TextDumpPath)
                Assert.EndsWith(".md", p.TextDumpPath))))


[<Fact>]
let ``updateSummaryBatch — 단일 transaction UPDATE 후 listPending 에서 제외`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "A 본문" |> ignore
        writeFile dir "b.txt" "B 본문" |> ignore
        indexThen dir (fun conn ->
            let n = SummaryStore.updateSummaryBatch conn [ (1L, "A 요약") ]
            Assert.Equal(1, n)
            // 다음 listPending = b 만 (a 는 SummaryText 박제됨)
            let remaining = SummaryStore.listPendingSummaries conn |> Seq.toArray
            Assert.Equal(1, remaining.Length)
            Assert.Equal(2L, remaining.[0].DocId)))


[<Fact>]
let ``updateSummaryBatch — empty batch no-op (transaction 미생성)`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "A 본문" |> ignore
        indexThen dir (fun conn ->
            let n = SummaryStore.updateSummaryBatch conn Seq.empty
            Assert.Equal(0, n)
            // 다음 listPending = 그대로 1 (UPDATE 0 회)
            let remaining = SummaryStore.listPendingSummaries conn |> Seq.toArray
            Assert.Equal(1, remaining.Length)))


[<Fact>]
let ``updateSummaryBatch — 환각 docId 시 0 반환 (review B fix, r4)`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "A 본문" |> ignore
        indexThen dir (fun conn ->
            // 실제 docId = 1L (단일 doc), 환각 docId 999L 시도 — DB 의 UPDATE WHERE Id=999 는 0 row affected.
            // n <- n + 1 (구 박제) 결함 시 1 반환, n <- n + ExecuteNonQuery() (r4 fix) 시 0 반환.
            let n = SummaryStore.updateSummaryBatch conn [ (999L, "환각 doc 요약") ]
            Assert.Equal(0, n)
            // 정상 docId + 환각 docId 혼합 시 실제 affected row 만 count (1 + 0 = 1)
            let n2 = SummaryStore.updateSummaryBatch conn [ (1L, "정상 요약"); (888L, "환각") ]
            Assert.Equal(1, n2)))


[<Fact>]
let ``updateSummaryBatch — 다중 row atomic commit`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "A 본문" |> ignore
        writeFile dir "b.txt" "B 본문" |> ignore
        writeFile dir "c.txt" "C 본문" |> ignore
        indexThen dir (fun conn ->
            let n = SummaryStore.updateSummaryBatch conn [
                (1L, "A 요약")
                (2L, "B 요약")
                (3L, "C 요약")
            ]
            Assert.Equal(3, n)
            // 모든 doc 박제 → listPending 비어있음
            let remaining = SummaryStore.listPendingSummaries conn |> Seq.toArray
            Assert.Empty(remaining)))
