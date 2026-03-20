using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainWindowSimulationPanelTests
{
    [Fact]
    public void MainWindow_hosts_simulation_panel_below_workspace_with_simulation_datacontext()
    {
        StaTestRunner.Run(() =>
        {
            var window = new MainWindow();

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                var workspace = Assert.IsType<SplitCanvasContainer>(window.FindName("WorkspacePane"));
                var panel = Assert.IsType<SimulationPanel>(window.FindName("SimulationPanelHost"));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                var centerGrid = Assert.IsType<Grid>(workspace.Parent);

                Assert.Same(centerGrid, panel.Parent);
                Assert.Equal(0, Grid.GetRow(workspace));
                Assert.Equal(2, Grid.GetRow(panel));
                Assert.Same(viewModel.Simulation, panel.DataContext);
                Assert.Equal(Visibility.Visible, panel.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
