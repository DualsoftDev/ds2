using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>이 Call 에 등록된 Condition 의 실제 ApiCall 심볼만 + 사용 여부 — Refresh 마다 갱신.</summary>
    private void UpdateSymbolProvider(CoilCondition cond)
    {
        // 현재 rung 에서 사용 중인 심볼 set (used).
        var usedSet = new HashSet<string>(
            CoilAst.Leaves(cond).Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);
        // 현재 owner(Call/Work)의 Condition 에 등록된 ApiCall display name 을 우선 노출.
        var registered = ConditionDialogSymbolResolver
            .BuildRegisteredDisplayNames(_store, _callId, _ownerKind, _condType)
            .ToList();
        // 현재 rung 에 있는데 conditions 에 안 보이는 leaf 도 포함 (drag 로 추가된 임시 심볼 등).
        foreach (var u in usedSet)
            if (!registered.Contains(u, StringComparer.OrdinalIgnoreCase))
                registered.Add(u);
        _ctx.SymbolProvider = new AnnotatedSymbolProvider(registered, usedSet);
    }

    /// <summary>심볼 + 사용 상태 라벨. SymbolPopupEditor 가   이후 상태 텍스트는 잘라냄.</summary>
    private sealed class AnnotatedSymbolProvider : ISymbolProvider
    {
        private static readonly string[] BuiltIn = { "_ON", "_OFF" };
        private const string SEP = " ";  // em-space — 표시용 구분자
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
}
