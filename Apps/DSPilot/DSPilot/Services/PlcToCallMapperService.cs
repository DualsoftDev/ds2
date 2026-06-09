// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
    // Call.Id → (InTag, OutTag) 역방향 lookup. _tagMappings 는 주소가 key 라 같은 주소를 공유하는
    // 복제 Flow 들은 last-write-wins 로 사라지지만, 이 dict 은 Call.Id 가 unique 라 4개 다 보존됨.
    private readonly Dictionary<Guid, (string? InTag, string? OutTag)> _callIdToTags = new();
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
    /// 강제 재빌드 — AASX 재로딩(Promaker "agent 보내기") 후 매핑이 stale 해지지 않도록.
    /// <see cref="Initialize"/> 의 _isInitialized 가드를 우회하여 현재 DsStore 로 다시 빌드한다.
    /// </summary>
    public void Reinitialize()
    {
        _isInitialized = false;
        Initialize();
    }

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
        _callIdToTags.Clear();

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

                    // Call.Id 기준 역방향 lookup — 주소 공유(복제 Flow) 시에도 4개 모두 보존
                    _callIdToTags[call.Id] = (
                        apiCall.InTag?.Value.Address,
                        apiCall.OutTag?.Value.Address);

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
        // _callIdToTags 는 Call.Id 가 unique 이므로 주소를 공유하는 복제 Flow 의
        // Call 들도 각각 보존됨. 과거 _tagMappings 순회 구현은 주소 충돌 시 4개 중 3개의
        // Call.Id 가 dict 에 존재하지 않아 null 을 반환하던 버그가 있었음.
        if (_callIdToTags.TryGetValue(callId, out var tags))
        {
            if (tags.InTag != null || tags.OutTag != null)
                return tags;
        }
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

}
