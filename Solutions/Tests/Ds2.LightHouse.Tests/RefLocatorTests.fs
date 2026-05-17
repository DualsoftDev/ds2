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
[<InlineData("p=14#img=2")>]
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
let ``parsed → toStored 직접 조립 round-trip`` () =
    let r = {
        Main = { Unit = P; Value = "14" }
        Subs = [| { Key = Img; Value = "2" } |]
    }
    Assert.Equal("p=14#img=2", RefLocator.toStored r)
