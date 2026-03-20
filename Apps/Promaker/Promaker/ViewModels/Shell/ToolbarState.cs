using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Promaker.Resources;

namespace Promaker.ViewModels;

public enum RibbonGroup
{
    Project,
    Edit,
    Simulation,
    Tools
}

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRibbonGroupLabel))]
    private RibbonGroup _selectedRibbonGroup = RibbonGroup.Project;

    public string SelectedRibbonGroupLabel => SelectedRibbonGroup switch
    {
        RibbonGroup.Project => Strings.Project,
        RibbonGroup.Edit => Strings.Edit,
        RibbonGroup.Simulation => Strings.Simulation,
        RibbonGroup.Tools => Strings.Tools,
        _ => SelectedRibbonGroup.ToString()
    };

    [RelayCommand]
    private void ShowProjectRibbonGroup() => SelectedRibbonGroup = RibbonGroup.Project;

    [RelayCommand]
    private void ShowEditRibbonGroup() => SelectedRibbonGroup = RibbonGroup.Edit;

    [RelayCommand]
    private void ShowSimulationRibbonGroup() => SelectedRibbonGroup = RibbonGroup.Simulation;

    [RelayCommand]
    private void ShowToolsRibbonGroup() => SelectedRibbonGroup = RibbonGroup.Tools;
}
