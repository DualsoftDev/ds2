using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.Dialogs;
using Promaker.ViewModels;
using Promaker.Windows;

namespace Promaker.Controls;

public partial class MainToolbarSimulationContent : UserControl
{
    public MainToolbarSimulationContent()
    {
        InitializeComponent();
    }

    private MainViewModel? MainVm
        => DataContext as MainViewModel;

    private SimulationPanelState? Sim
        => MainVm?.Simulation;

    /// <summary>원위치 push-button 의 "누름" 진입.
    /// MouseLeftButtonDown 으로 mouse capture 하고 BeginHoming 호출 — 사용자가 캡처 동안 버튼 밖으로
    /// 나가도 release 이벤트가 같은 버튼에서 들어와 안전하게 EndHoming 으로 마무리된다.</summary>
    private void HomingButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn) return;
        var sim = Sim;
        if (sim is null || !sim.IsHomingButtonHotEnabled) return;

        // Mouse capture 로 release 가 어디서 일어나든 우리 버튼이 받도록 보장.
        Mouse.Capture(btn);
        sim.BeginHoming();
        e.Handled = true;
    }

    /// <summary>원위치 push-button 의 "놓음" — Up/LostCapture 동일 EndHoming.</summary>
    private void HomingButton_Release(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null && Mouse.Captured == btn) Mouse.Capture(null);
        Sim?.EndHoming();
    }

    /// <summary>수동 컨트롤러 버튼 클릭 — modeless 다이얼로그를 띄움.
    /// 다이얼로그 자체가 lifecycle 관리: Loaded 에서 broadcast 구독, Closed 에서 EndManualControlSession 호출.</summary>
    private void ManualControlButton_Click(object sender, RoutedEventArgs e)
    {
        var sim = Sim;
        var vm  = MainVm;
        if (sim is null || vm is null) return;

        // 이미 활성 다이얼로그가 있으면 그것만 앞으로 가져옴 (중복 띄움 방지).
        if (sim.CurrentManualControlState is not null)
        {
            // 활성 ManualControlState 의 owner window 를 찾기 어려우므로 메시지로 안내.
            DialogHelpers.Warn("수동 컨트롤러가 이미 열려 있습니다.");
            return;
        }

        // 안전 확인 — 실 라인이 동작한다는 점 명확히 알림.
        var confirm = MessageBox.Show(
            "실 PLC 라인의 모든 디바이스를 직접 제어합니다.\n" +
            "\n" +
            "• 시퀀스(자동 운전)는 수동 운전 동안 일시정지됩니다.\n" +
            "• 다이얼로그를 닫으면 모든 OUT 이 OFF 되고 Hub/PLC 게이트웨이가 종료됩니다.\n" +
            "\n" +
            "계속하시겠습니까?",
            "수동 컨트롤러 진입",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (!sim.BeginManualControlSession()) return;

        // 세션 진입 성공 — SimulationPanelState 가 store 접근 책임을 갖고 VM 조립.
        var manualVm = sim.BuildManualControlState();

        var dlg = new ManualControlDialog(sim, manualVm)
        {
            Owner = Window.GetWindow(this)
        };
        dlg.Show();   // modeless — 캔버스와 동시 조작 가능.
    }
}
