// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using DSPilot.Infrastructure;

namespace DSPilot.Services;

/// <summary>
/// 모델 System ↔ <c>plc</c> 행 귀속 재해석(순수 함수). (GUID, 이름) 이중 키.
/// <para>
/// 배경(2026-09-08 현장): 사용자가 AASX 에 flow 하나를 추가해 재업로드했는데 Promaker 가 System/Flow/Call GUID 를
/// 전부 새로 발급했다(이름은 동일). plc 행 확보가 <c>systemId</c> 만 보고 "없으니 새 행" 을 만들어 두 시스템의
/// plcTag 4,983행이 새로 생겼고, 이전 로그는 옛 GUID 행에 남아 <c>p.systemId = Flow.ParentId</c> 필터에서 전부
/// 걸러졌다 — 사용자에겐 "AASX 업데이트했더니 그동안의 기록이 없어짐" 으로 보였다.
/// </para>
/// <para>
/// 불변식:
/// ① GUID 가 일치하는 행이 있으면 그 행(이름이 바뀌어도 같은 System).
/// ② GUID 행이 없고, 현재 모델의 어느 System 에도 속하지 않은(고아) 행 중 <b>같은 이름</b>이 정확히 하나면
///    그 행을 새 GUID 로 재키잉한다 — plcTag id 가 보존되므로 plcTagLog 이력이 그대로 따라온다.
/// ③ 같은 이름의 고아가 둘 이상이면 모호 → 새 행(보고만). 이름까지 다르면 근거 없음 → 새 행.
/// ④ GUID 행과 같은 이름의 고아 행이 공존하면 "이력 분리" 상태 — 자동 병합은 하지 않고 보고만(사용자 결정).
/// </para>
/// 이름 비교는 <see cref="BaseName"/> — 행 생성 시 UNIQUE 회피로 붙인 <c>#guid8</c> 접미를 벗긴다.
/// </summary>
public static class PlcOwnerReconciler
{
    public readonly record struct ModelSystem(Guid Id, string Name);
    public readonly record struct PlcRow(int Id, string? SystemId, string Name);

    public enum DecisionKind { Existing, Rekey, Create }

    /// <param name="PlcId">Existing/Rekey 일 때 대상 plc 행 id. Create 는 null.</param>
    /// <param name="OldSystemKey">Rekey 일 때 덮어쓰기 전 systemId 키.</param>
    public readonly record struct Decision(Guid SystemId, string SystemName, DecisionKind Kind, int? PlcId, string? OldSystemKey);

    public sealed class Report
    {
        public List<Decision> Decisions { get; } = new();
        /// <summary>사람이 읽는 경고(모호·분리 상태). 비어 있으면 특이사항 없음.</summary>
        public List<string> Warnings { get; } = new();
        public IEnumerable<Decision> Rekeys => Decisions.Where(d => d.Kind == DecisionKind.Rekey);
    }

    /// <summary>기본 행 id(귀속 미상 버킷). 재키잉 후보에서 제외.</summary>
    public const int DefaultPlcId = 1;

    /// <summary>
    /// 행 이름에서 UNIQUE 회피 접미(<c>#</c> + 8 hex)를 벗긴다. 접미가 없으면 원문.
    /// </summary>
    public static string BaseName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var idx = name.LastIndexOf('#');
        if (idx <= 0 || name.Length - idx - 1 != 8) return name;
        for (var i = idx + 1; i < name.Length; i++)
            if (!Uri.IsHexDigit(name[i])) return name;
        return name[..idx];
    }

    public static Report Reconcile(IReadOnlyCollection<ModelSystem> systems, IReadOnlyCollection<PlcRow> rows)
    {
        var report = new Report();
        if (systems.Count == 0) return report;

        var modelKeys = new HashSet<string>(
            systems.Select(s => SystemKeyConvention.Key(s.Id)).Where(k => k.Length > 0));

        var rowByKey = new Dictionary<string, PlcRow>();
        foreach (var r in rows)
        {
            var k = SystemKeyConvention.Key(r.SystemId);
            if (k.Length > 0 && !rowByKey.ContainsKey(k)) rowByKey[k] = r;
        }

        // 고아 = systemId 가 있고(기본 행 제외) 현재 모델의 어느 System 도 아닌 행.
        var orphansByBaseName = rows
            .Where(r => r.Id != DefaultPlcId)
            .Where(r => { var k = SystemKeyConvention.Key(r.SystemId); return k.Length > 0 && !modelKeys.Contains(k); })
            .GroupBy(r => BaseName(r.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var claimed = new HashSet<int>();
        foreach (var s in systems)
        {
            var key = SystemKeyConvention.Key(s.Id);
            if (key.Length == 0) continue;
            var name = s.Name ?? string.Empty;

            orphansByBaseName.TryGetValue(BaseName(name), out var sameName);
            var candidates = sameName?.Where(r => !claimed.Contains(r.Id)).ToList() ?? new List<PlcRow>();

            if (rowByKey.TryGetValue(key, out var existing))
            {
                report.Decisions.Add(new Decision(s.Id, name, DecisionKind.Existing, existing.Id, null));
                if (candidates.Count > 0)
                    report.Warnings.Add(
                        $"System '{name}' 은 GUID 일치 행(id={existing.Id}) 과 같은 이름의 고아 행" +
                        $"({string.Join(", ", candidates.Select(c => $"id={c.Id} systemId={SystemKeyConvention.Key(c.SystemId)}"))}) 이 공존 — " +
                        "이력이 두 행에 분리되어 있습니다(자동 병합 안 함).");
                continue;
            }

            if (candidates.Count == 1)
            {
                var target = candidates[0];
                claimed.Add(target.Id);
                report.Decisions.Add(new Decision(s.Id, name, DecisionKind.Rekey, target.Id, SystemKeyConvention.Key(target.SystemId)));
                continue;
            }

            if (candidates.Count > 1)
                report.Warnings.Add(
                    $"System '{name}' 의 GUID({key}) 행이 없고 같은 이름의 고아 행이 {candidates.Count}개" +
                    $"({string.Join(", ", candidates.Select(c => $"id={c.Id}"))}) — 모호하여 재키잉하지 않고 새 행을 만듭니다.");
            report.Decisions.Add(new Decision(s.Id, name, DecisionKind.Create, null, null));
        }

        return report;
    }
}
