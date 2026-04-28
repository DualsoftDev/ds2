using DSPilot.Models;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using CallDirection = Ds2.Core.CallDirection;

namespace DSPilot.Services;

/// <summary>
/// PLC 태그와 Call 매핑 서비스
///
/// [InTag / OutTag 방향 기준: PLC 제어기 관점]
///   - OutTag: PLC가 장비로 출력(DO)하는 신호 (명령)  → Rising 시 Going (실행 시작)
///   - InTag:  장비에서 PLC로 입력(DI)되는 신호 (응답) → Rising 시 Finish (실행 완료)
/// </summary>
public class PlcToCallMapperService
{
    private readonly DsProjectService _projectService;
    private readonly ILogger<PlcToCallMapperService> _logger;
    private readonly Dictionary<string, Models.CallMappingInfo> _tagMappings = new();
    private readonly Dictionary<Guid, CallDirection> _callDirections = new();
    private bool _isInitialized;

    public PlcToCallMapperService(
        DsProjectService projectService,
        ILogger<PlcToCallMapperService> logger,
        IConfiguration configuration)
    {
        _projectService = projectService;
        _logger = logger;
    }

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Build tag mappings from DsStore (AASX data)
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized)
        {
            _logger.LogDebug("PlcToCallMapper already initialized");
            return;
        }

        _tagMappings.Clear();
        _callDirections.Clear();

        var store = _projectService.GetStore();
        if (store == null)
        {
            _logger.LogWarning("DsStore is null, cannot initialize PlcToCallMapper");
            return;
        }

        var allFlows = Queries.allFlows(store).ToList();
        _logger.LogInformation("Building tag mappings from {FlowCount} flows", allFlows.Count);

        int mappingCount = 0;
        int callCount = 0;

        foreach (var flow in allFlows)
        {
            var works = Queries.worksOf(flow.Id, store).ToList();

            foreach (var work in works)
            {
                var calls = Queries.callsOf(work.Id, store).ToList();

                foreach (var call in calls)
                {
                    if (call.ApiCalls.Count == 0) continue;

                    var apiCall = call.ApiCalls[0];
                    bool hasInTag = false;
                    bool hasOutTag = false;

                    // Check tags first
                    if (apiCall.OutTag != null)
                    {
                        hasOutTag = true;
                    }
                    if (apiCall.InTag != null)
                    {
                        hasInTag = true;
                    }

                    // Determine Direction once
                    var direction = DetermineDirection(hasInTag, hasOutTag);
                    _callDirections[call.Id] = direction;

                    // OutTag mapping
                    if (apiCall.OutTag != null)
                    {
                        var outTag = apiCall.OutTag.Value;
                        var entry = new Models.CallMappingInfo
                        {
                            Call = call,
                            ApiCall = apiCall,
                            IsInTag = false,
                            FlowName = flow.Name,
                            WorkName = work.Name,
                            Direction = direction
                        };

                        _tagMappings[outTag.Address] = entry;
                        mappingCount++;
                    }

                    // InTag mapping
                    if (apiCall.InTag != null)
                    {
                        var inTag = apiCall.InTag.Value;
                        var entry = new Models.CallMappingInfo
                        {
                            Call = call,
                            ApiCall = apiCall,
                            IsInTag = true,
                            FlowName = flow.Name,
                            WorkName = work.Name,
                            Direction = direction
                        };

                        _tagMappings[inTag.Address] = entry;
                        mappingCount++;
                    }

                    callCount++;
                }
            }
        }

        _isInitialized = true;
        _logger.LogInformation("PlcToCallMapper initialized: {MappingCount} tag mappings for {CallCount} calls",
            mappingCount, callCount);
    }

    /// <summary>
    /// Determine Call Direction based on tag configuration
    /// </summary>
    private CallDirection DetermineDirection(bool hasInTag, bool hasOutTag)
    {
        return (hasInTag, hasOutTag) switch
        {
            (true, true) => CallDirection.InOut,
            (true, false) => CallDirection.InOnly,
            (false, true) => CallDirection.OutOnly,
            _ => CallDirection.None
        };
    }

    /// <summary>
    /// Find Call mapping by tag address
    /// </summary>
    public Models.CallMappingInfo? FindCallByTag(string tagName, string tagAddress)
    {
        if (!_isInitialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized");
            return null;
        }

        return _tagMappings.GetValueOrDefault(tagAddress);
    }

    public Models.CallMappingInfo? FindCallByTagName(string tagName)
    {
        return FindCallByTag(tagName, tagName);
    }

    /// <summary>
    /// Get Call Direction by CallId
    /// </summary>
    public CallDirection GetDirection(Guid callId)
    {
        return _callDirections.GetValueOrDefault(callId, CallDirection.None);
    }

    /// <summary>
    /// Get all tag addresses
    /// </summary>
    public IEnumerable<string> GetAllTagAddresses()
    {
        return _tagMappings.Keys;
    }

    /// <summary>
    /// Get all mappings
    /// </summary>
    public IEnumerable<Models.CallMappingInfo> GetAllMappings()
    {
        return _tagMappings.Values;
    }

    /// <summary>
    /// Check if tag is InTag
    /// </summary>
    public bool IsInTag(string tagAddress)
    {
        if (_tagMappings.TryGetValue(tagAddress, out var entry))
        {
            return entry.IsInTag;
        }
        return false;
    }

    /// <summary>
    /// Check if tag is OutTag
    /// </summary>
    public bool IsOutTag(string tagAddress)
    {
        if (_tagMappings.TryGetValue(tagAddress, out var entry))
        {
            return !entry.IsInTag;
        }
        return false;
    }

    public (string? InTag, string? OutTag)? GetCallTagsByCallId(Guid callId)
    {
        string? inTag = null;
        string? outTag = null;

        foreach (var kvp in _tagMappings)
        {
            if (kvp.Value.Call.Id == callId)
            {
                if (kvp.Value.IsInTag)
                    inTag = kvp.Key;
                else
                    outTag = kvp.Key;
            }
        }

        if (inTag != null || outTag != null)
            return (inTag, outTag);

        return null;
    }

    /// <summary>
    /// 모든 Call의 태그 쌍 정보를 반환 (Heatmap 필터용)
    /// </summary>
    public List<(Guid CallId, string CallName, string FlowName, string WorkName, string? InTag, string? OutTag)> GetAllCallTagPairs()
    {
        var callMap = new Dictionary<Guid, (string CallName, string FlowName, string WorkName, string? InTag, string? OutTag)>();

        foreach (var kvp in _tagMappings)
        {
            var mapping = kvp.Value;
            var callId = mapping.Call.Id;

            if (!callMap.TryGetValue(callId, out var existing))
            {
                existing = (mapping.Call.Name, mapping.FlowName, mapping.WorkName, null, null);
            }

            if (mapping.IsInTag)
                existing.InTag = kvp.Key;
            else
                existing.OutTag = kvp.Key;

            callMap[callId] = existing;
        }

        return callMap.Select(kvp =>
            (kvp.Key, kvp.Value.CallName, kvp.Value.FlowName, kvp.Value.WorkName, kvp.Value.InTag, kvp.Value.OutTag))
            .ToList();
    }

    public void ValidateWithPlcTags(HashSet<string> plcTagKeys)
    {
        var unmappedTags = _tagMappings.Keys.Except(plcTagKeys).ToList();
        if (unmappedTags.Any())
        {
            _logger.LogWarning("Found {Count} tag mappings without PLC tags: {Tags}",
                unmappedTags.Count, string.Join(", ", unmappedTags.Take(10)));
        }
    }

    public (string NewState, bool StateChanged) DetermineCallState(
        string tagName,
        string tagAddress,
        TagEdgeState edgeState,
        string currentState)
    {
        var mapping = FindCallByTag(tagName, tagAddress);
        if (mapping == null)
            return (currentState, false);

        var direction = _callDirections.GetValueOrDefault(mapping.Call.Id, CallDirection.None);
        var edgeType = edgeState.EdgeType;

        // [PLC 제어기 관점] OutTag = PLC 출력(명령), InTag = PLC 입력(응답)
        // InOut: OutTag ON → Ready → Going, InTag ON → Going → Finish, InTag OFF → Finish → Ready
        // InOnly: InTag ON → Ready → Going → Finish (instant), InTag OFF → Finish → Ready
        // OutOnly: OutTag ON → Ready → Going, OutTag OFF → Going → Finish → Ready (auto)

        return (direction, mapping.IsInTag, edgeType) switch
        {
            // InOut Direction
            (CallDirection.InOut, false, EdgeType.RisingEdge) when currentState == "Ready"
                => ("Going", true),  // OutTag rising: Ready → Going

            (CallDirection.InOut, true, EdgeType.RisingEdge) when currentState == "Going"
                => ("Finish", true), // InTag rising: Going → Finish

            (CallDirection.InOut, true, EdgeType.FallingEdge) when currentState == "Finish"
                => ("Ready", true),  // InTag falling: Finish → Ready

            // InOnly Direction
            (CallDirection.InOnly, true, EdgeType.RisingEdge) when currentState == "Ready"
                => ("Finish", true), // InTag rising: Ready → Going → Finish (instant)

            (CallDirection.InOnly, true, EdgeType.FallingEdge) when currentState == "Finish"
                => ("Ready", true),  // InTag falling: Finish → Ready

            // OutOnly Direction
            (CallDirection.OutOnly, false, EdgeType.RisingEdge) when currentState == "Ready"
                => ("Going", true),  // OutTag rising: Ready → Going

            (CallDirection.OutOnly, false, EdgeType.FallingEdge) when currentState == "Going"
                => ("Finish", true), // OutTag falling: Going → Finish → Ready (auto)

            _ => (currentState, false) // No state change
        };
    }
}
