module Ds2.LightHouse.Tests.RefLocatorTests

open System
open Xunit
open Ds2.LightHouse

/// todo-lighthouse-kb-index.md §4.8a — RefLocator EBNF round-trip + 위반 거부.
///
/// `tryParse >> Option.map toStored = id` 동일성 보장이 §3.13 SSOT 의 핵심 invariant.

[<Theory>]
[<InlineData("p=14")>]
[<InlineData("slide=5")>]
[<InlineData("sheet=BOM")>]
[<InlineData("sheet=BOM!A1:D40")>]
// r1 Critical-2 regression guard — pdf 의 p=N#img=M 유지.
[<InlineData("p=14#img=2")>]
// Phase 2 Task 1/2 활성 (xlsx-pptx-images r2 Task 3) — slide/sheet × img sub-key **EBNF 일반 spec 보호**.
// **review MP2 명문화**: prod 의 OoxmlExtractor.ExtractPptx / ExtractXlsx 는 image RefLocator 박제 시
// `slide=N` / `sheet=<name>` 만 사용 + `Ordinal` 은 ExtractedImage record field 로 별도 박제
// (PDF/DOCX 와 동일 정책). 본 InlineData 4종은 *prod 가 생성하지 않는* `#img=N` 형태의 EBNF 일반 spec
// 보호 만 — attachment_read 의 ref 파서가 본 form 을 받을 가능성 + 향후 정책 변경 (RefLocator+Ordinal SSOT
// 통합) 시 회귀 차단 의도.
[<InlineData("slide=5#img=2")>]
[<InlineData("sheet=BOM#img=1")>]
[<InlineData("sheet=주요-사양#img=3")>]
[<InlineData("sheet=BOM!A1:D40#img=2")>]
// r2 Major-2 반론 검증 — 시트명 `=` 포함 round-trip (첫 `=` 만 split, Value="BOM=spec" 보존).
[<InlineData("sheet=BOM=spec")>]
[<InlineData("sheet=BOM=spec#img=1")>]
// Task 7 (r6) — standalone image 파일 RefLocator (N=1 고정, 향후 multi-frame 호환).
[<InlineData("image=1")>]
let ``저장형 → parsed → 저장형 round-trip`` (stored: string) =
    let parsed = RefLocator.parse stored
    Assert.Equal(stored, RefLocator.toStored parsed)

[<Theory>]
[<InlineData("")>]
[<InlineData("page=14")>]   // Unit 비매칭
[<InlineData("p=")>]        // 빈 value
[<InlineData("p=14#=2")>]   // SubKey 빈
[<InlineData("p=14#unknown=2")>]   // SubKey 비매칭
[<InlineData("p")>]         // = 없음
let ``EBNF 위반 입력은 tryParse None`` (stored: string) =
    Assert.Equal(None, RefLocator.tryParse stored)

[<Fact>]
let ``parse 는 EBNF 위반 시 ArgumentException`` () =
    Assert.Throws<ArgumentException>(fun () -> RefLocator.parse "page=14" |> ignore) |> ignore

[<Fact>]
let ``표시형 default 변환 — § 3.13 표`` () =
    Assert.Equal("p.14", RefLocator.formatDisplay (RefLocator.parse "p=14"))
    Assert.Equal("슬라이드 5", RefLocator.formatDisplay (RefLocator.parse "slide=5"))
    Assert.Equal("시트 BOM", RefLocator.formatDisplay (RefLocator.parse "sheet=BOM"))
    Assert.Equal("시트 BOM A1:D40", RefLocator.formatDisplay (RefLocator.parse "sheet=BOM!A1:D40"))
    Assert.Equal("p.14 그림 2", RefLocator.formatDisplay (RefLocator.parse "p=14#img=2"))

[<Fact>]
let ``표시형 변환 — Phase 2 slide/sheet × img sub-key (Task 3 regression guard)`` () =
    // r2 Task 3 박제 — pptx/xlsx 활성 이후 LLM citation 의 표시형 정합.
    Assert.Equal("슬라이드 5 그림 2", RefLocator.formatDisplay (RefLocator.parse "slide=5#img=2"))
    Assert.Equal("시트 BOM 그림 1", RefLocator.formatDisplay (RefLocator.parse "sheet=BOM#img=1"))
    Assert.Equal("시트 BOM A1:D40 그림 2", RefLocator.formatDisplay (RefLocator.parse "sheet=BOM!A1:D40#img=2"))

[<Fact>]
let ``표시형 변환 — Task 7 standalone image (image=N)`` () =
    // Task 7 (r6) — `image=1` → "이미지 1". sub-key 미박제 (단일 image 파일).
    Assert.Equal("이미지 1", RefLocator.formatDisplay (RefLocator.parse "image=1"))

[<Theory>]
[<InlineData("sheet=5-1. %23201", "5-1. #201")>]
[<InlineData("sheet=A%23B", "A#B")>]
[<InlineData("sheet=A%25B", "A%B")>]
[<InlineData("sheet=A%2523B", "A%23B")>]   // raw "A%23B" 의 round-trip (이중 escape)
let ``Backlog 5 — sheet-name % # escape round-trip`` (stored: string) (expectedValue: string) =
    // **Backlog 5 hotfix** — 산업 .xlsx 의 호기 번호 표기 (`5-1. #201`) round-trip 안전성.
    // `%` → `%25` / `#` → `%23` URL-style escape, tryParse 가 자동 decode.
    let parsed = RefLocator.tryParse stored
    Assert.True(parsed.IsSome)
    Assert.Equal(expectedValue, parsed.Value.Main.Value)
    Assert.Equal(stored, RefLocator.toStored parsed.Value)

[<Fact>]
let ``Backlog 5 — encodeMainValue / decodeMainValue (toStored) — caller 직접 활용 path`` () =
    // OoxmlExtractor.ExtractXlsx 가 `sprintf "sheet=%s" (RefLocator.encodeMainValue sheetName)` 박제.
    Assert.Equal("sheet=5-1. %23201", sprintf "sheet=%s" (RefLocator.encodeMainValue "5-1. #201"))
    Assert.Equal("sheet=A%25B%23C", sprintf "sheet=%s" (RefLocator.encodeMainValue "A%B#C"))
    // formatDisplay 는 Main.Value 가 이미 decoded 상태 (record) 라 raw 표시.
    let parsed = RefLocator.tryParse "sheet=5-1. %23201"
    Assert.True(parsed.IsSome)
    Assert.Equal("시트 5-1. #201", RefLocator.formatDisplay parsed.Value)

[<Fact>]
let ``parsed → toStored 직접 조립 round-trip`` () =
    let r = {
        Main = { Unit = P; Value = "14" }
        Subs = [| { Key = Img; Value = "2" } |]
    }
    Assert.Equal("p=14#img=2", RefLocator.toStored r)
