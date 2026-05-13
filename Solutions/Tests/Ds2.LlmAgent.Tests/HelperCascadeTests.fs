module HelperCascadeTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// extend-mcp §5.6 신규 1 — Tier 1 helper 4종 cascade 결과 검증.
///
/// helper 의 발행 op 갯수 = `cascadeOpCount n opposing` SSOT 와 일치해야 함.
/// 본 테스트는 plan.Operations 의 ImportPlanOperation 분포 + 핵심 attribute (SystemType / Arrow type
/// = ResetReset / Work.Duration) 만 검증. ApiCall 자동 cascade 는 helper 책임 외이므로
/// `ImportPlanApplyApiCallTests` 에서 별도 확인.

let private newStoreWithProject () =
    let store = DsStore()
    store.AddProject("M1") |> ignore
    store

let private countByKind (plan: ImportPlanBuilder) =
    let mutable sys = 0
    let mutable link = 0
    let mutable flow = 0
    let mutable work = 0
    let mutable apiDef = 0
    let mutable arrowWork = 0
    for op in plan.Operations do
        match op with
        | AddSystem _ -> sys <- sys + 1
        | LinkSystemToProject _ -> link <- link + 1
        | AddFlow _ -> flow <- flow + 1
        | AddWork _ -> work <- work + 1
        | AddApiDef _ -> apiDef <- apiDef + 1
        | AddArrowWork _ -> arrowWork <- arrowWork + 1
        | _ -> ()
    sys, link, flow, work, apiDef, arrowWork

// ─── add_cylinder ────────────────────────────────────────────────────────────

[<Fact>]
let ``cylinder default (apiNames=[]) = N=2 chain → 8 op (System+Link+Flow+Work×2+ApiDef×2+Arrow)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let sysId, apiDefIds = ToolOperations.queueAddCylinder plan store "Cyl1" [] None
    Assert.Equal(8, plan.Count)
    let s, l, f, w, a, ar = countByKind plan
    Assert.Equal(1, s)
    Assert.Equal(1, l)
    Assert.Equal(1, f)
    Assert.Equal(2, w)
    Assert.Equal(2, a)
    Assert.Equal(1, ar)
    // default apiNames = ADV/RET (순서 보존 contract)
    Assert.Equal(2, apiDefIds.Length)
    Assert.Equal("ADV", fst apiDefIds.[0])
    Assert.Equal("RET", fst apiDefIds.[1])
    Assert.NotEqual(Guid.Empty, sysId)

[<Fact>]
let ``cylinder cascade — SystemType=Unit + Arrow=ResetReset(4) + Work.Duration=500ms`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddCylinder plan store "Cyl1" [] None |> ignore
    // SystemType
    let passiveSys =
        plan.Operations
        |> Seq.pick (function AddSystem s -> Some s | _ -> None)
    Assert.Equal(Some "Unit", passiveSys.SystemType)
    // Arrow ResetReset (enum 4)
    let arrow =
        plan.Operations
        |> Seq.pick (function AddArrowWork a -> Some a | _ -> None)
    Assert.Equal(int ArrowType.ResetReset, int arrow.ArrowType)
    // Work duration 500ms
    let works =
        plan.Operations
        |> Seq.choose (function AddWork w -> Some w | _ -> None)
        |> List.ofSeq
    Assert.Equal(2, works.Length)
    for w in works do
        Assert.Equal(Some (TimeSpan.FromMilliseconds 500.), w.Duration)

[<Fact>]
let ``cylinder apiNames length != 2 = invalidOp`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddCylinder plan store "C" ["A"; "B"; "C"] None |> ignore)
    Assert.Contains("정확히 2개", ex.Message)

// ─── add_clamp ───────────────────────────────────────────────────────────────

[<Fact>]
let ``clamp default = 8 op + apiNames CLP/UNCLP (rev 11 SSOT 정합)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let _, apiDefIds = ToolOperations.queueAddClamp plan store "Clp1" [] None
    Assert.Equal(8, plan.Count)
    Assert.Equal("CLP", fst apiDefIds.[0])
    Assert.Equal("UNCLP", fst apiDefIds.[1])

// ─── add_robot ───────────────────────────────────────────────────────────────

[<Fact>]
let ``robot opposing=none N=4 = 3+2N = 11 op (ResetReset 0)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let _, apiDefIds =
        ToolOperations.queueAddRobot plan store "RB1"
            ["HOME"; "W1"; "W2"; "W3"] "none" None
    Assert.Equal(11, plan.Count)
    let _, _, _, w, a, ar = countByKind plan
    Assert.Equal(4, w)
    Assert.Equal(4, a)
    Assert.Equal(0, ar)
    Assert.Equal(4, apiDefIds.Length)

[<Fact>]
let ``robot opposing=chain N=4 = 3+2N+(N-1) = 14 op`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddRobot plan store "RB1"
        ["HOME"; "W1"; "W2"; "W3"] "chain" None |> ignore
    Assert.Equal(14, plan.Count)
    let _, _, _, _, _, ar = countByKind plan
    Assert.Equal(3, ar)

[<Fact>]
let ``robot opposing=all-pairs N=4 = 3+2N+C(N,2) = 17 op`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddRobot plan store "RB1"
        ["HOME"; "W1"; "W2"; "W3"] "all-pairs" None |> ignore
    Assert.Equal(17, plan.Count)
    let _, _, _, _, _, ar = countByKind plan
    Assert.Equal(6, ar)

[<Fact>]
let ``robot SystemType=Robot (deviceType 자동 fix)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddRobot plan store "RB1" ["HOME"] "none" None |> ignore
    let passiveSys =
        plan.Operations
        |> Seq.pick (function AddSystem s -> Some s | _ -> None)
    Assert.Equal(Some "Robot", passiveSys.SystemType)

[<Fact>]
let ``robot 빈 apiNames = invalidOp`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddRobot plan store "RB1" [] "none" None |> ignore)
    Assert.Contains("apiNames", ex.Message)

[<Fact>]
let ``robot opposing 잘못된 값 = invalidOp + 허용 목록 안내`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddRobot plan store "RB1" ["HOME"] "weird" None |> ignore)
    Assert.Contains("허용:", ex.Message)
    Assert.Contains("none|chain|all-pairs", ex.Message)

// ─── add_device generic ──────────────────────────────────────────────────────

[<Fact>]
let ``device deviceType=Conveyor + apiNames=[MOVE] none = 5 op (3+2*1)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let _, apiDefIds = ToolOperations.queueAddDevice plan store "Conv1" "Conveyor" ["MOVE"] "none" None
    Assert.Equal(5, plan.Count)
    let s, l, f, w, a, ar = countByKind plan
    Assert.Equal(1, s)
    Assert.Equal(1, l)
    Assert.Equal(1, f)
    Assert.Equal(1, w)
    Assert.Equal(1, a)
    Assert.Equal(0, ar)
    Assert.Equal(1, apiDefIds.Length)
    Assert.Equal("MOVE", fst apiDefIds.[0])
    let passiveSys =
        plan.Operations
        |> Seq.pick (function AddSystem s -> Some s | _ -> None)
    Assert.Equal(Some "Conveyor", passiveSys.SystemType)

// ─── apiNames sanitize (rev 13 Issue 2) ──────────────────────────────────────

[<Fact>]
let ``apiNames 빈 항목 = invalidOp`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddDevice plan store "X" "Unit" ["A"; ""; "B"] "none" None |> ignore)
    Assert.Contains("빈 항목", ex.Message)

[<Fact>]
let ``apiNames 중복 항목 = invalidOp (silent miscompile 차단)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddDevice plan store "X" "Robot" ["HOME"; "HOME"] "none" None |> ignore)
    Assert.Contains("중복", ex.Message)

// ─── D9 동명 PassiveSystem 충돌 차단 (rev 12 신설) ──────────────────────────

[<Fact>]
let ``동명 PassiveSystem 이 store 에 존재하면 helper 진입 시 invalidOp`` () =
    let store = newStoreWithProject ()
    let plan1 = ImportPlanBuilder()
    ToolOperations.queueAddCylinder plan1 store "Cyl1" [] None |> ignore
    store.ApplyImportPlan("first cyl", plan1.Build())
    // 두 번째 helper 호출 — 같은 PassiveSystem 이름 → D9 차단
    let plan2 = ImportPlanBuilder()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddCylinder plan2 store "Cyl1" [] None |> ignore)
    Assert.Contains("이미 존재", ex.Message)
    Assert.Contains("find_by_name", ex.Message)
