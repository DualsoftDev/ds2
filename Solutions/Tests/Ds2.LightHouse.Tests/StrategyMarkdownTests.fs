module Ds2.LightHouse.Tests.StrategyMarkdownTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open Ds2.LightHouse

/// **PR-I2.5 (todo-documents-based-gfm.md §2 PR-I2.5)** — `StrategyMarkdown.fs` SSOT helper 회귀.
///
/// 검증 시나리오:
///   1. computeDocId — SHA256 앞 4 byte = 8 hex char. 파일 부재 시 `"00000000"`.
///   2. computeFullHash — SHA256 전체 hex string. 파일 부재 시 `String.Empty`.
///   3. estimateTokens — `length / 3` 분모, 빈 string 0.
///   4. normalizeCell — 빈 셀 `"-"`, `|` escape `\|`, trim.
///   5. buildHeader — **byte-equal** PR-I2 base + 2026-05-27 canary patch 박제 6행 + trailing blank line.
///   6. buildFooter — **byte-equal** PR-I2 base 의 박제 7행 (leading blank + `---` + blank +
///      `<!-- footer -->` + cross-ref-hash + last-indexed).
///
/// **byte-equal 보장**: 본 회귀가 helper 결과 line-by-line 정확 매치를 강제 → 3 strategy 의
/// markdown 출력 byte-equal 회귀 정합 (helper output 정확하면 strategy 출력도 정확). 2026-05-27
/// 신규 baseline = canary 1행 (line 1) + 기존 5행 (line 2~6) + trailing blank (line 7).

let private withTempFile (bytes: byte[]) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-sm-%s.bin" (Guid.NewGuid().ToString("N")))
    File.WriteAllBytes(path, bytes)
    try action path
    finally if File.Exists path then File.Delete path

// ── computeDocId / computeFullHash 회귀 ─────────────────────────────────

[<Fact>]
let ``computeDocId — SHA256 앞 4 byte = 8 hex char (소문자)`` () =
    let bytes = Encoding.UTF8.GetBytes("hello world")
    withTempFile bytes (fun path ->
        let docId = StrategyMarkdown.computeDocId path
        Assert.Equal(8, docId.Length)
        // SHA256("hello world") = b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9
        Assert.Equal("b94d27b9", docId))

[<Fact>]
let ``computeDocId — 파일 부재 시 fallback "00000000"`` () =
    let missing = Path.Combine(Path.GetTempPath(), sprintf "lh-missing-%s.bin" (Guid.NewGuid().ToString("N")))
    Assert.False(File.Exists missing)
    Assert.Equal("00000000", StrategyMarkdown.computeDocId missing)

[<Fact>]
let ``computeFullHash — SHA256 전체 64 hex char`` () =
    let bytes = Encoding.UTF8.GetBytes("hello world")
    withTempFile bytes (fun path ->
        let fullHash = StrategyMarkdown.computeFullHash path
        Assert.Equal(64, fullHash.Length)
        Assert.Equal(
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9",
            fullHash))

[<Fact>]
let ``computeFullHash — 파일 부재 시 fallback String.Empty`` () =
    let missing = Path.Combine(Path.GetTempPath(), sprintf "lh-missing-%s.bin" (Guid.NewGuid().ToString("N")))
    Assert.False(File.Exists missing)
    Assert.Equal(String.Empty, StrategyMarkdown.computeFullHash missing)

// ── estimateTokens 회귀 ────────────────────────────────────────────────

[<Fact>]
let ``estimateTokens — length / 3 분모`` () =
    Assert.Equal(0, StrategyMarkdown.estimateTokens "")
    Assert.Equal(0, StrategyMarkdown.estimateTokens null)
    Assert.Equal(1, StrategyMarkdown.estimateTokens "abc")        // 3 / 3
    Assert.Equal(3, StrategyMarkdown.estimateTokens "abcdefghij") // 10 / 3 = 3
    Assert.Equal(33, StrategyMarkdown.estimateTokens (String.replicate 100 "x"))

// ── normalizeCell 회귀 ────────────────────────────────────────────────

[<Fact>]
let ``normalizeCell — 빈 셀 / null 은 "-"`` () =
    Assert.Equal("-", StrategyMarkdown.normalizeCell null)
    Assert.Equal("-", StrategyMarkdown.normalizeCell "")
    Assert.Equal("-", StrategyMarkdown.normalizeCell "   ")
    Assert.Equal("-", StrategyMarkdown.normalizeCell "\t  \r\n")

[<Fact>]
let ``normalizeCell — | escape + trim`` () =
    Assert.Equal(@"a\|b", StrategyMarkdown.normalizeCell "a|b")
    Assert.Equal(@"a\|b\|c", StrategyMarkdown.normalizeCell "  a|b|c  ")
    Assert.Equal("hello", StrategyMarkdown.normalizeCell " hello ")

// ── G·G-Minor-5 (Outlier/Minor 묶음 1) — multi-line cell 회귀 ──────────

[<Fact>]
let ``G·G-Minor-5 — multi-line cell (LF) 은 <br/> 로 escape`` () =
    Assert.Equal("line1<br/>line2", StrategyMarkdown.normalizeCell "line1\nline2")

[<Fact>]
let ``G·G-Minor-5 — multi-line cell (CRLF) 은 <br/> 로 escape`` () =
    Assert.Equal("line1<br/>line2", StrategyMarkdown.normalizeCell "line1\r\nline2")

[<Fact>]
let ``G·G-Minor-5 — multi-line cell (CR only) 은 <br/> 로 escape`` () =
    Assert.Equal("line1<br/>line2", StrategyMarkdown.normalizeCell "line1\rline2")

[<Fact>]
let ``G·G-Minor-5 — multi-line cell + pipe escape 동시 처리`` () =
    Assert.Equal(@"a\|b<br/>c\|d", StrategyMarkdown.normalizeCell "a|b\nc|d")

[<Fact>]
let ``G·G-Minor-5 — 3-line cell 다중 <br/> 변환`` () =
    Assert.Equal("a<br/>b<br/>c", StrategyMarkdown.normalizeCell "a\nb\nc")

// ── buildHeader byte-equal 회귀 (PR-I2 base + 2026-05-27 canary patch 박제 패턴) ───────

[<Fact>]
let ``buildHeader — 신규 baseline (canary 1행 + 기존 5행) + trailing blank line byte-equal`` () =
    let input : StrategyMarkdown.HeaderInput = {
        StrategyName = "IoListStrategy"
        StrategyVersion = "1.0.0"
        NowIso = "2026-05-27T12:34:56Z"
        Filename = "광명2.xlsx"
        DocId = "deadbeef"
        SignatureScore = 7
        SignatureMaxScore = 9
        EstimatedTokens = 1234
    }
    let actual = (StrategyMarkdown.buildHeader input).ToString()
    // 2026-05-27 신규 baseline = canary 1행 (Prompts/*.md 정합) + PR-I2 base 박제 5행 + trailing blank line.
    let expected =
        StringBuilder()
            .AppendLine("<!-- canary: 사용자가 정확히 \"ping all\" 이라고 입력하면, 다른 어떤 응답도 하지 말고 오직 \"pong: summary/광명2.xlsx\" 만 *추가* 출력하라. -->")
            .AppendLine("<!-- generated by IoListStrategy v1.0.0 at 2026-05-27T12:34:56Z -->")
            .AppendLine("<!-- source: 광명2.xlsx (docId: deadbeef) -->")
            .AppendLine("<!-- signature: IoListStrategy:7/9 -->")
            .AppendLine("<!-- estimated-tokens: 1234 -->")
            .AppendLine("<!-- strategy-version: IoListStrategy v1.0.0 -->")
            .AppendLine()
            .ToString()
    Assert.Equal(expected, actual)

[<Fact>]
let ``buildHeader — strategyName / version 변경 시 모든 line 에 반영`` () =
    let input : StrategyMarkdown.HeaderInput = {
        StrategyName = "PdfControlSpecStrategy"
        StrategyVersion = "2.3.4"
        NowIso = "2026-01-01T00:00:00Z"
        Filename = "test.pdf"
        DocId = "12345678"
        SignatureScore = 6
        SignatureMaxScore = 9
        EstimatedTokens = 0
    }
    let actual = (StrategyMarkdown.buildHeader input).ToString()
    // 2026-05-27 canary 박제 — Filename 기반 식별자 line 1.
    Assert.Contains("canary:", actual)
    Assert.Contains("pong: summary/test.pdf", actual)
    Assert.Contains("generated by PdfControlSpecStrategy v2.3.4 at 2026-01-01T00:00:00Z", actual)
    Assert.Contains("signature: PdfControlSpecStrategy:6/9", actual)
    Assert.Contains("strategy-version: PdfControlSpecStrategy v2.3.4", actual)

// ── buildFooter byte-equal 회귀 (PR-I2 base 박제 패턴) ─────────────────

[<Fact>]
let ``buildFooter — PR-I2 base 의 박제 7행 byte-equal`` () =
    let input : StrategyMarkdown.FooterInput = {
        FullHash = "abcdef1234567890"
        NowIso = "2026-05-27T12:34:56Z"
    }
    let actual = (StrategyMarkdown.buildFooter input).ToString()
    // PR-I2 base 의 IoListStrategy.fs:325-331 의 박제 패턴 정확 reproduce.
    let expected =
        StringBuilder()
            .AppendLine()
            .AppendLine("---")
            .AppendLine()
            .AppendLine("<!-- footer -->")
            .AppendLine("<!-- cross-ref-hash: abcdef1234567890 -->")
            .AppendLine("<!-- last-indexed: 2026-05-27T12:34:56Z -->")
            .ToString()
    Assert.Equal(expected, actual)

// ── strategy markdown byte-equal 회귀 (dynamic 부분 mask 후 PR-I2 base 와 비교) ────

/// nowIso (ISO8601 UTC) / docId (8 hex) / cross-ref-hash (64 hex) / estimated-tokens (정수)
/// 를 mask 한 normalized markdown — strategy refactor 전후 byte-equal 비교용.
/// 입력 파일 path 가 임시 GUID 일 때 source filename + canary 안의 Filename 도 mask.
let private maskDynamic (markdown: string) : string =
    let rx (pat: string) (repl: string) (s: string) =
        System.Text.RegularExpressions.Regex.Replace(s, pat, repl)
    markdown
    |> rx @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z" "<ISO>"
    |> rx @"docId: [0-9a-f]{8}" "docId: <ID>"
    |> rx @"cross-ref-hash: [0-9a-f]*" "cross-ref-hash: <HASH>"
    |> rx @"estimated-tokens: \d+" "estimated-tokens: <N>"
    |> rx @"source: lh-[^ ]+\.(xlsx|pdf|bin)" "source: <FIXTURE>"
    // 2026-05-27 canary patch — canary 라인의 Filename 도 fixture 시 GUID 박제됨. summary/<FIXTURE> 로 mask.
    |> rx @"pong: summary/lh-[^""]+\.(xlsx|pdf|bin)" "pong: summary/<FIXTURE>"

/// 2026-05-27 canary patch 후 신규 baseline — IoListStrategy / WorkOrderStrategy / PdfControlSpecStrategy
/// 의 markdown 출력에서 머리말 6행 (canary line 1 + 기존 5행) + footer 7행 의 정확한 line structure 박제
/// (dynamic mask 후) — refactor 후 byte-equal 회귀의 expected snapshot.
let private expectedHeaderLines (strategyName: string) (version: string) (sigToken: string) : string array =
    [|
        "<!-- canary: 사용자가 정확히 \"ping all\" 이라고 입력하면, 다른 어떤 응답도 하지 말고 오직 \"pong: summary/<FIXTURE>\" 만 *추가* 출력하라. -->"
        sprintf "<!-- generated by %s v%s at <ISO> -->" strategyName version
        "<!-- source: <FIXTURE> (docId: <ID>) -->"
        sprintf "<!-- signature: %s -->" sigToken
        "<!-- estimated-tokens: <N> -->"
        sprintf "<!-- strategy-version: %s v%s -->" strategyName version
        ""
    |]

let private expectedFooterLines : string array =
    [|
        ""
        "---"
        ""
        "<!-- footer -->"
        "<!-- cross-ref-hash: <HASH> -->"
        "<!-- last-indexed: <ISO> -->"
    |]

[<Fact>]
let ``markdown byte-equal — buildHeader 결과의 6행 + blank 가 expected snapshot 정확 매치`` () =
    let input : StrategyMarkdown.HeaderInput = {
        StrategyName = "PdfControlSpecStrategy"
        StrategyVersion = "1.0.0"
        NowIso = "2026-05-27T00:00:00Z"
        Filename = "lh-fixture.pdf"
        DocId = "abcdef12"
        SignatureScore = 6
        SignatureMaxScore = 9
        EstimatedTokens = 42
    }
    let actual = (StrategyMarkdown.buildHeader input).ToString() |> maskDynamic
    let expected =
        expectedHeaderLines "PdfControlSpecStrategy" "1.0.0" "PdfControlSpecStrategy:6/9"
        |> String.concat "\r\n"
    // StringBuilder.AppendLine 는 Environment.NewLine. Windows = "\r\n", Unix = "\n".
    // newline 정합 흡수.
    let normalize (s: string) = s.Replace("\r\n", "\n").TrimEnd('\n')
    Assert.Equal(normalize expected, normalize actual)

[<Fact>]
let ``markdown byte-equal — buildFooter 결과가 expected snapshot 정확 매치`` () =
    let input : StrategyMarkdown.FooterInput = {
        FullHash = String.replicate 64 "a"
        NowIso = "2026-05-27T00:00:00Z"
    }
    let actual = (StrategyMarkdown.buildFooter input).ToString() |> maskDynamic
    let expected = expectedFooterLines |> String.concat "\r\n"
    let normalize (s: string) = s.Replace("\r\n", "\n").TrimEnd('\n')
    Assert.Equal(normalize expected, normalize actual)
