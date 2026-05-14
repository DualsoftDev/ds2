using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Promaker.Dialogs;
using Promaker.ViewModels;

namespace Promaker.Windows;

/// <summary>
/// PLC 연결 정보를 편집하는 다이얼로그. 태그 매핑은 AASX IO 설정에서 자동 import 되므로
/// 여기서는 벤더와 연결 파라미터만 입력한다.
/// </summary>
public partial class PlcSettingsDialog : Window
{
    private readonly PlcSettings _vm;

    public PlcSettingsDialog(PlcSettings settings, int? autoImportedTagCount = null)
    {
        _vm = settings;
        InitializeComponent();

        // VM → UI 초기 로드
        switch (_vm.Vendor)
        {
            case PlcVendorChoice.LsXgi: RbLsXgi.IsChecked = true; break;
            case PlcVendorChoice.LsXgk: RbLsXgk.IsChecked = true; break;
            case PlcVendorChoice.Mitsubishi: RbMx.IsChecked = true; break;
        }
        NameBox.Text = _vm.Name;
        IpBox.Text = _vm.IpAddress;
        PortBox.Text = _vm.Port.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = _vm.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        ScanBox.Text = _vm.ScanIntervalMs.ToString(CultureInfo.InvariantCulture);
        LocalEthernetBox.IsChecked = _vm.LocalEthernet;
        NetworkNumberBox.Text = _vm.NetworkNumber.ToString(CultureInfo.InvariantCulture);
        StationNumberBox.Text = _vm.StationNumber.ToString(CultureInfo.InvariantCulture);
        // MX 전송 방식 — UDP 가 true 면 UDP, 아니면 TCP (기본).
        if (_vm.IsUdp) RbTransportUdp.IsChecked = true;
        else RbTransportTcp.IsChecked = true;

        TagSummaryText.Text = autoImportedTagCount switch
        {
            null => "현재 IO 매핑은 PLAY 시점에 빌드되어 자동 import 됩니다.",
            0    => "⚠ AASX IO 매핑에서 주소가 발견되지 않았습니다. ApiCall 의 OutTag/InTag 주소를 먼저 설정하세요.",
            int n => $"AASX IO 매핑에서 {n}개 주소가 자동 import 됩니다."
        };

        UpdateVendorSpecificPanels();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void VendorRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateVendorSpecificPanels();

        // 사용자가 명시적으로 벤더를 바꿨을 때 기본 포트도 반영 — 단, 사용자가 직접 입력한 값이 기본값
        // (2004 / 5007) 인 경우에만 자동 갱신해 의도치 않은 덮어쓰기 방지.
        if (int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            && (p == 2004 || p == 5007))
        {
            PortBox.Text = (RbMx.IsChecked == true ? 5007 : 2004).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void UpdateVendorSpecificPanels()
    {
        var isMx = RbMx.IsChecked == true;
        LsOnlyPanel.Visibility = isMx ? Visibility.Collapsed : Visibility.Visible;
        MxOnlyPanel.Visibility = isMx ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        // 입력값 검증 — 실패하면 다이얼로그 유지.
        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port <= 0 || port > 65535)
        {
            DialogHelpers.Warn("Port 는 1–65535 범위 정수여야 합니다.");
            PortBox.Focus();
            return;
        }
        if (!int.TryParse(TimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
            || timeout <= 0)
        {
            DialogHelpers.Warn("Timeout(ms) 은 양의 정수여야 합니다.");
            TimeoutBox.Focus();
            return;
        }
        if (!int.TryParse(ScanBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scan)
            || scan <= 0)
        {
            DialogHelpers.Warn("Scan interval(ms) 은 양의 정수여야 합니다.");
            ScanBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(IpBox.Text))
        {
            DialogHelpers.Warn("IP 주소를 입력하세요.");
            IpBox.Focus();
            return;
        }

        if (!byte.TryParse(NetworkNumberBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var net))
            net = 0;
        if (!byte.TryParse(StationNumberBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stn))
            stn = 0xFF;

        // VM 으로 반영 — 태그는 PLAY 시점 IO 매핑에서 자동 import 되므로 여기서 손대지 않음.
        _vm.Vendor =
            RbMx.IsChecked == true ? PlcVendorChoice.Mitsubishi
          : RbLsXgk.IsChecked == true ? PlcVendorChoice.LsXgk
          : PlcVendorChoice.LsXgi;
        _vm.Name = NameBox.Text?.Trim() ?? "PLC#1";
        _vm.IpAddress = IpBox.Text.Trim();
        _vm.Port = port;
        _vm.TimeoutMs = timeout;
        _vm.ScanIntervalMs = scan;
        _vm.LocalEthernet = LocalEthernetBox.IsChecked == true;
        _vm.NetworkNumber = net;
        _vm.StationNumber = stn;
        _vm.IsUdp = RbTransportUdp.IsChecked == true;

        // 다음 실행 시에도 같은 값이 채워지도록 영속화.
        _vm.Save();

        DialogResult = true;
        Close();
    }
}
