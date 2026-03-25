using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTokenSpecDialog()
    {
        var specs = DsQuery.getTokenSpecs(_store).ToList();

        // Source Work 목록 수집 (TokenRole에 Source 플래그가 포함된 Work)
        var sourceWorks = _store.Works.Values
            .Where(w => w.TokenRole.HasFlag(TokenRole.Source))
            .Select(w => new WorkOption(w.Id, w.ReferenceOf != null ? $"{w.Name} #" : w.Name))
            .ToList();

        var dialog = new TokenSpecDialog(specs, sourceWorks);
        if (!DialogHelpers.ShowOwnedDialog(dialog))
            return;

        var result = dialog.Result;
        if (result.SequenceEqual(specs))
            return;

        if (TryEditorAction(() => _store.UpdateTokenSpecs(result)))
            StatusText = $"TokenSpec 변경: {result.Count}건";
    }
}
