module Ds2.LightHouse.Tests.ChunkerTests

open System
open Xunit
open Ds2.LightHouse

/// todo-lighthouse-kb-index.md §4.8a — Chunker boundary 결정성.
///
/// Phase 1 default 한도 = 500 token. 한도 안이면 1 chunk, 초과 시 단락/문장/hard cascade.

let private mkSegment (text: string) =
    { OutlineIndex = None; RefLocator = "p=1"; Text = text }

[<Fact>]
let ``빈 입력 → chunk 0`` () =
    let chunks = Chunker.chunkify [| mkSegment "" |]
    Assert.Empty(chunks)

[<Fact>]
let ``단일 token → chunk 1`` () =
    let chunks = Chunker.chunkify [| mkSegment "Conveyor" |]
    Assert.Single(chunks) |> ignore
    Assert.Equal("Conveyor", chunks.[0].Text)
    Assert.Equal(0, chunks.[0].Ordinal)

[<Fact>]
let ``한도 안 — 1 chunk 유지`` () =
    let text = String.replicate 100 "한글 "    // ascii 약 100 + 한글 100 ≈ token 75 안
    let chunks = Chunker.chunkify [| mkSegment text |]
    Assert.Single(chunks) |> ignore

[<Fact>]
let ``한도 초과 + 단락 다수 → 다중 chunk`` () =
    // 각 단락 300 char ASCII ≈ 75 token. 8 단락 → 600 token. 단락 단위 보조 분할 발생.
    let paragraph = String.replicate 300 "x"
    let text = String.concat "\n\n" (List.replicate 8 paragraph)
    let chunks = Chunker.chunkify [| mkSegment text |]
    Assert.True(chunks.Length >= 2, sprintf "chunk count = %d, 한도 초과 시 ≥ 2 기대" chunks.Length)
    // 각 chunk 가 TokenCount ≤ Chunker.DefaultMaxTokens
    for c in chunks do
        Assert.True(c.TokenCount <= Chunker.DefaultMaxTokens,
                    sprintf "chunk token = %d > %d" c.TokenCount Chunker.DefaultMaxTokens)

[<Fact>]
let ``매우 긴 단일 단락 — 문장 단위 분할 후 hard 자르기 cascade`` () =
    // 한 단락에 4000 char ASCII ≈ 1000 token. 문장 부호 없음 → hard 자르기.
    let text = String.replicate 4000 "x"
    let chunks = Chunker.chunkify [| mkSegment text |]
    Assert.True(chunks.Length >= 2)
    // hard 자르기 결과 ordinal 0..n-1
    for i in 0 .. chunks.Length - 1 do
        Assert.Equal(i, chunks.[i].Ordinal)

[<Fact>]
let ``RefLocator 와 OutlineIndex 보존`` () =
    let seg = {
        OutlineIndex = Some 3
        RefLocator = "p=42"
        Text = "본문"
    }
    let chunks = Chunker.chunkify [| seg |]
    Assert.Single(chunks) |> ignore
    Assert.Equal("p=42", chunks.[0].RefLocator)
    Assert.Equal(Some 3, chunks.[0].OutlineIndex)

[<Fact>]
let ``boundary — 정확 한도 token 은 1 chunk 유지 (§4.8a SSOT)`` () =
    // ASCII char 4 → 1 token. 정확 maxTokens token = char 4 × maxTokens.
    let target = Chunker.DefaultMaxTokens
    let text = String.replicate (target * 4) "x"
    Assert.Equal(target, Chunker.estimateTokens text)
    let chunks = Chunker.chunkify [| mkSegment text |]
    Assert.Single(chunks) |> ignore

[<Fact>]
let ``boundary — 한도 + 1 token 초과는 분할 (§4.8a SSOT)`` () =
    // 한도 + 1 token 명시 입력 — splitByBudget cascade 진입 확인.
    let target = Chunker.DefaultMaxTokens
    let text = String.replicate ((target + 1) * 4) "y"
    Assert.True(Chunker.estimateTokens text > target)
    let chunks = Chunker.chunkify [| mkSegment text |]
    Assert.True(chunks.Length >= 2)
    for c in chunks do
        Assert.True(c.TokenCount <= target)

[<Fact>]
let ``estimateTokens 한국어 가중`` () =
    // 한글 2 char ≈ 1 token (ko+1)/2 = (2+1)/2 = 1
    // ASCII 4 char ≈ 1 token (ascii+3)/4 = (4+3)/4 = 1
    Assert.Equal(1, Chunker.estimateTokens "가나")
    Assert.Equal(1, Chunker.estimateTokens "abcd")
    Assert.Equal(0, Chunker.estimateTokens "")
