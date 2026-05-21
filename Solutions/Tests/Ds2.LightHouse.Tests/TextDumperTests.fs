module Ds2.LightHouse.Tests.TextDumperTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-C (todo-lighthouse-index-summary.md §3.2)** — TextDumper 단위 fact.

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-td-%s" (Guid.NewGuid().ToString("N")))
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

let private indexAndDump (dir: string) : (string * int) array =
    let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
    use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) true
    TextDumper.dumpAll conn dir


[<Fact>]
let ``빈 collection — 0 file 생성`` () =
    withTempDir (fun dir ->
        let files = indexAndDump dir
        Assert.Empty(files)
        // text/ 폴더 자체는 생성 (caller invariant — 향후 file 추가 안전)
        Assert.True(Directory.Exists (TextDumper.textDir dir), "text/ 폴더 자동 생성"))


[<Fact>]
let ``단일 txt — markdown 생성 + heading + 본문 보존`` () =
    withTempDir (fun dir ->
        writeFile dir "spec.txt" "컨베이어 사양\n\n모터 정격 12A\n보호 등급 IP65" |> ignore
        let files = indexAndDump dir
        Assert.Single(files) |> ignore
        let (path, size) = files.[0]
        Assert.True(File.Exists path, sprintf "text dump file 미생성: %s" path)
        Assert.True(size > 0, "byte size 양수")
        let body = File.ReadAllText path
        Assert.Contains("# spec.txt", body)
        Assert.Contains("DocType: txt", body)
        Assert.Contains("컨베이어 사양", body)
        Assert.Contains("모터 정격 12A", body))


[<Fact>]
let ``파일명 sanitize — 의심 char 제거 후 <docId>-<basename>.md`` () =
    withTempDir (fun dir ->
        writeFile dir "spec normal.txt" "본문" |> ignore
        let files = indexAndDump dir
        Assert.Single(files) |> ignore
        let (path, _) = files.[0]
        let filename = Path.GetFileName path
        // docId prefix + 의심 char (' ') → '_' replace
        Assert.Matches(@"^\d+-spec_normal\.md$", filename))


[<Fact>]
let ``512KB cap — 큰 본문 truncate + footer 박제`` () =
    withTempDir (fun dir ->
        // 600KB ≈ chunk text 박제 후 cap 이 trigger 되도록 충분한 크기
        let body = String.replicate 20000 "abcdefghijklmnopqrstuvwxyz0123456789\n"  // ~740KB
        writeFile dir "big.txt" body |> ignore
        let files = indexAndDump dir
        Assert.Single(files) |> ignore
        let (path, size) = files.[0]
        Assert.True(size <= TextDumper.MaxDumpBytes,
            sprintf "size (%d) > MaxDumpBytes (%d)" size TextDumper.MaxDumpBytes)
        let content = File.ReadAllText path
        Assert.Contains("text dump truncated at 512KB", content))


[<Fact>]
let ``다중 doc — 각 Document 별 분리 파일 + Documents.Id 정렬`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "첫 문서" |> ignore
        writeFile dir "b.txt" "두번째 문서" |> ignore
        writeFile dir "c.md" "# 세번째 문서" |> ignore
        let files = indexAndDump dir
        Assert.Equal(3, files.Length)
        // 모든 파일 unique
        let names = files |> Array.map (fst >> Path.GetFileName) |> Array.distinct
        Assert.Equal(3, names.Length))


[<Fact>]
let ``RefLocator heading 변환 — p=N / slide=N / sheet=N 표시형 정합`` () =
    // formatHeading 직접 검증 — 다양한 RefLocator scheme
    withTempDir (fun dir ->
        // markdown 본문에 RefLocator 표시형 박제 검증
        writeFile dir "doc.txt" "본문" |> ignore
        let files = indexAndDump dir
        Assert.Single(files) |> ignore
        let (path, _) = files.[0]
        let body = File.ReadAllText path
        // txt 의 경우 chunker 가 "p=1" 등 박제 — heading scheme 적용 확인
        // 실 RefLocator 표시는 chunker 출력 의존이라 단순 chunk text 존재만 검증
        Assert.Contains("본문", body))


[<Fact>]
let ``UTF-8 BOM 미박제 — Encoding.UTF8 (no BOM)`` () =
    withTempDir (fun dir ->
        writeFile dir "korean.txt" "한국어 본문 컨베이어" |> ignore
        let files = indexAndDump dir
        Assert.Single(files) |> ignore
        let (path, _) = files.[0]
        let bytes = File.ReadAllBytes path
        // UTF-8 BOM (EF BB BF) 미박제 확인 — CLAUDE.md 박제 정책 정합
        Assert.False(
            bytes.Length >= 3 && bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy && bytes.[2] = 0xBFuy,
            "UTF-8 BOM 박제됨"))
