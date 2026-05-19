module Ds2.Store.Editor.Tests.SelectionSummaryTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Xunit

let private key (kind: EntityKind) = SelectionKey(Guid.NewGuid(), kind)

[<Fact>]
let ``empty selection has count 0 and null kind`` () =
    let s = SelectionSummary.Build([])
    Assert.Equal(0, s.Count)
    Assert.False(s.UniformKind.HasValue)
    Assert.False(s.IsSingleSelection)
    Assert.False(s.IsMultiSelection)
    Assert.False(s.IsWorkSelected)
    Assert.False(s.IsCallSelected)

[<Fact>]
let ``single Work selection sets IsSingleWorkSelected and IsWorkSelected`` () =
    let s = SelectionSummary.Build [ key EntityKind.Work ]
    Assert.Equal(1, s.Count)
    Assert.True(s.UniformKind.HasValue)
    Assert.Equal(EntityKind.Work, s.UniformKind.Value)
    Assert.True(s.IsSingleSelection)
    Assert.False(s.IsMultiSelection)
    Assert.True(s.IsSingleWorkSelected)
    Assert.True(s.IsWorkSelected)
    Assert.False(s.IsCallSelected)
    Assert.False(s.IsSingleCallSelected)
    Assert.False(s.IsSingleSystemSelected)

[<Fact>]
let ``single Call selection sets IsSingleCallSelected`` () =
    let s = SelectionSummary.Build [ key EntityKind.Call ]
    Assert.True(s.IsSingleCallSelected)
    Assert.True(s.IsCallSelected)
    Assert.False(s.IsSingleWorkSelected)

[<Fact>]
let ``single System selection sets IsSingleSystemSelected`` () =
    let s = SelectionSummary.Build [ key EntityKind.System ]
    Assert.True(s.IsSingleSystemSelected)
    Assert.False(s.IsWorkSelected)
    Assert.False(s.IsCallSelected)

[<Fact>]
let ``multiple Works are uniform Work but not single`` () =
    let s = SelectionSummary.Build [ key EntityKind.Work; key EntityKind.Work; key EntityKind.Work ]
    Assert.Equal(3, s.Count)
    Assert.True(s.IsMultiSelection)
    Assert.False(s.IsSingleSelection)
    Assert.True(s.IsWorkSelected)
    Assert.False(s.IsSingleWorkSelected)
    Assert.Equal(EntityKind.Work, s.UniformKind.Value)

[<Fact>]
let ``mixed selection has no uniform kind`` () =
    let s = SelectionSummary.Build [ key EntityKind.Work; key EntityKind.Call ]
    Assert.Equal(2, s.Count)
    Assert.True(s.IsMultiSelection)
    Assert.False(s.UniformKind.HasValue)
    Assert.False(s.IsWorkSelected)
    Assert.False(s.IsCallSelected)
