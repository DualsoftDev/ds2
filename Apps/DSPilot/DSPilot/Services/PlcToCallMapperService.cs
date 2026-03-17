using Ds2.Core;
using DSPilot.Engine;
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

    // Tag Key (Name or Address) → (Call, ApiCall, IsInTag) 매핑
    private Dictionary<string, (Call Call, ApiCall ApiCall, bool IsInTag)> _tagToCallMap = new();

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
    /// AASX에서 Call/ApiCall/Tag 매핑 초기화
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

        var allCalls = _projectService.GetAllCalls();

        foreach (var call in allCalls)
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
                        _tagToCallMap[inTagKey] = (call, apiCall, IsInTag: true);
                        _logger.LogDebug("Mapped InTag '{TagKey}' ({Mode}) → Call '{CallName}'",
                            inTagKey, _tagMatchMode, call.Name);
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
                        _tagToCallMap[outTagKey] = (call, apiCall, IsInTag: false);
                        _logger.LogDebug("Mapped OutTag '{TagKey}' ({Mode}) → Call '{CallName}'",
                            outTagKey, _tagMatchMode, call.Name);
                    }
                }
            }

            _callTagMap[call.Name] = (inTagKey, outTagKey);
        }

        _initialized = true;
        _logger.LogInformation(
            "PlcToCallMapper initialized: {CallCount} calls, {TagCount} tags mapped",
            allCalls.Count, _tagToCallMap.Count);

        // 매핑된 태그 목록 상세 로그
        if (_tagToCallMap.Count > 0)
        {
            _logger.LogDebug("Mapped tags:");
            foreach (var kvp in _tagToCallMap)
            {
                var (call, apiCall, isInTag) = kvp.Value;
                _logger.LogDebug("  {TagName} → Call '{CallName}' ({TagType})",
                    kvp.Key, call.Name, isInTag ? "IN" : "OUT");
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
    /// </summary>
    public (Call Call, ApiCall ApiCall, bool IsInTag)? FindCallByTag(string tagName, string tagAddress)
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
    public (Call Call, ApiCall ApiCall, bool IsInTag)? FindCallByTagName(string tagName)
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

        var (call, apiCall, isInTag) = mapping;
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
                "Call '{CallName}': State transition {OldState} → {NewState} (Tag: {TagName}, Edge: {EdgeType})",
                call.Name, currentCallState, newStateStr, tagName, edgeState.EdgeType);
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
