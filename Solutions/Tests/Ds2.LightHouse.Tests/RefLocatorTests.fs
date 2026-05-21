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
let ``parsed → toStored 직접 조립 round-trip`` () =
    let r = {
        Main = { Unit = P; Value = "14" }
        Subs = [| { Key = Img; Value = "2" } |]
    }
    Assert.Equal("p=14#img=2", RefLocator.toStored r)
