using System.ComponentModel;
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
        _vm.Canvas.CenterOnNodeRequested = WorkspacePane.CenterOnNode;
        _vm.Canvas.FitToViewZoomOutRequested = WorkspacePane.FitToViewZoomOut;
        _vm.Canvas.GetViewportCenterRequested = WorkspacePane.GetViewportCenter;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscardChangesPublic())
            e.Cancel = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
