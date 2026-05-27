module Ds2.Store.Editor.Tests.DsStoreMoveCallAcrossFlowsTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module private Helpers =
    /// Source flow + 같은 system 안 target flow + target work 까지 준비 (모두 빈 work).
    let buildTwoFlowFixture () =
        let store = createStore ()
        let project, system, sourceFlow, sourceWork = setupBasicHierarchy store
        let targetFlowId = store.AddFlow("TargetFlow", system.Id)
        let targetWorkId = store.AddWork("TargetWork", targetFlowId)
        store, project, system, sourceFlow, sourceWork, targetFlowId, targetWorkId

    /// sourceCall 을 sourceWork 에 device alias 1개 만들어 붙임 (ADV 또는 RET 등).
    /// Call name = "{devAlias}.{apiName}" 형식 — 같은 work 안 ApiName 만 다르면 충돌 없음.
    let addDeviceCall (store: DsStore) (sourceWorkId: Guid) (devAlias: string) (apiName: string) =
        store.AddCallWithMultipleDevicesResolved(
            EntityKind.Work, sourceWorkId, sourceWorkId,
            devAlias, apiName, [ devAlias ], true, None)

    let unwrapMoved (result: CrossFlowMoveResult) =
        match result with
        | Moved ids -> ids
        | Blocked v -> failwith $"Expected Moved but got Blocked({v})"

open Helpers


[<Fact>]
let ``MoveCallsAcrossFlow CloneSystem creates new device system with cloned ApiDefs`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_1" "ADV"
    let sourceSystemsBefore = Queries.passiveSystemsOf project.Id store |> List.length
    let movedIds = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    Assert.Equal(1, movedIds.Length)
    let passiveSystems = Queries.passiveSystemsOf project.Id store
    Assert.Equal(sourceSystemsBefore + 1, passiveSystems.Length)
    Assert.Contains(passiveSystems, fun s -> s.Name = "TargetFlow_Conv_1")


[<Fact>]
let ``MoveCallsAcrossFlow CloneSystem preserves InTag OutTag InputSpec OutputSpec`` () =
    let store, _project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_2" "ADV"
    let original = store.Calls.[callId]
    let originalApiCall = original.ApiCalls.[0]
    // 임의로 spec / 주소 설정해서 보존성 검증
    originalApiCall.InTag <- Some (IOTag("In", "%IX0.0", "desc-in"))
    originalApiCall.OutTag <- Some (IOTag("Out", "%QX0.0", "desc-out"))
    originalApiCall.InputSpec <- ValueSpec.singleBool true
    originalApiCall.OutputSpec <- ValueSpec.singleBool true
    let movedIds = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    let movedCall = store.Calls.[movedIds.Head]
    let movedApiCall = movedCall.ApiCalls.[0]
    Assert.Equal(Some "%IX0.0", movedApiCall.InTag |> Option.map (fun t -> t.Address))
    Assert.Equal(Some "%QX0.0", movedApiCall.OutTag |> Option.map (fun t -> t.Address))
    Assert.Equal(ValueSpec.singleBool true, movedApiCall.InputSpec)
    Assert.Equal(ValueSpec.singleBool true, movedApiCall.OutputSpec)


[<Fact>]
let ``MoveCallsAcrossFlow CloneSystem rewires ApiDefId to cloned ApiDef in new device system`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_3" "ADV"
    let originalApiDefId = store.Calls.[callId].ApiCalls.[0].ApiDefId
    let movedIds = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    let movedApiCall = store.Calls.[movedIds.Head].ApiCalls.[0]
    Assert.NotEqual(originalApiDefId, movedApiCall.ApiDefId)
    // 새 ApiDefId 가 target flow 의 device system 의 ApiDef 를 가리켜야 함
    match movedApiCall.ApiDefId with
    | Some newApiDefId ->
        let apiDef = Queries.getApiDef newApiDefId store |> Option.get
        let parentSystem = Queries.getSystem apiDef.ParentId store |> Option.get
        Assert.Equal("TargetFlow_Conv_3", parentSystem.Name)
        Assert.True(Queries.passiveSystemsOf project.Id store |> List.exists (fun s -> s.Id = parentSystem.Id))
    | None -> failwith "Cloned ApiCall must have a remapped ApiDefId"


[<Fact>]
let ``MoveCallsAcrossFlow RenameSourceSystem renames system and reuses ApiDefId`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_4" "ADV"
    let originalApiDefId = store.Calls.[callId].ApiCalls.[0].ApiDefId
    let passiveSystemsBefore = Queries.passiveSystemsOf project.Id store |> List.length
    let movedIds = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.RenameSourceSystem) |> unwrapMoved
    let movedApiCall = store.Calls.[movedIds.Head].ApiCalls.[0]
    Assert.Equal(originalApiDefId, movedApiCall.ApiDefId)
    let passiveSystemsAfter = Queries.passiveSystemsOf project.Id store
    Assert.Equal(passiveSystemsBefore, passiveSystemsAfter.Length)
    Assert.Contains(passiveSystemsAfter, fun s -> s.Name = "TargetFlow_Conv_4")


[<Fact>]
let ``MoveCallsAcrossFlow RenameSourceSystem blocked when device shared with other flow`` () =
    let store, _project, system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_5" "ADV"
    // 같은 device 를 *다른 Flow* Call 한테도 공유시키기 — 3번째 Flow + 거기 Call 에서 같은 ApiDef 참조
    let otherFlowId = store.AddFlow("OtherFlow", system.Id)
    let otherWorkId = store.AddWork("OtherWork", otherFlowId)
    let otherCallId = addDeviceCall store otherWorkId "Conv_5" "ADV"
    // 두 Call 의 ApiCall 이 *같은* ApiDef 를 참조하도록 강제: otherCall 의 ApiDefId 를 sourceCall 의 것으로
    let sourceApiDefId = store.Calls.[callId].ApiCalls.[0].ApiDefId
    store.Calls.[otherCallId].ApiCalls.[0].ApiDefId <- sourceApiDefId
    store.RebuildApiCallsDictionary()
    let result = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.RenameSourceSystem)
    match result with
    | Blocked (RenameModeConflicts conflicts) ->
        Assert.NotEmpty(conflicts)
        Assert.Contains(conflicts, fun (devAlias, _) -> devAlias = "Conv_5")
    | other -> failwith $"Expected RenameModeConflicts but got {other}"


[<Fact>]
let ``MoveCallsAcrossFlow RenameSourceSystem blocked when target name already taken`` () =
    let store, _project, system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_6" "ADV"
    // {TargetFlow}_{devAlias} 이름의 system 을 미리 만들어두기
    let projectId = (store.Projects |> Seq.head).Value.Id
    let blockerSystemId = store.AddSystem("TargetFlow_Conv_6", projectId, false)
    Assert.NotEqual(Guid.Empty, blockerSystemId)
    let _ = system  // suppress unused warning
    let result = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.RenameSourceSystem)
    match result with
    | Blocked (RenameModeConflicts conflicts) ->
        Assert.Contains(conflicts, fun (devAlias, c) ->
            devAlias = "Conv_6" && (match c with NameTaken _ -> true | _ -> false))
    | other -> failwith $"Expected RenameModeConflicts(NameTaken) but got {other}"


[<Fact>]
let ``MoveCallsAcrossFlow is a single undo step that fully restores the source`` () =
    let store, _project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_7" "ADV"
    let snapshotCallParent = store.Calls.[callId].ParentId
    let projectId = (store.Projects |> Seq.head).Value.Id
    let snapshotPassiveCount = Queries.passiveSystemsOf projectId store |> List.length
    let _ = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    store.Undo()
    // Source Call 이 원위치로 복귀
    Assert.True(store.Calls.ContainsKey(callId))
    Assert.Equal(snapshotCallParent, store.Calls.[callId].ParentId)
    // 새로 만든 device system 도 사라짐
    let passiveAfterUndo = Queries.passiveSystemsOf projectId store |> List.length
    Assert.Equal(snapshotPassiveCount, passiveAfterUndo)


[<Fact>]
let ``MoveCallsAcrossFlow blocked on duplicate names in target work`` () =
    let store, _project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_8" "ADV"
    let conflictName = store.Calls.[callId].Name
    // target work 에 같은 이름 Call 을 미리 추가
    let _preExisting = addDeviceCall store targetWorkId "Conv_8" "ADV"
    Assert.Equal(conflictName, store.Calls.[_preExisting].Name)
    let result = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem)
    match result with
    | Blocked (DuplicateNamesInTarget dupes) ->
        Assert.NotEmpty(dupes)
        Assert.Contains(dupes, fun (id, _) -> id = callId)
    | other -> failwith $"Expected DuplicateNamesInTarget but got {other}"


[<Fact>]
let ``MoveCallsAcrossFlow batch with 3 calls across 2 devices`` () =
    let store, project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let c1 = addDeviceCall store sourceWork.Id "ConvA_1" "ADV"
    let c2 = addDeviceCall store sourceWork.Id "ConvA_1" "RET"
    let c3 = addDeviceCall store sourceWork.Id "ConvB_1" "ADV"
    let passiveBefore = Queries.passiveSystemsOf project.Id store |> List.length
    let movedIds = store.MoveCallsAcrossFlow([ c1; c2; c3 ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    Assert.Equal(3, movedIds.Length)
    // 새로 생성된 device system 2개 (TargetFlow_ConvA_1, TargetFlow_ConvB_1)
    let passiveAfter = Queries.passiveSystemsOf project.Id store
    Assert.Equal(passiveBefore + 2, passiveAfter.Length)
    Assert.Contains(passiveAfter, fun s -> s.Name = "TargetFlow_ConvA_1")
    Assert.Contains(passiveAfter, fun s -> s.Name = "TargetFlow_ConvB_1")
    // 3 개의 새 Call 모두 target work 에 있음
    let inTarget = Queries.callsOf targetWorkId store |> List.length
    Assert.Equal(3, inTarget)


[<Fact>]
let ``MoveCallsAcrossFlow is SameWorkMove when all sources already in target work`` () =
    let store, _project, _system, _sourceFlow, _sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store targetWorkId "Conv_X" "ADV"
    let result = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem)
    Assert.Equal(Blocked SameWorkMove, result)


[<Fact>]
let ``MoveCallsAcrossFlow within same flow falls back to DifferentWork paste semantics`` () =
    let store = createStore ()
    let _project, _system, _flow, sourceWork = setupBasicHierarchy store
    // 같은 flow 안 두 번째 work
    let workB = store.AddWork("WorkB", sourceWork.ParentId)
    let callId = addDeviceCall store sourceWork.Id "Conv_Y" "ADV"
    let originalApiDefId = store.Calls.[callId].ApiCalls.[0].ApiDefId
    let movedIds = store.MoveCallsAcrossFlow([ callId ], workB, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    Assert.Equal(1, movedIds.Length)
    // 같은 flow → DifferentWork paste → ApiDefId 그대로 (device system 새로 안 만듦)
    Assert.Equal(originalApiDefId, store.Calls.[movedIds.Head].ApiCalls.[0].ApiDefId)
    // 원본은 cascade-remove
    Assert.False(store.Calls.ContainsKey(callId))


[<Fact>]
let ``MoveCallsAcrossFlow removes source calls and orphan ApiCalls`` () =
    let store, _project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let callId = addDeviceCall store sourceWork.Id "Conv_9" "ADV"
    let originalApiCallId = store.Calls.[callId].ApiCalls.[0].Id
    let _ = store.MoveCallsAcrossFlow([ callId ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    Assert.False(store.Calls.ContainsKey(callId))
    Assert.False(store.ApiCalls.ContainsKey(originalApiCallId))


[<Fact>]
let ``MoveCallsAcrossFlow auto-rewires ApiCall references in other Call Conditions`` () =
    // source Call 의 ApiCall.Id 를 다른 Flow Call 의 Condition 에서 leaf 참조 → 이동 시 자동 rewire 검증.
    let store, _project, _system, _sourceFlow, sourceWork, _targetFlowId, targetWorkId = buildTwoFlowFixture ()
    let movableCallId = addDeviceCall store sourceWork.Id "Conv_R1" "ADV"
    let originalApiCallId = store.Calls.[movableCallId].ApiCalls.[0].Id

    // 다른 Flow 의 Call 의 Condition 에 movable Call 의 ApiCall 을 leaf 로 추가 (panel API 사용)
    let otherCallId = addDeviceCall store targetWorkId "Conv_R2" "ADV"
    let _condId = store.AddConditionWithApiCalls(otherCallId, ConditionType.AutoAux, [ originalApiCallId ])

    let movedIds = store.MoveCallsAcrossFlow([ movableCallId ], targetWorkId, CrossFlowDeviceMode.CloneSystem) |> unwrapMoved
    Assert.Equal(1, movedIds.Length)

    // 원본 ApiCall 은 사라졌어야 함
    Assert.False(store.ApiCalls.ContainsKey(originalApiCallId))

    // otherCall 의 Condition 안 ApiCall 이 *새 ApiCall* 로 자동 rewire 되었는지 확인
    let otherCall = store.Calls.[otherCallId]
    let rewired = otherCall.Conditions.[0].ApiCalls.[0]
    Assert.NotEqual(originalApiCallId, rewired.Id)
    // 그 새 ApiCall 은 새로 paste 된 Call 의 ApiCall 과 동일 객체
    let newApiCall = store.Calls.[movedIds.Head].ApiCalls.[0]
    Assert.Same(newApiCall, rewired)
