module Ds2.Store.Editor.Tests.SimulationProjectionTests

open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine.Core
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

[<Fact>]
let ``indexed entries include active canonical works and calls only`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let activeFlow = addFlow store "F" activeSystem.Id
    let activeWork = addWork store "W" activeFlow.Id
    let refWorkId = store.AddReferenceWork(activeWork.Id)

    let deviceSystem = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DF" deviceSystem.Id
    let deviceWork = addWork store "ADV" deviceFlow.Id
    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.TxGuid <- Some deviceWork.Id
    apiDef.RxGuid <- Some deviceWork.Id

    let callId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ apiDef.Id ])
    let refCallId = store.AddReferenceCall(callId)

    let index = SimIndex.build store 10
    let entries = SimulationProjection.indexedEntries index

    Assert.True(entries |> Array.exists (fun entry ->
        entry.Id = activeWork.Id
        && entry.Kind = EntityKind.Work
        && entry.Name = "F.W"
        && not entry.ParentWorkId.HasValue))

    Assert.True(entries |> Array.exists (fun entry ->
        entry.Id = callId
        && entry.Kind = EntityKind.Call
        && entry.Name.EndsWith("ADV")
        && entry.ParentWorkId.HasValue
        && entry.ParentWorkId.Value = activeWork.Id))

    Assert.False(entries |> Array.exists (fun entry -> entry.Id = refWorkId))
    Assert.False(entries |> Array.exists (fun entry -> entry.Id = refCallId))
    Assert.False(entries |> Array.exists (fun entry -> entry.Id = deviceWork.Id))
