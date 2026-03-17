using Ds2.Core;
using Ds2.UI.Core;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

public class DsProjectService
{
    private readonly DsStore _store;
    private readonly ILogger<DsProjectService> _logger;
    private readonly string? _aasxFilePath;

    public bool IsLoaded { get; private set; }

    public DsProjectService(IConfiguration configuration, ILogger<DsProjectService> logger)
    {
        _logger = logger;
        _store = new DsStore();
        var configPath = configuration["DsPilot:AasxFilePath"];
        _aasxFilePath = string.IsNullOrEmpty(configPath) ? null : Path.GetFullPath(configPath);

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
                _logger.LogWarning("Failed to import AASX: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading AASX file: {Path}", path);
            IsLoaded = false;
        }
    }

    public Project? GetProject()
    {
        var projects = DsQuery.allProjects(_store);
        return ListModule.IsEmpty(projects) ? null : ListModule.Head(projects);
    }

    public List<DsSystem> GetActiveSystems()
    {
        var project = GetProject();
        if (project == null) return [];
        return [.. DsQuery.activeSystemsOf(project.Id, _store)];
    }

    public List<DsSystem> GetPassiveSystems()
    {
        var project = GetProject();
        if (project == null) return [];
        return [.. DsQuery.passiveSystemsOf(project.Id, _store)];
    }

    public List<Flow> GetFlows(Guid systemId)
    {
        return [.. DsQuery.flowsOf(systemId, _store)];
    }

    public List<Flow> GetAllFlows()
    {
        return [.. DsQuery.allFlows(_store)];
    }

    public List<Work> GetWorks(Guid flowId)
    {
        return [.. DsQuery.worksOf(flowId, _store)];
    }

    public int GetTotalWorkCount()
    {
        return GetAllFlows().Sum(f => GetWorks(f.Id).Count);
    }

    public List<Call> GetCalls(Guid workId)
    {
        return [.. DsQuery.callsOf(workId, _store)];
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

    public DsStore GetStore() => _store;

    public List<(double X, double Y)> ComputeArrowPath(Xywh source, Xywh target)
    {
        var visual = Ds2.UI.Core.ArrowPathCalculator.computePath(source, target);
        return [.. visual.Points.Select(p => (p.Item1, p.Item2))];
    }
}
