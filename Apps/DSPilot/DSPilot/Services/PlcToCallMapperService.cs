using Ds2.Core;
using Ds2.UI.Core;
using DSPilot.Engine;
using DSPilot.Models;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

/// <summary>
/// PLC 태그와 Call 매핑 서비스 (F# PlcToCallMapper 사용)
/// </summary>
public class PlcToCallMapperService
{
    private readonly DsProjectService _projectService;
    private readonly ILogger<PlcToCallMapperService> _logger;
    private readonly IConfiguration _configuration;
    private TagMatchMode _tagMatchMode;

    // F# Map 타입 저장
    private FSharpMap<string, Engine.CallMappingInfo>? _tagToCallMap;
    private FSharpMap<Guid, Tuple<FSharpOption<string>, FSharpOption<string>>>? _callTagMap;

    private bool _initialized = false;

    public PlcToCallMapperService(
        DsProjectService projectService,
        ILogger<PlcToCallMapperService> logger,
        IConfiguration configuration)
    {
        _projectService = projectService;
        _logger = logger;
        _configuration = configuration;

        var mode = _configuration.GetValue<string>("PlcDatabase:TagMatchMode") ?? "Address";
        _tagMatchMode = mode.Equals("Name", StringComparison.OrdinalIgnoreCase)
            ? TagMatchMode.ByName
            : TagMatchMode.ByAddress;
    }

    /// <summary>
    /// AASX에서 Call/ApiCall/Tag 매핑 초기화 (F# PlcToCallMapper 사용)
    /// </summary>
    public void Initialize()
    {
        if (!_projectService.IsLoaded)
        {
            _logger.LogWarning("Cannot initialize PlcToCallMapper: AASX project not loaded");
            return;
        }

        // F# PlcToCallMapper.initialize 호출
        var store = _projectService.GetStore();
        var (tagToCallMap, callTagMap) = PlcToCallMapper.initialize(store, _tagMatchMode, _logger);

        _tagToCallMap = tagToCallMap;
        _callTagMap = callTagMap;
        _initialized = true;
    }

    /// <summary>
    /// PLC 태그로 Call 찾기
    /// </summary>
    public Models.CallMappingInfo? FindCallByTag(string tagName, string tagAddress)
    {
        if (!_initialized || _tagToCallMap == null)
        {
            _logger.LogWarning("PlcToCallMapper not initialized");
            return null;
        }

        // F# PlcToCallMapper.findCallByTag 호출
        var mappingOpt = PlcToCallMapper.findCallByTag(_tagMatchMode, tagName, tagAddress, _tagToCallMap);

        if (FSharpOption<Engine.CallMappingInfo>.get_IsSome(mappingOpt))
        {
            var fsMapping = mappingOpt.Value;

            // F# CallMappingInfo → C# CallMappingInfo 변환
            return new Models.CallMappingInfo
            {
                Call = fsMapping.Call,
                ApiCall = fsMapping.ApiCall,
                IsInTag = fsMapping.IsInTag,
                FlowName = fsMapping.FlowName
            };
        }

        return null;
    }

    /// <summary>
    /// 태그 이름으로 Call 찾기 (하위 호환성)
    /// </summary>
    [Obsolete("Use FindCallByTag(tagName, tagAddress) instead")]
    public Models.CallMappingInfo? FindCallByTagName(string tagName)
    {
        return FindCallByTag(tagName, tagName);
    }

    /// <summary>
    /// Call ID로 InTag/OutTag 이름 조회
    /// </summary>
    public (string? InTag, string? OutTag)? GetCallTagsByCallId(Guid callId)
    {
        if (!_initialized || _callTagMap == null)
        {
            return null;
        }

        // F# PlcToCallMapper.getCallTags 호출
        var tagsOpt = PlcToCallMapper.getCallTags(callId, _callTagMap);

        if (FSharpOption<Tuple<FSharpOption<string>, FSharpOption<string>>>.get_IsSome(tagsOpt))
        {
            var (inTagOpt, outTagOpt) = tagsOpt.Value;
            var inTag = FSharpOption<string>.get_IsSome(inTagOpt) ? inTagOpt.Value : null;
            var outTag = FSharpOption<string>.get_IsSome(outTagOpt) ? outTagOpt.Value : null;
            return (inTag, outTag);
        }

        return null;
    }

    /// <summary>
    /// PLC 태그 목록과 비교하여 실제 존재하지 않는 InTag 매핑을 제거
    /// </summary>
    public void ValidateWithPlcTags(HashSet<string> plcTagKeys)
    {
        if (!_initialized || _tagToCallMap == null || _callTagMap == null)
        {
            return;
        }

        // F# PlcToCallMapper.validateWithPlcTags 호출
        var (newTagToCallMap, newCallTagMap) = PlcToCallMapper.validateWithPlcTags(
            plcTagKeys, _logger, _tagToCallMap, _callTagMap);

        _tagToCallMap = newTagToCallMap;
        _callTagMap = newCallTagMap;
    }

    /// <summary>
    /// 태그 값 변경에 따른 Call 상태 결정 (F# PlcToCallMapper 사용)
    /// </summary>
    public (string NewState, bool StateChanged) DetermineCallState(
        string tagName,
        string tagAddress,
        TagEdgeState edgeState,
        string currentCallState)
    {
        if (!_initialized || _tagToCallMap == null || _callTagMap == null)
        {
            _logger.LogWarning("PlcToCallMapper not initialized");
            return (currentCallState, false);
        }

        // F# PlcToCallMapper.determineCallState 호출
        var (newStateStr, stateChanged) = PlcToCallMapper.determineCallState(
            _tagMatchMode,
            tagName,
            tagAddress,
            edgeState,
            currentCallState,
            _tagToCallMap,
            _callTagMap,
            _logger);

        return (newStateStr, stateChanged);
    }

    /// <summary>
    /// 매핑된 태그 개수
    /// </summary>
    public int MappedTagCount => _tagToCallMap?.Count ?? 0;

    /// <summary>
    /// 매핑된 Call 개수
    /// </summary>
    public int MappedCallCount => _callTagMap?.Count ?? 0;

    /// <summary>
    /// 초기화 여부
    /// </summary>
    public bool IsInitialized => _initialized;
}
