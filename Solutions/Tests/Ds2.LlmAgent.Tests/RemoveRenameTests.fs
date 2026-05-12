module RemoveRenameTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// Test fixture helper — Ds2.Core 의 ApiDef ctor 가 internal 이라 직접 호출 불가.
/// ToolOperations.queueAddApiDef + ApplyImportPlan 으로 우회 (Ds2.LlmAgent 측 path 그대로).
let private addApiDefDirect (store: DsStore) (name: string) (systemId: Guid) : Guid =
    let plan = ImportPlanBuilder()
    let id = ToolOperations.queueAddApiDef plan store name systemId None None
    store.ApplyImportPlan($"test add apidef {name}", plan.Build())
    id

/// Phase 2 — RemoveEntity (cascade) + RenameEntity (System / ApiDef) 회귀.

let private buildFixture () =
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    let sysId = store.AddSystem("Sys", projectId, true)
    let flowId = store.AddFlow("F", sysId)
    let workId = store.AddWork("W", flowId)
    store, projectId, sysId, flowId, workId

let private applyPlan (store: DsStore) (plan: ImportPlanBuilder) (label: string) =
    store.ApplyImportPlan(label, plan.Build())

// ─── Remove ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``queueRemoveEntity 는 EntityKind 자동 판별 + RemoveEntity op 누적`` () =
    let store, _, sysId, flowId, workId = buildFixture ()
    let plan = ImportPlanBuilder()
    let kind = ToolOperations.queueRemoveEntity plan store sysId
    Assert.Equal(EntityKind.System, kind)
    Assert.Equal(1, plan.Count)
    let ops = plan.Operations |> Seq.toList
    match ops.[0] with
    | RemoveEntity (k, id) ->
        Assert.Equal(EntityKind.System, k)
        Assert.Equal(sysId, id)
    | _ -> Assert.Fail("RemoveEntity op 누적 실패")
    // store 는 아직 변경 X (turn end 까지 보류)
    Assert.True(store.Systems.ContainsKey(sysId))
    Assert.True(store.Flows.ContainsKey(flowId))
    Assert.True(store.Works.ContainsKey(workId))

[<Fact>]
let ``ApplyImportPlan 후 cascade — System 제거 시 자식 Flow / Work 모두 사라짐`` () =
    let store, _, sysId, flowId, workId = buildFixture ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueRemoveEntity plan store sysId |> ignore
    applyPlan store plan "remove sys"
    Assert.False(store.Systems.ContainsKey(sysId))
    Assert.False(store.Flows.ContainsKey(flowId))
    Assert.False(store.Works.ContainsKey(workId))

[<Fact>]
let ``Project 의 ActiveSystemIds 에서도 제거됨 (RemoveSystem cascade)`` () =
    let store, projectId, sysId, _, _ = buildFixture ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueRemoveEntity plan store sysId |> ignore
    applyPlan store plan "remove sys"
    let project = store.Projects.[projectId]
    Assert.False(project.ActiveSystemIds.Contains(sysId))

[<Fact>]
let ``존재하지 않는 GUID 의 remove 는 invalidOp`` () =
    let store, _, _, _, _ = buildFixture ()
    let plan = ImportPlanBuilder()
    let bogus = Guid.NewGuid()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueRemoveEntity plan store bogus |> ignore)
    Assert.Contains("어디에도 없음", ex.Message)

[<Fact>]
let ``같은 turn 의 add_* 직후 remove_entity 는 명확한 진단 메시지로 거부`` () =
    let store, _, _, _, _ = buildFixture ()
    let plan = ImportPlanBuilder()
    // 같은 turn 안에서 add_active_system 을 plan 에 누적 — store 는 아직 미반영 (extend-mcp L2 분리)
    let newSysId = ToolOperations.queueAddActiveSystem plan store "TempSys"
    Assert.False(store.Systems.ContainsKey(newSysId))   // store 는 모름 (turn end 까지)
    let countAfterAdd = plan.Count   // queueAddActiveSystem 은 AddSystem + LinkSystemToProject 누적
    // 같은 plan 으로 remove_entity 호출 → "같은 turn add 직후 remove" 진단
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueRemoveEntity plan store newSysId |> ignore)
    Assert.Contains("같은 apply_model_doc turn", ex.Message)
    // C1 fix: 메시지가 doc-level 어휘 (patch.remove / apply_model_doc) 로 재작성됨.
    Assert.Contains("patch.remove", ex.Message)
    // plan 에는 RemoveEntity 가 누적되지 않음 (count 는 변하지 않음)
    Assert.Equal(countAfterAdd, plan.Count)

[<Fact>]
let ``Flow 단독 제거 시 자식 Work 도 cascade`` () =
    let store, _, _, flowId, workId = buildFixture ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueRemoveEntity plan store flowId |> ignore
    applyPlan store plan "remove flow"
    Assert.False(store.Flows.ContainsKey(flowId))
    Assert.False(store.Works.ContainsKey(workId))

[<Fact>]
let ``ApiDef 단독 제거 — 자식 cascade 없음`` () =
    let store, _, sysId, _, _ = buildFixture ()
    let apiDefId = addApiDefDirect store "Api1" sysId
    let plan = ImportPlanBuilder()
    let kind = ToolOperations.queueRemoveEntity plan store apiDefId
    Assert.Equal(EntityKind.ApiDef, kind)
    applyPlan store plan "remove apidef"
    Assert.False(store.ApiDefs.ContainsKey(apiDefId))
    Assert.True(store.Systems.ContainsKey(sysId))  // System 은 그대로

// ─── Rename ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``System rename — name 변경 + EntityKind.System 반환`` () =
    let store, _, sysId, _, _ = buildFixture ()
    let plan = ImportPlanBuilder()
    let kind = ToolOperations.queueRenameEntity plan store sysId "RenamedSys"
    Assert.Equal(EntityKind.System, kind)
    applyPlan store plan "rename sys"
    Assert.Equal("RenamedSys", store.Systems.[sysId].Name)

[<Fact>]
let ``ApiDef rename — name 변경 + 같은 System 내 중복 invalidOp`` () =
    let store, _, sysId, _, _ = buildFixture ()
    let apiDefId1 = addApiDefDirect store "Api1" sysId
    let apiDefId2 = addApiDefDirect store "Api2" sysId
    // 정상: Api1 → Api3
    let plan1 = ImportPlanBuilder()
    ToolOperations.queueRenameEntity plan1 store apiDefId1 "Api3" |> ignore
    applyPlan store plan1 "rename apidef ok"
    Assert.Equal("Api3", store.ApiDefs.[apiDefId1].Name)
    // 중복: Api3 → Api2 (이미 존재)
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueRenameEntity plan2 store apiDefId1 "Api2" |> ignore)
    Assert.Contains("이미", ex.Message)
    Assert.Contains("ApiDef", ex.Message)
    // 변경 없어야 함
    Assert.Equal("Api3", store.ApiDefs.[apiDefId1].Name)
    Assert.Equal("Api2", store.ApiDefs.[apiDefId2].Name)

[<Fact>]
let ``Flow rename 은 phase 2 미지원 — invalidOp`` () =
    let store, _, _, flowId, _ = buildFixture ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueRenameEntity plan store flowId "X" |> ignore)
    Assert.Contains("System 또는 ApiDef", ex.Message)

[<Fact>]
let ``Work rename 은 phase 2 미지원 — invalidOp`` () =
    let store, _, _, _, workId = buildFixture ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueRenameEntity plan store workId "X" |> ignore)
    Assert.Contains("System 또는 ApiDef", ex.Message)

[<Fact>]
let ``Project rename 은 phase 2 미지원 — invalidOp`` () =
    let store, projectId, _, _, _ = buildFixture ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueRenameEntity plan store projectId "X" |> ignore)
    Assert.Contains("System 또는 ApiDef", ex.Message)

[<Fact>]
let ``ApiDef rename — 자기 자신 이름 유지는 OK (자기 self-clash 회피)`` () =
    let store, _, sysId, _, _ = buildFixture ()
    let apiDefId = addApiDefDirect store "Api1" sysId
    let plan = ImportPlanBuilder()
    // 같은 이름으로 rename — clash 검사가 자기 자신 (d.Id <> id) 제외하므로 통과
    let kind = ToolOperations.queueRenameEntity plan store apiDefId "Api1"
    Assert.Equal(EntityKind.ApiDef, kind)
    applyPlan store plan "rename same"
    Assert.Equal("Api1", store.ApiDefs.[apiDefId].Name)
