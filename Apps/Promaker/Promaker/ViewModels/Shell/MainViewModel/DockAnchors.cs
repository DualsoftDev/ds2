using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _isExplorerVisible = true;
    [ObservableProperty] private bool _isSimulationVisible = true;
    [ObservableProperty] private bool _isPropertiesVisible = true;
    [ObservableProperty] private bool _isHistoryVisible = true;
    [ObservableProperty] private bool _isLogVisible = true;
}
