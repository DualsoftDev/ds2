using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTokenSpecDialog()
    {
        if (!GuardSimulationSemanticEdit("TokenSpec 변경"))
            return;

        var specs = NormalizeTokenSpecsForDialog(_store, Queries.getTokenSpecs(_store));
        var allWorks = BuildTokenSpecPickerWorks(_store);

        var dialog = new TokenSpecDialog(specs, allWorks);
        if (!DialogHelpers.ShowOwnedDialog(dialog))
            return;

        var result = dialog.Result;
        var worksRequiringSourceRole = dialog.WorksRequiringSourceRole;
        if (result.SequenceEqual(specs) && worksRequiringSourceRole.Count == 0)
            return;

        if (TryEditorAction(() =>
            {
                foreach (var workId in worksRequiringSourceRole)
                    _store.UpdateWorkTokenRole(workId, TokenRole.Source);
                _store.UpdateTokenSpecs(result);
            }))
        {
            var sourceMsg = worksRequiringSourceRole.Count > 0
                ? $", Source Role 등록 {worksRequiringSourceRole.Count}건"
                : "";
            StatusText = $"TokenSpec 변경: {result.Count}건{sourceMsg}";
        }
    }

    private static List<WorkOption> BuildTokenSpecPickerWorks(DsStore store)
    {
        // 원본/레퍼런스 어느 쪽이든 Source Role 을 가지면 원본 Work 그룹을 Source 로 인식
        var sourceCanonicalIds = store.Works.Values
            .Where(w => w.TokenRole.HasFlag(TokenRole.Source))
            .Select(w => Queries.resolveOriginalWorkId(w.Id, store))
            .ToHashSet();

        return store.Works.Values
            .Select(w => Queries.resolveOriginalWorkId(w.Id, store))
            .Distinct()
            .Select(workId => Queries.getWork(workId, store))
            .Where(work => work is not null)
            .Select(work => new WorkOption(
                work!.Value.Id,
                work.Value.Name,
                sourceCanonicalIds.Contains(work.Value.Id)))
            .OrderBy(work => work.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TokenSpec> NormalizeTokenSpecsForDialog(DsStore store, IEnumerable<TokenSpec> specs)
    {
        return specs
            .Select(spec =>
            {
                var workId = spec.WorkId is { } linkedWorkId
                    ? FSharpOption<Guid>.Some(Queries.resolveOriginalWorkId(linkedWorkId.Value, store))
                    : null;
                return new TokenSpec(spec.Id, spec.Label, spec.Fields, workId);
            })
            .ToList();
    }
}
