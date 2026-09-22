// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;
using Ds2.Core;

namespace DSPilot.Services;

/// <summary>
/// 디바이스별 이상감지 차단 규칙(<see cref="AbnormalDeviceFilter"/>)의 매칭/정규화 단일 소스.
/// 디바이스 식별 = Call 이름 "{DevicesAlias}.{ApiName}" 의 DevicesAlias 접두부 — DTO(CallName)와
/// userTagAlertLog.tagAddress("WORK / DEVICE.API") 양쪽이 같은 규칙으로 걸러지도록 여기서만 정의한다.
/// 대소문자는 무시(OrdinalIgnoreCase) — SQLite LIKE 의 ASCII 대소문자 무시와 동작을 맞춘다.
/// </summary>
public static class AbnormalDeviceFilterHelpers
{
    /// <summary>
    /// 차단 가능한 이상감지 유형 (int 값, enum 이름, 한글 라벨) — UI 체크박스/라벨의 단일 소스.
    /// 센서 2종(SensorOpen/SensorShort)은 메모리 전용 경로(CCTV 이상탐지)로만 흘러 알람·통계·기록에
    /// 구조적으로 나오지 않으므로 차단 대상에서 제외 — Normalize 가 기존 저장 규칙의 센서 kind 도 걸러낸다.
    /// </summary>
    public static readonly IReadOnlyList<(int Kind, string Name, string Label)> KindOptions =
    [
        ((int)AbnormalKind.ActionOver,  nameof(AbnormalKind.ActionOver),  LabelOf(AbnormalKind.ActionOver)),
        ((int)AbnormalKind.ActionUnder, nameof(AbnormalKind.ActionUnder), LabelOf(AbnormalKind.ActionUnder)),
    ];

    /// <summary>Kind → 한글 라벨 (AbnormalEventService.Classify 와 공유).</summary>
    public static string LabelOf(AbnormalKind kind) => kind switch
    {
        AbnormalKind.SensorOpen  => "센서 단선/이탈",
        AbnormalKind.SensorShort => "센서 오감지",
        AbnormalKind.ActionOver  => "동작 지연(시간 초과)",
        AbnormalKind.ActionUnder => "동작 과속(시간 미만)",
        _ => "이상",
    };

    /// <summary>
    /// (kind, callName) 이 차단 규칙에 걸리는지. callName 이 비어 있으면(미해석 Call) 디바이스를
    /// 특정할 수 없으므로 차단하지 않는다(놓침보다 오차단이 위험 — 보수적).
    /// </summary>
    public static bool IsSuppressed(IReadOnlyList<AbnormalDeviceFilter> rules, int kind, string? callName)
    {
        if (rules is not { Count: > 0 } || string.IsNullOrEmpty(callName)) return false;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Device) || rule.Kinds is not { Count: > 0 }) continue;
            if (!rule.Kinds.Contains(kind)) continue;

            var device = rule.Device.Trim();
            // "{DevicesAlias}.{ApiName}" — 접두 일치("Conveyor1." 는 "Conveyor12.MOVE" 에 안 걸림).
            if (callName.StartsWith(device + ".", StringComparison.OrdinalIgnoreCase)
                || string.Equals(callName, device, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 경로 "FLOW / WORK / CALL" 구성 — 빈 세그먼트·직전과 동일한 세그먼트(예 Work==Flow)는 생략.
    /// userTagAlertLog.tagAddress 기록(AbnormalEventService)과 차단 관리 UI 의 디바이스 경로 표시가
    /// 같은 형식을 쓰도록 단일 소스 — SQL LIKE 매칭(AppendDeviceFilterExclusion)이 이 형식에 의존한다.
    /// </summary>
    public static string BuildPath(params string?[] segments)
    {
        var parts = new List<string>();
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var v = s.Trim();
            if (parts.Count > 0 && string.Equals(parts[^1], v, StringComparison.Ordinal)) continue;
            parts.Add(v);
        }
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// 저장 입력 정규화: 디바이스 trim·빈값 제거, 같은 디바이스 규칙 병합, kind 는 KindOptions 로 한정·중복 제거,
    /// 유형이 하나도 없는 규칙은 삭제(= 차단 해제). 디바이스명 순 정렬.
    /// </summary>
    public static List<AbnormalDeviceFilter> Normalize(IEnumerable<AbnormalDeviceFilter>? rules)
    {
        var merged = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var knownKinds = KindOptions.Select(o => o.Kind).ToHashSet();

        foreach (var rule in rules ?? [])
        {
            var device = rule?.Device?.Trim();
            if (string.IsNullOrEmpty(device)) continue;

            if (!merged.TryGetValue(device, out var kinds))
                merged[device] = kinds = [];
            foreach (var k in rule!.Kinds ?? [])
                if (knownKinds.Contains(k))
                    kinds.Add(k);
        }

        return merged
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new AbnormalDeviceFilter { Device = kv.Key, Kinds = [.. kv.Value] })
            .ToList();
    }

    // ── 사용자정의(UserTag) 알람 차단 ──
    // 식별키 = UserTag 정의의 TagAddress(UserTagAlertService._definitionsByAddress 와 동일 고유키).
    // userTagAlertLog 의 usertag 행(valueType != 'Abnormal')은 tagAddress 에 이 주소를 그대로 담으므로
    // 소스 차단(폴링 skip)·읽기 필터(SQL)·라이브 큐 필터가 모두 같은 키로 동작한다.

    /// <summary>tagAddress 가 UserTag 차단 목록에 포함되는지(대소문자 무시).</summary>
    public static bool IsUserTagSuppressed(IReadOnlyCollection<string>? blockedAddresses, string? tagAddress)
    {
        if (blockedAddresses is not { Count: > 0 } || string.IsNullOrWhiteSpace(tagAddress)) return false;
        foreach (var a in blockedAddresses)
            if (!string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), tagAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>UserTag 차단 목록 정규화: trim·빈값 제거·중복 제거(대소문자 무시)·정렬.</summary>
    public static List<string> NormalizeUserTagFilters(IEnumerable<string>? addresses)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in addresses ?? [])
            if (!string.IsNullOrWhiteSpace(a))
                set.Add(a.Trim());
        return [.. set];
    }

    // ── 이상알람TAG → 디바이스 귀속 ──
    // 키 = (System 이름, 태그 주소). MTBF/MTTR 회복 게이트가 볼 flow 집합의 근거다.
    // 값이 빈 문자열이면 '전역'(고의로 안 묶음), 항목 자체가 없으면 '미지정'(아직 안 묶음) — 둘은 다르다.

    /// <summary>(System, 주소) 복합키. 두 칸 모두 trim + 대소문자 무시로 맞춘다.</summary>
    private static string BindingKey(string? system, string? tagAddress) =>
        (system ?? string.Empty).Trim() + "\u0001" + (tagAddress ?? string.Empty).Trim();

    /// <summary>
    /// 귀속 목록 정규화: trim, 주소 빈 항목 제거, 같은 (System, 주소) 는 <b>뒤엣것이 이긴다</b>(편집기가
    /// 최종 목록을 통째로 보내므로 마지막 값이 사용자의 의도다). System·주소 순 정렬.
    /// </summary>
    public static List<UserTagDeviceBinding> NormalizeUserTagDeviceBindings(IEnumerable<UserTagDeviceBinding>? bindings)
    {
        var merged = new Dictionary<string, UserTagDeviceBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings ?? [])
        {
            var addr = b?.TagAddress?.Trim();
            if (string.IsNullOrEmpty(addr)) continue;
            var sys = b!.System?.Trim() ?? string.Empty;
            merged[BindingKey(sys, addr)] = new UserTagDeviceBinding
            {
                System = sys,
                SystemId = NormId(b.SystemId),
                Endpoint = b.Endpoint?.Trim() ?? string.Empty,
                TagAddress = addr,
                Device = b.Device?.Trim() ?? string.Empty,
            };
        }

        return merged.Values
            .OrderBy(b => b.System, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.TagAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>GUID 표기 정규화 — 중괄호·대문자·공백 차이로 키가 어긋나지 않게 한다.</summary>
    public static string NormId(string? id) =>
        Guid.TryParse((id ?? string.Empty).Trim(), out var g) ? g.ToString("D") : string.Empty;

    /// <summary>
    /// 이상알람TAG 귀속 조회 색인 — <b>리네임 내성</b>을 담당한다(2026-09-22).
    /// <para>
    /// 종전엔 (System 이름, 주소) 한 가지 키뿐이라, AASX 교체로 이름이 바뀌면 과거 알람이 통째로
    /// '미지정' 으로 떨어졌다(현장 실측: 사흘치 2,000여 건). 이제 <b>GUID 와 이름 두 색인</b>을 만들고
    /// 조회 때 순서대로 시도한다.
    /// </para>
    /// </summary>
    public sealed class UserTagDeviceIndex
    {
        /// <summary>
        /// <b>(엔드포인트, 주소) → 디바이스 — 정본 색인.</b> 신호의 정체가 이것이다(doc/31 §6).
        /// 이름·GUID 색인은 이 칸이 비어 있던 시절의 데이터를 받는 폴백이다.
        /// </summary>
        public Dictionary<string, string> ByEndpoint { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>(System GUID, 주소) → 디바이스. 폴백.</summary>
        public Dictionary<string, string> BySystemId { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>(System 이름, 주소) → 디바이스. GUID 가 없던 시절 매핑의 폴백.</summary>
        public Dictionary<string, string> BySystemName { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>옛 이름 → 현재 이름. 별칭 이력 + 현재 모델(GUID 가 같은데 이름만 다른 경우)에서 만든다.</summary>
        public Dictionary<string, string> NameOfAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>System GUID → 현재 이름. 알람 행의 GUID 로 현재 이름을 찾아 이름 색인을 두드린다.</summary>
        public Dictionary<string, string> NameOfId { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>옛 GUID → 현재 GUID(별칭 이력). GUID 재발급을 받는다.</summary>
        public Dictionary<string, string> IdOfAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>System GUID → 엔드포인트(현재 모델). 엔드포인트가 없는 옛 알람 행을 끌어올릴 때 쓴다.</summary>
        public Dictionary<string, string> EndpointOfId { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Count => ByEndpoint.Count + BySystemId.Count + BySystemName.Count;
    }

    /// <summary>
    /// 조회 색인 생성. 값 <c>""</c> 는 전역, <b>키 부재는 미지정</b>이라 호출부가 둘을 구분할 수 있어야 한다
    /// (빈 문자열을 null 로 접지 말 것).
    /// </summary>
    /// <param name="currentSystems">
    /// 현재 모델의 (GUID, 이름, 엔드포인트). 엔드포인트가 없던 시절의 알람 행을 현재 모델로 끌어올리고,
    /// 옛 이름의 알람을 현재 이름의 매핑으로 잇는 데 쓴다.
    /// </param>
    /// <param name="aliases">별칭 이력 — 엔드포인트까지 바뀐 극단(진짜 설비 교체)을 받는 수동 예외.</param>
    public static UserTagDeviceIndex BuildUserTagDeviceIndex(
        IEnumerable<UserTagDeviceBinding>? bindings,
        IEnumerable<(string Id, string Name, string Endpoint)>? currentSystems = null,
        IEnumerable<SystemAlias>? aliases = null)
    {
        var idx = new UserTagDeviceIndex();

        foreach (var b in NormalizeUserTagDeviceBindings(bindings))
        {
            idx.BySystemName[BindingKey(b.System, b.TagAddress)] = b.Device;
            if (b.SystemId.Length > 0)
                idx.BySystemId[BindingKey(b.SystemId, b.TagAddress)] = b.Device;
            if (b.Endpoint.Length > 0)
                idx.ByEndpoint[BindingKey(b.Endpoint, b.TagAddress)] = b.Device;
        }

        foreach (var (id, name, endpoint) in currentSystems ?? [])
        {
            var gid = NormId(id);
            if (gid.Length == 0) continue;
            if (!string.IsNullOrWhiteSpace(name)) idx.NameOfId[gid] = name.Trim();
            if (!string.IsNullOrWhiteSpace(endpoint)) idx.EndpointOfId[gid] = endpoint.Trim();
        }

        foreach (var a in aliases ?? [])
        {
            var from = a?.FromSystem?.Trim() ?? string.Empty;
            var to = a?.ToSystem?.Trim() ?? string.Empty;
            if (from.Length > 0 && to.Length > 0) idx.NameOfAlias[from] = to;

            var fromId = NormId(a?.FromSystemId);
            var toId = NormId(a?.ToSystemId);
            if (fromId.Length > 0 && toId.Length > 0) idx.IdOfAlias[fromId] = toId;
        }

        return idx;
    }

    /// <summary>
    /// 한 알람의 귀속 디바이스를 찾는다 — <b>해석 사슬</b>. 첫 성공에서 멈춘다.
    /// <para>
    /// <b>정본은 ①(엔드포인트, 주소)</b> 다 — 신호의 정체가 그것이므로 이름·GUID·프로젝트 재생성을
    /// 전부 통과한다. ②~⑤는 엔드포인트가 없던 시절의 행을 받는 <b>과거 호환 폴백</b>이고, 앞으로
    /// 쌓이는 데이터는 ①에서 끝난다.
    /// </para>
    /// <list type="number">
    ///   <item>알람의 엔드포인트로 — 정본</item>
    ///   <item>알람의 GUID → 현재 모델의 엔드포인트 → 엔드포인트 색인 (엔드포인트 없던 옛 행)</item>
    ///   <item>알람의 System GUID 로 (이름만 바뀐 경우)</item>
    ///   <item>알람의 GUID → 현재 System 이름 → 이름 색인 (GUID 없이 저장된 옛 매핑)</item>
    ///   <item>알람의 System 이름 그대로, 그리고 별칭 이력을 거쳐 한 번 더 (종전 동작)</item>
    /// </list>
    /// 반환 <c>false</c> = 미지정(지표 제외), <c>true</c> + 빈 문자열 = 전역.
    /// </summary>
    public static bool TryGetBoundDevice(
        UserTagDeviceIndex? index, string? endpoint, string? systemId, string? systemName, string? tagAddress,
        out string device)
    {
        device = string.Empty;
        if (index is null || index.Count == 0 || string.IsNullOrWhiteSpace(tagAddress)) return false;

        var gid = NormId(systemId);
        var ep = (endpoint ?? string.Empty).Trim();

        if (ep.Length > 0 && index.ByEndpoint.TryGetValue(BindingKey(ep, tagAddress), out device!)) return true;

        if (ep.Length == 0 && gid.Length > 0 && index.EndpointOfId.TryGetValue(gid, out var curEp)
            && index.ByEndpoint.TryGetValue(BindingKey(curEp, tagAddress), out device!)) return true;

        if (gid.Length > 0 && index.BySystemId.TryGetValue(BindingKey(gid, tagAddress), out device!)) return true;

        if (gid.Length > 0 && index.IdOfAlias.TryGetValue(gid, out var newId)
            && index.BySystemId.TryGetValue(BindingKey(newId, tagAddress), out device!)) return true;

        if (gid.Length > 0 && index.NameOfId.TryGetValue(gid, out var curName)
            && index.BySystemName.TryGetValue(BindingKey(curName, tagAddress), out device!)) return true;

        if (index.BySystemName.TryGetValue(BindingKey(systemName, tagAddress), out device!)) return true;

        var sys = (systemName ?? string.Empty).Trim();
        if (sys.Length > 0 && index.NameOfAlias.TryGetValue(sys, out var aliasName)
            && index.BySystemName.TryGetValue(BindingKey(aliasName, tagAddress), out device!)) return true;

        device = string.Empty;
        return false;
    }

    /// <summary>엔드포인트를 모르는 호출부용 간편 오버로드 — ②부터 시작한다.</summary>
    public static bool TryGetBoundDevice(
        UserTagDeviceIndex? index, string? systemId, string? systemName, string? tagAddress, out string device) =>
        TryGetBoundDevice(index, null, systemId, systemName, tagAddress, out device);

    /// <summary>GUID 도 엔드포인트도 모르는 호출부용 — 이름 색인만 두드린다(테스트·구 경로).</summary>
    public static bool TryGetBoundDeviceByName(
        UserTagDeviceIndex? index, string? systemName, string? tagAddress, out string device) =>
        TryGetBoundDevice(index, null, null, systemName, tagAddress, out device);
}
