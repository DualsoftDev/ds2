using Ds2.Core;
using DSPilot.Engine;
using DSPilot.Models;
using DSPilot.Models.Dsp;

namespace DSPilot.Services;

/// <summary>
/// PLC 태그와 Call 매핑 서비스 (F# StateTransition 사용)
/// </summary>
public class PlcToCallMapperService
{
    private readonly DsProjectService _projectService;
    private readonly ILogger<PlcToCallMapperService> _logger;
    private readonly IConfiguration _configuration;
    private string _tagMatchMode = "Address"; // "Address" or "Name"

    // Tag Key (Name or Address) → CallMappingInfo 매핑
    private Dictionary<string, CallMappingInfo> _tagToCallMap = new();

    // CallName → (InTagName, OutTagName) 매핑
    private Dictionary<string, (string? InTag, string? OutTag)> _callTagMap = new();

    private bool _initialized = false;

    public PlcToCallMapperService(
        DsProjectService projectService,
        ILogger<PlcToCallMapperService> logger,
        IConfiguration configuration)
    {
        _projectService = projectService;
        _logger = logger;
        _configuration = configuration;
        _tagMatchMode = _configuration.GetValue<string>("PlcDatabase:TagMatchMode") ?? "Address";
    }

    /// <summary>
    /// AASX에서 Call/ApiCall/Tag 매핑 초기화 (FlowName/WorkName 포함)
    /// </summary>
    public void Initialize()
    {
        if (!_projectService.IsLoaded)
        {
            _logger.LogWarning("Cannot initialize PlcToCallMapper: AASX project not loaded");
            return;
        }

        _tagToCallMap.Clear();
        _callTagMap.Clear();

        _logger.LogInformation("Initializing PlcToCallMapper with TagMatchMode: {Mode}", _tagMatchMode);

        // Flow → Work → Call 계층 구조로 순회하여 FlowName/WorkName 캡처
        var allFlows = _projectService.GetAllFlows();

        foreach (var flow in allFlows)
        {
            var works = _projectService.GetWorks(flow.Id);

            foreach (var work in works)
            {
                var calls = _projectService.GetCalls(work.Id);

                foreach (var call in calls)
                {
                    string? inTagKey = null;
                    string? outTagKey = null;

                    // Call의 모든 ApiCall 순회
                    foreach (var apiCall in call.ApiCalls)
                    {
                        // InTag 매핑 (F# Option 처리)
                        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.InTag))
                        {
                            var inTag = apiCall.InTag.Value;
                            var tagKey = GetTagKey(inTag);

                            if (!string.IsNullOrEmpty(tagKey))
                            {
                                inTagKey = tagKey;
                                _tagToCallMap[inTagKey] = new CallMappingInfo
                                {
                                    Call = call,
                                    ApiCall = apiCall,
                                    IsInTag = true,
                                    FlowName = flow.Name,
                                    WorkName = work.Name
                                };

                                _logger.LogDebug("Mapped InTag '{TagKey}' ({Mode}) → Call '{CallName}' (Flow: {FlowName}, Work: {WorkName})",
                                    inTagKey, _tagMatchMode, call.Name, flow.Name, work.Name);
                            }
                        }

                        // OutTag 매핑 (F# Option 처리)
                        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.OutTag))
                        {
                            var outTag = apiCall.OutTag.Value;
                            var tagKey = GetTagKey(outTag);

                            if (!string.IsNullOrEmpty(tagKey))
                            {
                                outTagKey = tagKey;
                                _tagToCallMap[outTagKey] = new CallMappingInfo
                                {
                                    Call = call,
                                    ApiCall = apiCall,
                                    IsInTag = false,
                                    FlowName = flow.Name,
                                    WorkName = work.Name
                                };

                                _logger.LogDebug("Mapped OutTag '{TagKey}' ({Mode}) → Call '{CallName}' (Flow: {FlowName}, Work: {WorkName})",
                                    outTagKey, _tagMatchMode, call.Name, flow.Name, work.Name);
                            }
                        }
                    }

                    _callTagMap[call.Name] = (inTagKey, outTagKey);
                }
            }
        }

        _initialized = true;
        _logger.LogInformation(
            "PlcToCallMapper initialized: {FlowCount} flows, {TagCount} tags mapped",
            allFlows.Count, _tagToCallMap.Count);

        // 매핑된 태그 목록 상세 로그
        if (_tagToCallMap.Count > 0)
        {
            _logger.LogDebug("Mapped tags:");
            foreach (var kvp in _tagToCallMap)
            {
                var mapping = kvp.Value;
                _logger.LogDebug("  {TagName} → Call '{CallName}' (Flow: {FlowName}, {TagType})",
                    kvp.Key, mapping.Call.Name, mapping.FlowName, mapping.IsInTag ? "IN" : "OUT");
            }
        }
        else
        {
            _logger.LogWarning("No tags were mapped! Please check that AASX ApiCall InTag/OutTag names match PLC tag names.");
        }
    }

    /// <summary>
    /// Tag Key (Name 또는 Address)로 매핑된 태그 키 추출
    /// </summary>
    private string? GetTagKey(IOTag tag)
    {
        return _tagMatchMode.Equals("Address", StringComparison.OrdinalIgnoreCase)
            ? tag.Address
            : tag.Name;
    }

    /// <summary>
    /// PLC 태그로 Call 찾기 (Name 또는 Address 기준)
    /// FlowName과 WorkName을 포함한 CallMappingInfo 반환
    /// </summary>
    public CallMappingInfo? FindCallByTag(string tagName, string tagAddress)
    {
        if (!_initialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized");
            return null;
        }

        var tagKey = _tagMatchMode.Equals("Address", StringComparison.OrdinalIgnoreCase)
            ? tagAddress
            : tagName;

        return _tagToCallMap.TryGetValue(tagKey, out var mapping) ? mapping : null;
    }

    /// <summary>
    /// 태그 이름으로 Call 찾기 (하위 호환성)
    /// </summary>
    [Obsolete("Use FindCallByTag(tagName, tagAddress) instead")]
    public CallMappingInfo? FindCallByTagName(string tagName)
    {
        if (!_initialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized");
            return null;
        }

        return _tagToCallMap.TryGetValue(tagName, out var mapping) ? mapping : null;
    }

    /// <summary>
    /// Call의 InTag/OutTag 이름 조회
    /// </summary>
    public (string? InTag, string? OutTag)? GetCallTags(string callName)
    {
        return _callTagMap.TryGetValue(callName, out var tags) ? tags : null;
    }

    /// <summary>
    /// PLC 태그 목록과 비교하여 실제 존재하지 않는 InTag 매핑을 제거.
    /// AASX에서 InTag을 정의했지만 PLC에 해당 태그가 없으면
    /// hasInTag=false로 처리되어 OutTag Falling으로 Finish 전이가 가능해진다.
    /// </summary>
    public void ValidateWithPlcTags(HashSet<string> plcTagKeys)
    {
        if (!_initialized) return;

        int invalidCount = 0;
        foreach (var (callName, (inTag, outTag)) in _callTagMap)
        {
            if (!string.IsNullOrEmpty(inTag) && !plcTagKeys.Contains(inTag))
            {
                _callTagMap[callName] = (null, outTag);
                _tagToCallMap.Remove(inTag);
                invalidCount++;

                _logger.LogWarning(
                    "Call '{CallName}': InTag '{InTag}' not found in PLC tags. Removed (OutTag Falling will trigger Finish).",
                    callName, inTag);
            }
        }

        if (invalidCount > 0)
        {
            _logger.LogInformation(
                "Validated tag mappings: {InvalidCount} InTag(s) removed (not in PLC), {RemainingTags} tags remain",
                invalidCount, _tagToCallMap.Count);
        }
    }

    /// <summary>
    /// 태그 값 변경에 따른 Call 상태 결정 (F# StateTransition 사용)
    /// </summary>
    public (string NewState, bool StateChanged) DetermineCallState(
        string tagName,
        string tagAddress,
        TagEdgeState edgeState,
        string currentCallState)
    {
        if (!_initialized)
        {
            _logger.LogWarning("PlcToCallMapper not initialized");
            return (currentCallState, false);
        }

        var tagKey = _tagMatchMode.Equals("Address", StringComparison.OrdinalIgnoreCase)
            ? tagAddress
            : tagName;

        if (!_tagToCallMap.TryGetValue(tagKey, out var mapping))
        {
            return (currentCallState, false);
        }

        var call = mapping.Call;
        var isInTag = mapping.IsInTag;
        var (inTag, outTag) = _callTagMap[call.Name];
        var hasInTag = !string.IsNullOrEmpty(inTag);

        // F# StateTransition 사용
        var (newState, stateChanged) = StateTransition.tryTransition(
            currentCallState,
            isInTag,
            hasInTag,
            edgeState.EdgeType
        );

        if (stateChanged)
        {
            var newStateStr = StateTransition.stateToString(newState);
            _logger.LogInformation(
                "Call '{CallName}' (Flow: {FlowName}): State transition {OldState} → {NewState} (Tag: {TagName}, Edge: {EdgeType})",
                call.Name, mapping.FlowName, currentCallState, newStateStr, tagName, edgeState.EdgeType);
            return (newStateStr, true);
        }

        return (currentCallState, false);
    }

    /// <summary>
    /// 매핑된 태그 개수
    /// </summary>
    public int MappedTagCount => _tagToCallMap.Count;

    /// <summary>
    /// 매핑된 Call 개수
    /// </summary>
    public int MappedCallCount => _callTagMap.Count;

    /// <summary>
    /// 초기화 여부
    /// </summary>
    public bool IsInitialized => _initialized;
}
