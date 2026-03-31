using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
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
        var sourceWorks = BuildTokenSpecSourceWorks(_store);

        var dialog = new TokenSpecDialog(specs, sourceWorks);
        if (!DialogHelpers.ShowOwnedDialog(dialog))
            return;

        var result = dialog.Result;
        if (result.SequenceEqual(specs))
            return;

        if (TryEditorAction(() => _store.UpdateTokenSpecs(result)))
            StatusText = $"TokenSpec 변경: {result.Count}건";
    }

    private static List<WorkOption> BuildTokenSpecSourceWorks(DsStore store)
    {
        return store.Works.Values
            .Where(w => w.TokenRole.HasFlag(TokenRole.Source))
            .Select(w => Queries.resolveOriginalWorkId(w.Id, store))
            .Distinct()
            .Select(workId => Queries.getWork(workId, store))
            .Where(work => work is not null)
            .Select(work => new WorkOption(work!.Value.Id, work.Value.Name))
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
