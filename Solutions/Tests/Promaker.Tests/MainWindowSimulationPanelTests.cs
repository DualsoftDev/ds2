using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainWindowSimulationPanelTests
{
    [Fact]
    public void MainWindow_hosts_simulation_control_panel_in_canvas_and_toggles_visibility()
    {
        StaTestRunner.Run(() =>
        {
            var window = new MainWindow();

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                var overlayCanvas = Assert.IsType<Canvas>(window.FindName("SimulationOverlayCanvas"));
                var panel = Assert.IsType<SimulationControlPanel>(window.FindName("SimulationControlPanelHost"));
                var dragHandle = Assert.IsType<Border>(panel.FindName("DragHandle"));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);

                Assert.Same(overlayCanvas, VisualTreeHelper.GetParent(panel));
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.Equal(Cursors.SizeAll, dragHandle.Cursor);

                viewModel.Simulation.IsSimulating = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.Equal(Visibility.Visible, panel.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
