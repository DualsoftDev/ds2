module Ds2.LightHouse.Tests.SpecializedDigestBuilderTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-I4 (todo-documents-based-gfm.md §2 PR-I4 + documents-based-gfm.md §8.3 / §8.7)** —
/// `SpecializedDigestBuilder` 합본 + metadata 회귀.
///
/// 검증 시나리오:
///   1. synthetic 3 markdown fixture (`summary/*.md`) → 합본 정합 + separator 정합 + metadata 정합.
///   2. 빈 `summary/` 디렉토리 → 빈 합본 (Combined="", FileCount=0).
///   3. `summary/` 디렉토리 부재 → graceful 빈 합본.
///   4. 멱등 — 동일 collection 재호출 시 byte-identical (path-sorted enumeration).
///   5. buildMany — 다중 collection 합본 정합 + 빈 collection skip.

// ── 공통 helpers ────────────────────────────────────────────────────────

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-spdig-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// `<root>/.lighthouse-kb/summary/` 박제 helper.
let private mkSummaryDir (collectionRoot: string) : string =
    let dir = TextDumper.summaryDir collectionRoot
    Directory.CreateDirectory dir |> ignore
    dir

/// 단일 `summary/*.md` 박제 — content + utf-8 (BOM 없음). TextDumper 와 동형 정합.
let private writeSummary (summaryDir: string) (filename: string) (content: string) =
    let path = Path.Combine(summaryDir, filename)
    File.WriteAllText(path, content, UTF8Encoding(false))
    path

// ── 시나리오 1: 3 markdown fixture → 합본 정합 ──────────────────────────────

[<Fact>]
let ``build - 3 summary markdown 합본 + separator 정합`` () =
    withTempDir (fun root ->
        let summaryDir = mkSummaryDir root
        // path-sorted enumeration 검증 위해 의도적으로 alphabetical order 와 다른 박제 순서 사용.
        writeSummary summaryDir "b-fixture.md" "# B Doc\n\nBody B" |> ignore
        writeSummary summaryDir "a-fixture.md" "# A Doc\n\nBody A" |> ignore
        writeSummary summaryDir "c-fixture.md" "# C Doc\n\nBody C" |> ignore

        let result = SpecializedDigestBuilder.build root

        // FileCount 정합.
        Assert.Equal(3, result.Metadata.FileCount)
        // path-sorted (a, b, c) 순서 합본 + separator `\n\n---\n\n`.
        let expected =
            "# A Doc\n\nBody A"
            + "\n\n---\n\n"
            + "# B Doc\n\nBody B"
            + "\n\n---\n\n"
            + "# C Doc\n\nBody C"
        Assert.Equal(expected, result.Combined)
        // EstimatedTokens = combined.Length / 3 (StrategyMarkdown.estimateTokens 정합).
        Assert.Equal(expected.Length / 3, result.Metadata.EstimatedTokens)
        // LastIndexedUtc Some — 방금 박제했으므로 not-None.
        Assert.True(result.Metadata.LastIndexedUtc.IsSome))

// ── 시나리오 2: 빈 summary/ 디렉토리 ─────────────────────────────────────

[<Fact>]
let ``build - 빈 summary 디렉토리 빈 합본`` () =
    withTempDir (fun root ->
        let _ = mkSummaryDir root  // 디렉토리 생성만, 파일 박제 없음.
        let result = SpecializedDigestBuilder.build root
        Assert.Equal("", result.Combined)
        Assert.Equal(0, result.Metadata.FileCount)
        Assert.Equal(0, result.Metadata.EstimatedTokens)
        Assert.True(result.Metadata.LastIndexedUtc.IsNone))

// ── 시나리오 3: summary/ 디렉토리 부재 graceful ──────────────────────────

[<Fact>]
let ``build - summary 디렉토리 부재 graceful 빈 합본`` () =
    withTempDir (fun root ->
        // `.lighthouse-kb/summary/` 미생성 — collection 색인 0 케이스.
        let result = SpecializedDigestBuilder.build root
        Assert.Equal("", result.Combined)
        Assert.Equal(0, result.Metadata.FileCount)
        Assert.True(result.Metadata.LastIndexedUtc.IsNone))

[<Fact>]
let ``build - .lighthouse-kb 자체 부재 graceful 빈 합본`` () =
    withTempDir (fun root ->
        // `.lighthouse-kb` 자체 부재 — collection 등록 후 색인 미수행 케이스.
        let result = SpecializedDigestBuilder.build root
        Assert.Equal("", result.Combined)
        Assert.Equal(0, result.Metadata.FileCount))

// ── 시나리오 4: 멱등 (byte-identical) ───────────────────────────────────

[<Fact>]
let ``build - 멱등 byte-identical 재호출`` () =
    withTempDir (fun root ->
        let summaryDir = mkSummaryDir root
        writeSummary summaryDir "alpha.md" "# Alpha\nContent" |> ignore
        writeSummary summaryDir "beta.md" "# Beta\nContent" |> ignore

        let r1 = SpecializedDigestBuilder.build root
        let r2 = SpecializedDigestBuilder.build root
        Assert.Equal(r1.Combined, r2.Combined)
        Assert.Equal(r1.Metadata.FileCount, r2.Metadata.FileCount))

// ── 시나리오 5: 단일 markdown ──────────────────────────────────────────

[<Fact>]
let ``build - 단일 markdown separator 박제 0`` () =
    withTempDir (fun root ->
        let summaryDir = mkSummaryDir root
        writeSummary summaryDir "only.md" "# Only Doc\n\nSingle" |> ignore

        let result = SpecializedDigestBuilder.build root
        Assert.Equal(1, result.Metadata.FileCount)
        // separator 박제 0 — 단일 파일은 content 그대로.
        Assert.Equal("# Only Doc\n\nSingle", result.Combined))

// ── 시나리오 6: buildMany 다중 collection ───────────────────────────────

[<Fact>]
let ``buildMany - 다중 collection 합본 + metadata 합산`` () =
    withTempDir (fun root1 ->
        withTempDir (fun root2 ->
            let dir1 = mkSummaryDir root1
            let dir2 = mkSummaryDir root2
            writeSummary dir1 "c1-a.md" "Coll1 A" |> ignore
            writeSummary dir1 "c1-b.md" "Coll1 B" |> ignore
            writeSummary dir2 "c2-a.md" "Coll2 A" |> ignore

            let result = SpecializedDigestBuilder.buildMany [ root1; root2 ]

            // 합산 FileCount = 2 + 1 = 3.
            Assert.Equal(3, result.Metadata.FileCount)
            // collection 순서대로 합본 + 각 collection 안 path-sorted + collection 사이 separator.
            // root1 = "Coll1 A\n\n---\n\nColl1 B", root2 = "Coll2 A".
            // 둘 사이도 separator.
            let expected =
                "Coll1 A"
                + "\n\n---\n\n"
                + "Coll1 B"
                + "\n\n---\n\n"
                + "Coll2 A"
            Assert.Equal(expected, result.Combined)))

[<Fact>]
let ``buildMany - 빈 collection skip 정합`` () =
    withTempDir (fun root1 ->
        withTempDir (fun rootEmpty ->
            withTempDir (fun root2 ->
                let dir1 = mkSummaryDir root1
                let dir2 = mkSummaryDir root2
                writeSummary dir1 "x.md" "Content X" |> ignore
                writeSummary dir2 "y.md" "Content Y" |> ignore
                // rootEmpty 는 summary/ 미생성 — 합본 skip 되어야 함 (separator 박제 없이).

                let result = SpecializedDigestBuilder.buildMany [ root1; rootEmpty; root2 ]

                // separator 가 빈 사이에 박제되면 `Content X\n\n---\n\n\n\n---\n\nContent Y` 처럼 깨짐.
                // 빈 collection 은 nonEmpty 필터로 skip 되어 separator 1개만 박제되어야 함.
                Assert.Equal("Content X" + "\n\n---\n\n" + "Content Y", result.Combined)
                Assert.Equal(2, result.Metadata.FileCount))))

[<Fact>]
let ``buildMany - 빈 seq 빈 합본`` () =
    let result = SpecializedDigestBuilder.buildMany []
    Assert.Equal("", result.Combined)
    Assert.Equal(0, result.Metadata.FileCount)
    Assert.True(result.Metadata.LastIndexedUtc.IsNone)

// ── 시나리오 7: 머리말 / footer 박제된 strategy 산출물 정합 (회귀) ──────

[<Fact>]
let ``build - strategy 산출물 헤더 + footer 그대로 합본 (가공 0)`` () =
    withTempDir (fun root ->
        let summaryDir = mkSummaryDir root
        // 실 strategy 가 박제하는 5행 머리말 + 본문 + 7행 footer 정합.
        let strategyMd =
            "<!-- generated by IoListStrategy v1.0 at 2026-05-27T00:00:00Z -->\n"
            + "<!-- source: sample.xlsx (docId: abc12345) -->\n"
            + "<!-- signature: IoListStrategy:5/5 -->\n"
            + "<!-- estimated-tokens: 100 -->\n"
            + "<!-- strategy-version: IoListStrategy v1.0 -->\n"
            + "\n"
            + "# IO List Sample\n\nBody"
            + "\n\n---\n\n"
            + "<!-- footer -->\n"
            + "<!-- cross-ref-hash: deadbeef -->\n"
            + "<!-- last-indexed: 2026-05-27T00:00:00Z -->\n"
        writeSummary summaryDir "iolist-abc12345-sample.md" strategyMd |> ignore

        let result = SpecializedDigestBuilder.build root
        // 단일 파일 — separator 박제 0 — content 그대로 (head/foot 가공 0).
        Assert.Equal(strategyMd, result.Combined)
        Assert.Equal(1, result.Metadata.FileCount))

// ── 시나리오 8: Backlog G (async wiring) ─────────────────────────────────
// `RefreshSpecializedDigestAsync` 의 `Task.Yield()` 후 동기 IO 패턴 → 정합 async 전환.
// 회귀 0 보장 = `buildAsync` / `buildManyAsync` 가 동기 path 와 byte-identical 결과 반환.

[<Fact>]
let ``buildAsync - 동기 build 와 byte-identical 합본 + metadata`` () =
    withTempDir (fun root ->
        let summaryDir = mkSummaryDir root
        writeSummary summaryDir "b-fixture.md" "# B Doc\n\nBody B ***REDACTED***2" |> ignore
        writeSummary summaryDir "a-fixture.md" "# A Doc\n\nBody A 한글" |> ignore
        writeSummary summaryDir "c-fixture.md" "# C Doc\n\nBody C" |> ignore

        let sync = SpecializedDigestBuilder.build root
        let async_ =
            (SpecializedDigestBuilder.buildAsync root CancellationToken.None)
                .GetAwaiter()
                .GetResult()

        // byte-identical Combined (FileSeparator / path-sort / UTF-8 동일).
        Assert.Equal(sync.Combined, async_.Combined)
        Assert.Equal(sync.Metadata.FileCount, async_.Metadata.FileCount)
        Assert.Equal(sync.Metadata.EstimatedTokens, async_.Metadata.EstimatedTokens)
        // LastIndexedUtc 도 동일 file system snapshot.
        Assert.Equal(sync.Metadata.LastIndexedUtc, async_.Metadata.LastIndexedUtc))

[<Fact>]
let ``buildAsync - summary 부재 graceful 빈 합본`` () =
    withTempDir (fun root ->
        let result =
            (SpecializedDigestBuilder.buildAsync root CancellationToken.None)
                .GetAwaiter()
                .GetResult()
        Assert.Equal("", result.Combined)
        Assert.Equal(0, result.Metadata.FileCount)
        Assert.True(result.Metadata.LastIndexedUtc.IsNone))

[<Fact>]
let ``buildAsync - 빈 summary 디렉토리 빈 합본`` () =
    withTempDir (fun root ->
        let _ = mkSummaryDir root
        let result =
            (SpecializedDigestBuilder.buildAsync root CancellationToken.None)
                .GetAwaiter()
                .GetResult()
        Assert.Equal("", result.Combined)
        Assert.Equal(0, result.Metadata.FileCount))

[<Fact>]
let ``buildManyAsync - 동기 buildMany 와 byte-identical 다중 collection`` () =
    withTempDir (fun root1 ->
        withTempDir (fun rootEmpty ->
            withTempDir (fun root2 ->
                let dir1 = mkSummaryDir root1
                let dir2 = mkSummaryDir root2
                writeSummary dir1 "c1-a.md" "Coll1 A" |> ignore
                writeSummary dir1 "c1-b.md" "Coll1 B" |> ignore
                writeSummary dir2 "c2-a.md" "Coll2 A" |> ignore
                // rootEmpty 는 summary/ 미생성 — 동기/비동기 양쪽 모두 separator skip 정합.

                let roots = [ root1; rootEmpty; root2 ]
                let sync = SpecializedDigestBuilder.buildMany roots
                let async_ =
                    (SpecializedDigestBuilder.buildManyAsync roots CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()

                Assert.Equal(sync.Combined, async_.Combined)
                Assert.Equal(sync.Metadata.FileCount, async_.Metadata.FileCount)
                Assert.Equal(sync.Metadata.EstimatedTokens, async_.Metadata.EstimatedTokens))))

[<Fact>]
let ``buildManyAsync - 빈 seq 빈 합본`` () =
    let result =
        (SpecializedDigestBuilder.buildManyAsync [] CancellationToken.None)
            .GetAwaiter()
            .GetResult()
    Assert.Equal("", result.Combined)
    Assert.Equal(0, result.Metadata.FileCount)
    Assert.True(result.Metadata.LastIndexedUtc.IsNone)
