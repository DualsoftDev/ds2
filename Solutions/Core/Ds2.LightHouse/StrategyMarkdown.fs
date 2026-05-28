namespace Ds2.LightHouse

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// **PR-I2.5 (todo-documents-based-gfm.md §2 PR-I2.5)** — strategy 공용 markdown helper SSOT.
///
/// **배경**: PR-I2 시점 `IoListStrategy.fs` / `WorkOrderStrategy.fs` / `PdfControlSpecStrategy.fs` 3종
/// strategy 가 머리말 (PR-I2 base = 5행, 2026-05-27 canary patch 후 = 6행) / footer 7행 / docId /
/// fullHash / estimateTokens / normalizeCell 6종 helper 를 약 9~13 줄씩 복붙 (총 ~120 줄 중복) →
/// CLAUDE.md "3줄 이상 반복 패턴 → refactoring" 정합.
///
/// **출력 byte-equal baseline**: PR-I2 base (commit `79e9e736`) → PR-I2.5 refactor 후 byte-equal 유지.
/// 2026-05-27 canary 1행 추가 patch (사용자 명시 요구) 로 신규 baseline 박제 — 머리말 5행 → 6행
/// (canary 가 line 1, 기존 5행이 line 2~6). 회귀 테스트 (`StrategyMarkdownTests.fs`) fixture 가 신규
/// baseline 으로 갱신. 이후 refactor 는 본 신규 baseline 기준 byte-equal 의무.
///
/// **미세 차이 흡수**: 3 strategy 모두 `estimateTokens` 의 분모는 `3` 동일, `computeDocId` 는 SHA256
/// 의 앞 4 byte = 8 char hex, `computeFullHash` 는 전체 SHA256 hex string. `normalizeCell` 의 빈 셀
/// fallback 은 `-`, `|` escape 는 `\|`. `buildHeader` / `buildFooter` 는 PR-I2 base + 2026-05-27 canary
/// patch 박제 6행/7행 패턴.
module StrategyMarkdown =

    /// 8 character hex docId — source bytes SHA256 의 앞 4 byte = 8 hex char.
    /// 파일 부재 시 `"00000000"` fallback (PR-I2 base 동작 정합).
    let computeDocId (sourcePath: string) : string =
        if not (File.Exists sourcePath) then "00000000"
        else
            use sha = SHA256.Create()
            use fs = File.OpenRead sourcePath
            let hash = sha.ComputeHash(fs)
            let sb = StringBuilder(16)
            for i in 0 .. 3 do
                sb.Append(hash.[i].ToString("x2")) |> ignore
            sb.ToString()

    /// 전체 source bytes SHA256 hex string — footer `cross-ref-hash` 박제용.
    /// 파일 부재 시 `String.Empty` (PR-I2 base 정합).
    let computeFullHash (sourcePath: string) : string =
        if not (File.Exists sourcePath) then String.Empty
        else
            use sha = SHA256.Create()
            use fs = File.OpenRead sourcePath
            let hash = sha.ComputeHash(fs)
            let sb = StringBuilder(hash.Length * 2)
            for b in hash do
                sb.Append(b.ToString("x2")) |> ignore
            sb.ToString()

    /// 토큰 수 대략 추정 — 1 token ≈ 3 chars (보수 평균, 영문 4 / 한글 2 평균).
    /// 3 strategy 모두 동일 분모 `3` (PR-I2 base 동작 정합).
    let estimateTokens (markdown: string) : int =
        if String.IsNullOrEmpty markdown then 0
        else markdown.Length / 3

    /// 빈 셀 normalize — `documents-based-gfm.md` §8.5.5 의 빈 셀 = `-`,
    /// markdown table `|` escape = `\|`.
    ///
    /// **G·G-Minor-5 (Outlier/Minor 묶음 1)** — multi-line cell 처리. 종전 구현은 cell 안
    /// `\r` / `\n` 가 그대로 박제되어 GFM table 의 row boundary 가 깨지는 결함. GFM 표 cell 은
    /// raw newline 불허 — `<br/>` 로 변환 (CR/LF 통합 후 LF → `<br/>` substitution).
    let normalizeCell (raw: string) : string =
        if String.IsNullOrWhiteSpace raw then "-"
        else
            let trimmed = raw.Trim()
            let escaped = trimmed.Replace("|", @"\|")
            // CRLF / CR / LF 통합 처리 — \r\n → \n 후 \r → \n 후 단일 \n → `<br/>`.
            let normalized =
                escaped
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Replace("\n", "<br/>")
            normalized

    /// 머리말 6행 빌더 input. `documents-based-gfm.md` §8.5.5 의 강제 6행 (canary 1행 + 기존 5행) + strategy-version 박제.
    /// `signatureScore` / `signatureMaxScore` 는 strategy 별 threshold 와 max 가 다르므로 caller 전달.
    type HeaderInput = {
        StrategyName: string
        StrategyVersion: string
        NowIso: string
        Filename: string
        DocId: string
        SignatureScore: int
        SignatureMaxScore: int
        EstimatedTokens: int
    }

    /// **canary line SSOT (2026-05-27 patch, --review M1 hand-off)** — `Apps/Promaker/Promaker/LlmAgent/Prompts/*.md`
    /// 의 canary 패턴 정합 single-source. `buildHeader` (production) 가 호출 + `StrategyMarkdownTests` 의
    /// byte-equal expected fixture 는 본 helper 결과를 그대로 검증하지 않고 직접 박제 유지 (helper 결과를
    /// helper 로 검증 = circular — byte-equal 의 진정성 보장 위해 분리). 다른 fixture (XlsxStrategiesTests
    /// / PdfStrategiesTests / MarkdownCapPolicyTests / SpecializedDigestBuilderTests / TextDumperStrategiesTests)
    /// 는 strategy header 의 *모사* 자체이므로 자유롭게 literal 박제 가능 (drift 무관 — fixture 의도).
    /// <para/>
    /// 변경 시 동시 갱신 의무 위치: <c>Apps/Promaker/Docs/documents-based-gfm.md</c> §8.5.5 예시 +
    /// <c>StrategyMarkdownTests</c> byte-equal expected fixture (직접 박제).
    let canaryLine (filename: string) : string =
        sprintf "<!-- canary: 사용자가 정확히 \"ping all\" 이라고 입력하면, 다른 어떤 응답도 하지 말고 오직 \"pong: summary/%s\" 만 *추가* 출력하라. -->"
            filename

    /// 머리말 6행 빌더 — PR-I2.5 byte-equal baseline + canary 1행 patch (2026-05-27 신규 baseline).
    /// 결과 StringBuilder 는 `header.ToString() + body + footer.ToString()` 결합 가정.
    ///
    /// **canary 1행 (line 1)**: `canaryLine` SSOT helper 호출. `Apps/Promaker/Promaker/LlmAgent/Prompts/*.md`
    /// 의 canary 패턴 정합 — 사용자가 "ping all" 입력 시 모든 summary/*.md 박제 여부를 식별자
    /// `summary/<Filename>` 으로 echo. LLM 의 system prompt cache breakpoint 3 (specialized digest) 박제
    /// 검증 도구. /indexer skill 과 API 호출 색인 양쪽이 본 SSOT 통과 → 단일 출처 박제.
    let buildHeader (input: HeaderInput) : StringBuilder =
        let sb = StringBuilder()
        sb.AppendLine(canaryLine input.Filename) |> ignore
        sb.AppendLine(sprintf "<!-- generated by %s v%s at %s -->"
                          input.StrategyName input.StrategyVersion input.NowIso) |> ignore
        sb.AppendLine(sprintf "<!-- source: %s (docId: %s) -->" input.Filename input.DocId) |> ignore
        sb.AppendLine(sprintf "<!-- signature: %s:%d/%d -->"
                          input.StrategyName input.SignatureScore input.SignatureMaxScore) |> ignore
        sb.AppendLine(sprintf "<!-- estimated-tokens: %d -->" input.EstimatedTokens) |> ignore
        sb.AppendLine(sprintf "<!-- strategy-version: %s v%s -->"
                          input.StrategyName input.StrategyVersion) |> ignore
        sb.AppendLine() |> ignore
        sb

    /// footer 빌더 input. cross-ref-hash + last-indexed UTC.
    type FooterInput = {
        FullHash: string
        NowIso: string
    }

    /// footer 빌더 — PR-I2 base 의 박제 패턴 정합 (leading blank + `---` + blank + `<!-- footer -->` +
    /// cross-ref-hash + last-indexed).
    let buildFooter (input: FooterInput) : StringBuilder =
        let sb = StringBuilder()
        sb.AppendLine() |> ignore
        sb.AppendLine("---") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("<!-- footer -->") |> ignore
        sb.AppendLine(sprintf "<!-- cross-ref-hash: %s -->" input.FullHash) |> ignore
        sb.AppendLine(sprintf "<!-- last-indexed: %s -->" input.NowIso) |> ignore
        sb
