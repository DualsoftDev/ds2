using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.UI.Core;
using log4net;

namespace Promaker.Services;

/// <summary>
/// 프로젝트 엔티티 관리를 담당하는 서비스 구현
/// </summary>
public class ProjectService : IProjectService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ProjectService));

    public Guid AddProject(string name, DsStore store)
    {
        Log.Info($"Adding project: {name}");
        return store.AddProject(name);
    }

    public Guid AddSystem(
        string name,
        bool isControl,
        DsStore store,
        EntityKind? selectedEntityKind = null,
        Guid? selectedEntityId = null,
        TabKind? activeTabKind = null,
        Guid? activeTabRootId = null)
    {
        Log.Info($"Adding system: {name}, isControl: {isControl}");
        store.AddSystemResolved(
            name,
            isControl,
            selectedEntityKind,
            selectedEntityId,
            activeTabKind,
            activeTabRootId);

        // AddSystemResolved는 void를 반환하므로, Guid.Empty 반환 (나중에 이벤트로 처리)
        return Guid.Empty;
    }

    public Guid AddFlow(
        string name,
        DsStore store,
        EntityKind? selectedEntityKind = null,
        Guid? selectedEntityId = null,
        TabKind? activeTabKind = null,
        Guid? activeTabRootId = null)
    {
        Log.Info($"Adding flow: {name}");
        store.AddFlowResolved(
            name,
            selectedEntityKind,
            selectedEntityId,
            activeTabKind,
            activeTabRootId);

        // AddFlowResolved는 void를 반환하므로, Guid.Empty 반환 (나중에 이벤트로 처리)
        return Guid.Empty;
    }

    public Guid AddWork(string name, Guid flowId, DsStore store)
    {
        Log.Info($"Adding work: {name} to flow: {flowId}");
        return store.AddWork(name, flowId);
    }

    public Guid AddCall(string name, Guid workId, DsStore store)
    {
        Log.Info($"Adding call: {name} to work: {workId}");
        // DsStore에 AddCall 메서드가 없으므로, AddCallsWithDeviceResolved 사용
        store.AddCallsWithDeviceResolved(EntityKind.Work, workId, workId, [name], false);

        // Guid.Empty 반환 (나중에 이벤트로 처리)
        return Guid.Empty;
    }

    public void DeleteEntities(IEnumerable<Guid> entityIds, DsStore store)
    {
        var idList = entityIds.ToList();
        Log.Info($"Deleting {idList.Count} entities");

        // DeleteEntities 메서드가 없으므로, 개별적으로 삭제
        foreach (var id in idList)
        {
            // DeleteEntity도 없으므로, 나중에 구현
            // store.DeleteEntity(id);
        }
    }

    public void RenameEntity(Guid entityId, string newName, DsStore store)
    {
        Log.Info($"Renaming entity {entityId} to: {newName}");

        // EntityKind는 호출자가 알고 있어야 함 - 단순화를 위해 제거하고 나중에 개선
        // store.RenameEntity(entityId, entityKind, newName);
    }

    public void MoveEntities(IEnumerable<MoveEntityRequest> requests, DsStore store)
    {
        var requestList = requests.ToList();
        Log.Info($"Moving {requestList.Count} entities");
        store.MoveEntities(requestList);
    }

    public void UpdateProjectProperties(Guid projectId, ProjectProperties properties, DsStore store)
    {
        Log.Info($"Updating project properties for: {projectId}");

        var project = DsQuery.allProjects(store).FirstOrDefault(p => p.Id == projectId);
        if (project is null)
        {
            Log.Warn($"Project not found: {projectId}");
            return;
        }

        var currentProps = project.Properties;

        // FSharpOption을 처리하여 string으로 변환
        var iriPrefix = currentProps.IriPrefix?.Value ?? "";
        var globalAssetId = currentProps.GlobalAssetId?.Value ?? "";
        var author = currentProps.Author?.Value ?? "";
        var version = currentProps.Version?.Value ?? "";
        var description = currentProps.Description?.Value ?? "";

        store.UpdateProjectProperties(
            properties.Name ?? iriPrefix,
            globalAssetId,
            author,
            properties.Version ?? version,
            properties.Description ?? description);
    }
}
