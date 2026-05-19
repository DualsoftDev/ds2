module Ds2.Store.Editor.Tests.CanvasContextMenuStateTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

let private build store tabKind selKind selId =
    CanvasContextMenuState.Build(store, tabKind, selKind, selId)

[<Fact>]
let ``no active tab returns empty state`` () =
    let store = createStore ()
    let s = build store (Nullable()) (Nullable()) (Nullable())
    Assert.False(s.ShowAddWork)
    Assert.False(s.ShowAddCall)
    Assert.False(s.ShowAddRefWork)
    Assert.False(s.ShowAddRefCall)
    Assert.False(s.ShowTokenRole)

[<Fact>]
let ``System tab shows AddWork only`` () =
    let store = createStore ()
    let s = build store (Nullable(TabKind.System)) (Nullable()) (Nullable())
    Assert.True(s.ShowAddWork)
    Assert.False(s.ShowAddCall)

[<Fact>]
let ``Work tab shows AddCall only`` () =
    let store = createStore ()
    let s = build store (Nullable(TabKind.Work)) (Nullable()) (Nullable())
    Assert.False(s.ShowAddWork)
    Assert.True(s.ShowAddCall)

[<Fact>]
let ``AddRefWork visible only when Work selected on System or Flow tab`` () =
    let store = createStore ()
    let selWork = Nullable(EntityKind.Work)
    Assert.True((build store (Nullable(TabKind.System)) selWork (Nullable())).ShowAddRefWork)
    Assert.True((build store (Nullable(TabKind.Flow))   selWork (Nullable())).ShowAddRefWork)
    Assert.False((build store (Nullable(TabKind.Work))  selWork (Nullable())).ShowAddRefWork)
    // Call 선택은 AddRefWork 와 무관
    Assert.False((build store (Nullable(TabKind.System)) (Nullable(EntityKind.Call)) (Nullable())).ShowAddRefWork)

[<Fact>]
let ``AddRefCall visible only when Call selected on Work tab`` () =
    let store = createStore ()
    let selCall = Nullable(EntityKind.Call)
    Assert.True((build store (Nullable(TabKind.Work))   selCall (Nullable())).ShowAddRefCall)
    Assert.False((build store (Nullable(TabKind.System)) selCall (Nullable())).ShowAddRefCall)
    Assert.False((build store (Nullable(TabKind.Flow))   selCall (Nullable())).ShowAddRefCall)
    // Work 선택은 AddRefCall 과 무관
    Assert.False((build store (Nullable(TabKind.Work)) (Nullable(EntityKind.Work)) (Nullable())).ShowAddRefCall)

[<Fact>]
let ``TokenRole visible only when Work selected`` () =
    let store = createStore ()
    Assert.True((build store (Nullable(TabKind.System)) (Nullable(EntityKind.Work)) (Nullable())).ShowTokenRole)
    Assert.False((build store (Nullable(TabKind.System)) (Nullable(EntityKind.Call)) (Nullable())).ShowTokenRole)
    Assert.False((build store (Nullable(TabKind.System)) (Nullable()) (Nullable())).ShowTokenRole)

[<Fact>]
let ``TokenRole flags reflect store role bits`` () =
    let store = createStore ()
    let _, _, _, work = setupBasicHierarchy store
    // 초기엔 None
    let s0 = build store (Nullable(TabKind.System)) (Nullable(EntityKind.Work)) (Nullable(work.Id))
    Assert.False(s0.TokenRoleSourceChecked)
    Assert.False(s0.TokenRoleIgnoreChecked)
    Assert.False(s0.TokenRoleSinkChecked)
    // Source | Sink 비트 적용 후
    store.ToggleWorkTokenRoleFlag([ work.Id ], TokenRole.Source)
    store.ToggleWorkTokenRoleFlag([ work.Id ], TokenRole.Sink)
    let s1 = build store (Nullable(TabKind.System)) (Nullable(EntityKind.Work)) (Nullable(work.Id))
    Assert.True(s1.TokenRoleSourceChecked)
    Assert.False(s1.TokenRoleIgnoreChecked)
    Assert.True(s1.TokenRoleSinkChecked)
