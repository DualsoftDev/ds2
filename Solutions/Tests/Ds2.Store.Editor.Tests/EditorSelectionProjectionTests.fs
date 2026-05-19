module Ds2.Store.Editor.Tests.EditorSelectionProjectionTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

let private key (kind: EntityKind) id = SelectionKey(id, kind)

[<Fact>]
let ``empty selection yields Empty-equivalent slots`` () =
    let store = createStore ()
    let p = EditorSelectionProjection.Build(
                store, [],
                Nullable(), Nullable(), "", Nullable())
    Assert.Equal(0, p.Summary.Count)
    Assert.False(p.Summary.IsSingleSelection)
    Assert.Equal("", p.NameParts.Editable)
    Assert.False(p.WorkState.PeriodMs.HasValue)
    Assert.False(p.CallState.TimeoutMs.HasValue)
    Assert.Equal("", p.SystemType)

[<Fact>]
let ``single Work selection populates WorkState and Work name parts`` () =
    let store = createStore ()
    let _, _, _, work = setupBasicHierarchy store
    let p = EditorSelectionProjection.Build(
                store,
                [ key EntityKind.Work work.Id ],
                Nullable(work.Id), Nullable(EntityKind.Work), work.Name, Nullable())
    Assert.True(p.Summary.IsSingleWorkSelected)
    // Work full name = "Flow.Work" 형식. NameParts.ForWork 가 prefix/local 분리
    Assert.Equal(work.Name, p.NameParts.Prefix + p.NameParts.Editable)
    Assert.Equal("", p.NameParts.Suffix)
    // WorkSelectionState 는 PeriodMs 등을 채움 (값 자체는 0 또는 None 일 수 있음)
    Assert.Equal("", p.SystemType)
    Assert.False(p.CallState.TimeoutMs.HasValue)

[<Fact>]
let ``single System selection populates SystemType`` () =
    let store = createStore ()
    let _, system, _, _ = setupBasicHierarchy store
    let p = EditorSelectionProjection.Build(
                store,
                [ key EntityKind.System system.Id ],
                Nullable(system.Id), Nullable(EntityKind.System), system.Name, Nullable())
    Assert.True(p.Summary.IsSingleSystemSelected)
    // SystemType 은 "" 가 기본 (resolveSystemType 이 빈 string 반환).
    Assert.NotNull(p.SystemType)
    // Work/Call 슬롯은 Empty 동등
    Assert.False(p.WorkState.PeriodMs.HasValue)
    Assert.False(p.CallState.TimeoutMs.HasValue)

[<Fact>]
let ``multiple Work selection populates WorkState batch but no single Work fields`` () =
    let store = createStore ()
    let _, _, flow, work1 = setupBasicHierarchy store
    let work2Id = store.AddWork("Work2", flow.Id)
    let p = EditorSelectionProjection.Build(
                store,
                [ key EntityKind.Work work1.Id; key EntityKind.Work work2Id ],
                Nullable(), Nullable(), "", Nullable())
    Assert.True(p.Summary.IsWorkSelected)
    Assert.False(p.Summary.IsSingleWorkSelected)
    Assert.True(p.Summary.IsMultiSelection)
    // 다중이면 NameParts 는 fallback (전체 빈 이름)
    Assert.Equal("", p.NameParts.Editable)

[<Fact>]
let ``mixed selection yields empty Work/Call batch slots`` () =
    let store = createStore ()
    let _, system, _, work = setupBasicHierarchy store
    let p = EditorSelectionProjection.Build(
                store,
                [ key EntityKind.Work work.Id; key EntityKind.System system.Id ],
                Nullable(), Nullable(), "", Nullable())
    Assert.False(p.Summary.IsWorkSelected)
    Assert.False(p.Summary.IsCallSelected)
    Assert.False(p.WorkState.PeriodMs.HasValue)
    Assert.False(p.CallState.TimeoutMs.HasValue)
    Assert.Equal("", p.SystemType)
