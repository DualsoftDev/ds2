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
let ``System scope 는 Orphan check skipped footer`` () =
    let store = DsStore()
    let projectId = store.AddProject("Project")
    let sysId = store.AddSystem("Sys", projectId, true)
    let result = ToolOperations.validateModelByGuid store (Some sysId)
    Assert.StartsWith("(no issues; scope=System(id=", result)
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
