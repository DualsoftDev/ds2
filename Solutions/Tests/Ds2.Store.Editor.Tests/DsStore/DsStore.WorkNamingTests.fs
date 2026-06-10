module Ds2.Store.Editor.Tests.WorkNamingTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

[<Fact>]
let ``AddWork sets FlowPrefix from parent Flow`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id
    Assert.Equal("TestFlow", work2.FlowPrefix)
    Assert.Equal("Work2", work2.LocalName)
    Assert.Equal("TestFlow.Work2", work2.Name)

[<Fact>]
let ``AddWork rejects duplicate LocalName in same Flow`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    Assert.Throws<InvalidOperationException>(fun () ->
        store.AddWork("TestWork", flow.Id) |> ignore)

[<Fact>]
let ``AddFlow rejects duplicate name in same System`` () =
    let store = createStore()
    let _, system, _, _ = setupBasicHierarchy store
    Assert.Throws<InvalidOperationException>(fun () ->
        store.AddFlow("TestFlow", system.Id) |> ignore)

[<Fact>]
let ``isCallNameUniqueInWork detects duplicate Call name`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    Assert.False(Queries.isCallNameUniqueInWork work.Id "Dev.Api" None store)

[<Fact>]
let ``isCallNameUniqueInWork allows different Call name`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    Assert.True(Queries.isCallNameUniqueInWork work.Id "Dev.Api2" None store)

[<Fact>]
let ``AddCallsWithDevice rejects duplicate Call name (use AddReferenceCallToWork instead)`` () =
    // 정책: 같은 Work 안 동일 callName 의 *원본 Call* 재추가 금지.
    // 사용자가 같은 device.api 를 반복 호출하고 싶으면 호출자가 명시적으로 Reference Call 사용.
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)) |> ignore
    // 원본 1개 유지.
    let originals = Queries.originalCallsOf work.Id store |> List.filter (fun c -> c.Name = "Dev.Api")
    Assert.Equal(1, originals.Length)
    // Reference Call 은 별도 path 로 추가 가능.
    let originalId = originals.Head.Id
    store.AddReferenceCallToWork(originalId, work.Id) |> ignore
    let allWithName = Queries.callsOf work.Id store |> List.filter (fun c -> c.Name = "Dev.Api")
    Assert.Equal(2, allWithName.Length)

[<Fact>]
let ``isCallNameUniqueInWork with excludeId ignores self`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let call = Queries.originalCallsOf work.Id store |> List.head
    Assert.False(Queries.isCallNameUniqueInWork work.Id "Dev.Api" None store)
    Assert.True(Queries.isCallNameUniqueInWork work.Id "Dev.Api" (Some call.Id) store)

[<Fact>]
let ``RenameEntity Call rejects duplicate name in same Work`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev2.Api" ], true, None)
    let call2 = Queries.originalCallsOf work.Id store |> List.find (fun c -> c.DevicesAlias = "Dev2")
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameEntity(call2.Id, EntityKind.Call, "Dev"))

[<Fact>]
let ``AddReferenceCall creates reference with same naming`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    let refCall = store.Calls.[refId]
    Assert.Equal(Some originalCall.Id, refCall.ReferenceOf)
    Assert.Equal("Dev", refCall.DevicesAlias)
    Assert.Equal("Api", refCall.ApiName)
    Assert.Equal(work.Id, refCall.ParentId)

[<Fact>]
let ``Reference Call blocks property mutations`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    // Timeout 수정 차단
    Assert.Throws<InvalidOperationException>(fun () ->
        store.UpdateCallTimeoutMs(refId, Some 1000)) |> ignore
    // 조건 추가 차단
    Assert.Throws<InvalidOperationException>(fun () ->
        store.AddCallCondition(refId, ConditionType.AutoAux)) |> ignore
    // Rename 차단
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameEntity(refId, EntityKind.Call, "NewName")) |> ignore
    // 원본은 정상 편집 가능
    store.UpdateCallTimeoutMs(originalCall.Id, Some 1000)

[<Fact>]
let ``RemoveEntities cascades to reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    store.RemoveEntities([ (EntityKind.Call, originalCall.Id) ])
    Assert.False(store.Calls.ContainsKey(refId))

[<Fact>]
let ``originalCallsOf excludes reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    store.AddReferenceCall(originalCall.Id) |> ignore
    let originals = Queries.originalCallsOf work.Id store
    Assert.Equal(1, originals.Length)
    let all = Queries.callsOf work.Id store
    Assert.Equal(2, all.Length)

[<Fact>]
let ``callReferenceGroupOf returns original plus references`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    let group = Queries.callReferenceGroupOf originalCall.Id store
    Assert.Contains(originalCall.Id, group)
    Assert.Contains(refId, group)
    Assert.Equal(2, group.Length)

[<Fact>]
let ``isCallNameUniqueInWork ignores reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    store.AddReferenceCall(originalCall.Id) |> ignore
    // 참조 Call이 있어도 원본 1개만 있으므로 같은 이름은 중복
    Assert.False(Queries.isCallNameUniqueInWork work.Id "Dev.Api" None store)
    // 다른 이름은 허용
    Assert.True(Queries.isCallNameUniqueInWork work.Id "Dev.Api2" None store)

[<Fact>]
let ``RenameEntity Work changes LocalName only`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    store.RenameEntity(work.Id, EntityKind.Work, "Renamed")
    Assert.Equal("Renamed", work.LocalName)
    Assert.Equal("TestFlow", work.FlowPrefix)
    Assert.Equal("TestFlow.Renamed", work.Name)

[<Fact>]
let ``RenameEntity Work emits treeName as LocalName`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let mutable captured = None
    use _sub = store.ObserveEvents().Subscribe(fun evt ->
        match evt with
        | EntityRenamed(id, newName, treeName) when id = work.Id ->
            captured <- Some (newName, treeName)
        | _ -> ())
    store.RenameEntity(work.Id, EntityKind.Work, "Renamed")
    let (displayName, treeName) = captured.Value
    Assert.Equal("TestFlow.Renamed", displayName) // 캔버스용: Flow.LocalName
    Assert.Equal("Renamed", treeName)              // 트리용: LocalName만

[<Fact>]
let ``RenameEntity Work with dot extracts LocalName`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    store.RenameEntity(work.Id, EntityKind.Work, "SomeFlow.NewLocal")
    Assert.Equal("NewLocal", work.LocalName)
    Assert.Equal("TestFlow", work.FlowPrefix) // FlowPrefix는 변경 안 됨

[<Fact>]
let ``RenameEntity Work rejects duplicate LocalName`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    addWork store "Other" flow.Id |> ignore
    let otherId = store.Works.Values |> Seq.find (fun w -> w.LocalName = "Other") |> fun w -> w.Id
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameEntity(otherId, EntityKind.Work, "TestWork"))

[<Fact>]
let ``RenameEntity Flow cascades FlowPrefix to child Works`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    let work2 = addWork store "W2" flow.Id
    store.RenameEntity(flow.Id, EntityKind.Flow, "RenamedFlow")
    Assert.Equal("RenamedFlow", flow.Name)
    Assert.Equal("RenamedFlow", work.FlowPrefix)
    Assert.Equal("RenamedFlow", work2.FlowPrefix)
    Assert.Equal("RenamedFlow.TestWork", work.Name)

[<Fact>]
let ``RenameEntity Flow Undo restores FlowPrefix`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    let workId = work.Id
    store.RenameEntity(flow.Id, EntityKind.Flow, "NewFlow")
    Assert.Equal("NewFlow", store.Works.[workId].FlowPrefix)
    store.Undo()
    Assert.Equal("TestFlow", store.Works.[workId].FlowPrefix)
    Assert.Equal("TestFlow.TestWork", store.Works.[workId].Name)

[<Fact>]
let ``AddReferenceWork creates reference with same naming`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)
    let refWork = store.Works.[refId]
    Assert.Equal(Some work.Id, refWork.ReferenceOf)
    Assert.Equal("TestFlow", refWork.FlowPrefix)
    Assert.Equal("TestWork", refWork.LocalName)
    Assert.Equal(work.ParentId, refWork.ParentId)

[<Fact>]
let ``AddReferenceWork from reference resolves to original`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)
    // Ref에서 또 Ref를 만들면 원본을 직접 가리켜야 함
    let refOfRefId = store.AddReferenceWork(refId)
    let refOfRef = store.Works.[refOfRefId]
    Assert.Equal(Some work.Id, refOfRef.ReferenceOf)

[<Fact>]
let ``AddReferenceCall from reference resolves to original`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    // Ref에서 또 Ref를 만들면 원본을 직접 가리켜야 함
    let refOfRefId = store.AddReferenceCall(refId)
    let refOfRef = store.Calls.[refOfRefId]
    Assert.Equal(Some originalCall.Id, refOfRef.ReferenceOf)

[<Fact>]
let ``RemoveEntities cascades to reference Works`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)
    Assert.True(store.Works.ContainsKey(refId))
    store.RemoveEntities([ EntityKind.Work, work.Id ])
    Assert.False(store.Works.ContainsKey(work.Id))
    Assert.False(store.Works.ContainsKey(refId))

[<Fact>]
let ``DsQuery nextUniqueName auto-increments`` () =
    Assert.Equal("Work", Queries.nextUniqueName "Work" [])
    Assert.Equal("Work_1", Queries.nextUniqueName "Work" ["Work"])
    Assert.Equal("Work_2", Queries.nextUniqueName "Work" ["Work"; "Work_1"])

[<Fact>]
let ``DsQuery referenceGroupOf returns original plus references`` () =
    let store = createStore()
    let _, _, _, work = setupBasicHierarchy store
    let refId = store.AddReferenceWork(work.Id)

    let group = Queries.referenceGroupOf work.Id store
    Assert.Contains(work.Id, group)
    Assert.Contains(refId, group)
    Assert.Equal(2, group.Length)

    // reference에서 조회해도 같은 그룹
    let group2 = Queries.referenceGroupOf refId store
    Assert.Equal(2, group2.Length)

[<Fact>]
let ``DsQuery originalWorksOf excludes reference Works`` () =
    let store = createStore()
    let _, _, flow, work = setupBasicHierarchy store
    store.AddReferenceWork(work.Id) |> ignore

    let originals = Queries.originalWorksOf flow.Id store
    Assert.Equal(1, originals.Length)
    Assert.Equal(work.Id, originals.[0].Id)

    let all = Queries.worksOf flow.Id store
    Assert.Equal(2, all.Length)

[<Fact>]
let ``parseWorkNameParts splits prefix and localName`` () =
    let struct(prefix, local) = TokenRoleOps.parseWorkNameParts "Flow1.Work1"
    Assert.Equal("Flow1.", prefix)
    Assert.Equal("Work1", local)

[<Fact>]
let ``tryFindOriginalCallInWork returns Some when original Call with name exists`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let result = Queries.tryFindOriginalCallInWork work.Id "Dev.Api" store
    Assert.True(result.IsSome)
    let originalCall = Queries.originalCallsOf work.Id store |> List.head
    Assert.Equal(originalCall.Id, result.Value)

[<Fact>]
let ``tryFindOriginalCallInWork returns None when name absent`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let result = Queries.tryFindOriginalCallInWork work.Id "Other.Api" store
    Assert.True(result.IsNone)

[<Fact>]
let ``tryFindOriginalCallInWork ignores reference Calls`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.callsOf work.Id store |> List.head
    let refId = store.AddReferenceCall(originalCall.Id)
    // 참조 Call 도 같은 이름 — but tryFindOriginalCallInWork 는 원본만.
    let result = Queries.tryFindOriginalCallInWork work.Id "Dev.Api" store
    Assert.Equal(originalCall.Id, result.Value)
    Assert.NotEqual(refId, result.Value)

[<Fact>]
let ``AddReferenceCallToWork places ref in target Work (not original's parent)`` () =
    let store = createStore()
    let project, _, flow, work = setupBasicHierarchy store
    // 두 번째 Work 추가
    let work2Id = store.AddWork("Work2", flow.Id)
    // 원본 Call 은 첫 번째 Work 에
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.originalCallsOf work.Id store |> List.head
    // Reference 를 두 번째 Work 에 추가
    let refId = store.AddReferenceCallToWork(originalCall.Id, work2Id)
    let refCall = store.Calls.[refId]
    Assert.Equal(Some originalCall.Id, refCall.ReferenceOf)
    Assert.Equal(work2Id, refCall.ParentId)
    // 원본은 그대로
    Assert.Equal(work.Id, originalCall.ParentId)

[<Fact>]
let ``AddReferenceCallToWork resolves from reference origin to true original`` () =
    let store = createStore()
    let project, _, flow, work = setupBasicHierarchy store
    let work2Id = store.AddWork("Work2", flow.Id)
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let originalCall = Queries.originalCallsOf work.Id store |> List.head
    let firstRefId = store.AddReferenceCall(originalCall.Id)
    // 두 번째 ref 는 *첫 번째 ref* 의 ID 로 호출해도 결국 원본을 가리켜야.
    let secondRefId = store.AddReferenceCallToWork(firstRefId, work2Id)
    let secondRef = store.Calls.[secondRefId]
    Assert.Equal(Some originalCall.Id, secondRef.ReferenceOf)
    Assert.Equal(work2Id, secondRef.ParentId)

[<Fact>]
let ``parseWorkNameParts with no dot returns empty prefix`` () =
    let struct(prefix, local) = TokenRoleOps.parseWorkNameParts "JustName"
    Assert.Equal("", prefix)
    Assert.Equal("JustName", local)

// ─────────────────────────────────────────────────────────────────────────
// findConflictingDeviceSystemType — DevicesAlias 단위 SystemType 충돌 판정
// ─────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``findConflictingDeviceSystemType returns None when types match`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "dev.ADV" ], true, Some "Conveyor")
    Assert.Equal(None, Queries.findConflictingDeviceSystemType project.Id "dev" (Some "Conveyor") store)

[<Fact>]
let ``findConflictingDeviceSystemType returns Some when types differ`` () =
    // devAlias 단위 검증이므로 같은 devAlias 면 ApiName 이 달라도(dev.ADV → dev.MOVE) 동일 충돌.
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "dev.ADV" ], true, Some "Conveyor")
    let result = Queries.findConflictingDeviceSystemType project.Id "dev" (Some "Robot") store
    Assert.Equal(Some ("Conveyor", "Robot"), result)

[<Fact>]
let ``findConflictingDeviceSystemType returns None when existing SystemType is None`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "dev.ADV" ], true, None)
    Assert.Equal(None, Queries.findConflictingDeviceSystemType project.Id "dev" (Some "Robot") store)

[<Fact>]
let ``findConflictingDeviceSystemType returns None for different devAlias`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "dev.ADV" ], true, Some "Conveyor")
    Assert.Equal(None, Queries.findConflictingDeviceSystemType project.Id "other" (Some "Robot") store)

[<Fact>]
let ``findConflictingDeviceSystemType returns None when new SystemType is None`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "dev.ADV" ], true, Some "Conveyor")
    Assert.Equal(None, Queries.findConflictingDeviceSystemType project.Id "dev" None store)

// ─── Core 진입 가드 회귀 — 사용자 우려 ──────────────────────────────
// 사용자 명시: "Promaker UI 가드만 있어서 AASX/LLM import 로 깨진 데이터가 들어옴".
// 본 module 은 Core 진입점 (AddProject/AddSystem/AddFlow/AddWork/AddCall*) 에 추가된
// 가드를 직접 검증 — UI 경로 우회해도 invalidOp 던지는지 확인.

[<Fact>]
let ``AddProject rejects empty name`` () =
    let store = createStore()
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddProject("") |> ignore) |> ignore

[<Fact>]
let ``AddProject rejects whitespace-only name`` () =
    let store = createStore()
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddProject("   ") |> ignore) |> ignore

[<Fact>]
let ``AddProject rejects duplicate name`` () =
    let store = createStore()
    store.AddProject("P") |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddProject("P") |> ignore) |> ignore

[<Fact>]
let ``AddSystem rejects empty name`` () =
    let store = createStore()
    let project = addProject store "P"
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddSystem("", project.Id, true) |> ignore) |> ignore

[<Fact>]
let ``AddSystem rejects duplicate name in same Project (active + active)`` () =
    let store = createStore()
    let project = addProject store "P"
    store.AddSystem("S1", project.Id, true) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddSystem("S1", project.Id, true) |> ignore) |> ignore

[<Fact>]
let ``AddSystem rejects duplicate name in same Project (active + passive)`` () =
    // 사용자 우려의 핵심 — 같은 Project 안에 active + passive 가 동일 이름이면 충돌.
    // 이전엔 AASX/Mermaid import 경로로 빠져나갈 수 있던 케이스.
    let store = createStore()
    let project = addProject store "P"
    store.AddSystem("S1", project.Id, true) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddSystem("S1", project.Id, false) |> ignore) |> ignore

[<Fact>]
let ``AddSystem allows same name in different Projects`` () =
    let store = createStore()
    let p1 = addProject store "P1"
    let p2 = addProject store "P2"
    store.AddSystem("S", p1.Id, true) |> ignore
    // 다른 Project 에는 같은 System 이름 OK.
    store.AddSystem("S", p2.Id, true) |> ignore
    Assert.Equal(2, store.Systems.Count)

[<Fact>]
let ``AddFlow rejects empty name`` () =
    let store = createStore()
    let _, system, _, _ = setupBasicHierarchy store
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddFlow("", system.Id) |> ignore) |> ignore

[<Fact>]
let ``AddWork rejects empty name`` () =
    let store = createStore()
    let _, _, flow, _ = setupBasicHierarchy store
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddWork("", flow.Id) |> ignore) |> ignore

[<Fact>]
let ``AddCallsWithDevice rejects empty Call name`` () =
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallsWithDevice(project.Id, work.Id, [ "" ], true, None)) |> ignore

[<Fact>]
let ``AddCallWithLinkedApiDefs rejects empty DevicesAlias or ApiName`` () =
    let store = createStore()
    let _project, system, _, work = setupBasicHierarchy store
    let apiDef = addApiDef store "ADV" system.Id
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallWithLinkedApiDefs(work.Id, "", "ADV", [ apiDef.Id ]) |> ignore) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallWithLinkedApiDefs(work.Id, "dev", "", [ apiDef.Id ]) |> ignore) |> ignore

[<Fact>]
let ``AddCallWithLinkedApiDefs rejects dot in DevicesAlias or ApiName`` () =
    // Call.Name = "{DevicesAlias}.{ApiName}" — 양쪽에 추가 점 들어가면 parseCallName ambiguous.
    let store = createStore()
    let _, system, _, work = setupBasicHierarchy store
    let apiDef = addApiDef store "ADV" system.Id
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallWithLinkedApiDefs(work.Id, "dev.bad", "ADV", [ apiDef.Id ]) |> ignore) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallWithLinkedApiDefs(work.Id, "dev", "ADV.bad", [ apiDef.Id ]) |> ignore) |> ignore

[<Fact>]
let ``AddCallsWithDevice rejects malformed Call name (not single-dot)`` () =
    // "DevicesAlias.ApiName" 형식 강제 — 점 0개 또는 2개 이상이면 reject.
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallsWithDevice(project.Id, work.Id, [ "noDot" ], true, None)) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallsWithDevice(project.Id, work.Id, [ "too.many.dots" ], true, None)) |> ignore

[<Fact>]
let ``AddApiDefWithProperties rejects empty name and duplicate name in same System`` () =
    let store = createStore()
    let _, system, _, _ = setupBasicHierarchy store
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddApiDefWithProperties("", system.Id) |> ignore) |> ignore
    store.AddApiDefWithProperties("ADV", system.Id) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddApiDefWithProperties("ADV", system.Id) |> ignore) |> ignore

[<Fact>]
let ``AddCallsWithDevice rejects SystemType conflict (same devAlias different SystemType)`` () =
    // 사용자 우려: 같은 Project 안에서 동일 DevicesAlias 가 *다른 SystemType* 으로 등록되면 충돌.
    // 이전엔 Promaker UI Create.cs 만 검사했고 AASX/Mermaid/LlmAgent 통과.
    let store = createStore()
    let project, _, _, work = setupBasicHierarchy store
    // 먼저 SystemType "Conveyor" 로 등록.
    store.AddCallsWithDevice(project.Id, work.Id, [ "dev.ADV" ], true, Some "Conveyor")
    // 같은 dev alias 를 "Robot" 으로 추가 시도 → reject.
    Assert.Throws<System.InvalidOperationException>(fun () ->
        store.AddCallsWithDevice(project.Id, work.Id, [ "dev.MOVE" ], true, Some "Robot")) |> ignore

[<Fact>]
let ``ImportPlan.applyDirect skips invalid operations with log (graceful import)`` () =
    // AASX/Mermaid/CSV 의 ImportPlan 경로가 *invalid op* 받아도 *valid op 는 정상 import*.
    // 위반은 skip + log — 외부 import 가 부분 깨졌어도 valid 부분 보존.
    let store = createStore()
    let validProject = Project("P_Valid")
    let invalidProject = Project("")  // 빈 이름 — invariant 위반
    let plan : ImportPlan = {
        Operations = [
            AddProject validProject
            AddProject invalidProject  // skip 되어야
        ]
    }
    ImportPlan.applyDirect store plan
    Assert.True(store.Projects.ContainsKey(validProject.Id))
    Assert.False(store.Projects.ContainsKey(invalidProject.Id))
