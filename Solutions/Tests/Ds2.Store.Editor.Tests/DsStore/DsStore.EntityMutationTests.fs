module Ds2.Store.Editor.Tests.DsStoreEntityMutationTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module RemoveTests =

    [<Fact>]
    let ``RemoveEntities deletes project and cascades`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        store.RemoveEntities([ (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        Assert.Equal(0, store.Systems.Count)
        Assert.Equal(0, store.Flows.Count)
        Assert.Equal(0, store.Works.Count)

    [<Fact>]
    let ``RemoveEntities with work removes descendant calls`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(Guid.Empty, work.Id, ["Dev.Api"], false, None)
        Assert.True(store.Calls.Count > 0)
        store.RemoveEntities([ (EntityKind.Work, work.Id) ])
        Assert.Equal(0, store.Calls.Count)

    [<Fact>]
    let ``RemoveEntities keeps nested condition api call reference alive`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let sourceCall = Queries.callsOf work.Id store |> List.find (fun c -> c.Name = "Src.Api")
        let targetCall = Queries.callsOf work.Id store |> List.find (fun c -> c.Name = "Target.Api")
        let sourceApiCallId = sourceCall.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
        let parentCondition = targetCall.CallConditions |> Seq.head
        store.AddChildCondition(targetCall.Id, parentCondition.Id, false)
        let childCondition = parentCondition.Children |> Seq.head

        let added = store.AddApiCallsToConditionBatch(targetCall.Id, childCondition.Id, [ sourceApiCallId ])
        Assert.Equal(1, added)

        store.RemoveEntities([ (EntityKind.Call, sourceCall.Id) ])

        Assert.True(store.ApiCalls.ContainsKey(sourceApiCallId))
        let nestedApiCallId =
            childCondition.Conditions
            |> Seq.head
            |> fun apiCall -> apiCall.Id
        Assert.Equal(sourceApiCallId, nestedApiCallId)

    [<Fact>]
    let ``Undo restores deleted entities`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let projectCount = store.Projects.Count
        store.RemoveEntities([ (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        store.Undo()
        Assert.Equal(projectCount, store.Projects.Count)

// =============================================================================
// Rename
// =============================================================================

module RenameTests =

    [<Fact>]
    let ``RenameEntity changes project name`` () =
        let store = createStore ()
        let project = addProject store "OldName"
        store.RenameEntity(project.Id, EntityKind.Project, "NewName")
        Assert.Equal("NewName", store.Projects.[project.Id].Name)

    [<Fact>]
    let ``RenameEntity with same name does not create extra undo step`` () =
        let store = createStore ()
        let project = addProject store "Same"
        store.RenameEntity(project.Id, EntityKind.Project, "Same")
        store.Undo() // should undo AddProject directly (rename no-op must not push)
        Assert.False(store.Projects.ContainsKey(project.Id))

    [<Fact>]
    let ``RenameEntity for Call changes only DevicesAlias`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls |> Seq.head |> (fun kv -> kv.Value)
        Assert.Equal("Dev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)

        // UI는 전체 이름("NewDev.Api")을 전달 — RenameEntity가 alias만 추출
        store.RenameEntity(call.Id, EntityKind.Call, "NewDev.Api")
        Assert.Equal("NewDev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)  // ApiName 불변
        Assert.Equal("NewDev.Api", call.Name)

// =============================================================================
// Arrows
// =============================================================================

module ArrowTests =

    [<Fact>]
    let ``ConnectSelectionInOrder creates ArrowBetweenWorks with parentId = systemId`` () =
        let store = createStore ()
        let _, system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowWorks.Count)
        let arrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(system.Id, arrow.ParentId)

    [<Fact>]
    let ``ConnectSelectionInOrder creates ArrowBetweenCalls with parentId = workId for allowed call arrow type`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)
        let count = store.ConnectSelectionInOrder(callIds, ArrowType.Start)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowCalls.Count)
        let arrow = store.ArrowCalls.Values |> Seq.head
        Assert.Equal(work.Id, arrow.ParentId)
        Assert.Equal(ArrowType.Start, arrow.ArrowType)

    [<Fact>]
    let ``wouldCreateCallCycle detects simple cycle among calls`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1"; "D.A2"; "D.A3" ], true, None)
        let calls = Queries.callsOf work.Id store
        let c1, c2, c3 = calls.[0], calls.[1], calls.[2]
        // c1 → c2 → c3, then check if c3 → c1 would create cycle
        store.ConnectSelectionInOrder([ c1.Id; c2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ c2.Id; c3.Id ], ArrowType.Start) |> ignore
        Assert.True(ConnectionQueries.wouldCreateCallCycle store c3.Id c1.Id)
        // c1 → c3 직접 연결은 사이클 아님 (c3에서 c1로 가는 기존 경로 없음)
        Assert.False(ConnectionQueries.wouldCreateCallCycle store c1.Id c3.Id)

    [<Fact>]
    let ``wouldCreateCallCycle returns false for non-cycle connection`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1"; "D.A2" ], true, None)
        let calls = Queries.callsOf work.Id store
        Assert.False(ConnectionQueries.wouldCreateCallCycle store calls.[0].Id calls.[1].Id)

    [<Fact>]
    let ``ConnectSelectionInOrder blocks disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)

        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.Reset))
        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.StartReset))
        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.ResetReset))
        Assert.Equal(0, store.ArrowCalls.Count)

    [<Fact>]
    let ``UpdateArrowType blocks disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore

        let arrowId = store.ArrowCalls.Values |> Seq.head |> fun a -> a.Id

        Assert.False(store.UpdateArrowType(arrowId, ArrowType.Reset))
        Assert.False(store.UpdateArrowType(arrowId, ArrowType.StartReset))
        Assert.False(store.UpdateArrowType(arrowId, ArrowType.ResetReset))
        Assert.Equal(ArrowType.Start, store.ArrowCalls.[arrowId].ArrowType)

    [<Fact>]
    let ``UpdateArrowTypesBatch updates all selected work arrows in single transaction`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let work4 = addWork store "W4" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work3.Id; work4.Id ], ArrowType.Start) |> ignore
        let arrowIds = store.ArrowWorks.Values |> Seq.map (fun a -> a.Id) |> Seq.toList
        Assert.Equal(3, arrowIds.Length)

        let changed = store.UpdateArrowTypesBatch(arrowIds, ArrowType.StartReset)

        Assert.Equal(3, changed)
        for id in arrowIds do
            Assert.Equal(ArrowType.StartReset, store.ArrowWorks.[id].ArrowType)

        // 단일 트랜잭션 검증: Undo 1회로 3개 화살표 모두 원래 타입으로 복구
        store.Undo()
        for id in arrowIds do
            Assert.Equal(ArrowType.Start, store.ArrowWorks.[id].ArrowType)

    [<Fact>]
    let ``UpdateArrowTypesBatch skips arrows already at target type`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.StartReset) |> ignore
        let arrowIds = store.ArrowWorks.Values |> Seq.map (fun a -> a.Id) |> Seq.toList

        let changed = store.UpdateArrowTypesBatch(arrowIds, ArrowType.StartReset)

        // 1개는 이미 StartReset이므로 1개만 변경
        Assert.Equal(1, changed)

    [<Fact>]
    let ``toggleCallArrowType toggles between Start and Group`` () =
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.Start)
        Assert.Equal(ArrowType.Start, EntityKindRules.toggleCallArrowType ArrowType.Group)

    [<Fact>]
    let ``toggleCallArrowType normalizes disallowed inputs to Group`` () =
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.Reset)
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.StartReset)
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.ResetReset)
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.Unspecified)

    [<Fact>]
    let ``UpdateArrowTypesBatch skips disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2"; "Dev.Api3" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore
        let arrowIds = store.ArrowCalls.Values |> Seq.map (fun a -> a.Id) |> Seq.toList
        Assert.True(arrowIds.Length >= 2)

        let changed = store.UpdateArrowTypesBatch(arrowIds, ArrowType.StartReset)

        Assert.Equal(0, changed)
        for id in arrowIds do
            Assert.Equal(ArrowType.Start, store.ArrowCalls.[id].ArrowType)

    [<Fact>]
    let ``ReverseArrowsBatch reverses all selected work arrows in single transaction`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let work4 = addWork store "W4" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work3.Id; work4.Id ], ArrowType.Start) |> ignore
        let arrows = store.ArrowWorks.Values |> Seq.toList
        let original = arrows |> List.map (fun a -> a.Id, a.SourceId, a.TargetId)
        let arrowIds = arrows |> List.map (fun a -> a.Id)

        let changed = store.ReverseArrowsBatch(arrowIds)

        Assert.Equal(3, changed)
        for (id, oldSrc, oldTgt) in original do
            let a = store.ArrowWorks.[id]
            Assert.Equal(oldTgt, a.SourceId)
            Assert.Equal(oldSrc, a.TargetId)

        // 단일 트랜잭션 검증: Undo 1회로 3개 모두 원위치
        store.Undo()
        for (id, oldSrc, oldTgt) in original do
            let a = store.ArrowWorks.[id]
            Assert.Equal(oldSrc, a.SourceId)
            Assert.Equal(oldTgt, a.TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch skips arrow when reversed key conflicts with non-batch arrow`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Start) |> ignore
        // batch에는 W1→W2 1개만 포함; W2→W1는 store에 별도 존재
        let forward =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work1.Id && a.TargetId = work2.Id)

        let changed = store.ReverseArrowsBatch([ forward.Id ])

        Assert.Equal(0, changed)
        // W1→W2 그대로 유지
        let after = store.ArrowWorks.[forward.Id]
        Assert.Equal(work1.Id, after.SourceId)
        Assert.Equal(work2.Id, after.TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch accepts symmetric pair both in batch`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Start) |> ignore
        let forward =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work1.Id && a.TargetId = work2.Id)
        let backward =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work2.Id && a.TargetId = work1.Id)

        let changed = store.ReverseArrowsBatch([ forward.Id; backward.Id ])

        Assert.Equal(2, changed)
        // forward는 W2→W1, backward는 W1→W2 (서로 swap)
        Assert.Equal(work2.Id, store.ArrowWorks.[forward.Id].SourceId)
        Assert.Equal(work1.Id, store.ArrowWorks.[forward.Id].TargetId)
        Assert.Equal(work1.Id, store.ArrowWorks.[backward.Id].SourceId)
        Assert.Equal(work2.Id, store.ArrowWorks.[backward.Id].TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch handles mixed work and call arrows`` () =
        let store = createStore ()
        let project, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        let workArrowId = (store.ArrowWorks.Values |> Seq.head).Id

        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work1.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore
        let callArrowId = (store.ArrowCalls.Values |> Seq.head).Id

        let workSrcBefore = store.ArrowWorks.[workArrowId].SourceId
        let workTgtBefore = store.ArrowWorks.[workArrowId].TargetId
        let callSrcBefore = store.ArrowCalls.[callArrowId].SourceId
        let callTgtBefore = store.ArrowCalls.[callArrowId].TargetId

        let changed = store.ReverseArrowsBatch([ workArrowId; callArrowId ])

        Assert.Equal(2, changed)
        Assert.Equal(workTgtBefore, store.ArrowWorks.[workArrowId].SourceId)
        Assert.Equal(workSrcBefore, store.ArrowWorks.[workArrowId].TargetId)
        Assert.Equal(callTgtBefore, store.ArrowCalls.[callArrowId].SourceId)
        Assert.Equal(callSrcBefore, store.ArrowCalls.[callArrowId].TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch dedupes duplicate ids`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        let arrowId = (store.ArrowWorks.Values |> Seq.head).Id

        let changed = store.ReverseArrowsBatch([ arrowId; arrowId; arrowId ])

        Assert.Equal(1, changed)
        // 1번만 반전 — 3번 반전되어 원위치하면 안됨
        Assert.Equal(work2.Id, store.ArrowWorks.[arrowId].SourceId)
        Assert.Equal(work1.Id, store.ArrowWorks.[arrowId].TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch reverses non-conflicting arrows when some conflict`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let work4 = addWork store "W4" flow.Id
        // 충돌 쌍: W1→W2 + W2→W1 (둘 다 존재, batch에는 W1→W2만 포함)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Start) |> ignore
        // 정상 화살표: W3→W4 (반전 충돌 없음)
        store.ConnectSelectionInOrder([ work3.Id; work4.Id ], ArrowType.Start) |> ignore

        let forward12 =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work1.Id && a.TargetId = work2.Id)
        let arrow34 =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work3.Id && a.TargetId = work4.Id)

        let changed = store.ReverseArrowsBatch([ forward12.Id; arrow34.Id ])

        // W3→W4만 반전, W1→W2는 W2→W1와 충돌해서 skip
        Assert.Equal(1, changed)
        Assert.Equal(work1.Id, store.ArrowWorks.[forward12.Id].SourceId)
        Assert.Equal(work2.Id, store.ArrowWorks.[forward12.Id].TargetId)
        Assert.Equal(work4.Id, store.ArrowWorks.[arrow34.Id].SourceId)
        Assert.Equal(work3.Id, store.ArrowWorks.[arrow34.Id].TargetId)

    [<Fact>]
    let ``ConnectSelectionInOrder creates cross-flow ArrowBetweenWorks in same system`` () =
        let store = createStore ()
        let _, system, _, work1 = setupBasicHierarchy store
        let flow2 = addFlow store "Flow2" system.Id
        let work2 = addWork store "W2" flow2.Id
        let count = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset)
        Assert.Equal(1, count)
        let arrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(system.Id, arrow.ParentId)

    [<Fact>]
    let ``ConnectSelectionInOrder allows different ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count1 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(1, count1)
        // 같은 소스/타겟이지만 다른 타입 → 허용
        let count2 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Reset)
        Assert.Equal(1, count2)
        Assert.Equal(2, store.ArrowWorks.Count)

    [<Fact>]
    let ``ConnectSelectionInOrder blocks same ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count1 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(1, count1)
        // 동일 소스/타겟/타입 → 중복 차단
        let count2 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(0, count2)
        Assert.Equal(1, store.ArrowWorks.Count)

    [<Fact>]
    let ``ReconnectArrow allows different ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work3.Id ], ArrowType.Reset) |> ignore

        let arrowToReconnect =
            store.ArrowWorks.Values
            |> Seq.find (fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work3.Id && arrow.ArrowType = ArrowType.Reset)

        let changed = store.ReconnectArrow(arrowToReconnect.Id, false, work2.Id)

        Assert.True(changed)
        Assert.Equal(2, store.ArrowWorks.Count)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work2.Id && arrow.ArrowType = ArrowType.Start)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work2.Id && arrow.ArrowType = ArrowType.Reset)

    [<Fact>]
    let ``ReconnectArrow blocks same ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work3.Id ], ArrowType.Start) |> ignore

        let arrowToReconnect =
            store.ArrowWorks.Values
            |> Seq.find (fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work3.Id && arrow.ArrowType = ArrowType.Start)

        let changed = store.ReconnectArrow(arrowToReconnect.Id, false, work2.Id)

        Assert.False(changed)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.Id = arrowToReconnect.Id && arrow.TargetId = work3.Id)

    [<Fact>]
    let ``RemoveArrows deletes arrows`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset) |> ignore
        let arrowId = store.ArrowWorks.Values |> Seq.head |> fun a -> a.Id
        let removed = store.RemoveArrows([ arrowId ])
        Assert.Equal(1, removed)
        Assert.Equal(0, store.ArrowWorks.Count)

// =============================================================================
// Paste
// =============================================================================

