module ImportPlanApplyApiCallTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// extend-mcp §5.6 신규 6 — `queueAddCall` 의 ApiCall 자동 cascade 가
/// `ImportPlanApply` 후 store.ApiCalls 에 정확히 binding 되는지 검증.
///
/// 핵심 회귀 방어:
///   ① `apiCall.ApiDefId` = 인자로 받은 apiDefId
///   ② `apiCall.OriginFlowId` = workId 의 parent Flow.Id (todo §9.6 / rev 5 강조)
///   ③ helper + 후속 add_call 통합 시퀀스 — helper 발행 ApiDef.Id 를 후속 add_call 이 정확히 참조

[<Fact>]
let ``queueAddCall plan 에 AddApiCall op 누적 — ApiDefId / OriginFlowId 정확히 set`` () =
    let store = DsStore()
    let projectId = store.AddProject("M1")
    let activeId = store.AddSystem("Ctl", projectId, true)
    let passiveId = store.AddSystem("Cyl", projectId, false)
    let activeFlowId = store.AddFlow("Run", activeId)
    let workId = store.AddWork("Adv", activeFlowId)
    // Passive 에 ApiDef 생성 (queueAddApiDef 우회 — primitive ctor internal 회피)
    let plan0 = ImportPlanBuilder()
    let apiDefId = ToolOperations.queueAddApiDef plan0 store "ADV" passiveId None None
    store.ApplyImportPlan("apidef", plan0.Build())
    // queueAddCall — ApiCall cascade 가 plan 에 누적되는지
    let plan = ImportPlanBuilder()
    let callId = ToolOperations.queueAddCall plan store workId apiDefId
    let apiCall =
        plan.Operations
        |> Seq.pick (function AddApiCall a -> Some a | _ -> None)
    Assert.Equal(Some apiDefId, apiCall.ApiDefId)
    Assert.Equal(Some activeFlowId, apiCall.OriginFlowId)
    // Call → ApiCall 컨테이너에도 등록
    let call =
        plan.Operations
        |> Seq.pick (function AddCall c when c.Id = callId -> Some c | _ -> None)
    Assert.Single(call.ApiCalls) |> ignore
    Assert.Equal(apiDefId, call.ApiCalls.[0].ApiDefId.Value)

[<Fact>]
let ``ApplyImportPlan 후 store.ApiCalls 에 ApiCall 등록 + binding 보존`` () =
    let store = DsStore()
    let projectId = store.AddProject("M1")
    let activeId = store.AddSystem("Ctl", projectId, true)
    let passiveId = store.AddSystem("Cyl", projectId, false)
    let activeFlowId = store.AddFlow("Run", activeId)
    let workId = store.AddWork("Adv", activeFlowId)
    let plan0 = ImportPlanBuilder()
    let apiDefId = ToolOperations.queueAddApiDef plan0 store "ADV" passiveId None None
    store.ApplyImportPlan("apidef", plan0.Build())
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddCall plan store workId apiDefId |> ignore
    store.ApplyImportPlan("call", plan.Build())
    // store 에 ApiCall 1개 등록
    Assert.Equal(1, store.ApiCalls.Count)
    let apiCall = store.ApiCalls.Values |> Seq.head
    Assert.Equal(Some apiDefId, apiCall.ApiDefId)
    Assert.Equal(Some activeFlowId, apiCall.OriginFlowId)

[<Fact>]
let ``helper add_cylinder 발행 ApiDef.Id 가 후속 add_call 통합 시퀀스에서 정확히 binding`` () =
    let store = DsStore()
    let projectId = store.AddProject("M1")
    let activeId = store.AddSystem("Ctl", projectId, true)
    let activeFlowId = store.AddFlow("Run", activeId)
    let workIdAdv = store.AddWork("Adv", activeFlowId)
    let workIdRet = store.AddWork("Ret", activeFlowId)
    let plan = ImportPlanBuilder()
    // helper 가 PassiveSystem cascade + ApiDef×2 발행
    let _, apiDefIds = ToolOperations.queueAddCylinder plan store "Cyl1" [] None
    let advApiDefId = apiDefIds |> List.find (fun (n, _) -> n = "ADV") |> snd
    let retApiDefId = apiDefIds |> List.find (fun (n, _) -> n = "RET") |> snd
    // 후속 add_call 두 번 — Active Work 가 Passive 의 ApiDef 를 참조
    let _ = ToolOperations.queueAddCall plan store workIdAdv advApiDefId
    let _ = ToolOperations.queueAddCall plan store workIdRet retApiDefId
    store.ApplyImportPlan("cyl turn", plan.Build())
    // store 에 ApiCall 2개, 각각 정확한 ApiDef 참조
    Assert.Equal(2, store.ApiCalls.Count)
    let apiCallsByName =
        store.ApiCalls.Values
        |> Seq.map (fun ac -> ac.Name, ac)
        |> Map.ofSeq
    let advCall = apiCallsByName |> Map.find "Cyl1.ADV"
    let retCall = apiCallsByName |> Map.find "Cyl1.RET"
    Assert.Equal(Some advApiDefId, advCall.ApiDefId)
    Assert.Equal(Some retApiDefId, retCall.ApiDefId)
    // OriginFlowId = Active Flow.Id (workId 의 parent)
    Assert.Equal(Some activeFlowId, advCall.OriginFlowId)
    Assert.Equal(Some activeFlowId, retCall.OriginFlowId)

[<Fact>]
let ``룰 C 런타임 차단 — Active System 의 ApiDef 참조 add_call 은 invalidOp`` () =
    let store = DsStore()
    let projectId = store.AddProject("M1")
    let activeId = store.AddSystem("Ctl", projectId, true)
    let activeFlowId = store.AddFlow("Run", activeId)
    let workId = store.AddWork("Adv", activeFlowId)
    // Active System 에 ApiDef — 정상 모델은 아니지만 런타임 검증 회귀 방어
    let plan0 = ImportPlanBuilder()
    let badApiDefId = ToolOperations.queueAddApiDef plan0 store "BAD" activeId None None
    store.ApplyImportPlan("bad apidef on active", plan0.Build())
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddCall plan store workId badApiDefId |> ignore)
    Assert.Contains("Active", ex.Message)
    Assert.Contains("Passive", ex.Message)
    Assert.Contains("룰 C", ex.Message)
