using System.Windows;
using Promaker.ViewModels;

namespace Promaker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.FocusNameEditorRequested = PropertyPane.FocusNameEditorControl;
        _vm.CenterOnNodeRequested = WorkspacePane.CenterOnNode;
        _vm.GetViewportCenterRequested = WorkspacePane.GetViewportCenter;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
