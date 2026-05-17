module Ds2.LightHouse.Tests.IndexerTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// todo-lighthouse-kb-index.md §4.8b — Indexer 전체 흐름 + 0-doc / 0-byte / idempotent / IndexerVersion bump.

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-ix-%s" (Guid.NewGuid().ToString("N")))
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
]

let private noProgress (_: IngestProgress) = ()

[<Fact>]
let ``0-doc collection — 빈 폴더 정상 ingest (index.db 생성 + Documents 0)`` () =
    withTempDir (fun dir ->
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Empty(results)
        Assert.True(File.Exists (SqliteStore.dbPath dir)))

[<Fact>]
let ``기본 흐름 — txt/md 파일 색인`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "첫 문서 본문" |> ignore
        writeFile dir "b.md" "# 헤더\n\n본문" |> ignore
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Equal(2, results.Length)
        for (_, r) in results do
            match r with
            | Ingested _ -> ()
            | other -> Assert.Fail(sprintf "기대 = Ingested, 실제 = %A" other))

[<Fact>]
let ``FileHash idempotent — 같은 파일 두 번 ingest → Documents 1개`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        let results2 = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Single(results2) |> ignore
        match snd results2.[0] with
        | Skipped reason -> Assert.Contains("already ingested", reason)
        | other -> Assert.Fail(sprintf "재 ingest 는 Skipped 기대, 실제 = %A" other))

[<Fact>]
let ``미지원 ext (.dwg) — Skipped`` () =
    withTempDir (fun dir ->
        let path = writeFile dir "design.dwg" "binary-ish"
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        let pair = results |> Array.find (fun (p, _) -> p = path)
        match snd pair with
        | Skipped reason -> Assert.Contains("unsupported ext", reason)
        | other -> Assert.Fail(sprintf "기대 = Skipped, 실제 = %A" other))

[<Fact>]
let ``rejected ext (.env) — Skipped`` () =
    withTempDir (fun dir ->
        let path = writeFile dir "secrets.env" "API_KEY=xxx"
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        let pair = results |> Array.find (fun (p, _) -> p = path)
        match snd pair with
        | Skipped reason -> Assert.Contains("rejected ext", reason)
        | other -> Assert.Fail(sprintf "기대 = Skipped, 실제 = %A" other))

[<Fact>]
let ``0-byte 파일 — extractor 가 빈 결과로 처리 (Ingested with 0 segments)`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "empty.txt")
        File.WriteAllBytes(path, [||])
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Single(results) |> ignore
        match snd results.[0] with
        | Ingested _ -> ()
        | other -> Assert.Fail(sprintf "기대 = Ingested, 실제 = %A" other))

[<Fact>]
let ``IndexerVersion drift → shadow rebuild 발생`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None

        // drift 유도
        let dbPath = SqliteStore.dbPath dir
        (
            use conn = SqliteStore.openConnection dbPath false
            SqliteStore.setMeta conn "indexer_version" "0.0.0"
        )

        // 재 ingest → shadow rebuild → indexer_version 이 Current 로 복귀
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        use conn = SqliteStore.openConnection dbPath false
        Assert.Equal(Some IndexerVersion.Current, SqliteStore.getMeta conn "indexer_version"))

[<Fact>]
let ``.lighthouse-kb 폴더 자체는 색인 대상에서 제외`` () =
    withTempDir (fun dir ->
        // 첫 ingest 후 .lighthouse-kb/index.db 가 생성됨
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None

        // .lighthouse-kb 안에 가짜 txt 추가 — 재 ingest 시 enumerate 에서 제외 확인
        let kbDir = SqliteStore.kbDir dir
        let bogus = Path.Combine(kbDir, "inside.txt")
        File.WriteAllText(bogus, "should not be ingested", Encoding.UTF8)
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        // 새 파일이 .lighthouse-kb 안이라 enumerate 단계에서 제외 — results 에 inside.txt 없음
        let touched = results |> Array.exists (fun (p, _) -> p = bogus)
        Assert.False(touched, "inside .lighthouse-kb/ 파일은 enumerate 에서 제외되어야 함"))
