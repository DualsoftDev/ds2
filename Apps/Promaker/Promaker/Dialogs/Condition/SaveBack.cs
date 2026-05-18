using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Adapters;
using AAStoPLC.LadderEditor.Expression;
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

public partial class ConditionEditDialog
{
    private void CommitText()
    {
        if (StTextBox is null || StTextBox.IsReadOnly) return;
        if (_rung is null) return;
        var text = StTextBox.Text ?? "";

        if (!CoilConditionParser.TryParse(text, out var cond, out var err) || cond is null)
        {
            ShowStatus("✕ " + err, ErrorText);
            if (TextBoxBorder is not null) { TextBoxBorder.BorderBrush = ErrorBorder; TextBoxBorder.BorderThickness = new Thickness(2); }
            int pos = StEditorOps.ExtractErrorPosition(err);
            if (pos >= 0 && pos <= text.Length)
            {
                StTextBox.SelectionStart = pos;
                StTextBox.SelectionLength = 0;
                StTextBox.Focus();
            }
            UpdateValidationStatus(false, 0);
            return;
        }

        _suppressTextSync = true;
        try
        {
            _rung.Condition = CoilConditionModule.simplify(cond);
            _isDirty = true;
            _lastCommittedText = text;
            UpdateSymbolProvider(_rung.Condition);
            ShowStatus("✓ 적용됨", OkText);
            if (ApplyBtn is not null) ApplyBtn.IsEnabled = false;
            if (TextBoxBorder is not null)
            {
                TextBoxBorder.BorderBrush = _normalBorder ?? TextBoxBorder.BorderBrush;
                TextBoxBorder.BorderThickness = new Thickness(1);
            }
        }
        finally { _suppressTextSync = false; }
        UpdateStatusBar();
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
    /// per-leaf ContactKind, group IsInverted 모두 보존.
    /// ApiCall 매핑 안 되는 leaf (_ON/_OFF 등 raw 심볼) 는 RawSymbols 에 보존.</summary>
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
        var rawSymbols = new List<string>();
        var rawSymbolKinds = new List<ContactKind>();
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
                // ApiCall 매핑 안 되는 leaf — RawSymbols 에 보존 (이전에는 drop 됐던 _ON/_OFF 등).
                if (TryExtractRawLeaf(op, out var rawName, out var rawKind)
                    && !string.IsNullOrEmpty(rawName))
                {
                    rawSymbols.Add(rawName);
                    rawSymbolKinds.Add(rawKind);
                }
            }
            else
            {
                var child = ToDto(op, nameToId);
                if (child is not null) children.Add(child);
            }
        }
        return new CallConditionTreeDto(isOr, isInverted,
                                         apiCallIds, apiCallKinds,
                                         rawSymbols, rawSymbolKinds,
                                         children);
    }

    /// <summary>매핑 안 된 leaf → (심볼 이름, ContactKind). 이름이 비었거나 분류 불가 시 false.</summary>
    private static bool TryExtractRawLeaf(CoilCondition c, out string name, out ContactKind kind)
    {
        switch (c)
        {
            case CoilCondition.Var v:      name = v.name;  kind = ContactKind.NoContact;     return true;
            case CoilCondition.NegVar nv:  name = nv.name; kind = ContactKind.NcContact;     return true;
            case CoilCondition.Rising rs:  name = rs.name; kind = ContactKind.RisingPulse;   return true;
            case CoilCondition.Falling fl: name = fl.name; kind = ContactKind.FallingPulse;  return true;
            case CoilCondition.Raw raw:    name = raw.expr; kind = ContactKind.NoContact;    return true;
            default:                        name = "";      kind = ContactKind.NoContact;    return false;
        }
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
        d.ApiCallIds.Count + d.RawSymbols.Count + d.Children.Sum(CountLeaves);

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
}
