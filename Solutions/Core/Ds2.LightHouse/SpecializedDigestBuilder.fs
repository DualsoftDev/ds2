namespace Ds2.LightHouse

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// **PR-I4 (todo-documents-based-gfm.md §2 PR-I4 + documents-based-gfm.md §8.3 / §8.7)** —
/// active collection 의 `.lighthouse-kb/summary/*.md` 전부 읽어 합쳐 single string 반환.
///
/// 용도 = `SystemContentBuilder` (Llm.Shared.Api) 가 본 결과를 별 `TextContent` 로 wrap
/// → Anthropic prompt cache 의 **cache breakpoint 3** (base + KB digest 다음) 박제.
/// chat 시작 시 강제 주입 — LLM 이 자료 A/B/C 의 specialized markdown 전문을 첫 turn 부터 인식.
///
/// 토큰 예산 (광명2 3 자료 기준 잠정 — `documents-based-gfm.md` §8.4):
///   IoList ~30K + WorkOrder ~3K + PdfControlSpec ~5K = **~38K tokens**.
///   200K context 의 ~19%, 1M context 의 ~4% — 충분히 여유.
///
/// 합본 separator (각 markdown 사이) = `\n\n---\n\n` (markdown horizontal rule).
/// 각 strategy summary 는 이미 머리말 5행 + footer 7행 (StrategyMarkdown.buildHeader/buildFooter)
/// 박제되어 있으므로 본 builder 는 추가 머리말 박제 없이 단순 concat.
///
/// 멱등 — file 읽기는 path-sorted 순서 (deterministic). 동일 collection 재호출 시 byte-identical.
/// 사용자가 `summary/*.md` 1개를 외부에서 수정 시 cache breakpoint 3 invalidate (cross-ref-hash drift).
[<RequireQualifiedAccess>]
module SpecializedDigestBuilder =

    /// 합본 markdown 사이 separator. markdown horizontal rule 로 LLM 의 boundary 인식 강화.
    /// `documents-based-gfm.md` §8.3 의 SpecializedDigestBuilder pseudo-code 정합.
    [<Literal>]
    let private FileSeparator = "\n\n---\n\n"

    /// SpecializedDigestBuilder 산출 metadata — caller 진단 / cache breakpoint 3 size monitoring.
    /// `documents-based-gfm.md` §8.5.5 의 단일 collection 합산 ≤ 128K tokens 권장 한도 대비 측정.
    [<NoComparison; NoEquality>]
    type DigestMetadata = {
        /// 합본에 포함된 `summary/*.md` 파일 수.
        FileCount: int
        /// 합본 string 의 추정 토큰 수 (StrategyMarkdown.estimateTokens 동일 분모 3).
        EstimatedTokens: int
        /// `summary/*.md` 파일 중 가장 최근 last-write UTC (없으면 None).
        /// strategy 재색인 → cache breakpoint 3 invalidate 시점 감지에 사용.
        LastIndexedUtc: DateTime option
    }

    /// 합본 string + metadata. summary/ 디렉토리 부재 또는 빈 → 빈 합본 + FileCount=0.
    [<NoComparison; NoEquality>]
    type DigestResult = {
        /// 합본 markdown string. 빈 디렉토리 또는 부재 시 "".
        /// SystemContentBuilder 가 빈 string → cache breakpoint 3 박제 skip.
        Combined: string
        /// 합본 metadata.
        Metadata: DigestMetadata
    }

    /// `<collectionRoot>/.lighthouse-kb/summary/` 디렉토리의 `*.md` 합본 + metadata 반환.
    /// 디렉토리 부재 / 빈 → 빈 합본 (Combined = "", FileCount = 0).
    /// 파일 enumeration 순서 = path-sorted (deterministic, 멱등 cache hit 보장).
    let build (collectionRoot: string) : DigestResult =
        let dir = TextDumper.summaryDir collectionRoot
        if not (Directory.Exists dir) then
            { Combined = ""
              Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }
        else
            // path-sorted enumeration — Directory.GetFiles 는 OS 별 순서 비결정적, 명시 sort 로 cache hit 보장.
            let files = Directory.GetFiles(dir, "*.md") |> Array.sort
            if files.Length = 0 then
                { Combined = ""
                  Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }
            else
                let contents = files |> Array.map (fun f -> File.ReadAllText(f, Encoding.UTF8))
                let combined = String.concat FileSeparator contents
                let lastIndexed =
                    files
                    |> Array.map File.GetLastWriteTimeUtc
                    |> Array.max
                { Combined = combined
                  Metadata =
                    { FileCount = files.Length
                      EstimatedTokens = StrategyMarkdown.estimateTokens combined
                      LastIndexedUtc = Some lastIndexed } }

    /// 다중 collection 지원 — `documents-based-gfm.md` §8.3 의 `activeCollections seq` 정합.
    /// 각 collection 의 build 결과를 다시 FileSeparator 로 concat. metadata 는 합산.
    /// 빈 seq → 빈 합본. multi-collection 의 cache breakpoint 3 사용 시 호출.
    let buildMany (collectionRoots: string seq) : DigestResult =
        let results = collectionRoots |> Seq.map build |> Seq.toArray
        // 빈 합본 결과는 separator 박제 skip — 빈 string 사이 `\n\n---\n\n` 박제 방어.
        let nonEmpty = results |> Array.filter (fun r -> not (String.IsNullOrEmpty r.Combined))
        if nonEmpty.Length = 0 then
            { Combined = ""
              Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }
        else
            let combined =
                nonEmpty |> Array.map (fun r -> r.Combined) |> String.concat FileSeparator
            let totalFiles = nonEmpty |> Array.sumBy (fun r -> r.Metadata.FileCount)
            let lastIndexed =
                nonEmpty
                |> Array.choose (fun r -> r.Metadata.LastIndexedUtc)
                |> fun arr -> if arr.Length = 0 then None else Some (Array.max arr)
            { Combined = combined
              Metadata =
                { FileCount = totalFiles
                  EstimatedTokens = StrategyMarkdown.estimateTokens combined
                  LastIndexedUtc = lastIndexed } }

    // ── PR-G (Backlog G — todo-documents-based-gfm.md §2 PR-I5 review minor) ─────
    // 비동기 overload — `LlmChatViewModel.RefreshSpecializedDigestAsync` 가
    // `Task.Yield()` 후 동기 IO 수행하던 패턴을 정합 async 로 전환. 동기 `build` / `buildMany` 는
    // 회귀 0 보장 위해 그대로 유지 (테스트 + 기존 caller backward-compat).
    //
    // 정합 의무 (byte-equal): `buildAsync` 와 `build` 는 동일 collection 입력에 대해 byte-identical
    // 합본 + metadata 반환. enumeration / sort / separator / encoding 모두 동기 path SSOT 재사용.

    /// `<collectionRoot>/.lighthouse-kb/summary/*.md` 합본을 비동기 IO 로 읽어 `DigestResult` 반환.
    /// 동기 `build` 와 byte-identical (FileSeparator / path-sort / UTF-8 동일).
    /// cancellation 지원 — 각 `ReadAllTextAsync` 사이 token check.
    let buildAsync (collectionRoot: string) (ct: CancellationToken) : Task<DigestResult> =
        task {
            let dir = TextDumper.summaryDir collectionRoot
            if not (Directory.Exists dir) then
                return
                    { Combined = ""
                      Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }
            else
                let files = Directory.GetFiles(dir, "*.md") |> Array.sort
                if files.Length = 0 then
                    return
                        { Combined = ""
                          Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }
                else
                    let contents = Array.zeroCreate<string> files.Length
                    for i = 0 to files.Length - 1 do
                        ct.ThrowIfCancellationRequested()
                        let! txt = File.ReadAllTextAsync(files.[i], Encoding.UTF8, ct)
                        contents.[i] <- txt
                    let combined = String.concat FileSeparator contents
                    let lastIndexed =
                        files
                        |> Array.map File.GetLastWriteTimeUtc
                        |> Array.max
                    return
                        { Combined = combined
                          Metadata =
                            { FileCount = files.Length
                              EstimatedTokens = StrategyMarkdown.estimateTokens combined
                              LastIndexedUtc = Some lastIndexed } }
        }

    /// 다중 collection 비동기 overload. 동기 `buildMany` 와 byte-identical.
    /// 각 collection 의 `buildAsync` 결과를 순차 await (병렬 X — 결과 결정성 + 동기 path 정합).
    let buildManyAsync (collectionRoots: string seq) (ct: CancellationToken) : Task<DigestResult> =
        task {
            let roots = collectionRoots |> Seq.toArray
            let results = Array.zeroCreate<DigestResult> roots.Length
            for i = 0 to roots.Length - 1 do
                ct.ThrowIfCancellationRequested()
                let! r = buildAsync roots.[i] ct
                results.[i] <- r
            let nonEmpty = results |> Array.filter (fun r -> not (String.IsNullOrEmpty r.Combined))
            if nonEmpty.Length = 0 then
                return
                    { Combined = ""
                      Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }
            else
                let combined =
                    nonEmpty |> Array.map (fun r -> r.Combined) |> String.concat FileSeparator
                let totalFiles = nonEmpty |> Array.sumBy (fun r -> r.Metadata.FileCount)
                let lastIndexed =
                    nonEmpty
                    |> Array.choose (fun r -> r.Metadata.LastIndexedUtc)
                    |> fun arr -> if arr.Length = 0 then None else Some (Array.max arr)
                return
                    { Combined = combined
                      Metadata =
                        { FileCount = totalFiles
                          EstimatedTokens = StrategyMarkdown.estimateTokens combined
                          LastIndexedUtc = lastIndexed } }
        }
