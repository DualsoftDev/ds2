using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenPlcXmlGenerator()
    {
        var dialog = new PlcXmlGeneratorDialog(_store, _currentFilePath);
        _dialogService.ShowDialog(dialog);
    }
}
