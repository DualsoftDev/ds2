module Ds2.Store.Editor.Tests.DsStoreCopyPastePanelTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module PasteTests =

    [<Fact>]
    let ``PasteEntities copies flow and returns new flow id`` () =
        let store = createStore ()
        let _, system, flow, _ = setupBasicHierarchy store
        let pastedIds = store.PasteEntities(EntityKind.Flow, [ flow.Id ], EntityKind.System, system.Id, 0)
        Assert.Equal(1, pastedIds.Length)
        Assert.NotEqual(flow.Id, pastedIds.Head)
        Assert.Equal(2, DsQuery.flowsOf system.Id store |> List.length)

    [<Fact>]
    let ``PasteEntities copies works and returns new work ids`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let pastedIds = store.PasteEntities(EntityKind.Work, [ work.Id ], EntityKind.Flow, flow.Id, 0)
        Assert.Equal(1, pastedIds.Length)
        Assert.NotEqual(work.Id, pastedIds.Head)
        Assert.Equal(2, DsQuery.worksOf flow.Id store |> List.length)

    [<Fact>]
    let ``PasteEntities copies Work TokenRole and Properties`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        work.TokenRole <- TokenRole.Source
        work.Properties.Period <- Some (TimeSpan.FromSeconds 5.0)
        work.Properties.NumRepeat <- 3
        let pastedIds = store.PasteEntities(EntityKind.Work, [ work.Id ], EntityKind.Flow, flow.Id, 0)
        let pastedWork = DsQuery.getWork pastedIds.Head store |> Option.get
        Assert.Equal(TokenRole.Source, pastedWork.TokenRole)
        Assert.Equal(Some (TimeSpan.FromSeconds 5.0), pastedWork.Properties.Period)
        Assert.Equal(3, pastedWork.Properties.NumRepeat)

    [<Fact>]
    let ``PasteEntities copies call with multiple device ApiCalls across flows`` () =
        let store = createStore ()
        let project, system, _, work = setupBasicHierarchy store
        // ApiCall 복제 모드: 1 Call + 3 ApiCalls pointing to 3 different Device Systems
        let callId = store.AddCallWithMultipleDevicesResolved(
                        EntityKind.Work, work.Id, work.Id,
                        "Conv", "ADV", [ "Conv_1"; "Conv_2"; "Conv_3" ])
        let originalCall = store.Calls.[callId]
        Assert.Equal(3, originalCall.ApiCalls.Count)
        // 다른 Flow 생성
        let flow2Id = store.AddFlow("Flow2", system.Id)
        let work2Id = store.AddWork("Work2", flow2Id)
        // Call을 다른 Flow의 Work로 복사
        let pastedIds = store.PasteEntities(EntityKind.Call, [ callId ], EntityKind.Work, work2Id, 0)
        Assert.Equal(1, pastedIds.Length)
        let pastedCall = store.Calls.[pastedIds.Head]
        // 복사된 Call에 3개 ApiCall이 있어야 함
        Assert.Equal(3, pastedCall.ApiCalls.Count)
        // 각 ApiCall이 서로 다른 ApiDef를 가리켜야 함 (원본과 다른 ID)
        let pastedApiDefIds =
            pastedCall.ApiCalls
            |> Seq.choose (fun ac -> ac.ApiDefId)
            |> Seq.distinct |> Seq.toList
        Assert.Equal(3, pastedApiDefIds.Length)
        // 새 Device System이 Flow2 기준으로 생성되어야 함
        let passiveSystems = DsQuery.passiveSystemsOf project.Id store
        // 원본 3 + 복사본 3 = 6 Device Systems
        Assert.True(passiveSystems.Length >= 6)

    [<Fact>]
    let ``PasteEntities copies device Work duration across flows`` () =
        let store = createStore ()
        let _, system, _, work = setupBasicHierarchy store
        let callId = store.AddCallWithMultipleDevicesResolved(
                        EntityKind.Work, work.Id, work.Id,
                        "Conv", "ADV", [ "Conv_1" ])
        let originalCall = store.Calls.[callId]
        // 원본 Device System의 Work에 Period 설정
        let srcApiCall = originalCall.ApiCalls.[0]
        let srcApiDef = DsQuery.getApiDef (srcApiCall.ApiDefId.Value) store |> Option.get
        let srcWork = DsQuery.getWork (srcApiDef.Properties.TxGuid.Value) store |> Option.get
        srcWork.Properties.Period <- Some (TimeSpan.FromSeconds 3.5)
        // 다른 Flow 생성 후 Cross-flow paste
        let flow2Id = store.AddFlow("Flow2", system.Id)
        let work2Id = store.AddWork("Work2", flow2Id)
        let pastedIds = store.PasteEntities(EntityKind.Call, [ callId ], EntityKind.Work, work2Id, 0)
        let pastedCall = store.Calls.[pastedIds.Head]
        let pastedApiDef = DsQuery.getApiDef (pastedCall.ApiCalls.[0].ApiDefId.Value) store |> Option.get
        // TxGuid, RxGuid 모두 설정되어야 함
        Assert.True(pastedApiDef.Properties.TxGuid.IsSome)
        Assert.True(pastedApiDef.Properties.RxGuid.IsSome)
        let pastedWork = DsQuery.getWork (pastedApiDef.Properties.RxGuid.Value) store |> Option.get
        Assert.Equal(Some (TimeSpan.FromSeconds 3.5), pastedWork.Properties.Period)
        // tryGetDeviceDurationMs 경로 검증 (시뮬레이션에서 사용하는 경로)
        let deviceDuration = DsQuery.tryGetDeviceDurationMs work2Id store
        Assert.Equal(Some 3500, deviceDuration)

    [<Fact>]
    let ``ValidateCopySelection returns Ok for single copyable entity`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let keys = [| SelectionKey(flow.Id, EntityKind.Flow) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsOk)

    [<Fact>]
    let ``ValidateCopySelection returns NothingToCopy for non-copyable type`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let keys = [| SelectionKey(project.Id, EntityKind.Project) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsNothingToCopy)

    [<Fact>]
    let ``ValidateCopySelection returns MixedTypes for different entity types`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        let keys = [| SelectionKey(flow.Id, EntityKind.Flow); SelectionKey(work.Id, EntityKind.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsMixedTypes)

    [<Fact>]
    let ``ValidateCopySelection returns MixedParents for works in different flows`` () =
        let store = createStore ()
        let _, system, _, work1 = setupBasicHierarchy store
        let flow2 = addFlow store "Flow2" system.Id
        let work2 = addWork store "Work2" flow2.Id
        let keys = [| SelectionKey(work1.Id, EntityKind.Work); SelectionKey(work2.Id, EntityKind.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsMixedParents)

    [<Fact>]
    let ``ValidateCopySelection returns Ok for same-parent works`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id
        let keys = [| SelectionKey(work1.Id, EntityKind.Work); SelectionKey(work2.Id, EntityKind.Work) |]
        let result = store.ValidateCopySelection(keys)
        Assert.True(result.IsOk)
        match result with
        | CopyValidationResult.Ok items -> Assert.Equal(2, items.Length)
        | _ -> Assert.Fail("Expected Ok")

// =============================================================================
// Move
// =============================================================================

module MoveTests =

    [<Fact>]
    let ``MoveEntities updates work position by id lookup`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let request = MoveEntityRequest(work.Id, Xywh(100, 200, 50, 30))
        let moved = store.MoveEntities([ request ])
        Assert.Equal(1, moved)
        Assert.True(store.Works.[work.Id].Position.IsSome)

    [<Fact>]
    let ``MoveEntities updates call position by id lookup`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1" ], true)
        let call = DsQuery.callsOf work.Id store |> List.head
        let request = MoveEntityRequest(call.Id, Xywh(50, 60, 120, 40))
        let moved = store.MoveEntities([ request ])
        Assert.Equal(1, moved)
        Assert.True(store.Calls.[call.Id].Position.IsSome)

// =============================================================================
// Panel (도메인 타입 직접 사용)
// =============================================================================

module PanelTests =


    [<Fact>]
    let ``AddApiDefWithProperties creates apiDef with all properties set`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let id = store.AddApiDefWithProperties("Api1", system.Id, true, None, None, 200, Some "test desc")
        let apiDef = store.ApiDefs.[id]
        Assert.Equal("Api1", apiDef.Name)
        Assert.Equal(system.Id, apiDef.ParentId)
        Assert.True(apiDef.Properties.IsPush)
        Assert.Equal(200, apiDef.Properties.Period)
        Assert.Equal(Some "test desc", apiDef.Properties.Description)

    [<Fact>]
    let ``TryGetCallApiCallForPanel returns item when api call exists`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)

        let call = store.Calls.Values |> Seq.head
        let apiCall = call.ApiCalls |> Seq.head

        let row = store.TryGetCallApiCallForPanel(call.Id, apiCall.Id)
        Assert.True(row.IsSome)
        Assert.Equal(apiCall.Id, row.Value.ApiCallId)

    [<Fact>]
    let ``AddApiCallFromPanel uses ApiDef name when panel no longer supplies api call name`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let flow = addFlow store "F" system.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Seed.Api" ], true)
        let callId = store.Calls.Values |> Seq.head |> fun call -> call.Id
        let apiDef = addApiDef store "DeviceApi" system.Id

        let createdId =
            store.AddApiCallFromPanel(
                callId,
                apiDef.Id,
                "", "out-addr",
                "", "in-addr",
                0, "",
                0, "")

        let created = store.ApiCalls.[createdId]
        Assert.Equal("DeviceApi", created.Name)
        Assert.Equal(Some apiDef.Id, created.ApiDefId)

    [<Fact>]
    let ``UpdateApiDef changes name and properties atomically`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let apiDef = addApiDef store "Api1" system.Id
        store.UpdateApiDef(apiDef.Id, "ApiRenamed", true, None, None, 300, Some "new desc")
        let updated = store.ApiDefs.[apiDef.Id]
        Assert.Equal("ApiRenamed", updated.Name)
        Assert.True(updated.Properties.IsPush)
        Assert.Equal(300, updated.Properties.Period)
        Assert.Equal(Some "new desc", updated.Properties.Description)

    [<Fact>]
    let ``UpdateApiDef is single undo step`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let apiDef = addApiDef store "OldName" system.Id
        store.UpdateApiDef(apiDef.Id, "NewName", true, None, None, 100, None)
        store.Undo()
        let reverted = store.ApiDefs.[apiDef.Id]
        Assert.Equal("OldName", reverted.Name)
        Assert.False(reverted.Properties.IsPush)
        Assert.Equal(0, reverted.Properties.Period)

    [<Fact>]
    let ``UpdateConditionApiCallOutputSpec updates selected condition api call`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true)

        let call = store.Calls.Values |> Seq.head
        let sourceApiCallId = call.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(call.Id, CallConditionType.ComAux)
        let condId = store.Calls.[call.Id].CallConditions |> Seq.head |> fun cc -> cc.Id

        let added = store.AddApiCallsToConditionBatch(call.Id, condId, [ sourceApiCallId ])
        Assert.Equal(1, added)

        let conditionApiCallId =
            store.Calls.[call.Id].CallConditions
            |> Seq.head
            |> fun cc -> cc.Conditions |> Seq.head |> fun ac -> ac.Id

        let changed = store.UpdateConditionApiCallOutputSpec(call.Id, condId, conditionApiCallId, 4, "123")
        Assert.True(changed)

        let updatedSpec =
            store.Calls.[call.Id].CallConditions
            |> Seq.head
            |> fun cc -> cc.Conditions |> Seq.find (fun ac -> ac.Id = conditionApiCallId) |> fun ac -> ac.OutputSpec

        match updatedSpec with
        | Int32Value (Single v) -> Assert.Equal(123, v)
        | _ -> Assert.Fail("OutputSpec should be Int32Value(Single 123)")

// =============================================================================
// File I/O
// =============================================================================
