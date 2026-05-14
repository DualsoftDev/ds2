using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Adapters;
using AAStoPLC.LadderEditor.Models;
using AAStoPLC.LadderEditor.Rendering;
using AAStoPLC.Pipeline;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Dialogs;

/// <summary>
/// Call 조건 편집 다이얼로그 — 트리 입력 UI 제거 후 LadderEditor 단독 화면.
/// 현재는 표시 + 인터랙션 만 — store 로의 역반영(save-back) 은 후속 작업.
/// 변경 진입점은 그대로 Call drag-drop / 별도 ApiCall 추가 다이얼로그 사용.
/// </summary>
public partial class ConditionEditDialog : Window
{
    private readonly DsStore _store;
    private readonly MainViewModel.PropertyPanelHost _host;
    private readonly Guid _callId;
    private readonly CallConditionType _condType;
    private readonly ObservableCollection<RungViewModel> _rungs = new();
    private readonly EditorContext _ctx = new() { GridCols = 14 };
    private CoilRungViewModel? _rung;
    private bool _isDirty;  // 사용자가 LadderEditor 에서 한 번이라도 편집했는지.

    public ConditionEditDialog(
        DsStore store,
        MainViewModel.PropertyPanelHost host,
        Guid callId,
        CallConditionType condType)
    {
        InitializeComponent();
        _store = store;
        _host = host;
        _callId = callId;
        _condType = condType;
        SectionTitle.Text = $"{condType} 조건 편집";
        StatusText.Text   = "닫기 시 LadderEditor 변경 사항이 store 에 저장됩니다.";

        EditorView.Context = _ctx;
        EditorView.Rungs   = _rungs;
        SyncTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += SyncTheme;
        Closing += (_, _) => SaveBack();
        Closed  += (_, _) => ThemeManager.ThemeChanged -= SyncTheme;

        Refresh();

        // 편집 발생 추적 — RungEdited 이벤트 + Condition 변경 알림.
        Loaded += (_, _) =>
        {
            if (EditorView.Edit is { } edit) edit.RungEdited += (_, _) => OnEdited();
        };
    }

    private void OnEdited()
    {
        _isDirty = true;
        if (_rung is not null) UpdateSymbolProvider(_rung.Condition);
    }

    /// <summary>편집된 CoilCondition 을 CallConditionTreeDto 로 재귀 매핑 후 ReplaceCallConditionTree 호출.
    /// 중첩 And/Or 구조 보존. 알 수 없는 leaf 는 무시.
    /// 안전장치: _isDirty=false 또는 매핑된 leaf 0 개면 skip — 기존 store 보존.
    /// </summary>
    private void SaveBack()
    {
        if (!_isDirty || _rung is null) return;
        var nameToId = BuildDisplayNameToApiCallId();

        var simp = CoilConditionModule.simplify(_rung.Condition);
        var dto = ToDto(simp, nameToId);
        if (dto is null) return;
        // 매핑된 leaf 한 개도 없으면 skip — 빈 트리 저장 방지.
        if (CountLeaves(dto) == 0) return;

        _host.TryAction(() => _store.ReplaceCallConditionTree(_callId, _condType, dto));
    }

    /// <summary>CoilCondition → CallConditionTreeDto 재귀 변환.
    /// per-leaf ContactKind, group IsInverted 모두 보존. 알 수 없는 leaf 는 drop.</summary>
    private static CallConditionTreeDto? ToDto(CoilCondition c, Dictionary<string, Guid> nameToId)
    {
        // Leaf: 단일 leaf 를 isOR=false 그룹으로 감싸 단일화 처리.
        // Not(Raw "") 인버터 placeholder 는 leaf 로 처리.
        // Not(inner) 일반 NOT 은 IsInverted=true 그룹.
        bool isOr = false;
        bool isInverted = false;
        IEnumerable<CoilCondition> ops;

        switch (c)
        {
            case CoilCondition.Or o: isOr = true;  ops = o.operands; break;
            case CoilCondition.And a: isOr = false; ops = a.operands; break;
            case CoilCondition.Not n:
                // Not(Raw "") = inverter placeholder leaf.
                if (n.operand is CoilCondition.Raw r0 && string.IsNullOrEmpty(r0.expr))
                {
                    ops = new[] { c };
                    break;
                }
                // 일반 Not — 그룹에 IsInverted=true 부여, 내부를 single-op AND 로 펼침.
                isInverted = true;
                ops = new[] { n.operand };
                break;
            case CoilCondition.Var or CoilCondition.NegVar
                 or CoilCondition.Rising or CoilCondition.Falling
                 or CoilCondition.Raw:
                ops = new[] { c };
                break;
            default: return null;  // AlwaysTrue/False — drop
        }

        var apiCallIds = new List<Guid>();
        var apiCallKinds = new List<ContactKind>();
        var children = new List<CallConditionTreeDto>();

        foreach (var op in ops)
        {
            if (TryClassifyLeaf(op, nameToId, out var leafId, out var leafKind))
            {
                apiCallIds.Add(leafId);
                apiCallKinds.Add(leafKind);
            }
            else if (CoilAst.IsLeaf(op) || op is CoilCondition.Raw)
            {
                // 매핑 불가 leaf — drop (이름이 nameToId 에 없음). 재귀하면 무한 루프.
                continue;
            }
            else
            {
                var child = ToDto(op, nameToId);
                if (child is not null) children.Add(child);
            }
        }
        return new CallConditionTreeDto(isOr, isInverted, apiCallIds, apiCallKinds, children);
    }

    /// <summary>단일 노드가 leaf 인지 분류 + ContactKind 결정.
    /// Not(Raw "") = Inverter placeholder.</summary>
    private static bool TryClassifyLeaf(
        CoilCondition c, Dictionary<string, Guid> nameToId,
        out Guid id, out ContactKind kind)
    {
        switch (c)
        {
            case CoilCondition.Var v:
                kind = ContactKind.NoContact;
                return nameToId.TryGetValue(v.name, out id);
            case CoilCondition.NegVar nv:
                kind = ContactKind.NcContact;
                return nameToId.TryGetValue(nv.name, out id);
            case CoilCondition.Rising rs:
                kind = ContactKind.RisingPulse;
                return nameToId.TryGetValue(rs.name, out id);
            case CoilCondition.Falling fl:
                kind = ContactKind.FallingPulse;
                return nameToId.TryGetValue(fl.name, out id);
            case CoilCondition.Not n
                when n.operand is CoilCondition.Raw r && string.IsNullOrEmpty(r.expr):
                kind = ContactKind.Inverter;
                id = Guid.Empty;
                return true;
            case CoilCondition.Raw raw when nameToId.TryGetValue(raw.expr, out id):
                kind = ContactKind.NoContact;
                return true;
            default:
                kind = ContactKind.NoContact;
                id = Guid.Empty;
                return false;
        }
    }

    private static int CountLeaves(CallConditionTreeDto d) =>
        d.ApiCallIds.Count + d.Children.Sum(CountLeaves);

    /// <summary>buildPreview 와 동일한 `{System.Name}.{ApiDef.Name}` 포맷 → ApiCall.Id lookup.</summary>
    private Dictionary<string, Guid> BuildDisplayNameToApiCallId()
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var ac in _store.ApiCalls.Values)
        {
            if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId)) continue;
            if (!_store.ApiDefs.TryGetValue(ac.ApiDefId.Value, out var def)) continue;
            if (!_store.Systems.TryGetValue(def.ParentId, out var sys)) continue;
            var key = $"{sys.Name}.{def.Name}";
            if (!map.ContainsKey(key)) map[key] = ac.Id;
        }
        return map;
    }

    /// <summary>이 Call 에 등록된 CallCondition 의 실제 ApiCall 심볼만 + 사용 여부 — Refresh 마다 갱신.</summary>
    private void UpdateSymbolProvider(CoilCondition cond)
    {
        // 현재 rung 에서 사용 중인 심볼 set (used).
        var usedSet = new HashSet<string>(
            CoilAst.Leaves(cond).Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);
        // 이 Call 에 실제로 등록된 CallCondition 들의 ApiCall display name 만 추출.
        var registered = new List<string>();
        if (_host.TryRef(() => _store.GetCallConditionsForPanel(_callId), out var conds))
        {
            foreach (var c in conds)
                CollectApiCallNames(c, registered);
        }
        // 현재 rung 에 있는데 conditions 에 안 보이는 leaf 도 포함 (drag 로 추가된 임시 심볼 등).
        foreach (var u in usedSet)
            if (!registered.Contains(u, StringComparer.OrdinalIgnoreCase))
                registered.Add(u);
        _ctx.SymbolProvider = new AnnotatedSymbolProvider(registered, usedSet);
    }

    private static void CollectApiCallNames(Ds2.Editor.CallConditionPanelItem c, List<string> acc)
    {
        foreach (var item in c.Items)
            if (!acc.Contains(item.ApiDefDisplayName, StringComparer.OrdinalIgnoreCase))
                acc.Add(item.ApiDefDisplayName);
        foreach (var child in c.Children) CollectApiCallNames(child, acc);
    }

    /// <summary>심볼 + 사용 상태 라벨. SymbolPopupEditor 가 \u2003 이후 상태 텍스트는 잘라냄.</summary>
    private sealed class AnnotatedSymbolProvider : ISymbolProvider
    {
        private static readonly string[] BuiltIn = { "_ON", "_OFF" };
        private const string SEP = "\u2003";  // em-space — 표시용 구분자
        private readonly List<string> _displays; // 정렬: 미사용 먼저
        private readonly HashSet<string> _known;

        public AnnotatedSymbolProvider(IEnumerable<string> all, HashSet<string> usedSet)
        {
            var allList = BuiltIn
                .Concat(all)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _known = new(allList, StringComparer.OrdinalIgnoreCase);

            // 미사용을 먼저 (사용자가 미배치 심볼을 우선 선택하도록 유도).
            string Decorate(string s) => usedSet.Contains(s)
                ? $"{s}{SEP}✓ 사용중"
                : $"{s}{SEP}○ 미사용";
            _displays = allList
                .OrderBy(s => usedSet.Contains(s) ? 1 : 0)
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(Decorate)
                .ToList();
        }

        public IEnumerable<string> Search(string prefixOrSubstring) =>
            string.IsNullOrEmpty(prefixOrSubstring)
                ? _displays
                : _displays.Where(s => s.IndexOf(prefixOrSubstring, StringComparison.OrdinalIgnoreCase) >= 0);

        public bool IsKnown(string symbol) => _known.Contains(symbol);
    }


    private void SyncTheme(AppTheme theme) =>
        EditorView.Theme = theme == AppTheme.Dark ? new DefaultDarkTheme() : new DefaultLightTheme();

    /// <summary>현재 store 상태 → CoilCondition → 단일 CoilRung 으로 표시.</summary>
    private void Refresh()
    {
        if (!_host.TryRef(() => _store.Calls[_callId], out var call)) return;
        var condOpt = ConditionExprBuilder.buildPreview(_store, call, _condType);
        var cond = FSharpOption<CoilCondition>.get_IsSome(condOpt)
            ? condOpt.Value : CoilCondition.AlwaysTrue;
        const string coilName = "OUT";

        if (_rung is null)
        {
            _rung = new CoilRungViewModel(cond, coilName);
            _rungs.Clear();
            _rungs.Add(_rung);
        }
        else
        {
            _rung.Condition = cond;
            _rung.CoilBit   = coilName;
        }
        UpdateSymbolProvider(cond);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_rung is null) { StatusText.Text = "rung 없음"; return; }
        var nameToId = BuildDisplayNameToApiCallId();
        var leaves = CoilAst.Leaves(_rung.Condition).Select(l => l.Name).ToList();
        var matched = leaves.Where(n => nameToId.ContainsKey(n)).ToList();
        var unmatched = leaves.Where(n => !nameToId.ContainsKey(n)).ToList();

        _isDirty = true;
        SaveBack();
        _isDirty = false;

        var msg = $"✓ 저장 — leaf {leaves.Count}, 매핑 {matched.Count}, 미매핑 {unmatched.Count}";
        if (unmatched.Count > 0) msg += $" [미매핑: {string.Join(", ", unmatched.Take(3))}]";
        StatusText.Text = msg;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
