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
/// ②' GUID 행이 없고 <b>같은 엔드포인트</b>의 고아 행이 정확히 하나면 재키잉 — <b>가장 강한 근거</b>다.
/// ② 엔드포인트로 못 찾으면 <b>같은 이름</b>의 고아 행이 정확히 하나일 때 재키잉.
///    재키잉하면 plcTag id 가 보존되므로 plcTagLog 이력이 그대로 따라온다.
/// ③ 후보가 둘 이상이면 모호 → 새 행(보고만). 근거가 하나도 없으면 새 행.
/// ④ GUID 행과 같은 이름의 고아 행이 공존하면 "이력 분리" 상태 — 자동 병합은 하지 않고 보고만(사용자 결정).
/// </para>
/// <para>
/// ②' 를 2026-09-22 에 더했다. 종전엔 이름이 마지막 끈이었는데, 현장에서 AASX 를 갈면서 System 이름까지
/// 정리해(<c>ub1_#121_#134</c> → <c>UB_#121_#134</c>) 그 끈이 끊어졌다. 엔드포인트(PLC ip:port)는 물리
/// 접속이라 이름·GUID 가 <b>동시에</b> 바뀌어도 남는다 — 신호의 정체를 (엔드포인트, 주소)로 잡은 doc/31 §6
/// 과 같은 원리다. 엔드포인트까지 바뀐 경우는 진짜 설비 교체로 보고 잇지 않는다.
/// </para>
/// 이름 비교는 <see cref="BaseName"/> — 행 생성 시 UNIQUE 회피로 붙인 <c>#guid8</c> 접미를 벗긴다.
/// ★대소문자는 무시한다 — 대소문자만 바꾼 리네임(<c>ub1_x</c> → <c>UB1_x</c>)은 사용자 눈에 "안 바꿈" 인데
/// 종전 <c>Ordinal</c> 비교는 조용히 실패했다.
/// </summary>
public static class PlcOwnerReconciler
{
    /// <param name="Endpoint">그 System 의 PLC 엔드포인트(ip:port). 모르면 빈 문자열 — ②' 를 건너뛴다.</param>
    public readonly record struct ModelSystem(Guid Id, string Name, string Endpoint = "");

    /// <param name="Endpoint">행에 박제된 엔드포인트. 이 칸이 비어 있던 시절의 행은 빈 문자열이다.</param>
    public readonly record struct PlcRow(int Id, string? SystemId, string Name, string Endpoint = "");

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
        var orphans = rows
            .Where(r => r.Id != DefaultPlcId)
            .Where(r => { var k = SystemKeyConvention.Key(r.SystemId); return k.Length > 0 && !modelKeys.Contains(k); })
            .ToList();

        // ★대소문자 무시 — 대소문자만 바꾼 리네임은 사용자에게 "안 바꿈" 인데 Ordinal 은 조용히 실패한다.
        var orphansByBaseName = orphans
            .GroupBy(r => BaseName(r.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var orphansByEndpoint = orphans
            .Where(r => !string.IsNullOrWhiteSpace(r.Endpoint))
            .GroupBy(r => r.Endpoint.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var claimed = new HashSet<int>();
        foreach (var s in systems)
        {
            var key = SystemKeyConvention.Key(s.Id);
            if (key.Length == 0) continue;
            var name = s.Name ?? string.Empty;

            orphansByBaseName.TryGetValue(BaseName(name), out var sameName);
            var candidates = sameName?.Where(r => !claimed.Contains(r.Id)).ToList() ?? new List<PlcRow>();

            // ②' 엔드포인트가 이름보다 강한 근거다 — 이름·GUID 가 동시에 바뀌어도 남는다.
            var byEp = new List<PlcRow>();
            if (!string.IsNullOrWhiteSpace(s.Endpoint)
                && orphansByEndpoint.TryGetValue(s.Endpoint.Trim(), out var sameEp))
                byEp = sameEp.Where(r => !claimed.Contains(r.Id)).ToList();

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

            if (byEp.Count == 1)
            {
                var target = byEp[0];
                claimed.Add(target.Id);
                report.Decisions.Add(new Decision(s.Id, name, DecisionKind.Rekey, target.Id, SystemKeyConvention.Key(target.SystemId)));
                if (!string.Equals(BaseName(target.Name), BaseName(name), StringComparison.OrdinalIgnoreCase))
                    report.Warnings.Add(
                        $"System '{name}' 은 이름도 GUID 도 바뀌었지만 엔드포인트({s.Endpoint})가 같아 " +
                        $"행 id={target.Id}(이름 '{target.Name}') 을 재키잉합니다 — 이력을 승계합니다.");
                continue;
            }

            if (byEp.Count > 1)
                report.Warnings.Add(
                    $"System '{name}' 의 엔드포인트({s.Endpoint}) 고아 행이 {byEp.Count}개 — 모호하여 이름으로 넘어갑니다.");

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
