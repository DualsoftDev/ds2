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
    // ★대표(첫) 쌍만 담는다 — 단일 태그 소비자(표시/레거시)용. 전체 쌍은 _callIdToPairs.
    private readonly Dictionary<Guid, (string? InTag, string? OutTag)> _callIdToTags = new();
    // Call.Id → 전체 ApiCall(I/O 쌍) 목록 — 사이클 경계(시작 OR/완료 AND)·간트 쌍별 인터벌의 정본(2026-09-02).
    private readonly Dictionary<Guid, List<CallTagPair>> _callIdToPairs = new();
    // Call.Id → 표시 메타 — GetAllCallTagPairs 를 _tagMappings 순회(비결정적 last-wins) 대신 이 dict 로 만든다.
    private readonly Dictionary<Guid, (string CallName, string FlowName, string WorkName)> _callInfoById = new();
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
        _callIdToPairs.Clear();
        _callInfoById.Clear();

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

                    // ★전 ApiCall(I/O 쌍) 등록 (2026-09-02) — 종전엔 ApiCalls[0]만 등록해 복수 쌍 Call 의
                    // 나머지 IN/OUT 주소가 매핑에서 통째로 빠졌다(간트 신호/사이클 경계/알람 귀속 누락).
                    bool hasInTag = false;
                    bool hasOutTag = false;
                    var pairs = new List<CallTagPair>(call.ApiCalls.Count);
                    foreach (var ac in call.ApiCalls)
                    {
                        var inAddr = ac.InTag?.Value.Address;
                        var outAddr = ac.OutTag?.Value.Address;
                        if (inAddr is null && outAddr is null) continue;
                        hasInTag |= inAddr is not null;
                        hasOutTag |= outAddr is not null;
                        pairs.Add(new CallTagPair(
                            ac.Id, inAddr, outAddr,
                            ActiveValueOf(ac.InputSpec),
                            ActiveValueOf(ac.OutputSpec)));
                    }

                    // Direction 은 쌍 union 기준(어느 쌍이든 IN 이 있으면 응답 관측 가능).
                    var direction = DetermineDirection(hasInTag, hasOutTag);
                    _callDirections[call.Id] = direction;

                    // Call.Id 기준 역방향 lookup — 대표(첫) 쌍. 주소 공유(복제 Flow) 시에도 Call 별 보존.
                    var firstAc = call.ApiCalls[0];
                    _callIdToTags[call.Id] = (
                        firstAc.InTag?.Value.Address,
                        firstAc.OutTag?.Value.Address);
                    _callIdToPairs[call.Id] = pairs;
                    _callInfoById[call.Id] = (call.Name, flow.Name, work.Name);

                    // 주소 → Call 매핑 — 쌍마다 IN/OUT 각각 등록(같은 주소 공유 시 last-wins, 종전과 동일 규약).
                    foreach (var ac in call.ApiCalls)
                    {
                        if (ac.OutTag != null)
                        {
                            _tagMappings[ac.OutTag.Value.Address] = new Models.CallMappingInfo
                            {
                                Call = call,
                                ApiCall = ac,
                                IsInTag = false,
                                FlowName = flow.Name,
                                WorkName = work.Name,
                                Direction = direction
                            };
                            mappingCount++;
                        }
                        if (ac.InTag != null)
                        {
                            _tagMappings[ac.InTag.Value.Address] = new Models.CallMappingInfo
                            {
                                Call = call,
                                ApiCall = ac,
                                IsInTag = true,
                                FlowName = flow.Name,
                                WorkName = work.Name,
                                Direction = direction
                            };
                            mappingCount++;
                        }
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
    /// ValueSpec → 엣지 쿼리용 활성값. 엔진(RuntimeSemantics/ValueSpec.evaluate)과 같은 정의를 SQL 로 옮기기 위한
    /// 근사: null = bool 관용 정규화('1'/'true'/'on', 종전 FindRisingEdges 와 동일), "false" = 반전 bool(활성=false),
    /// 그 외 = 값 일치(대표값 문자열). Multiple/Ranges 스펙은 대표값(첫 값) 근사 — 현장 스펙은 Single 이 정상이고,
    /// 완전한 집합/범위 매칭은 SQL 로 못 옮긴다(필요해지면 앱단 판정 경로 추가).
    /// </summary>
    private static string? ActiveValueOf(ValueSpec spec)
    {
        if (spec.Tag == ValueSpec.Tags.UndefinedValue) return null;
        var v = ValueSpecModule.toDefaultString(spec);
        if (spec.Tag == ValueSpec.Tags.BoolValue)
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ? null : "false";
        return v;
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
    /// Call 의 전체 ApiCall(I/O 쌍) 목록 — 사이클 경계(시작 OR/완료 AND)·간트 쌍별 인터벌의 정본.
    /// 미등록 Call 은 빈 목록.
    /// </summary>
    public IReadOnlyList<CallTagPair> GetCallTagPairsByCallId(Guid callId)
        => _callIdToPairs.TryGetValue(callId, out var pairs) ? pairs : Array.Empty<CallTagPair>();

    /// <summary>
    /// flow + Call 이름 → 전체 ApiCall(I/O 쌍) 목록. 동명 Call(복제 Flow)은 flow 로 구분되고,
    /// 같은 flow 안 동명 Call 은 첫 매칭 — 종전 GetAllCallTagPairs().FirstOrDefault 소비자와 동일 규약.
    /// </summary>
    public IReadOnlyList<CallTagPair> GetCallTagPairsByName(string flowName, string callName)
    {
        foreach (var kvp in _callInfoById)
        {
            if (string.Equals(kvp.Value.FlowName, flowName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(kvp.Value.CallName, callName, StringComparison.OrdinalIgnoreCase)
                && _callIdToPairs.TryGetValue(kvp.Key, out var pairs))
                return pairs;
        }
        return Array.Empty<CallTagPair>();
    }

    /// <summary>
    /// 모든 Call의 태그 쌍 정보를 반환 (Heatmap 필터 등 단일 태그 소비자용 — 대표(첫) 쌍).
    /// 종전 _tagMappings 순회(복수 쌍에서 last-wins 비결정)를 Call.Id 기준 dict 로 교체 — 항상 첫 쌍.
    /// </summary>
    public List<(Guid CallId, string CallName, string FlowName, string WorkName, string? InTag, string? OutTag)> GetAllCallTagPairs()
    {
        var result = new List<(Guid, string, string, string, string?, string?)>(_callInfoById.Count);
        foreach (var kvp in _callInfoById)
        {
            if (!_callIdToTags.TryGetValue(kvp.Key, out var tags))
                continue;
            result.Add((kvp.Key, kvp.Value.CallName, kvp.Value.FlowName, kvp.Value.WorkName, tags.InTag, tags.OutTag));
        }
        return result;
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

/// <summary>
/// Call 1개의 ApiCall(I/O 쌍) 1개 — 사이클 경계/간트 쌍별 소비 단위.
/// *ActiveValue = 엣지 쿼리 활성 판정 값(엔진 RuntimeSemantics/ValueSpec 정의 공유):
/// null = bool 관용('1'/'true'/'on'), "false" = 반전 bool(활성=false), 그 외 = 값 일치.
/// </summary>
public sealed record CallTagPair(
    Guid ApiCallId,
    string? InTag,
    string? OutTag,
    string? InActiveValue,
    string? OutActiveValue);
