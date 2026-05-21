namespace Ds2.LightHouse

open System
open System.Text
open System.Text.RegularExpressions

/// Extractor 의 raw segment → token 한도 chunk 분할 (todo-lighthouse-kb-index.md §3.8).
///
/// 구조 우선 — 한 segment = 한 페이지 / 슬라이드 / 시트 / 단락. 한도 (200~500 token) 안이면 1 chunk, 초과 시 보조 분할.
/// 보조 분할 순서: ① 빈 line 단위 (단락) → ② 문장 단위 (마침표/물음표/느낌표) → ③ 그래도 크면 hard 자르기.
///
/// token 추정은 자체 구현 — LlmAgent 의 `TokenEstimator` 미참조 (LightHouse 가 base, §3.2). 한국어 char/2 + ASCII char/4 가중.
[<RequireQualifiedAccess>]
module Chunker =

    /// Phase 1 default 한도. §3.8 의 "200~500 token" 권고 중 상한.
    [<Literal>]
    let DefaultMaxTokens = 500

    /// 문장 boundary regex — 영문 종결부호 (`.` / `?` / `!`) + CJK 종결부호 (`。` / `?` / `!`) lookbehind.
    /// trailing whitespace 까지 consume. 무공백 마지막 sentence 도 정상 분리 (review M1).
    let private sentenceBoundary = Regex(@"(?<=[.!?。?!])\s*", RegexOptions.Compiled)

    /// 한국어 / ASCII / 기타 가중 token 추정.
    /// (Hangul Syllables U+AC00..U+D7A3 + Hangul Jamo U+1100..U+11FF + Compat Jamo U+3130..U+318F 모두 한글로 카운트.)
    ///
    /// 한계: UTF-16 surrogate pair (U+10000 이상 보충 평면, 이모지 등) 는 2 char 로 카운트되어 token 추정이 약 2배 가중됨.
    /// 산업 KB 한정에서는 영향 미미하나 (이모지/SMP 거의 등장 X), 필요시 `StringInfo.GetTextElementEnumerator` 로 정밀화 가능 (Phase 2 review m3).
    let estimateTokens (text: string) : int =
        if String.IsNullOrEmpty text then 0
        else
            let mutable ko = 0
            let mutable ascii = 0
            let mutable other = 0
            for ch in text do
                let cp = int ch
                if (cp >= 0xAC00 && cp <= 0xD7A3)
                   || (cp >= 0x1100 && cp <= 0x11FF)
                   || (cp >= 0x3130 && cp <= 0x318F) then
                    ko <- ko + 1
                elif cp < 0x80 then ascii <- ascii + 1
                else other <- other + 1
            (ko + 1) / 2 + (ascii + 3) / 4 + (other + 1) / 2

    /// 한 segment 의 text 를 token 한도로 분할. 분할 단위 cascade.
    let private splitByBudget (text: string) (maxTokens: int) : string array =
        if estimateTokens text <= maxTokens then [| text.Trim() |]
        else
            let pieces = ResizeArray<string>()
            let buffer = StringBuilder()
            let flushBuffer () =
                if buffer.Length > 0 then
                    pieces.Add (buffer.ToString().Trim())
                    buffer.Clear() |> ignore

            // 1차: 빈 line 단위 단락. 빈 line 이 없으면 단일 문단으로 취급.
            let paragraphs =
                text.Split([| "\r\n\r\n"; "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun p -> p.Trim())
                |> Array.filter (fun p -> p.Length > 0)
                |> fun arr -> if Array.isEmpty arr then [| text.Trim() |] else arr

            let addToBuffer (s: string) =
                if buffer.Length = 0 then buffer.Append s |> ignore
                else buffer.Append("\n\n").Append s |> ignore

            // 단일 piece 가 한도 초과 시 문장 단위 분할.
            //
            // self-contained invariant (review M2): 진입 시점에 buffer 잔여물 선행 flush — 호출자 위치 무관하게
            // 내부 ordinal 정합성 보장. 종료 시점에도 flushBuffer 로 종결.
            let splitBySentences (piece: string) =
                flushBuffer ()
                let sents =
                    sentenceBoundary.Split(piece)
                    |> Array.map (fun s -> s.Trim())
                    |> Array.filter (fun s -> s.Length > 0)
                if sents.Length <= 1 then
                    // 문장 분리도 안 됨 → hard 자르기 (한도의 4x char 기준).
                    let approxCharLimit = maxTokens * 4
                    let mutable cursor = 0
                    while cursor < piece.Length do
                        let len = min approxCharLimit (piece.Length - cursor)
                        let slice = piece.Substring(cursor, len).Trim()
                        if slice.Length > 0 then pieces.Add slice
                        cursor <- cursor + len
                else
                    for s in sents do
                        let candidate =
                            if buffer.Length = 0 then s
                            else buffer.ToString() + " " + s
                        if estimateTokens candidate <= maxTokens then
                            buffer.Clear() |> ignore
                            buffer.Append candidate |> ignore
                        else
                            flushBuffer ()
                            buffer.Append s |> ignore
                    flushBuffer ()

            for para in paragraphs do
                let candidate =
                    if buffer.Length = 0 then para
                    else buffer.ToString() + "\n\n" + para
                if estimateTokens candidate <= maxTokens then
                    buffer.Clear() |> ignore
                    buffer.Append candidate |> ignore
                elif estimateTokens para > maxTokens then
                    flushBuffer ()
                    splitBySentences para
                else
                    flushBuffer ()
                    addToBuffer para

            flushBuffer ()
            pieces.ToArray()

    /// segments → chunks (token 한도 분할).
    ///
    /// 같은 RefLocator 안에서 ordinal 0..n. RefLocator 의 §3.13 resolution rule ("ref=p=14 는 chunk-level 합")
    /// 와 정합. Indexer 가 `SqliteStore.insertChunks` 로 commit.
    let chunkify (segments: ExtractedSegment array) : ExtractedChunk array =
        let maxTokens = DefaultMaxTokens
        let result = ResizeArray<ExtractedChunk>()
        for seg in segments do
            let pieces = splitByBudget seg.Text maxTokens
            let mutable ord = 0
            for piece in pieces do
                if not (String.IsNullOrWhiteSpace piece) then
                    result.Add {
                        OutlineIndex = seg.OutlineIndex
                        RefLocator = seg.RefLocator
                        Ordinal = ord
                        TokenCount = estimateTokens piece
                        Text = piece
                    }
                    ord <- ord + 1
        result.ToArray()
