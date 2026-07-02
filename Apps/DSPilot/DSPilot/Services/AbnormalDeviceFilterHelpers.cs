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
    /// <summary>이상감지 4종의 (int 값, enum 이름, 한글 라벨) — UI 체크박스/라벨의 단일 소스.</summary>
    public static readonly IReadOnlyList<(int Kind, string Name, string Label)> KindOptions =
    [
        ((int)AbnormalKind.SensorOpen,  nameof(AbnormalKind.SensorOpen),  LabelOf(AbnormalKind.SensorOpen)),
        ((int)AbnormalKind.SensorShort, nameof(AbnormalKind.SensorShort), LabelOf(AbnormalKind.SensorShort)),
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
    /// 저장 입력 정규화: 디바이스 trim·빈값 제거, 같은 디바이스 규칙 병합, kind 는 알려진 4종으로 한정·중복 제거,
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
}
