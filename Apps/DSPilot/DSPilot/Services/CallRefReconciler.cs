// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// Call 의 배선 지문 — 그 Call 의 ApiCall 들이 쓰는 IN/OUT PLC 주소를 정렬해 이은 문자열(2026-09-11).
/// <para>
/// 이름은 사람이 수시로 바꾸고 GUID 는 복사·재생성·임포트에서 통째로 갈리지만, 주소는 PLC 프로그램을 실제로 고쳐야
/// 바뀐다. DSPilot 이 실제로 기록·판정하는 대상도 이 주소다. 그래서 이름·GUID 가 모두 어긋난 참조를 되찾는
/// 마지막 근거로 쓴다 — 단 공용 디바이스(같은 주소를 여러 call 이 참조)·미결선(주소 공백)이 있어 "정확히 하나"
/// 일 때만 자동 확정한다(<see cref="FlowCallLookup.TryGetIdBySig"/>).
/// </para>
/// 형식: <c>I:주소;O:주소;…</c> (ordinal 정렬, 중복 제거). 주소가 하나도 없으면 null.
/// </summary>
public static class CallSignature
{
    public static string? Build(IEnumerable<(string? In, string? Out)> pairs)
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (i, o) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(i)) parts.Add("I:" + i.Trim());
            if (!string.IsNullOrWhiteSpace(o)) parts.Add("O:" + o.Trim());
        }
        if (parts.Count == 0) return null;
        var list = parts.ToList();
        list.Sort(StringComparer.Ordinal);
        return string.Join(";", list);
    }
}

/// <summary>
/// 한 flow 의 Call 조회표(GUID ↔ 이름 ↔ 배선 지문). 이름 비교는 대소문자 무시(설정 저장 경로의 Distinct/검증과 동일).
/// </summary>
public sealed class FlowCallLookup
{
    private readonly Dictionary<Guid, string> _nameById;
    private readonly Dictionary<string, Guid> _idByName;
    private readonly Dictionary<Guid, string> _sigById;
    private readonly Dictionary<string, List<Guid>> _idsBySig;

    private FlowCallLookup(
        Dictionary<Guid, string> nameById, Dictionary<string, Guid> idByName,
        Dictionary<Guid, string> sigById, Dictionary<string, List<Guid>> idsBySig)
    {
        _nameById = nameById;
        _idByName = idByName;
        _sigById = sigById;
        _idsBySig = idsBySig;
    }

    /// <summary>지문 없는 조회표(테스트/구 호출부 호환).</summary>
    public static FlowCallLookup From(IEnumerable<(Guid Id, string Name)> calls)
        => From(calls.Select(c => (c.Id, c.Name, (string?)null)));

    public static FlowCallLookup From(IEnumerable<(Guid Id, string Name, string? Sig)> calls)
    {
        var byId = new Dictionary<Guid, string>();
        var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var sigById = new Dictionary<Guid, string>();
        var idsBySig = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var (id, name, sig) in calls)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(name)) continue;
            byId[id] = name;
            // 같은 flow 안 동명 call(Reference call 등) — 첫 GUID 유지. 이름→GUID 는 어차피 모호하므로 첫 것으로 고정.
            byName.TryAdd(name.Trim(), id);
            if (!string.IsNullOrWhiteSpace(sig))
            {
                sigById[id] = sig;
                if (!idsBySig.TryGetValue(sig, out var list)) idsBySig[sig] = list = [];
                if (!list.Contains(id)) list.Add(id);
            }
        }
        return new FlowCallLookup(byId, byName, sigById, idsBySig);
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

    /// <summary>현재 모델에서 이 call 의 배선 지문. 미결선/미등록이면 null.</summary>
    public string? SigOf(Guid id) => _sigById.TryGetValue(id, out var s) ? s : null;

    /// <summary>지문이 현재 모델의 <b>정확히 한</b> call 과 일치할 때만 true. 여럿(공용 주소)이면 모호 → false.</summary>
    public bool TryGetIdBySig(string? sig, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(sig)) return false;
        if (!_idsBySig.TryGetValue(sig, out var list) || list.Count != 1) return false;
        id = list[0];
        return true;
    }

    /// <summary>이 지문을 가진 call 수(0=없음, 1=유일, 2+=모호).</summary>
    public int SigCandidates(string? sig)
        => !string.IsNullOrWhiteSpace(sig) && _idsBySig.TryGetValue(sig, out var list) ? list.Count : 0;
}

public enum CallRefRole { BranchStart, BranchEnd, BranchExcluded, OverrideStart, OverrideEnd }

/// <summary>GUID 로 같은 Call 을 찾았는데 이름이 달라 스냅샷을 새 이름으로 갱신한 항목(또는 지문으로 되찾아 이름·GUID 를 갱신한 항목).</summary>
public sealed record CallRefRename(string FlowName, string? BranchName, CallRefRole Role, string OldName, string NewName);

/// <summary>GUID·이름·지문 어느 것으로도 현재 모델에서 찾지 못한 항목 — 삭제하지 않고 보고만 한다(사용자가 화면에서 제거/재지정).</summary>
public sealed record CallRefGhost(string FlowName, string? BranchName, CallRefRole Role, string Name);

public sealed class CallRefReconcileReport
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
    /// <summary>GUID 일치 → 이름 갱신(리네임 추종).</summary>
    public List<CallRefRename> Renamed { get; } = [];
    /// <summary>GUID·이름 모두 없음 → 배선 지문이 유일 일치 → 이름·GUID 갱신(리네임 + GUID 재발급 추종).</summary>
    public List<CallRefRename> Rematched { get; } = [];
    public List<CallRefGhost> Ghosts { get; } = [];
    /// <summary>GUID 가 없던(구 데이터) 참조에 이름으로 찾은 GUID 를 채운 개수 — 저장이 필요한 변경.</summary>
    public int FilledIds { get; set; }
    /// <summary>지문이 없던 참조에 현재 모델의 지문을 채운 개수(백필).</summary>
    public int FilledSigs { get; set; }
    /// <summary>지문이 있었는데 모델의 지문이 달라 갱신한 개수(배선 변경 추종 — 이름·GUID 는 그대로).</summary>
    public int Rewired { get; set; }
    public bool Changed => Renamed.Count > 0 || Rematched.Count > 0 || FilledIds > 0 || FilledSigs > 0 || Rewired > 0;
}

/// <summary>
/// 분기 정의·flow 경계 override 의 Call 참조를 현재 모델과 맞추는 순수 함수 모음(2026-09-08, 지문 2026-09-11).
/// <para>
/// 규약: 참조 = (GUID, 이름, 배선 지문) 다중 키. 해석 순서
/// ① GUID 일치 → 이름이 다르면 이름 스냅샷 갱신(리네임 추종)
/// ② GUID 없음/미존재 → 이름 정확 일치로 GUID 채움(구 데이터·재생성·프로젝트 간 복사)
/// ③ 둘 다 없음 → 지문이 모델의 정확히 한 call 과 일치하면 그 call(리네임 + GUID 재발급 추종)
/// ④ 전부 실패 → 유령(보고만).
/// 어느 경로로 찾았든 마지막에 지문을 모델 값으로 동기화한다(백필/배선 변경 추종) — 다음 사고의 근거를 항상 최신으로.
/// 유령을 여기서 지우지 않는 이유: 정의는 사용자 자산이고, 잘못 지우면 반증 규칙이 조용히 약해진다 —
/// 화면이 "모델에 없는 call n개" 로 보여주고 사용자가 제거/재지정한다. 남겨 두면 원래 AASX 로 되돌렸을 때 그대로 다시 유효해진다.
/// </para>
/// <para>
/// 배경: 2026-09-07 현장에서 사용자가 head/tail 표식용 접두어(`[H] `/`[T] `)만 뗀 Call 이름으로 AASX 를 재업로드
/// → 이름 스냅샷 48종이 유령이 되어 9 flow 전부 분기 저장이 거절되고 셔틀/#121/#131 은 재계산이 건너뛰어졌다.
/// 2026-09-10 에는 새 AASX 시험 후 되돌리기로 6건이 고립됐다(GUID·이름 모두 사라진 call).
/// 런타임(재도출·라이브·화면)은 계속 이름을 읽는다 — GUID·지문은 재로드 때 이름을 따라잡는 용도다.
/// </para>
/// </summary>
public static class CallRefReconciler
{
    public static string IdText(Guid id) => id.ToString("D");

    /// <summary>
    /// 참조 하나를 해석. 반환 = 값(이름/GUID/지문)이 바뀌었는지. <paramref name="ghost"/>=true 면 미해석(값 불변).
    /// </summary>
    private static bool ResolveOne(
        FlowCallLookup lookup, ref string? idText, ref string name, ref string? sig,
        out string? renamedFrom, out string? rematchedFrom, out bool filledId, out bool filledSig, out bool rewired, out bool ghost)
    {
        renamedFrom = null; rematchedFrom = null; filledId = false; filledSig = false; rewired = false; ghost = false;
        var changed = false;
        Guid id;

        if (Guid.TryParse(idText, out var parsed) && lookup.TryGetName(idText, out var current))
        {
            id = parsed;
            if (!string.Equals(current, name, StringComparison.Ordinal))
            {
                renamedFrom = name;
                name = current;
                changed = true;
            }
        }
        else if (lookup.TryGetId(name, out id))
        {
            var text = IdText(id);
            if (!string.Equals(text, idText, StringComparison.OrdinalIgnoreCase))
            {
                idText = text;
                name = name.Trim();
                filledId = true;
                changed = true;
            }
        }
        else if (lookup.TryGetIdBySig(sig, out id) && lookup.TryGetName(IdText(id), out var bySig))
        {
            // 이름·GUID 모두 모델에 없지만 배선 지문이 정확히 한 call 과 일치 — 리네임 + GUID 재발급을 함께 추종.
            rematchedFrom = name;
            name = bySig;
            idText = IdText(id);
            changed = true;
        }
        else
        {
            ghost = true;
            return false;
        }

        // 지문 동기화 — 모델에 지문이 있으면 스냅샷을 최신으로. 모델이 미결선(null)이면 기존 값을 남긴다(다음 재결선 때 근거).
        var modelSig = lookup.SigOf(id);
        if (modelSig is not null && !string.Equals(modelSig, sig, StringComparison.Ordinal))
        {
            if (sig is null) filledSig = true; else rewired = true;
            sig = modelSig;
            changed = true;
        }
        return changed;
    }

    private static void Tally(CallRefReconcileReport report, string flow, string? branch, CallRefRole role,
        string? renamedFrom, string? rematchedFrom, bool filledId, bool filledSig, bool rewired, string newName)
    {
        if (renamedFrom is not null) report.Renamed.Add(new(flow, branch, role, renamedFrom, newName));
        if (rematchedFrom is not null) report.Rematched.Add(new(flow, branch, role, rematchedFrom, newName));
        if (filledId) report.FilledIds++;
        if (filledSig) report.FilledSigs++;
        if (rewired) report.Rewired++;
    }

    public static void ReconcileBranchSet(FlowBranchSet set, FlowCallLookup lookup, CallRefReconcileReport report)
    {
        foreach (var b in set.Branches)
        {
            // Head/Tail
            var startId = b.StartCallId; var startName = b.StartCallName ?? ""; var startSig = b.StartCallSig;
            if (ResolveOne(lookup, ref startId, ref startName, ref startSig, out var from, out var rem, out var fid, out var fsig, out var rew, out var ghost))
            {
                Tally(report, set.FlowName, b.Name, CallRefRole.BranchStart, from, rem, fid, fsig, rew, startName);
                b.StartCallId = startId; b.StartCallName = startName; b.StartCallSig = startSig;
            }
            else if (ghost) report.Ghosts.Add(new(set.FlowName, b.Name, CallRefRole.BranchStart, startName));

            var endId = b.EndCallId; var endName = b.EndCallName ?? ""; var endSig = b.EndCallSig;
            if (ResolveOne(lookup, ref endId, ref endName, ref endSig, out from, out rem, out fid, out fsig, out rew, out ghost))
            {
                Tally(report, set.FlowName, b.Name, CallRefRole.BranchEnd, from, rem, fid, fsig, rew, endName);
                b.EndCallId = endId; b.EndCallName = endName; b.EndCallSig = endSig;
            }
            else if (ghost) report.Ghosts.Add(new(set.FlowName, b.Name, CallRefRole.BranchEnd, endName));

            // 제외 목록 — 이름/GUID/지문 병렬 리스트. 길이가 어긋나면(외부 편집·구 데이터) 그 키를 버리고 나머지로 재해석.
            var names = b.ExcludedCallNames ?? [];
            var ids = b.ExcludedCallIds is { } l && l.Count == names.Count
                ? l
                : Enumerable.Repeat<string?>(null, names.Count).ToList();
            var sigs = b.ExcludedCallSigs is { } sl && sl.Count == names.Count
                ? sl
                : Enumerable.Repeat<string?>(null, names.Count).ToList();
            var outNames = new List<string>(names.Count);
            var outIds = new List<string?>(names.Count);
            var outSigs = new List<string?>(names.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = b.ExcludedCallIds is null || b.ExcludedCallIds.Count != names.Count;
            var sigListChanged = b.ExcludedCallSigs is not null && b.ExcludedCallSigs.Count != names.Count;
            for (var i = 0; i < names.Count; i++)
            {
                var id = ids[i]; var nm = names[i] ?? ""; var sg = sigs[i];
                if (ResolveOne(lookup, ref id, ref nm, ref sg, out from, out rem, out fid, out fsig, out rew, out ghost))
                {
                    changed = true;
                    if (fsig || rew) sigListChanged = true;
                    Tally(report, set.FlowName, b.Name, CallRefRole.BranchExcluded, from, rem, fid, fsig, rew, nm);
                }
                else if (ghost) report.Ghosts.Add(new(set.FlowName, b.Name, CallRefRole.BranchExcluded, nm));
                // 리네임 추종으로 같은 이름이 둘이 되면(옛 이름 + 새 이름 공존) 첫 것만 남긴다.
                if (!seen.Add(nm)) { changed = true; continue; }
                outNames.Add(nm); outIds.Add(id); outSigs.Add(sg);
            }
            if (changed)
            {
                b.ExcludedCallNames = outNames;
                b.ExcludedCallIds = outIds;
                // 지문 리스트는 값이 하나라도 생겼거나 기존 리스트가 있을 때만 기록(지문 없는 구 데이터를 무의미한 null 리스트로 덮지 않음).
                if (sigListChanged || b.ExcludedCallSigs is not null || outSigs.Any(s => s is not null))
                    b.ExcludedCallSigs = outSigs;
            }
            else if (sigListChanged)
            {
                b.ExcludedCallSigs = outSigs;
            }
        }
    }

    public static void ReconcileOverride(FlowCycleOverride ov, FlowCallLookup lookup, CallRefReconcileReport report)
    {
        if (!string.IsNullOrWhiteSpace(ov.StartCallName))
        {
            var id = ov.StartCallId; var nm = ov.StartCallName!; var sg = ov.StartCallSig;
            if (ResolveOne(lookup, ref id, ref nm, ref sg, out var from, out var rem, out var fid, out var fsig, out var rew, out var ghost))
            {
                Tally(report, ov.FlowName, null, CallRefRole.OverrideStart, from, rem, fid, fsig, rew, nm);
                ov.StartCallId = id; ov.StartCallName = nm; ov.StartCallSig = sg;
            }
            else if (ghost) report.Ghosts.Add(new(ov.FlowName, null, CallRefRole.OverrideStart, nm));
        }
        if (!string.IsNullOrWhiteSpace(ov.EndCallName))
        {
            var id = ov.EndCallId; var nm = ov.EndCallName!; var sg = ov.EndCallSig;
            if (ResolveOne(lookup, ref id, ref nm, ref sg, out var from, out var rem, out var fid, out var fsig, out var rew, out var ghost))
            {
                Tally(report, ov.FlowName, null, CallRefRole.OverrideEnd, from, rem, fid, fsig, rew, nm);
                ov.EndCallId = id; ov.EndCallName = nm; ov.EndCallSig = sg;
            }
            else if (ghost) report.Ghosts.Add(new(ov.FlowName, null, CallRefRole.OverrideEnd, nm));
        }
    }

    /// <summary>저장 경로: 이름에서 GUID·지문을 채운다. 이름이 조회표에 없으면 둘 다 null 로 둔다(<see cref="InheritUnresolved"/> 가 이전 저장분에서 승계).</summary>
    public static void StampIds(FlowBranchDef def, FlowCallLookup lookup)
    {
        def.StartCallId = lookup.TryGetId(def.StartCallName, out var s) ? IdText(s) : null;
        def.StartCallSig = s == Guid.Empty ? null : lookup.SigOf(s);
        def.EndCallId = lookup.TryGetId(def.EndCallName, out var e) ? IdText(e) : null;
        def.EndCallSig = e == Guid.Empty ? null : lookup.SigOf(e);
        var names = def.ExcludedCallNames ?? [];
        def.ExcludedCallIds = names.Select(n => lookup.TryGetId(n, out var id) ? IdText(id) : null).ToList();
        def.ExcludedCallSigs = names.Select(n => lookup.TryGetId(n, out var id) ? lookup.SigOf(id) : null).ToList();
    }

    /// <summary>
    /// 저장 경로(2026-09-11): 현재 모델에 없어 <see cref="StampIds"/> 가 비워 둔 참조에, 이전 저장분(같은 flow 의 어느 분기든)이
    /// 같은 이름으로 들고 있던 GUID·지문을 승계한다. 유령을 격리 보존하되 "되돌리기/배선 재매칭" 의 근거는 잃지 않기 위한 것.
    /// 해석된 참조는 건드리지 않는다.
    /// </summary>
    public static void InheritUnresolved(FlowBranchDef def, FlowBranchSet? existing)
    {
        if (existing is null) return;
        var keysByName = new Dictionary<string, (string? Id, string? Sig)>(StringComparer.OrdinalIgnoreCase);
        void Learn(string? name, string? id, string? sig)
        {
            if (string.IsNullOrWhiteSpace(name) || (id is null && sig is null)) return;
            if (!keysByName.TryGetValue(name.Trim(), out var cur))
                keysByName[name.Trim()] = (id, sig);
            else
                keysByName[name.Trim()] = (cur.Id ?? id, cur.Sig ?? sig);
        }
        foreach (var b in existing.Branches)
        {
            Learn(b.StartCallName, b.StartCallId, b.StartCallSig);
            Learn(b.EndCallName, b.EndCallId, b.EndCallSig);
            var names = b.ExcludedCallNames ?? [];
            for (var i = 0; i < names.Count; i++)
            {
                var id = b.ExcludedCallIds is { } ids && ids.Count == names.Count ? ids[i] : null;
                var sg = b.ExcludedCallSigs is { } sgs && sgs.Count == names.Count ? sgs[i] : null;
                Learn(names[i], id, sg);
            }
        }
        if (keysByName.Count == 0) return;

        if (def.StartCallId is null && keysByName.TryGetValue(def.StartCallName.Trim(), out var ks))
        { def.StartCallId = ks.Id; def.StartCallSig ??= ks.Sig; }
        if (def.EndCallId is null && keysByName.TryGetValue(def.EndCallName.Trim(), out var ke))
        { def.EndCallId = ke.Id; def.EndCallSig ??= ke.Sig; }
        var exNames = def.ExcludedCallNames ?? [];
        if (def.ExcludedCallIds is { } exIds && exIds.Count == exNames.Count)
        {
            var exSigs = def.ExcludedCallSigs is { } s2 && s2.Count == exNames.Count
                ? s2 : Enumerable.Repeat<string?>(null, exNames.Count).ToList();
            for (var i = 0; i < exNames.Count; i++)
            {
                if (exIds[i] is not null) continue;
                if (!keysByName.TryGetValue(exNames[i].Trim(), out var k)) continue;
                exIds[i] = k.Id; exSigs[i] ??= k.Sig;
            }
            def.ExcludedCallSigs = exSigs;
        }
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
