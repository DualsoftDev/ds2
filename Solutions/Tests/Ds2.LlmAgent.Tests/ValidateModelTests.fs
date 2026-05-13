module ValidateModelTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// 1d-6 — Phase 1d-3 의 validate_model 출력 안정성 회귀.
/// `categoryOrder` / `formatScopeLabel` / "(no issues; ...)" 포맷이 후속 변경 시 즉시 실패하도록.

[<Fact>]
let ``빈 store global validate 는 no issues 메시지`` () =
    let store = DsStore()
    let result = ToolOperations.validateModelByGuid store None
    Assert.Equal("(no issues; scope=global)", result)

[<Fact>]
let ``존재하지 않는 GUID scope 는 VALIDATION_ERROR`` () =
    let store = DsStore()
    let bogusId = Guid.NewGuid()
    let result = ToolOperations.validateModelByGuid store (Some bogusId)
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains(bogusId.ToString("D"), result)

[<Fact>]
let ``project 만 있는 store 는 no issues`` () =
    let store = DsStore()
    let _projectId = store.AddProject("Project")
    let result = ToolOperations.validateModelByGuid store None
    Assert.Equal("(no issues; scope=global)", result)

[<Fact>]
let ``placeholder 이름의 system 은 TodoPlaceholder 로 보고`` () =
    let store = DsStore()
    let projectId = store.AddProject("Project")
    let _sysId = store.AddSystem("TODO", projectId, true)
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("TodoPlaceholder:", result)
    Assert.Contains("System \"TODO\"", result)

[<Fact>]
let ``System scope 는 Orphan check skipped footer`` () =
    let store = DsStore()
    let projectId = store.AddProject("Project")
    let sysId = store.AddSystem("Sys", projectId, true)
    let result = ToolOperations.validateModelByGuid store (Some sysId)
    // Phase 6: scope footer 가 path 기반 (GUID 노출 회피). 본 case = ".Project.Sys" path.
    Assert.StartsWith("(no issues; scope=System(path=.Project.Sys", result)
    Assert.Contains("Orphan check skipped", result)

[<Fact>]
let ``orphan system 은 global scope 에서 Orphan 카테고리로 보고`` () =
    // 시뮬: project 와 system 을 만든 후 project 의 ActiveSystemIds 에서 제거 →
    //       store.Systems 에는 남아있지만 어떤 project 에도 attach 안 된 상태 (= orphan)
    let store = DsStore()
    let projectId = store.AddProject("Project")
    let orphanId = store.AddSystem("Detached", projectId, true)
    let attachedId = store.AddSystem("Attached", projectId, true)
    // project 의 active list 에서 orphan 만 제거 → attached 는 유지
    let project = store.Projects.[projectId]
    project.ActiveSystemIds.Remove(orphanId) |> Assert.True
    let result = ToolOperations.validateModelByGuid store None
    // Orphan 카테고리에 detached system 이 보고되고, attached 는 보고되지 않아야 함
    Assert.Contains("Orphan:", result)
    Assert.Contains($"System \"Detached\" (id={orphanId:D}) is not attached to any Project", result)
    Assert.DoesNotContain($"id={attachedId:D}) is not attached", result)

[<Fact>]
let ``flow scope 는 sibling DuplicateName / ApiDef / ArrowBetweenWorks skip footer`` () =
    let store = DsStore()
    let projectId = store.AddProject("Project")
    let sysId = store.AddSystem("Sys", projectId, true)
    let flowId = store.AddFlow("Flow", sysId)
    let result = ToolOperations.validateModelByGuid store (Some flowId)
    Assert.Contains("Orphan / sibling-flow DuplicateName / ApiDef / ArrowBetweenWorks checks skipped", result)

// ─── EmptyFlow / EmptyWork / TodoPlaceholder 카테고리 ──────────────────────

[<Fact>]
let ``Work 가 없는 Flow 는 EmptyFlow 카테고리로 보고`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let _flowId = store.AddFlow("F", sysId)
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("EmptyFlow:", result)
    Assert.Contains("Flow \"F\"", result)
    Assert.Contains("has no Works", result)

[<Fact>]
let ``Call 이 없는 Work 는 EmptyWork 카테고리로 보고`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let flowId = store.AddFlow("F", sysId)
    let _workId = store.AddWork("W", flowId)
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("EmptyWork:", result)
    Assert.Contains("has no Calls", result)
    // Flow 에 Work 가 있으면 EmptyFlow 는 아님
    Assert.DoesNotContain("EmptyFlow:", result)

[<Fact>]
let ``Flow 이름이 placeholder 면 TodoPlaceholder 보고 (TBD / FIXME / ? / TODO 등)`` () =
    for name in [ "TODO"; "TBD"; "FIXME"; "XXX"; "?"; "??"; "???" ] do
        let store = DsStore()
        let projectId = store.AddProject("P")
        let sysId = store.AddSystem("S", projectId, true)
        let _flowId = store.AddFlow(name, sysId)
        let result = ToolOperations.validateModelByGuid store None
        Assert.Contains("TodoPlaceholder:", result)
        Assert.Contains($"Flow \"{name}\"", result)

[<Fact>]
let ``Work 이름이 placeholder 면 TodoPlaceholder 보고`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let flowId = store.AddFlow("F", sysId)
    let _workId = store.AddWork("TODO", flowId)
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("TodoPlaceholder:", result)
    Assert.Contains("Work \"F.TODO\"", result)  // Work 표시명 = "{flow.Name}.{localName}"

[<Fact>]
let ``ApiDef 이름이 placeholder 면 TodoPlaceholder 보고`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddApiDef plan store "TODO" sysId None None |> ignore
    store.ApplyImportPlan("test", plan.Build())
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("TodoPlaceholder:", result)
    Assert.Contains("ApiDef \"TODO\"", result)

// ─── DuplicateName (직접 store 변조 — InternalsVisibleTo 경유) ─────────────

[<Fact>]
let ``같은 System 에 Flow 이름 중복 시 DuplicateName 카테고리`` () =
    // public AddFlow 는 unique check 가 차단 → ImportPlan AddFlow op 직접 누적 (Flow ctor internal 접근).
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let f1 = Flow("DupFlow", sysId)
    let f2 = Flow("DupFlow", sysId)
    let plan = ImportPlanBuilder()
    plan.Add(AddFlow f1)
    plan.Add(AddFlow f2)
    store.ApplyImportPlan("dup test", plan.Build())
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("DuplicateName:", result)
    Assert.Contains("Flow \"DupFlow\" duplicated 2", result)

[<Fact>]
let ``같은 Flow 에 Work LocalName 중복 시 DuplicateName 카테고리`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let flowId = store.AddFlow("F", sysId)
    let w1 = Work("F", "DupW", flowId)
    let w2 = Work("F", "DupW", flowId)
    let plan = ImportPlanBuilder()
    plan.Add(AddWork w1)
    plan.Add(AddWork w2)
    store.ApplyImportPlan("dup work test", plan.Build())
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("DuplicateName:", result)
    Assert.Contains("Work \"DupW\" duplicated 2", result)

[<Fact>]
let ``같은 System 에 ApiDef 이름 중복 시 DuplicateName 카테고리`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let d1 = ApiDef("DupApi", sysId)
    let d2 = ApiDef("DupApi", sysId)
    let plan = ImportPlanBuilder()
    plan.Add(AddApiDef d1)
    plan.Add(AddApiDef d2)
    store.ApplyImportPlan("dup api test", plan.Build())
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("DuplicateName:", result)
    Assert.Contains("ApiDef \"DupApi\" duplicated 2", result)

// ─── DanglingArrow ────────────────────────────────────────────────────────

[<Fact>]
let ``Arrow 의 source Work 가 store 에서 사라진 경우 DanglingArrow 보고`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let sysId = store.AddSystem("S", projectId, true)
    let flowId = store.AddFlow("F", sysId)
    let srcW = store.AddWork("Src", flowId)
    let tgtW = store.AddWork("Tgt", flowId)
    let plan = ImportPlanBuilder()
    ToolOperations.queueAddArrow plan store srcW tgtW Ds2.Core.ArrowType.Start |> ignore
    store.ApplyImportPlan("arrow", plan.Build())
    // src work 만 store 에서 직접 제거 (cascade 없이) → arrow 만 dangling
    store.Works.Remove(srcW) |> ignore
    let result = ToolOperations.validateModelByGuid store None
    Assert.Contains("DanglingArrow:", result)
    Assert.Contains("missing source Work", result)

// ─── 6 카테고리 출력 순서 (categoryOrder invariant) ────────────────────────

[<Fact>]
let ``여러 카테고리가 동시에 발생하면 categoryOrder 고정 순서로 출력`` () =
    // Orphan / DanglingArrow / EmptyFlow / EmptyWork / DuplicateName / TodoPlaceholder 순서 검증.
    // 한 fixture 에 다섯 카테고리 동시 발생.
    let store = DsStore()
    let projectId = store.AddProject("P")
    // 1. Attached system (with TODO flow + dangling arrow + duplicate work)
    let sysAttachedId = store.AddSystem("AttachedSys", projectId, true)
    let _flowGoodId = store.AddFlow("FGood", sysAttachedId)  // EmptyFlow (의도적 미사용)
    let flowDupId  = store.AddFlow("FDup", sysAttachedId)
    // Work 중복 + EmptyWork (Calls 없음)
    let w1 = Work("FDup", "WDup", flowDupId)
    let w2 = Work("FDup", "WDup", flowDupId)
    let plan = ImportPlanBuilder()
    plan.Add(AddWork w1)
    plan.Add(AddWork w2)
    // TODO API def
    plan.Add(AddApiDef (ApiDef("TODO", sysAttachedId)))
    store.ApplyImportPlan("seed", plan.Build())
    // 2. Orphan system — project 의 active list 에서 제거
    let orphanSysId = store.AddSystem("OrphanSys", projectId, true)
    let project = store.Projects.[projectId]
    project.ActiveSystemIds.Remove(orphanSysId) |> ignore
    // 3. DanglingArrow on a separate flow (flowGoodId 는 EmptyFlow 보존을 위해 건드리지 않음)
    let flowArrowId = store.AddFlow("FArrow", sysAttachedId)
    let srcId = store.AddWork("Src", flowArrowId)
    let tgtId = store.AddWork("Tgt", flowArrowId)
    let plan2 = ImportPlanBuilder()
    ToolOperations.queueAddArrow plan2 store srcId tgtId Ds2.Core.ArrowType.Start |> ignore
    store.ApplyImportPlan("arrow", plan2.Build())
    store.Works.Remove(srcId) |> ignore  // dangling

    let result = ToolOperations.validateModelByGuid store None

    // 모두 보고됨
    Assert.Contains("Orphan:", result)
    Assert.Contains("DanglingArrow:", result)
    Assert.Contains("EmptyFlow:", result)
    Assert.Contains("EmptyWork:", result)
    Assert.Contains("DuplicateName:", result)
    Assert.Contains("TodoPlaceholder:", result)

    // 카테고리 출력 순서 = Orphan → DanglingArrow → EmptyFlow → EmptyWork → DuplicateName → TodoPlaceholder
    let categoryOrder =
        [ "Orphan:"; "DanglingArrow:"; "EmptyFlow:"; "EmptyWork:"; "DuplicateName:"; "TodoPlaceholder:" ]
    let positions =
        categoryOrder |> List.map (fun cat -> result.IndexOf(cat))
    // 모두 발견됨 (-1 없음)
    for (cat, pos) in List.zip categoryOrder positions do
        Assert.True(pos >= 0, $"카테고리 미발견: {cat}")
    // 단조 증가 (정의된 순서대로 출력)
    for i in 0 .. positions.Length - 2 do
        Assert.True(
            positions.[i] < positions.[i + 1],
            $"순서 위반: {categoryOrder.[i]}(pos={positions.[i]}) >= {categoryOrder.[i + 1]}(pos={positions.[i + 1]})")
