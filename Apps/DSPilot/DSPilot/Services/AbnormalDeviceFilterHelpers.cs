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
    /// 귀속 목록 정규화: trim, 주소 빈 항목 제거, 같은 스코프·주소는 <b>뒤엣것이 이긴다</b>(편집기가
    /// 최종 목록을 통째로 보내므로 마지막 값이 사용자의 의도다).
    /// <para>
    /// ★중복 판정 스코프는 <b>엔드포인트 우선</b>이다. System 이름으로만 묶으면, 엔드포인트만 들고
    /// 이름이 빈 매핑끼리 키가 겹쳐 다른 PLC 의 같은 주소가 서로를 덮어쓴다.
    /// </para>
    /// </summary>
    public static List<UserTagDeviceBinding> NormalizeUserTagDeviceBindings(IEnumerable<UserTagDeviceBinding>? bindings)
    {
        var merged = new Dictionary<string, UserTagDeviceBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings ?? [])
        {
            var addr = b?.TagAddress?.Trim();
            if (string.IsNullOrEmpty(addr)) continue;
            var sys = b!.System?.Trim() ?? string.Empty;
            var ep = b.Endpoint?.Trim() ?? string.Empty;
            merged[BindingKey(ep.Length > 0 ? ep : sys, addr)] = new UserTagDeviceBinding
            {
                System = sys,
                SystemId = NormId(b.SystemId),
                Endpoint = ep,
                TagAddress = addr,
                Device = b.Device?.Trim() ?? string.Empty,
            };
        }

        return merged.Values
            .OrderBy(b => b.Endpoint.Length > 0 ? b.Endpoint : b.System, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.TagAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>GUID 표기 정규화 — 중괄호·대문자·공백 차이로 키가 어긋나지 않게 한다.</summary>
    public static string NormId(string? id) =>
        Guid.TryParse((id ?? string.Empty).Trim(), out var g) ? g.ToString("D") : string.Empty;

    /// <summary>
    /// 이상알람TAG 귀속 조회 색인 — 키는 <b>(엔드포인트, 주소)</b> 하나다(doc/31 §6, 2026-09-25 단일화).
    /// <para>
    /// 종전엔 엔드포인트·GUID·이름·별칭을 순서대로 시도하는 5단 사슬이었다. 과거를 최대한 이어 주려는
    /// 설계였는데 대가가 컸다 — 어떤 건 이어지고 어떤 건 안 이어지는지 사용자가 예측할 수 없고, 이름이
    /// 우연히 겹치면 조용히 엉뚱한 디바이스에 붙는다. 기준을 하나로 잡는다.
    /// </para>
    /// <para>
    /// 근거는 실측이다 — 나흘 사이 System 이름이 두 번 바뀌었고(<c>ub1_#121_#134</c> → <c>UB_#121_#134</c>
    /// → <c>UB_121_134</c>), GUID 는 프로젝트를 다시 만들 때마다 바뀌며(SystemPackage 가 전면 remap),
    /// 엔드포인트만 그대로였다. 이름·GUID 는 AASX <b>안</b>에서 오고 엔드포인트와 주소는 <b>밖</b>에서 온다.
    /// </para>
    /// <para>
    /// ★이름·GUID 지식은 <b>색인을 만들 때 한 번</b>만 쓴다 — 엔드포인트가 비어 있는 옛 매핑을 현재 모델로
    /// 백필하는 용도다. 조회 자체는 키가 하나뿐이다.
    /// </para>
    /// </summary>
    public sealed class UserTagDeviceIndex
    {
        /// <summary>(엔드포인트, 주소) → 디바이스. 값 <c>""</c> 는 전역, <b>키 부재는 미지정</b>이다.</summary>
        public Dictionary<string, string> ByEndpoint { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>엔드포인트를 끝내 못 채운 매핑 수 — 그 System 이 모델에서 사라진 것이다(커버리지 경고).</summary>
        public int DeadBindingCount { get; internal set; }

        public int Count => ByEndpoint.Count;
    }

    /// <summary>
    /// 조회 색인 생성. 엔드포인트가 빈 매핑은 <paramref name="currentSystems"/> 에서 GUID → 이름 순으로
    /// 찾아 채운다(1회 백필). 못 찾으면 그 매핑은 죽은 것이고 개수만 센다.
    /// </summary>
    public static UserTagDeviceIndex BuildUserTagDeviceIndex(
        IEnumerable<UserTagDeviceBinding>? bindings,
        IEnumerable<(string Id, string Name, string Endpoint)>? currentSystems = null)
    {
        var idx = new UserTagDeviceIndex();

        var epById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var epByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name, endpoint) in currentSystems ?? [])
        {
            if (string.IsNullOrWhiteSpace(endpoint)) continue;
            var ep = endpoint.Trim();
            var gid = NormId(id);
            if (gid.Length > 0) epById[gid] = ep;
            if (!string.IsNullOrWhiteSpace(name)) epByName[name.Trim()] = ep;
        }

        foreach (var b in NormalizeUserTagDeviceBindings(bindings))
        {
            var ep = b.Endpoint;
            if (ep.Length == 0 && b.SystemId.Length > 0) epById.TryGetValue(b.SystemId, out ep!);
            if (string.IsNullOrEmpty(ep) && b.System.Length > 0) epByName.TryGetValue(b.System, out ep!);

            if (string.IsNullOrEmpty(ep)) { idx.DeadBindingCount++; continue; }
            idx.ByEndpoint[BindingKey(ep, b.TagAddress)] = b.Device;
        }

        return idx;
    }

    /// <summary>
    /// 한 알람의 귀속 디바이스를 찾는다 — 키는 (엔드포인트, 주소) 하나다.
    /// <para>
    /// 엔드포인트가 없는 알람(2026-09-22 이전에 쌓인 행)은 <b>잇지 않는다</b>. 그 시절 표식은
    /// 이름과 GUID 뿐인데 둘 다 그 뒤로 바뀌었고, 억지로 이으면 조용히 틀릴 수 있다.
    /// </para>
    /// 반환 <c>false</c> = 미지정(지표 제외), <c>true</c> + 빈 문자열 = 전역.
    /// </summary>
    public static bool TryGetBoundDevice(
        UserTagDeviceIndex? index, string? endpoint, string? tagAddress, out string device)
    {
        device = string.Empty;
        if (index is null || index.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(tagAddress)) return false;
        return index.ByEndpoint.TryGetValue(BindingKey(endpoint.Trim(), tagAddress), out device!);
    }
}
