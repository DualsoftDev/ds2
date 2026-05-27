module Ds2.Store.Editor.Tests.DsStorePasteAcrossFlowsModeTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module private Helpers =
    let buildTwoFlowFixture () =
        let store = createStore ()
        let project, system, sourceFlow, sourceWork = setupBasicHierarchy store
        let targetFlowId = store.AddFlow("TargetFlow", system.Id)
        let targetWorkId = store.AddWork("TargetWork", targetFlowId)
        store, project, system, sourceFlow, sourceWork, targetFlowId, targetWorkId

    let addDeviceCall (store: DsStore) (sourceWorkId: Guid) (devAlias: string) (apiName: string) =
        store.AddCallWithMultipleDevicesResolved(
            EntityKind.Work, sourceWorkId, sourceWorkId,
            devAlias, apiName, [ devAlias ], true, None)

    let unwrapOk (result: PasteResult) =
        match result with
        | PasteResult.Ok ids -> ids
        | PasteResult.Blocked reason -> failwith $"Expected Ok but got Blocked({reason})"

open Helpers


[<Fact>]
let ``PasteEntitiesWithMode CloneSystem matches existing PasteEntities default`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_A" "ADV"
    let before = Queries.passiveSystemsOf project.Id store |> List.length
    let pastedIds = store.PasteEntitiesWithMode([ callId ], EntityKind.Work, targetWorkId, 0, CrossFlowDeviceMode.CloneSystem) |> unwrapOk
    Assert.Equal(1, pastedIds.Length)
    let after = Queries.passiveSystemsOf project.Id store
    Assert.Equal(before + 1, after.Length)
    Assert.Contains(after, fun s -> s.Name = "TargetFlow_Conv_A")
    // 원본 Call 은 *그대로* 유지 (paste 의미)
    Assert.True(store.Calls.ContainsKey(callId))


[<Fact>]
let ``PasteEntitiesWithMode RenameSourceSystem renames source instead of cloning`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_B" "ADV"
    let originalApiDefId = store.Calls.[callId].ApiCalls.[0].ApiDefId
    let before = Queries.passiveSystemsOf project.Id store |> List.length
    let pastedIds = store.PasteEntitiesWithMode([ callId ], EntityKind.Work, targetWorkId, 0, CrossFlowDeviceMode.RenameSourceSystem) |> unwrapOk
    Assert.Equal(1, pastedIds.Length)
    let after = Queries.passiveSystemsOf project.Id store
    Assert.Equal(before, after.Length)  // system 개수 동일
    Assert.Contains(after, fun s -> s.Name = "TargetFlow_Conv_B")
    // ApiDefId 그대로 재사용
    Assert.Equal(originalApiDefId, store.Calls.[pastedIds.Head].ApiCalls.[0].ApiDefId)


[<Fact>]
let ``PasteEntitiesWithMode KeepReferences shares ApiDefId without touching device system`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_C" "ADV"
    let originalApiDefId = store.Calls.[callId].ApiCalls.[0].ApiDefId
    let before = Queries.passiveSystemsOf project.Id store |> List.length
    let originalSystemName =
        Queries.passiveSystemsOf project.Id store
        |> List.find (fun s -> s.Name.EndsWith "Conv_C")
        |> fun s -> s.Name
    let pastedIds = store.PasteEntitiesWithMode([ callId ], EntityKind.Work, targetWorkId, 0, CrossFlowDeviceMode.KeepReferences) |> unwrapOk
    Assert.Equal(1, pastedIds.Length)
    let after = Queries.passiveSystemsOf project.Id store
    Assert.Equal(before, after.Length)
    // System name 도 안 변함
    Assert.Contains(after, fun s -> s.Name = originalSystemName)
    Assert.Equal(originalApiDefId, store.Calls.[pastedIds.Head].ApiCalls.[0].ApiDefId)


[<Fact>]
let ``PasteEntitiesWithMode is a single undo step`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_D" "ADV"
    let before = Queries.passiveSystemsOf project.Id store |> List.length
    let _ = store.PasteEntitiesWithMode([ callId ], EntityKind.Work, targetWorkId, 0, CrossFlowDeviceMode.CloneSystem) |> unwrapOk
    store.Undo()
    Assert.Equal(before, Queries.passiveSystemsOf project.Id store |> List.length)
    // Original 은 그대로 (Paste 라서)
    Assert.True(store.Calls.ContainsKey(callId))


[<Fact>]
let ``PasteEntitiesWithMode preserves IO addresses across modes`` () =
    let store, _project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_E" "ADV"
    let original = store.Calls.[callId].ApiCalls.[0]
    original.InTag <- Some (IOTag("In", "%I0.5", "desc"))
    original.OutTag <- Some (IOTag("Out", "%Q1.5", "desc"))
    original.InputSpec <- ValueSpec.singleBool true
    let pastedIds = store.PasteEntitiesWithMode([ callId ], EntityKind.Work, targetWorkId, 0, CrossFlowDeviceMode.RenameSourceSystem) |> unwrapOk
    let pastedApiCall = store.Calls.[pastedIds.Head].ApiCalls.[0]
    Assert.Equal(Some "%I0.5", pastedApiCall.InTag |> Option.map (fun t -> t.Address))
    Assert.Equal(Some "%Q1.5", pastedApiCall.OutTag |> Option.map (fun t -> t.Address))
    Assert.Equal(ValueSpec.singleBool true, pastedApiCall.InputSpec)
