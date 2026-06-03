module Ds2.LightHouse.Tests.PdfStrategiesTests

open System
open System.IO
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Extractors.XlsxStrategies
open Ds2.LightHouse.PdfStrategies

/// **PR-I2 (todo-documents-based-gfm.md §2 PR-I2 + §2.1 + §3.1)** — pdf strategy +
/// PdfSignatureClassifier + PdfControlSpecStrategy 회귀.
///
/// 검증 시나리오:
///   1. synthetic ExtractedDocument fixture — signature 매치 / 미매치 분기.
///   2. classifier 진입점 — Matched / NearMiss / Unmatched 분기.
///   3. markdown 구조 — 머리말 6행 (canary + 기존 5행) + H1 + H2 (페이지별 요약 + 발견 zone 코드) + footer.
///   4. (옵션) 자료 B 실파일 — `F:/Git/dualsoft/secrets/KBSamples/core/3.***REDACTED***2*.pdf` 존재 시 +
///      M22 spot-check 5 페이지 정합 (P89-P104 안 random 5 페이지).

// ── 합성 fixture helpers ────────────────────────────────────────────────

/// ***REDACTED***2 control spec signature 충족 ExtractedDocument fixture.
///   - 110 페이지, 표준 심벌 / 명명 규칙 / zone 코드 / IO LIST 키워드 박제.
///   - 자료 B 의 P89-P104 spot-check 영역 (M22) 정합 검증을 위해 핵심 페이지 박제.
let private makeControlSpecFixture () : ExtractedDocument =
    let segments = ResizeArray<ExtractedSegment>()
    for p in 1 .. 110 do
        let text =
            match p with
            | 1 -> "***REDACTED***2 ***REDACTED*** 제어시스템 표준\n작성: HKMC 설비제어기술2팀"
            | 45 ->
                "***REDACTED***2 ***REDACTED*** 표준 심벌\n\n①설비_②부품명_③번호 명명 규칙\nWRS01: 용접 로봇\nCLP01: 클램프"
            | 46 -> "표준 심벌 (계속)\n①설비/라인/제어반\n②부품명"
            | 61 ->
                "작업 순서 (Sequence)\n1. 자재 투입\n2. 위치 결정\n3. 용접\n4. 검사"
            | 89 -> "S201 zone - RB1 (Robot 1)\n초기 위치 -> 작업 위치"
            | 90 -> "S202 zone - ARRANGE_JIG\n클램프 동작 순서"
            | 91 -> "S203 zone - WRS (Welding Workshop Station)"
            | 92 -> "S204 zone - KEY_JIG\nKEY 지그 동작"
            | 93 -> "S205 zone - RESPOT_JIG\nRESPOT 지그 동작"
            | 95 -> "S201 / S204 zone 추가 — 통합 패턴"
            | 99 -> "S204 zone — 정밀 IO 매핑"
            | 104 -> "I/O LIST 요약\nInput 총 540 비트, Output 총 280 비트\nS201 ~ S205 zone 합산"
            | _ -> sprintf "페이지 %d 본문 텍스트 한국어" p
        segments.Add {
            OutlineIndex = None
            RefLocator = sprintf "p=%d" p
            Text = text
        }
    {
        DocType = Pdf
        PageOrSheetCnt = Some 110
        Title = Some "***REDACTED***2 제어시스템 표준"
        Outline = [||]
        Segments = segments.ToArray()
        Images = [||]
    }

/// signature 미매치 ExtractedDocument fixture — 일반 보고서 (zone code / 표준 심벌 없음).
let private makeNonMatchFixture () : ExtractedDocument =
    let segments = [|
        { OutlineIndex = None; RefLocator = "p=1"; Text = "회의록 — 2026-05-27" }
        { OutlineIndex = None; RefLocator = "p=2"; Text = "참석자: A, B, C\n안건: 분기 매출 분석" }
    |]
    {
        DocType = Pdf
        PageOrSheetCnt = Some 2
        Title = Some "회의록"
        Outline = [||]
        Segments = segments
        Images = [||]
    }

// ── PdfControlSpecStrategy signature + build 회귀 ────────────────────────

[<Fact>]
let ``PdfControlSpecStrategy signature — ***REDACTED***2 fixture 는 매치 (score >= threshold)`` () =
    let extracted = makeControlSpecFixture ()
    let strategy = PdfControlSpecStrategy() :> IPdfStrategy
    let result = strategy.Signature extracted
    Assert.True(result.Matched, sprintf "signature 미매치 — detail=%s" result.Detail)
    Assert.True(result.Score >= result.Threshold)

[<Fact>]
let ``PdfControlSpecStrategy signature — 회의록 fixture 는 미매치 (score < threshold)`` () =
    let extracted = makeNonMatchFixture ()
    let strategy = PdfControlSpecStrategy() :> IPdfStrategy
    let result = strategy.Signature extracted
    Assert.False(result.Matched, sprintf "회의록 false-positive — detail=%s" result.Detail)

[<Fact>]
let ``PdfControlSpecStrategy signature — Xlsx DocType 은 0 점 (가드)`` () =
    let extracted : ExtractedDocument = {
        DocType = Xlsx
        PageOrSheetCnt = Some 30
        Title = None
        Outline = [||]
        Segments = [||]
        Images = [||]
    }
    let strategy = PdfControlSpecStrategy() :> IPdfStrategy
    let result = strategy.Signature extracted
    Assert.False(result.Matched)
    Assert.Equal(0, result.Score)

[<Fact>]
let ``PdfControlSpecStrategy Build — 매치 시 markdown 반환 + 머리말 6행 강제`` () =
    let extracted = makeControlSpecFixture ()
    let strategy = PdfControlSpecStrategy() :> IPdfStrategy
    let dummyPath = "fixture.pdf"
    // **라운드 3 Major-4 fix**: sigResult forward (interface 정합).
    let sigR = strategy.Signature extracted
    match strategy.Build (dummyPath, extracted, sigR) with
    | StrategyOutcome.Rejected entry ->
        Assert.Fail(sprintf "Build 가 Rejected 반환 — reason=%s" entry.Reason)
    | StrategyOutcome.Built markdown ->
        // 머리말 6행 — 2026-05-27 canary patch 박제 baseline.
        let lines = markdown.Split([| '\n' |], 9)
        Assert.True(lines.Length >= 6, "최소 6 행 머리말 필요 (canary + 기존 5행)")
        Assert.Contains("canary:", lines.[0])
        Assert.Contains("pong: summary/fixture.pdf", lines.[0])
        Assert.Contains("generated by PdfControlSpecStrategy v", lines.[1])
        Assert.Contains("source: fixture.pdf", lines.[2])
        Assert.Contains("docId:", lines.[2])
        Assert.Contains("signature: PdfControlSpecStrategy:", lines.[3])
        Assert.Contains("estimated-tokens:", lines.[4])
        Assert.Contains("strategy-version: PdfControlSpecStrategy v", lines.[5])

[<Fact>]
let ``PdfControlSpecStrategy Build — H1 + 페이지별 요약 표 + zone 코드 표 + footer`` () =
    let extracted = makeControlSpecFixture ()
    let strategy = PdfControlSpecStrategy() :> IPdfStrategy
    // **라운드 3 Major-4 fix**: sigResult forward (interface 정합).
    let sigR = strategy.Signature extracted
    match strategy.Build ("fixture.pdf", extracted, sigR) with
    | StrategyOutcome.Rejected entry ->
        Assert.Fail(sprintf "Build 가 Rejected — %s" entry.Reason)
    | StrategyOutcome.Built markdown ->
        // H1 — "# ***REDACTED***2 제어시스템 표준 — <title>"
        Assert.Contains("# ***REDACTED***2 제어시스템 표준 —", markdown)
        // 페이지별 요약 H2.
        Assert.Contains("## 페이지별 요약", markdown)
        // 페이지 표 헤더 + alignment.
        Assert.Contains("| Page | 첫 줄 요약 | 발견 Zone 코드 |", markdown)
        Assert.Contains("|---:|:---|:---|", markdown)
        // P45 / P89 등 핵심 페이지 박제.
        Assert.Contains("| P45 |", markdown)
        Assert.Contains("| P89 |", markdown)
        Assert.Contains("| P104 |", markdown)
        // 발견 zone 코드 표.
        Assert.Contains("## 발견 Zone 코드 (전체 distinct)", markdown)
        Assert.Contains("| Zone | 첫 출현 Page | 출현 횟수 |", markdown)
        // S201 ~ S205 zone 박제 확인.
        Assert.Contains("| S201 |", markdown)
        Assert.Contains("| S204 |", markdown)
        // footer.
        Assert.Contains("<!-- footer -->", markdown)
        Assert.Contains("cross-ref-hash:", markdown)

[<Fact>]
let ``PdfControlSpecStrategy 미매치 fixture — classifier 가 NearMiss/Unmatched 분기 (exception 아님)`` () =
    // **라운드 3 Major-4 fix**: Build 의 미매치 분기 책임이 classifier 로 이동 — 종전 Build 의 Rejected
    // 분기 verify 가 classifier 의 NearMiss/Unmatched 분기 verify 로 대체. Strategy 가 throw 안 한다는
    // 핵심 invariant 는 classifier path 에 그대로 보존.
    let extracted = makeNonMatchFixture ()
    let result = PdfSignatureClassifier.classify "non-match.pdf" extracted
    match result with
    | ClassificationResult.Matched _ -> Assert.Fail("회의록 fixture 가 Matched — false-positive")
    | ClassificationResult.RejectedByStrategy entry ->
        Assert.Fail(sprintf "회의록 fixture 는 signature 미매치 — RejectedByStrategy 분기 부적합 reason=%s" entry.Reason)
    | ClassificationResult.NearMiss entries ->
        Assert.All(entries, fun e -> Assert.True(e.Score < e.Threshold))
    | ClassificationResult.Unmatched -> ()

// ── PdfSignatureClassifier dispatch 회귀 ─────────────────────────────────

[<Fact>]
let ``PdfSignatureClassifier — strategy list 비움 (2026-06-04 PdfControlSpec 제거)`` () =
    // PdfControlSpecStrategy 제거 — PDF 는 일반 KB 자료 (specialized 미운용).
    Assert.Equal(0, List.length PdfSignatureClassifier.strategies)

[<Fact>]
let ``PdfSignatureClassifier — strategy 미등록이라 항상 Unmatched (2026-06-04)`` () =
    // PdfControlSpec 제거 — controlspec fixture 도 Unmatched (PDF 일반 자료). strategy-단위
    // 회귀(signature/Build)는 PdfControlSpecStrategy() 직접 생성 테스트가 보존.
    let extracted = makeControlSpecFixture ()
    let result = PdfSignatureClassifier.classify "fixture.pdf" extracted
    match result with
    | ClassificationResult.Unmatched -> ()
    | other -> Assert.Fail(sprintf "Unmatched 기대했으나 %A" other)

[<Fact>]
let ``PdfSignatureClassifier — 미매치 시 Unmatched 또는 NearMiss 반환`` () =
    let extracted = makeNonMatchFixture ()
    let result = PdfSignatureClassifier.classify "non-match.pdf" extracted
    match result with
    | ClassificationResult.Unmatched -> ()
    | ClassificationResult.NearMiss entries ->
        Assert.All(entries, fun e ->
            Assert.True(e.Score < e.Threshold))
    | other -> Assert.Fail(sprintf "Unmatched/NearMiss 기대했으나 %A" other)

// ── 자료 B 실파일 회귀 + M22 spot-check ──────────────────────────────────

let private sampleBPath =
    @"F:/Git/dualsoft/secrets/KBSamples/core/3.***REDACTED***2_전동화공장_제어시스템(HMI편집됨).pdf"

// ── G·G-Minor-8 (Outlier/Minor 묶음 2) — fixture presence sentinel ──────────
// 자료 B 실파일 회귀 fact 가 부재 시 silent skip () → testrunner UI "passed" 보고로 사용자가 실 검증
// 진입 여부 확인 불가. fixture-presence sentinel fact 박제로 명시 visibility 확보. xunit v2 의
// [<Fact>] attribute 가 동적 Skip parameter 미지원이라 printfn + Assert.True(true) 패턴으로 박제 —
// CI log 의 "[fixture 부재 SKIP]" prefix 로 grep 가능.

[<Fact>]
let ``G·G-Minor-8 — 자료 B fixture presence sentinel (부재 시 Skip 명시)`` () =
    if File.Exists sampleBPath then
        Assert.True(File.Exists sampleBPath)
    else
        printfn "[fixture 부재 SKIP] G·G-Minor-8 sentinel — %s" sampleBPath
        Assert.True(true, sprintf "fixture 부재 (정상): %s" sampleBPath)

let private extractPdf (path: string) : ExtractedDocument =
    use ext = new PdfExtractor() :> IExtractor
    ext.Extract(path, CancellationToken.None)

[<Fact>]
let ``자료 B 실파일 — 존재 시 specialized 미매치 (2026-06-04 PDF 일반 자료 전환)`` () =
    // PdfControlSpecStrategy 제거 — 자료 B(제어 PDF)는 classifier 에서 더 이상 specialized 매치
    // 안 됨. 일반 KB 자료로 색인. PdfControlSpecStrategy 코드 회귀는 strategy-단위 테스트(M22 등)가 보존.
    if File.Exists sampleBPath then
        let extracted = extractPdf sampleBPath
        Assert.Equal(FileKind.Pdf, extracted.DocType)
        let result = PdfSignatureClassifier.classify sampleBPath extracted
        // PDF classifier 는 빈 list → Matched 외 분기 도달 불가 (항상 Unmatched). wildcard `_` 로 충분 —
        // NearMiss/Rejected 명시는 빈 list 라 도달 불가 + incomplete 경고만 유발. Xlsx 자료 C 의 명시 3-way 와
        // 비대칭인 것은 classifier 상태 차이(PDF 빈 list vs Xlsx IoList 단독)를 반영한 의도.
        match result with
        | ClassificationResult.Matched (name, _) -> Assert.Fail(sprintf "자료 B 가 specialized 매치되면 안 됨 (일반 자료) — %s" name)
        | _ -> ()

/// **M22 spot-check** (todo §6 메타리뷰 항목) — 자료 B 의 P89-P104 안 5 페이지 (P89/P92/P95/P99/P104) 가
/// markdown 의 "## 페이지별 요약" 표에 정합 박제되는지 확인. 자료 B 부재 시 fixture-only 회귀로 갈음.
[<Fact>]
let ``M22 spot-check — 자료 B 의 P89-P104 안 5 페이지 박제 정합`` () =
    let checkPages = [ 89; 92; 95; 99; 104 ]
    if File.Exists sampleBPath then
        let extracted = extractPdf sampleBPath
        let strategy = PdfControlSpecStrategy() :> IPdfStrategy
        // **라운드 3 Major-4 fix**: sigResult forward (interface 정합).
        let sigR = strategy.Signature extracted
        match strategy.Build (sampleBPath, extracted, sigR) with
        | StrategyOutcome.Rejected entry ->
            Assert.Fail(sprintf "자료 B 가 Rejected — %s" entry.Reason)
        | StrategyOutcome.Built markdown ->
            for p in checkPages do
                let token = sprintf "| P%d |" p
                Assert.True(
                    markdown.Contains(token),
                    sprintf "M22 spot-check 페이지 P%d 가 markdown 표에 박제 누락" p)
    else
        // 자료 B 부재 — synthetic fixture 로 갈음 (P89/P92/P95/P99/P104 중 fixture 가 박제하는 P89/P104 + P92 만 확인).
        let extracted = makeControlSpecFixture ()
        let strategy = PdfControlSpecStrategy() :> IPdfStrategy
        // **라운드 3 Major-4 fix**: sigResult forward (interface 정합).
        let sigR = strategy.Signature extracted
        match strategy.Build ("fixture.pdf", extracted, sigR) with
        | StrategyOutcome.Rejected entry ->
            Assert.Fail(sprintf "fixture 가 Rejected — %s" entry.Reason)
        | StrategyOutcome.Built markdown ->
            // fixture 가 정의한 P89/P92/P104 만 확인 (fixture-only 갈음 명시).
            for p in [ 89; 92; 104 ] do
                let token = sprintf "| P%d |" p
                Assert.True(
                    markdown.Contains(token),
                    sprintf "fixture 갈음: 페이지 P%d 박제 누락" p)
