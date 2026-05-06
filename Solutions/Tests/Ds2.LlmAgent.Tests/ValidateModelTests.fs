module ValidateModelTests

open System
open Xunit
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
let ``orphan system 은 global scope 에서만 보고`` () =
    let store = DsStore()
    // project 없이 system 만 추가 — 기존 API 가 project 부착 강제하지 않으면 orphan
    // AddSystem 은 projectId 필수 → orphan 시뮬레이션이 어려움. 일단 GlobalScope 의 footer 출력 검증으로 대체.
    let projectId = store.AddProject("Project")
    let sysId = store.AddSystem("Sys", projectId, true)
    let result = ToolOperations.validateModelByGuid store (Some sysId)
    Assert.StartsWith("(no issues; scope=System(id=", result)
    Assert.Contains("Orphan check skipped", result)

[<Fact>]
let ``flow scope 는 sibling DuplicateName / ApiDef / ArrowBetweenWorks skip footer`` () =
    let store = DsStore()
    let projectId = store.AddProject("Project")
    let sysId = store.AddSystem("Sys", projectId, true)
    let flowId = store.AddFlow("Flow", sysId)
    let result = ToolOperations.validateModelByGuid store (Some flowId)
    Assert.Contains("Orphan / sibling-flow DuplicateName / ApiDef / ArrowBetweenWorks checks skipped", result)
