using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void OpenDurationBatchDialog()
    {
        var dialog = new DurationBatchDialog();
        DialogHelpers.ShowOwnedDialog(dialog);
    }
}
