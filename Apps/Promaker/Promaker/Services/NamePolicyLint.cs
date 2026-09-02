using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>열기 린트 결과 1건 — 배지 카운트/정리 다이얼로그 공용 행.</summary>
internal sealed class NamePolicyIssue
{
    public EntityKind Kind { get; init; }
    public Guid Id { get; init; }
    /// <summary>표시용 종류 라벨 — 활성 System 과 디바이스(Passive)를 구분해 보여준다.</summary>
    public string KindLabel { get; init; } = "";
    public string CurrentName { get; init; } = "";
    public string SuggestedName { get; init; } = "";
}

/// <summary>
/// 이름 정책 열기 린트 — 기존 모델(외부 생성 포함)의 정책 위반 이름을 찾는다.
/// 자동 변환은 절대 하지 않는다: 이름은 DSPilot 이력의 키라서, 조용한 개명은 사용자 모르게
/// 과거 이력을 단절시킨다. 발견 → 하단 상태줄 배지 → 사용자가 명시적으로 일괄 변환.
/// 검사 대상: 활성 System·Flow = 식별자('/' '\'), 디바이스(Passive) System·Work = 값(" / ").
/// Call 은 제외 — call 이름은 디바이스.Action(SSOT) 합성이라 디바이스 쪽 정리로 해소된다.
/// </summary>
internal static class NamePolicyLint
{
    /// <summary>활성(제어) System = 식별자 역할. 디바이스(Passive)는 값 역할 — 현장 어휘("D/L 지그") 보존.</summary>
    internal static bool IsIdentifierSystem(DsStore store, Guid systemId) =>
        ActiveSystemIds(store).Contains(systemId);

    private static HashSet<Guid> ActiveSystemIds(DsStore store) =>
        new(Queries.allProjects(store).SelectMany(p => p.ActiveSystemIds));

    internal static List<NamePolicyIssue> Scan(DsStore store)
    {
        var issues = new List<NamePolicyIssue>();
        var activeIds = ActiveSystemIds(store);

        foreach (var sys in store.SystemsReadOnly.Values)
        {
            var isIdentifier = activeIds.Contains(sys.Id);
            var suggested = isIdentifier
                ? NamePolicy.SanitizeIdentifier(sys.Name)
                : NamePolicy.SanitizeValue(sys.Name);
            if (suggested != sys.Name && suggested.Length > 0)
                issues.Add(new NamePolicyIssue
                {
                    Kind = EntityKind.System,
                    Id = sys.Id,
                    KindLabel = isIdentifier ? "System" : "디바이스",
                    CurrentName = sys.Name,
                    SuggestedName = suggested,
                });
        }

        foreach (var flow in store.FlowsReadOnly.Values)
        {
            var suggested = NamePolicy.SanitizeIdentifier(flow.Name);
            if (suggested != flow.Name && suggested.Length > 0)
                issues.Add(new NamePolicyIssue
                {
                    Kind = EntityKind.Flow,
                    Id = flow.Id,
                    KindLabel = "Flow",
                    CurrentName = flow.Name,
                    SuggestedName = suggested,
                });
        }

        foreach (var work in store.WorksReadOnly.Values)
        {
            var suggested = NamePolicy.SanitizeValue(work.LocalName);
            if (suggested != work.LocalName && suggested.Length > 0)
                issues.Add(new NamePolicyIssue
                {
                    Kind = EntityKind.Work,
                    Id = work.Id,
                    KindLabel = "Work",
                    CurrentName = work.LocalName,
                    SuggestedName = suggested,
                });
        }

        return issues;
    }

    /// <summary>
    /// 적용 직전 재계산 — 상위 개명(Flow rename → Work.FlowPrefix cascade)이 선행됐을 수 있으므로
    /// 다이얼로그에 표시했던 스냅샷 대신 현재 store 값으로 새 이름을 다시 도출한다.
    /// 반환 null = 이미 해결됨(건너뜀). Work 는 RenameEntitySmart 계약대로 full name 을 돌려준다.
    /// </summary>
    internal static string? RecomputeSuggested(DsStore store, EntityKind kind, Guid id)
    {
        switch (kind)
        {
            case EntityKind.System:
            {
                if (!store.SystemsReadOnly.TryGetValue(id, out var sys)) return null;
                var s = IsIdentifierSystem(store, id)
                    ? NamePolicy.SanitizeIdentifier(sys.Name)
                    : NamePolicy.SanitizeValue(sys.Name);
                return s != sys.Name && s.Length > 0 ? s : null;
            }
            case EntityKind.Flow:
            {
                if (!store.FlowsReadOnly.TryGetValue(id, out var flow)) return null;
                var s = NamePolicy.SanitizeIdentifier(flow.Name);
                return s != flow.Name && s.Length > 0 ? s : null;
            }
            case EntityKind.Work:
            {
                var fullOpt = Queries.tryGetWorkFullName(id, store);
                if (fullOpt == null) return null;
                var full = fullOpt.Value;
                var s = NamePolicy.SanitizeValue(full);
                return s != full && s.Length > 0 ? s : null;
            }
            default:
                return null;
        }
    }
}
