module Ds2.Store.Editor.Tests.GraphValidatorProjectionTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine.Core
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

let private setupRuntimeStore () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let activeFlow = addFlow store "F" activeSystem.Id
    let activeWork = addWork store "W" activeFlow.Id
    let deviceSystem = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DF" deviceSystem.Id
    store, project, activeWork, deviceSystem, deviceFlow

[<Fact>]
let ``duration warning is owned by runtime graph validator`` () =
    let store, _, activeWork, deviceSystem, deviceFlow = setupRuntimeStore ()
    activeWork.Duration <- Some (TimeSpan.FromMilliseconds 100.)

    let deviceWork = addWork store "ADV" deviceFlow.Id
    deviceWork.Duration <- Some (TimeSpan.FromMilliseconds 500.)
    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.TxGuid <- Some deviceWork.Id
    apiDef.RxGuid <- Some deviceWork.Id

    store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore

    let index = SimIndex.build store 10
    let warning = GraphWarningProjection.findDurationLessThanCriticalPathWarnings index |> List.exactlyOne

    Assert.Equal(activeWork.Id, warning.WorkGuid)
    Assert.Equal("Active", warning.SystemName)
    Assert.Equal("F.W", warning.WorkName)
    Assert.Equal(100, warning.ConfiguredMs)
    Assert.Equal(500, warning.CriticalPathMs)

[<Fact>]
let ``token spec warning reports token sources without bound specs`` () =
    let store, project, source, _, _ = setupRuntimeStore ()
    let otherSource = addWork store "W2" source.ParentId
    store.UpdateWorkTokenRole(source.Id, TokenRole.Source)
    store.UpdateWorkTokenRole(otherSource.Id, TokenRole.Source)
    project.TokenSpecs.Add({ Id = 1; Label = "T1"; Fields = Map.empty; WorkId = Some source.Id })

    let index = SimIndex.build store 10
    let warnings = GraphWarningProjection.findTokenSourcesWithoutSpecs index

    let warning = warnings |> List.exactlyOne
    Assert.Equal(otherSource.Id, warning.WorkGuid)
    Assert.Equal("F.W2", warning.WorkName)

[<Fact>]
let ``race warning projection deduplicates excluded call pairs`` () =
    let store, _, activeWork, deviceSystem, deviceFlow = setupRuntimeStore ()
    let advWork = addWork store "ADV" deviceFlow.Id
    advWork.Duration <- Some (TimeSpan.FromMilliseconds 500.)
    let retWork = addWork store "RET" deviceFlow.Id
    retWork.Duration <- Some (TimeSpan.FromMilliseconds 500.)

    let advDef = addApiDef store "ADV" deviceSystem.Id
    advDef.TxGuid <- Some advWork.Id
    advDef.RxGuid <- Some advWork.Id
    let retDef = addApiDef store "RET" deviceSystem.Id
    retDef.TxGuid <- Some retWork.Id
    retDef.RxGuid <- Some retWork.Id
    store.ConnectSelectionInOrder([ advWork.Id; retWork.Id ], ArrowType.ResetReset) |> ignore

    let advCallId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ advDef.Id ])
    let retCallId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "RET", [ retDef.Id ])

    let index = SimIndex.build store 10
    let warning = GraphWarningProjection.findRaceConditionWarnings index |> List.exactlyOne

    Assert.Equal(activeWork.Id, warning.WorkGuid)
    Assert.Equal("F.W", warning.WorkName)
    Assert.True(
        Set.ofList [ advCallId; retCallId ] = Set.ofList [ warning.LeftCallGuid; warning.RightCallGuid ])
    let callNames = [ warning.LeftCallName; warning.RightCallName ]
    Assert.True(callNames |> List.exists (fun name -> name.EndsWith("ADV", StringComparison.Ordinal)))
    Assert.True(callNames |> List.exists (fun name -> name.EndsWith("RET", StringComparison.Ordinal)))

[<Fact>]
let ``warning target expansion includes references and calls touching a work`` () =
    let store, _, activeWork, deviceSystem, deviceFlow = setupRuntimeStore ()
    let deviceWork = addWork store "ADV" deviceFlow.Id
    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.TxGuid <- Some deviceWork.Id
    apiDef.RxGuid <- Some deviceWork.Id

    let callId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ apiDef.Id ])
    let refWorkId = store.AddReferenceWork(deviceWork.Id)
    let refCallId = store.AddReferenceCall(callId)

    let expanded =
        GraphWarningProjection.expandWarningTarget store deviceWork.Id
        |> Set.ofArray

    Assert.True(Set.contains deviceWork.Id expanded)
    Assert.True(Set.contains refWorkId expanded)
    Assert.True(Set.contains callId expanded)
    Assert.True(Set.contains refCallId expanded)

    let clearTargets =
        GraphWarningProjection.warningGuidsForTarget store [ deviceWork.Id ] refCallId
        |> Set.ofArray

    Assert.True(Set.singleton deviceWork.Id = clearTargets)
