// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// 한 flow 의 Call 조회표(GUID ↔ 이름). 이름 비교는 대소문자 무시(설정 저장 경로의 Distinct/검증과 동일).
/// </summary>
public sealed class FlowCallLookup
{
    private readonly Dictionary<Guid, string> _nameById;
    private readonly Dictionary<string, Guid> _idByName;

    private FlowCallLookup(Dictionary<Guid, string> nameById, Dictionary<string, Guid> idByName)
    {
        _nameById = nameById;
        _idByName = idByName;
    }

    public static FlowCallLookup From(IEnumerable<(Guid Id, string Name)> calls)
    {
        var byId = new Dictionary<Guid, string>();
        var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in calls)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(name)) continue;
            byId[id] = name;
            // 같은 flow 안 동명 call(Reference call 등) — 첫 GUID 유지. 이름→GUID 는 어차피 모호하므로 첫 것으로 고정.
            byName.TryAdd(name.Trim(), id);
        }
        return new FlowCallLookup(byId, byName);
    }

    public int Count => _nameById.Count;

    public bool TryGetName(string? idText, out string name)
    {
        name = "";
        if (!Guid.TryParse(idText, out var id)) return false;
        return _nameById.TryGetValue(id, out name!);
    }

    public bool TryGetId(string? name, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _idByName.TryGetValue(name.Trim(), out id);
    }

    public bool ContainsName(string? name) => TryGetId(name, out _);
}

public enum CallRefRole { BranchStart, BranchEnd, BranchExcluded, OverrideStart, OverrideEnd }

/// <summary>GUID 로 같은 Call 을 찾았는데 이름이 달라 스냅샷을 새 이름으로 갱신한 항목.</summary>
public sealed record CallRefRename(string FlowName, string? BranchName, CallRefRole Role, string OldName, string NewName);

/// <summary>GUID 도 이름도 현재 모델에 없는 항목 — 삭제하지 않고 보고만 한다(사용자가 화면에서 제거/재지정).</summary>
public sealed record CallRefGhost(string FlowName, string? BranchName, CallRefRole Role, string Name);

public sealed class CallRefReconcileReport
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
    public List<CallRefRename> Renamed { get; } = [];
    public List<CallRefGhost> Ghosts { get; } = [];
    /// <summary>GUID 가 없던(구 데이터) 참조에 이름으로 찾은 GUID 를 채운 개수 — 저장이 필요한 변경.</summary>
    public int FilledIds { get; set; }
    public bool Changed => Renamed.Count > 0 || FilledIds > 0;
}

/// <summary>
/// 분기 정의·flow 경계 override 의 Call 참조를 현재 모델과 맞추는 순수 함수 모음(2026-09-08).
/// <para>
/// 규약: 참조 = (GUID, 이름) 이중 키. 해석 순서 ① GUID 일치 → 이름이 다르면 이름 스냅샷 갱신(리네임 추종)
/// ② GUID 없음/미존재 → 이름 정확 일치로 GUID 채움(구 데이터·재생성·프로젝트 간 복사) ③ 둘 다 실패 → 유령(보고만).
/// 유령을 여기서 지우지 않는 이유: 정의는 사용자 자산이고, 잘못 지우면 반증 규칙이 조용히 약해진다 —
/// 화면이 "모델에 없는 call n개" 로 보여주고 사용자가 제거/재지정한다.
/// </para>
/// <para>
/// 배경: 2026-09-07 현장에서 사용자가 head/tail 표식용 접두어(`[H] `/`[T] `)만 뗀 Call 이름으로 AASX 를 재업로드
/// → 이름 스냅샷 48종이 유령이 되어 9 flow 전부 분기 저장이 거절되고 셔틀/#121/#131 은 재계산이 건너뛰어졌다.
/// 런타임(재도출·라이브·화면)은 계속 이름을 읽는다 — GUID 는 재로드 때 이름을 따라잡는 용도다.
/// </para>
/// </summary>
public static class CallRefReconciler
{
    public static string IdText(Guid id) => id.ToString("D");

    /// <summary>
    /// 참조 하나를 해석. 반환 = 이름/GUID 가 바뀌었는지. <paramref name="ghost"/>=true 면 미해석(값 불변).
    /// </summary>
    private static bool ResolveOne(
        FlowCallLookup lookup, ref string? idText, ref string name,
        out string? renamedFrom, out bool ghost)
    {
        renamedFrom = null; ghost = false;
        if (lookup.TryGetName(idText, out var current))
        {
            if (!string.Equals(current, name, StringComparison.Ordinal))
            {
                renamedFrom = name;
                name = current;
                return true;
            }
            return false;
        }
        if (lookup.TryGetId(name, out var id))
        {
            var text = IdText(id);
            if (!string.Equals(text, idText, StringComparison.OrdinalIgnoreCase))
            {
                idText = text;
                name = name.Trim();
                return true;
            }
            return false;
        }
        ghost = true;
        return false;
    }

    public static void ReconcileBranchSet(FlowBranchSet set, FlowCallLookup lookup, CallRefReconcileReport report)
    {
        foreach (var b in set.Branches)
        {
            // Head/Tail
            var startId = b.StartCallId; var startName = b.StartCallName ?? "";
            if (ResolveOne(lookup, ref startId, ref startName, out var from, out var ghost))
            {
                if (from is not null) report.Renamed.Add(new(set.FlowName, b.Name, CallRefRole.BranchStart, from, startName));
                else report.FilledIds++;
                b.StartCallId = startId; b.StartCallName = startName;
            }
            else if (ghost) report.Ghosts.Add(new(set.FlowName, b.Name, CallRefRole.BranchStart, startName));

            var endId = b.EndCallId; var endName = b.EndCallName ?? "";
            if (ResolveOne(lookup, ref endId, ref endName, out from, out ghost))
            {
                if (from is not null) report.Renamed.Add(new(set.FlowName, b.Name, CallRefRole.BranchEnd, from, endName));
                else report.FilledIds++;
                b.EndCallId = endId; b.EndCallName = endName;
            }
            else if (ghost) report.Ghosts.Add(new(set.FlowName, b.Name, CallRefRole.BranchEnd, endName));

            // 제외 목록 — 이름/GUID 병렬 리스트. 길이가 어긋나면(외부 편집·구 데이터) GUID 를 버리고 이름으로 재해석.
            var names = b.ExcludedCallNames ?? [];
            var ids = b.ExcludedCallIds is { } l && l.Count == names.Count
                ? l
                : Enumerable.Repeat<string?>(null, names.Count).ToList();
            var outNames = new List<string>(names.Count);
            var outIds = new List<string?>(names.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = b.ExcludedCallIds is null || b.ExcludedCallIds.Count != names.Count;
            for (var i = 0; i < names.Count; i++)
            {
                var id = ids[i]; var nm = names[i] ?? "";
                if (ResolveOne(lookup, ref id, ref nm, out from, out ghost))
                {
                    changed = true;
                    if (from is not null) report.Renamed.Add(new(set.FlowName, b.Name, CallRefRole.BranchExcluded, from, nm));
                    else report.FilledIds++;
                }
                else if (ghost) report.Ghosts.Add(new(set.FlowName, b.Name, CallRefRole.BranchExcluded, nm));
                // 리네임 추종으로 같은 이름이 둘이 되면(옛 이름 + 새 이름 공존) 첫 것만 남긴다.
                if (!seen.Add(nm)) { changed = true; continue; }
                outNames.Add(nm); outIds.Add(id);
            }
            if (changed)
            {
                b.ExcludedCallNames = outNames;
                b.ExcludedCallIds = outIds;
            }
        }
    }

    public static void ReconcileOverride(FlowCycleOverride ov, FlowCallLookup lookup, CallRefReconcileReport report)
    {
        if (!string.IsNullOrWhiteSpace(ov.StartCallName))
        {
            var id = ov.StartCallId; var nm = ov.StartCallName!;
            if (ResolveOne(lookup, ref id, ref nm, out var from, out var ghost))
            {
                if (from is not null) report.Renamed.Add(new(ov.FlowName, null, CallRefRole.OverrideStart, from, nm));
                else report.FilledIds++;
                ov.StartCallId = id; ov.StartCallName = nm;
            }
            else if (ghost) report.Ghosts.Add(new(ov.FlowName, null, CallRefRole.OverrideStart, nm));
        }
        if (!string.IsNullOrWhiteSpace(ov.EndCallName))
        {
            var id = ov.EndCallId; var nm = ov.EndCallName!;
            if (ResolveOne(lookup, ref id, ref nm, out var from, out var ghost))
            {
                if (from is not null) report.Renamed.Add(new(ov.FlowName, null, CallRefRole.OverrideEnd, from, nm));
                else report.FilledIds++;
                ov.EndCallId = id; ov.EndCallName = nm;
            }
            else if (ghost) report.Ghosts.Add(new(ov.FlowName, null, CallRefRole.OverrideEnd, nm));
        }
    }

    /// <summary>저장 경로: 이름(검증 완료)에서 GUID 를 채운다. 이름이 조회표에 없으면 GUID 는 null 로 둔다.</summary>
    public static void StampIds(FlowBranchDef def, FlowCallLookup lookup)
    {
        def.StartCallId = lookup.TryGetId(def.StartCallName, out var s) ? IdText(s) : null;
        def.EndCallId = lookup.TryGetId(def.EndCallName, out var e) ? IdText(e) : null;
        def.ExcludedCallIds = (def.ExcludedCallNames ?? [])
            .Select(n => lookup.TryGetId(n, out var id) ? IdText(id) : null)
            .ToList();
    }

    /// <summary>화면/응답용 — 이 분기 참조 중 현재 모델에 없는 이름(시작·끝·제외 순, 중복 제거).</summary>
    public static List<string> UnknownNames(FlowBranchDef def, FlowCallLookup lookup)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(string? n)
        {
            if (string.IsNullOrWhiteSpace(n) || lookup.ContainsName(n) || !seen.Add(n)) return;
            result.Add(n);
        }
        Check(def.StartCallName);
        Check(def.EndCallName);
        foreach (var n in def.ExcludedCallNames ?? []) Check(n);
        return result;
    }
}
