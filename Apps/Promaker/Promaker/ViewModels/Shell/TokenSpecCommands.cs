using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTokenSpecDialog()
    {
        var specs = _store.GetTokenSpecs().ToList();

        var dialog = new TokenSpecDialog(specs);
        if (!DialogHelpers.ShowOwnedDialog(dialog))
            return;

        var result = dialog.Result;
        if (result.SequenceEqual(specs))
            return;

        if (TryEditorAction(() => _store.UpdateTokenSpecs(result)))
            StatusText = $"TokenSpec 변경: {result.Count}건";
    }
}
