using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

public class DsProjectService
{
    private readonly DsStore _store;
    private readonly ILogger<DsProjectService> _logger;
    private readonly string? _aasxFilePath;

    public bool IsLoaded { get; private set; }

    public DsStore GetStore() => _store;

    public DsProjectService(IConfiguration configuration, ILogger<DsProjectService> logger)
    {
        _logger = logger;
        _store = new DsStore();
        var configPath = configuration["DsPilot:AasxFilePath"];
        _logger.LogInformation("[DsProject] Raw config AasxFilePath = '{ConfigPath}'", configPath ?? "(null)");
        _aasxFilePath = string.IsNullOrEmpty(configPath) ? null : Path.GetFullPath(configPath);
        _logger.LogInformation("[DsProject] Resolved AasxFilePath = '{Path}', Exists = {Exists}",
            _aasxFilePath ?? "(null)", _aasxFilePath != null && File.Exists(_aasxFilePath));

        if (!string.IsNullOrEmpty(_aasxFilePath) && File.Exists(_aasxFilePath))
        {
            LoadProject(_aasxFilePath);
        }
        else if (!string.IsNullOrEmpty(_aasxFilePath))
        {
            _logger.LogWarning("AASX file not found: {Path}", _aasxFilePath);
        }
    }

    public void LoadProject(string path)
    {
        try
        {
            var result = Ds2.Aasx.AasxImporter.importIntoStore(_store, path);
            IsLoaded = result;
            if (result)
                _logger.LogInformation("Project loaded from: {Path}", path);
            else
                _logger.LogWarning("Failed to import AASX (구 포맷일 수 있음 — ds2 에디터에서 다시 Export 필요): {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading AASX file: {Path}", path);
            IsLoaded = false;
        }
    }

    public Project? GetProject()
    {
        var projects = Queries.allProjects(_store);
        return ListModule.IsEmpty(projects) ? null : ListModule.Head(projects);
    }

    public List<DsSystem> GetActiveSystems()
    {
        var project = GetProject();
        if (project == null) return [];
        return [.. Queries.activeSystemsOf(project.Id, _store)];
    }

    public List<DsSystem> GetPassiveSystems()
    {
        var project = GetProject();
        if (project == null) return [];
        return [.. Queries.passiveSystemsOf(project.Id, _store)];
    }

    public List<Flow> GetFlows(Guid systemId)
    {
        return [.. Queries.flowsOf(systemId, _store)];
    }

    public List<Flow> GetAllFlows()
    {
        return [.. Queries.allFlows(_store)];
    }

    public List<Work> GetWorks(Guid flowId)
    {
        return [.. Queries.worksOf(flowId, _store)];
    }

    public int GetTotalWorkCount()
    {
        return GetAllFlows().Sum(f => GetWorks(f.Id).Count);
    }

    public List<Call> GetCalls(Guid workId)
    {
        return [.. Queries.callsOf(workId, _store)];
    }

    public List<Call> GetAllCalls()
    {
        var allCalls = new List<Call>();
        foreach (var flow in GetAllFlows())
        {
            var works = GetWorks(flow.Id);
            foreach (var work in works)
            {
                allCalls.AddRange(GetCalls(work.Id));
            }
        }
        return allCalls;
    }

    /// <summary>
    /// Flow의 첫 번째 Call을 가져옵니다 (Head Call)
    /// </summary>
    public Call? GetHeadCall(Guid flowId)
    {
        var works = GetWorks(flowId);
        if (works.Count == 0) return null;

        var firstWork = works[0];
        var calls = GetCalls(firstWork.Id);
        return calls.Count > 0 ? calls[0] : null;
    }

    /// <summary>
    /// Flow 이름으로 Flow 객체 찾기
    /// </summary>
    public Flow? GetFlowByName(string flowName)
    {
        return GetAllFlows().FirstOrDefault(f => f.Name == flowName);
    }

    /// <summary>
    /// Call ID로 해당 Call이 속한 Flow 찾기
    /// </summary>
    public Flow? GetFlowByCallId(Guid callId)
    {
        var call = Queries.getCall(callId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Call>.get_IsSome(call))
            return null;

        var work = Queries.getWork(call.Value.ParentId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(work))
            return null;

        var flow = Queries.getFlow(work.Value.ParentId, _store);
        return Microsoft.FSharp.Core.FSharpOption<Flow>.get_IsSome(flow) ? flow.Value : null;
    }

    public List<(double X, double Y)> ComputeArrowPath(Xywh source, Xywh target)
    {
        var visual = Ds2.Editor.ArrowPathCalculator.computePath(source, target);
        return [.. visual.Points.Select(p => (p.Item1, p.Item2))];
    }
}
