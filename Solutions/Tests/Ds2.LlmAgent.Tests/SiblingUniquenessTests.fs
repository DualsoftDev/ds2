module SiblingUniquenessTests

open System
open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// 사용자 요청 rev2 — Sibling Uniqueness 일반화 회귀 방어.
/// SSOT: $1.entities.md §3a + $3.tooling.md "Sibling Uniqueness — MCP 자동 검사".

let private newStoreWithProject () =
    let store = DsStore()
    store.AddProject("P") |> ignore
    store

let private parseAndApply (store: DsStore) (yaml: string) =
    use jdoc = ModelProtocolYaml.yamlToJson yaml
    let plan = ImportPlanBuilder()
    let diag, refs = ModelProtocol.apply plan store jdoc.RootElement
    diag, refs, plan

// ─── Active System ───────────────────────────────────────────────────────────

[<Fact>]
let ``queueAddActiveSystem 은 같은 이름 active 가 store 에 있으면 fail`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    ToolOperations.queueAddActiveSystem plan1 store "Main" |> ignore
    store.ApplyImportPlan("first", plan1.Build())
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddActiveSystem plan2 store "Main" |> ignore)
    Assert.Contains("이미 존재", ex.Message)
    Assert.Contains("kind=active", ex.Message)

[<Fact>]
let ``queueAddActiveSystem 은 다른 이름 active 가 store 에 있으면 PLC 1 controller 규약으로 fail`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    ToolOperations.queueAddActiveSystem plan1 store "Main" |> ignore
    store.ApplyImportPlan("first active", plan1.Build())
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddActiveSystem plan2 store "Secondary" |> ignore)
    Assert.Contains("PLC 1 controller", ex.Message)

[<Fact>]
let ``dispatchActiveSystem 은 같은 이름 active 가 store 에 있으면 silent reuse`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    let firstId = ToolOperations.queueAddActiveSystem plan1 store "Main"
    store.ApplyImportPlan("first", plan1.Build())
    // 같은 이름 active 재발행 — dispatcher 가 silent reuse, queueAddActiveSystem 미호출.
    let yaml = """
protocol: promaker/v0
systems:
  - system: Main
    kind: active
    flow F:
      works:
        W1: { calls: [] }
"""
    let diag, _, plan2 = parseAndApply store yaml
    Assert.False(diag.HasErrors, diag.Format())
    // (a) plan 에 새 AddSystem 없음 — silent reuse 됨
    let addSystemCount =
        plan2.Operations
        |> Seq.filter (function AddSystem _ -> true | _ -> false)
        |> Seq.length
    Assert.Equal(0, addSystemCount)
    // (b) LinkSystemToProject op 도 plan 에 없음 — store 의 기존 link 유지
    let linkCount =
        plan2.Operations
        |> Seq.filter (function LinkSystemToProject _ -> true | _ -> false)
        |> Seq.length
    Assert.Equal(0, linkCount)
    // (c) 새 Flow 가 기존 active.Id 의 child 로 attach — silent reuse 의 core invariant
    let addFlows =
        plan2.Operations
        |> Seq.choose (function AddFlow f -> Some f | _ -> None)
        |> Seq.toList
    Assert.Equal(1, addFlows.Length)
    Assert.Equal(firstId, addFlows.[0].ParentId)

[<Fact>]
let ``dispatchActiveSystem wire 경로로 다른 이름 active 추가 시 PLC 1 controller fail`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    ToolOperations.queueAddActiveSystem plan1 store "Main" |> ignore
    store.ApplyImportPlan("first active", plan1.Build())
    // wire-path — dispatchActiveSystem 의 lookup-first 가 None → queueAddActiveSystem 호출 → PLC 1 controller fail.
    let yaml = """
protocol: promaker/v0
systems:
  - system: Secondary
    kind: active
    flow F:
      works:
        W1: { calls: [] }
"""
    let diag, _, _ = parseAndApply store yaml
    Assert.True(diag.HasErrors, "PLC 1 controller fail 진단 누락")
    Assert.Contains("PLC 1 controller", diag.Format())

// ─── Passive System (Project 하 System namespace) ────────────────────────────

[<Fact>]
let ``queueAddPassiveSystem 은 같은 이름 system 이 store 에 있으면 fail (active+passive 통합)`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    ToolOperations.queueAddActiveSystem plan1 store "Main" |> ignore
    store.ApplyImportPlan("active", plan1.Build())
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddPassiveSystem plan2 store "Main" "Unit" |> ignore)
    Assert.Contains("이미 존재", ex.Message)
    // 통일 메시지: active 충돌은 kind=active 로 단언 (M-A — 메시지 namespace 정합성)
    Assert.Contains("kind=active", ex.Message)

// ─── Flow (System 하) — 기존 검사 회귀 ───────────────────────────────────────

[<Fact>]
let ``queueAddFlow 는 같은 system 안 같은 이름 flow 가 있으면 fail`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    let sysId = ToolOperations.queueAddActiveSystem plan1 store "Main"
    ToolOperations.queueAddFlow plan1 store "F" sysId |> ignore
    store.ApplyImportPlan("setup", plan1.Build())
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddFlow plan2 store "F" sysId |> ignore)
    Assert.Contains("이미", ex.Message)

// ─── Work (Flow 하) — 기존 검사 회귀 ─────────────────────────────────────────

[<Fact>]
let ``queueAddWork 는 같은 flow 안 같은 이름 work 가 있으면 fail`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    let sysId = ToolOperations.queueAddActiveSystem plan1 store "Main"
    let flowId = ToolOperations.queueAddFlow plan1 store "F" sysId
    ToolOperations.queueAddWork plan1 store "W" flowId None |> ignore
    store.ApplyImportPlan("setup", plan1.Build())
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddWork plan2 store "W" flowId None |> ignore)
    Assert.Contains("이미", ex.Message)

// ─── Call (Work 하) — 예외: concurrent path 허용 ─────────────────────────────

[<Fact>]
let ``queueAddCallAllowDup 은 같은 work 안 같은 dotted-path call N회 허용 (regression guard)`` () =
    // 사용자 명시 예외 — SSOT yaml-protocol-v0.md §1.7. dispatchWork 의 concurrent path 가 이 함수 호출.
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let sysId = ToolOperations.queueAddActiveSystem plan store "Main"
    let flowId = ToolOperations.queueAddFlow plan store "F" sysId
    let workId = ToolOperations.queueAddWork plan store "W" flowId None
    let passiveId = ToolOperations.queueAddPassiveSystem plan store "Cyl" "Unit"
    let apiId = ToolOperations.queueAddApiDef plan store "ADV" passiveId None None
    // 첫 call
    ToolOperations.queueAddCallAllowDup plan store workId apiId |> ignore
    // 두 번째 동일 call — concurrent 의미, exception 없이 통과
    ToolOperations.queueAddCallAllowDup plan store workId apiId |> ignore
    let callCount =
        plan.Operations
        |> Seq.filter (function AddCall _ -> true | _ -> false)
        |> Seq.length
    Assert.Equal(2, callCount)
