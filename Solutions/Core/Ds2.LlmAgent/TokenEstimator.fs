namespace Ds2.LlmAgent

open System

/// 첨부 토큰 사전 추정 (정책 5). 실제 과금은 provider 응답으로 확정 — 본 모듈은 *UI 안내용 추정* 만.
///
/// rev 4 (2026-05-09 / commit-3): UI 호출자 미존재 (dead code 의도). UI commit (commit-4..N) 부터 chip 라벨에 표시.
module TokenEstimator =

    /// Anthropic 이미지 모델 cap (외부 검증 통과 — todo §3.2).
    /// Opus 4.7 = 4,784 / 그 외 (Sonnet 등) = 1,568.
    let opus47ImageCap : int64 = 4_784L
    let defaultImageCap : int64 = 1_568L

    /// 한국어 multibyte 보정 계수 (정책 5: 1.5 ~ 2.4 — 보수적으로 2.0 사용).
    /// 한글 UTF-8 = 3 byte/글자 ≈ 2 token → bytes/4 의 4배 가까운 보정 필요한 경우 있음.
    [<Literal>]
    let koreanCorrectionFactor = 2.0

    /// Anthropic 기준 image token 추정. `(W * H) / 750` clamped to modelCap.
    /// 반환: (tokens, capReached) — capReached 가 true 면 잘림 안내 필요.
    let anthropicImageTokens (widthPx: int) (heightPx: int) (modelCap: int64) : int64 * bool =
        if widthPx <= 0 || heightPx <= 0 then 0L, false
        else
            let raw = int64 widthPx * int64 heightPx / 750L
            if raw >= modelCap then modelCap, true
            else raw, false

    /// 텍스트 token 추정. byteLen / 4 × correction.
    /// `koreanRatio` = 0.0 (영어만) ~ 1.0 (한국어만). 보정 = 1.0 + (factor - 1.0) * ratio.
    let textTokens (byteLen: int64) (koreanRatio: float) : int64 =
        if byteLen <= 0L then 0L
        else
            let baseTokens = float byteLen / 4.0
            let factor = 1.0 + (koreanCorrectionFactor - 1.0) * (max 0.0 (min 1.0 koreanRatio))
            int64 (baseTokens * factor)

    /// 텍스트의 한국어 비율 추정 (단순 휴리스틱). 앞 64KB sample 기준.
    /// Hangul Syllables (AC00-D7A3) + Jamo (1100-11FF) + Compat Jamo (3130-318F) / 공백 제외 글자.
    /// review 2차 M2: `EnumerateRunes` 사용 — surrogate pair (emoji / 한자보충 U+20000+) 가 2 char 가 아닌
    /// 1 rune 으로 카운트되어 분모 (total) 정확. 이전 `text.[i]` UTF-16 code unit loop 는 surrogate 를 2번 카운트해
    /// 한국어 비율 underestimate.
    let estimateKoreanRatio (text: string) : float =
        if String.IsNullOrEmpty text then 0.0
        else
            let sample =
                if text.Length > 65536 then text.Substring(0, 65536)
                else text
            let mutable hangul = 0
            let mutable total = 0
            for rune in sample.EnumerateRunes() do
                if not (System.Text.Rune.IsWhiteSpace rune) then
                    total <- total + 1
                    let cp = rune.Value
                    if (cp >= 0xAC00 && cp <= 0xD7A3) ||
                       (cp >= 0x1100 && cp <= 0x11FF) ||
                       (cp >= 0x3130 && cp <= 0x318F) then
                        hangul <- hangul + 1
            if total = 0 then 0.0
            else float hangul / float total

    /// PDF 페이지수 → token 추정 범위 (low, high). 정책 5: 페이지당 1,500 ~ 3,000.
    /// UI 라벨은 보통 high 표시 + "≈" 접두 (정확치 아님 명시).
    let pdfTokensRange (pages: int) : int64 * int64 =
        if pages <= 0 then 0L, 0L
        else int64 pages * 1_500L, int64 pages * 3_000L

    /// OpenAI gpt-4o vision tile 기반 토큰 (별도 공식). base 85 + 170/tile, 512×512 tile count.
    /// 알고리즘 (review 2차 C1): ① long side 2048 fit (단순 cap 아님 — 비율 보존 scale) →
    /// ② short side 768 fit (long 도 비율 따라 재축소). 4096×1000 → 약 765 토큰 (이전 1단계 fit = 1445 토큰, 2배 오차).
    let openAiGpt4oImageTokens (widthPx: int) (heightPx: int) : int64 =
        if widthPx <= 0 || heightPx <= 0 then 85L
        else
            let mutable shortSide = float (min widthPx heightPx)
            let mutable longSide = float (max widthPx heightPx)
            // ① long side 2048 fit (비율 보존) — 더 긴 변이 2048 초과면 양쪽 모두 동일 비율 축소.
            if longSide > 2048.0 then
                let s = 2048.0 / longSide
                longSide <- longSide * s
                shortSide <- shortSide * s
            // ② short side 768 fit (비율 보존) — 짧은 변이 768 초과면 양쪽 모두 동일 비율 축소.
            if shortSide > 768.0 then
                let s = 768.0 / shortSide
                shortSide <- shortSide * s
                longSide <- longSide * s
            let tilesShort = int (Math.Ceiling(shortSide / 512.0))
            let tilesLong = int (Math.Ceiling(longSide / 512.0))
            int64 (tilesShort * tilesLong * 170 + 85)
