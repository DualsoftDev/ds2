using System.Windows;
using System.Windows.Input;
using Promaker.Dialogs;
using Promaker.ViewModels;
using Promaker.ViewModels.Manual;

namespace Promaker.Windows;

/// <summary>
/// 수동 컨트롤러 다이얼로그 — modeless. 다이얼로그 생명주기 = 수동 운전 세션:
/// Loaded → ManualControlState.AttachAsync (broadcast 구독, 초기 값 로드)
/// Closed → ManualControlState.Detach + SimulationPanelState.EndManualControlSession (모든 정리)
/// </summary>
public partial class ManualControlDialog : Window
{
    private readonly SimulationPanelState _sim;
    private readonly ManualControlState _vm;

    public ManualControlDialog(SimulationPanelState sim, ManualControlState vm)
    {
        _sim = sim;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.AttachAsync();
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        _vm.Detach();
        _sim.EndManualControlSession();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* ignore — 일부 윈도우 상태에서 throw */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
