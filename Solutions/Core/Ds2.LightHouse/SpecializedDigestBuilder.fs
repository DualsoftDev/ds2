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
/// 각 strategy summary 는 이미 머리말 6행 (canary 1행 + 기존 5행, 2026-05-27 patch) + footer 7행
/// (StrategyMarkdown.buildHeader/buildFooter) 박제되어 있으므로 본 builder 는 추가 머리말 박제 없이 단순 concat.
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

    /// **F·M5 (Outlier/Minor 묶음 1)** — 합본 결과에도 MarkdownCapPolicy.applyCap 적용.
    /// 단일 strategy summary 가 cap 안 들어가도 다중 collection 합본 시 cap 초과 가능 →
    /// cache breakpoint 3 의 system prompt 가 비대 / API token cap 초과 risk.
    /// Stage 0/1/2 결과의 `Markdown` 만 채택 (Stage 3 SplitParts 는 합본 path 에서 silent
    /// 무시 — caller 가 분할 박제 책임 없음). split 도달 시 첫 part 박제로 fallback.
    ///
    /// **fail-safe**: applyCap 의 Stage 1/2/3 escalation 이 표 구조 의존이라 일반 markdown
    /// (표 row 0 / data row 0) 합본은 흡수 불가 → fail-fast. 본 합본 path 는 LLM cache
    /// breakpoint 3 (사용자 진단 아님) 이라 fail-fast 보다 byte cap 강제 truncate + 안내
    /// comment 박제가 안전. try/with 로 escalation fail 흡수 후 단순 truncate fallback.
    let private capCombined (combined: string) : string =
        if String.IsNullOrEmpty combined then combined
        else
            let bytes = Encoding.UTF8.GetByteCount combined
            if bytes <= MarkdownCapPolicy.MaxMarkdownBytes then combined
            else
                try
                    let r = MarkdownCapPolicy.applyCap combined
                    r.Markdown
                with _ ->
                    // fail-fast escalation 실패 — UTF-8 boundary safe byte cap truncate.
                    let footer = "\n\n<!-- digest truncated at MarkdownCapPolicy.MaxMarkdownBytes -->\n"
                    let footerBytes = Encoding.UTF8.GetByteCount footer
                    let avail = MarkdownCapPolicy.MaxMarkdownBytes - footerBytes
                    let mutable cut = combined.Length
                    while cut > 0 && Encoding.UTF8.GetByteCount(combined.Substring(0, cut)) > avail do
                        cut <- cut - 1
                    combined.Substring(0, cut) + footer

    /// **A·m3 (Outlier/Minor 묶음 2) — build / buildAsync 공통 SSOT 빈 결과**.
    /// 디렉토리 부재 / 파일 0개 시 동일 빈 record 박제. 동기/비동기 path 가 모두 본 helper 호출 →
    /// metadata 초기값 drift 차단.
    let private emptyResult () : DigestResult =
        { Combined = ""
          Metadata = { FileCount = 0; EstimatedTokens = 0; LastIndexedUtc = None } }

    /// **A·m3 — build / buildAsync 공통 후처리 SSOT**. 파일 contents 가 채워진 시점부터의
    /// `String.concat FileSeparator` + cap + lastIndexed 계산 + DigestResult 박제를 통합.
    /// caller (동기 build / 비동기 buildAsync) 는 path-sorted files + contents 만 박제 책임.
    /// 동일 입력에 대해 byte-identical 출력 보장 (sync / async 회귀 0).
    let private finalizeSingle (files: string array) (contents: string array) : DigestResult =
        let combinedRaw = String.concat FileSeparator contents
        let combined = capCombined combinedRaw
        let lastIndexed =
            files
            |> Array.map File.GetLastWriteTimeUtc
            |> Array.max
        { Combined = combined
          Metadata =
            { FileCount = files.Length
              EstimatedTokens = StrategyMarkdown.estimateTokens combined
              LastIndexedUtc = Some lastIndexed } }

    /// **A·m3 — buildMany / buildManyAsync 공통 후처리 SSOT**. 각 root 의 DigestResult 배열을
    /// 빈 결과 skip → separator concat → cap → metadata 합산까지 통합.
    let private finalizeMany (results: DigestResult array) : DigestResult =
        let nonEmpty = results |> Array.filter (fun r -> not (String.IsNullOrEmpty r.Combined))
        if nonEmpty.Length = 0 then emptyResult ()
        else
            let combinedRaw =
                nonEmpty |> Array.map (fun r -> r.Combined) |> String.concat FileSeparator
            // F·M5 — multi-collection 합본도 cap 적용.
            let combined = capCombined combinedRaw
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

    /// `<collectionRoot>/.lighthouse-kb/summary/` 디렉토리의 `*.md` 합본 + metadata 반환.
    /// 디렉토리 부재 / 빈 → 빈 합본 (Combined = "", FileCount = 0).
    /// 파일 enumeration 순서 = path-sorted (deterministic, 멱등 cache hit 보장).
    let build (collectionRoot: string) : DigestResult =
        let dir = TextDumper.summaryDir collectionRoot
        if not (Directory.Exists dir) then emptyResult ()
        else
            // path-sorted enumeration — Directory.GetFiles 는 OS 별 순서 비결정적, 명시 sort 로 cache hit 보장.
            // **PR-N15**: `_user-guide-*.md` 가 underscore prefix 로 alphabetical sort 시 자동 맨 앞 박제 —
            // UserGuideImporter (N15) 산출물 ordering 정합 (별도 sort 로직 불필요).
            let files = Directory.GetFiles(dir, "*.md") |> Array.sort
            if files.Length = 0 then emptyResult ()
            else
                let contents = files |> Array.map (fun f -> File.ReadAllText(f, Encoding.UTF8))
                finalizeSingle files contents

    /// 다중 collection 지원 — `documents-based-gfm.md` §8.3 의 `activeCollections seq` 정합.
    /// 각 collection 의 build 결과를 다시 FileSeparator 로 concat. metadata 는 합산.
    /// 빈 seq → 빈 합본. multi-collection 의 cache breakpoint 3 사용 시 호출.
    /// **A·m3** — 후처리는 `finalizeMany` SSOT 위임 (sync / async 동일 helper).
    let buildMany (collectionRoots: string seq) : DigestResult =
        let results = collectionRoots |> Seq.map build |> Seq.toArray
        finalizeMany results

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
    /// **A·m3** — 후처리는 `finalizeSingle` SSOT 위임 (sync `build` 와 동일 helper).
    let buildAsync (collectionRoot: string) (ct: CancellationToken) : Task<DigestResult> =
        task {
            let dir = TextDumper.summaryDir collectionRoot
            if not (Directory.Exists dir) then return emptyResult ()
            else
                let files = Directory.GetFiles(dir, "*.md") |> Array.sort
                if files.Length = 0 then return emptyResult ()
                else
                    let contents = Array.zeroCreate<string> files.Length
                    for i = 0 to files.Length - 1 do
                        ct.ThrowIfCancellationRequested()
                        let! txt = File.ReadAllTextAsync(files.[i], Encoding.UTF8, ct)
                        contents.[i] <- txt
                    return finalizeSingle files contents
        }

    /// 다중 collection 비동기 overload. 동기 `buildMany` 와 byte-identical.
    /// 각 collection 의 `buildAsync` 결과를 순차 await (병렬 X — 결과 결정성 + 동기 path 정합).
    /// **A·m3** — 후처리는 `finalizeMany` SSOT 위임 (sync `buildMany` 와 동일 helper).
    let buildManyAsync (collectionRoots: string seq) (ct: CancellationToken) : Task<DigestResult> =
        task {
            let roots = collectionRoots |> Seq.toArray
            let results = Array.zeroCreate<DigestResult> roots.Length
            for i = 0 to roots.Length - 1 do
                ct.ThrowIfCancellationRequested()
                let! r = buildAsync roots.[i] ct
                results.[i] <- r
            return finalizeMany results
        }
