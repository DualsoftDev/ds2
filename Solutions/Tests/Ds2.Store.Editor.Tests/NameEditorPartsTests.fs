module Ds2.Store.Editor.Tests.NameEditorPartsTests

open Ds2.Editor
open Xunit

[<Fact>]
let ``ForFallback wraps name as editable only`` () =
    let parts = NameEditorParts.ForFallback "SomeName"
    Assert.Equal("", parts.Prefix)
    Assert.Equal("SomeName", parts.Editable)
    Assert.Equal("", parts.Suffix)

[<Fact>]
let ``ForCall splits on last dot — alias to editable suffix is dotted apiName`` () =
    let parts = NameEditorParts.ForCall "Cyl1.ADV"
    Assert.Equal("", parts.Prefix)
    Assert.Equal("Cyl1", parts.Editable)
    Assert.Equal(".ADV", parts.Suffix)

[<Fact>]
let ``ForCall without dot falls back to whole-name editable`` () =
    let parts = NameEditorParts.ForCall "NoDotName"
    Assert.Equal("", parts.Prefix)
    Assert.Equal("NoDotName", parts.Editable)
    Assert.Equal("", parts.Suffix)

[<Fact>]
let ``ForCall null name returns empty editable`` () =
    let parts = NameEditorParts.ForCall null
    Assert.Equal("", parts.Editable)

[<Fact>]
let ``ForWork delegates to parseWorkNameParts and emits no suffix`` () =
    // parseWorkNameParts 의 정확한 prefix 의미는 Ds2.Core 책임 — 호출 결과를 그대로 prefix/local 로 매핑.
    let parts = NameEditorParts.ForWork "SimpleWork"
    Assert.Equal("", parts.Suffix)
    // 결합하면 원본 복원 가능해야 (prefix + editable 가 fullName).
    Assert.Equal("SimpleWork", parts.Prefix + parts.Editable)
