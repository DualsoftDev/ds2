using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Promaker.Dialogs;
using Promaker.ViewModels;
using PromakerShared = Promaker.Shared;

namespace Promaker.Windows;

/// <summary>
/// PLC 연결 정보를 편집하는 다이얼로그. 태그 매핑은 AASX IO 설정에서 자동 import 되므로
/// 여기서는 벤더와 연결 파라미터만 입력한다.
///
/// 벤더(LS XGI / LS XGK / Mitsubishi) 마다 직전 입력값을 메모리에 보관 → 라디오를 토글해도
/// 그 벤더의 양식이 그대로 복원된다. Apply 시 VM 의 VendorProfiles 와 활성 플랫 필드 모두 갱신.
/// </summary>
public partial class PlcSettingsDialog : Window
{
    private readonly PlcSettings _vm;

    /// <summary>현재 폼에 로드된 벤더 — 다음 토글에서 어떤 키로 스냅샷할지 추적.</summary>
    private PlcVendorChoice _loadedVendor;

    /// <summary>다이얼로그 수명 동안 벤더 토글로 옮겨 다니는 작업본. Apply 에서만 VM 으로 commit.</summary>
    private readonly Dictionary<string, PromakerShared.PlcVendorProfile> _workingProfiles;

    /// <summary>다이얼로그 결과 — 자동 duration 정합 체크 상태. 호출자가 닫힌 후 SimulationPanelState 에 적용
    /// (hub 전파). PlcSettings(연결) 와 별개 축이라 VM 에 안 섞고 결과 property 로 노출한다.</summary>
    public bool AutoDurationCalibrate { get; private set; }

    public PlcSettingsDialog(PlcSettings settings, int? autoImportedTagCount = null, bool autoCalibrate = true)
    {
        _vm = settings;
        InitializeComponent();

        AutoDurationCalibrate = autoCalibrate;
        AutoCalibrateBox.IsChecked = autoCalibrate;

        // VM 의 벤더 프로파일을 복사 — Cancel 시 영향 없도록 작업본을 따로 관리.
        _workingProfiles = new Dictionary<string, PromakerShared.PlcVendorProfile>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _vm.VendorProfiles)
            _workingProfiles[kv.Key] = kv.Value.Clone();
        // 세 벤더 모두 키 존재 보장 (혹시 VM 에 누락된 게 있으면 기본값 채움).
        foreach (PlcVendorChoice v in System.Enum.GetValues(typeof(PlcVendorChoice)))
        {
            var key = v.ToString();
            if (!_workingProfiles.ContainsKey(key))
                _workingProfiles[key] = PromakerShared.PlcVendorProfile.Defaults(
                    (PromakerShared.PlcVendorChoice)v);
        }
        // 활성 벤더 프로파일은 VM 의 현재 플랫 값으로 최신화 — 다이얼로그 진입 직전 변경분 반영.
        _workingProfiles[_vm.Vendor.ToString()] = _vm.CaptureActiveProfile();

        // 초기 라디오 + 폼 로드 — _loadedVendor 가 곧 폼이 가리키는 벤더.
        _loadedVendor = _vm.Vendor;
        switch (_loadedVendor)
        {
            case PlcVendorChoice.LsXgi: RbLsXgi.IsChecked = true; break;
            case PlcVendorChoice.LsXgk: RbLsXgk.IsChecked = true; break;
            case PlcVendorChoice.Mitsubishi: RbMx.IsChecked = true; break;
        }
        LoadProfileToForm(_workingProfiles[_loadedVendor.ToString()]);

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

        var newVendor =
            RbMx.IsChecked == true ? PlcVendorChoice.Mitsubishi
          : RbLsXgk.IsChecked == true ? PlcVendorChoice.LsXgk
          : PlcVendorChoice.LsXgi;
        if (newVendor == _loadedVendor)
        {
            UpdateVendorSpecificPanels();
            return;
        }

        // 1) 현재 폼 (편집 중 값) 을 떠나는 벤더 프로파일로 스냅샷 — 토글 중에는 검증 없이 보관.
        _workingProfiles[_loadedVendor.ToString()] =
            CaptureFormLenient(_workingProfiles[_loadedVendor.ToString()]);

        // 2) 새 벤더 프로파일을 폼에 로드.
        _loadedVendor = newVendor;
        LoadProfileToForm(_workingProfiles[_loadedVendor.ToString()]);

        UpdateVendorSpecificPanels();
    }

    private void UpdateVendorSpecificPanels()
    {
        var isMx = RbMx.IsChecked == true;
        LsOnlyPanel.Visibility = isMx ? Visibility.Collapsed : Visibility.Visible;
        MxOnlyPanel.Visibility = isMx ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>프로파일 값을 폼 컨트롤에 채워 넣는다.</summary>
    private void LoadProfileToForm(PromakerShared.PlcVendorProfile p)
    {
        NameBox.Text = p.Name;
        IpBox.Text = p.IpAddress;
        PortBox.Text = p.Port.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = p.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        ScanSlider.Value = Math.Clamp(p.ScanIntervalMs, 10, 500);
        LocalEthernetBox.IsChecked = p.LocalEthernet;
        NetworkNumberBox.Text = p.NetworkNumber.ToString(CultureInfo.InvariantCulture);
        StationNumberBox.Text = p.StationNumber.ToString(CultureInfo.InvariantCulture);
        if (p.IsUdp) RbTransportUdp.IsChecked = true;
        else RbTransportTcp.IsChecked = true;
    }

    /// <summary>벤더 토글 중 부드러운 스냅샷 — 파싱 실패 필드는 기존 프로파일 값을 유지.</summary>
    private PromakerShared.PlcVendorProfile CaptureFormLenient(PromakerShared.PlcVendorProfile fallback) => new()
    {
        Name = NameBox.Text?.Trim() ?? fallback.Name,
        IpAddress = IpBox.Text?.Trim() ?? fallback.IpAddress,
        Port = TryParseInt(PortBox.Text, fallback.Port),
        TimeoutMs = TryParseInt(TimeoutBox.Text, fallback.TimeoutMs),
        ScanIntervalMs = (int)ScanSlider.Value,
        LocalEthernet = LocalEthernetBox.IsChecked == true,
        NetworkNumber = TryParseByte(NetworkNumberBox.Text, fallback.NetworkNumber),
        StationNumber = TryParseByte(StationNumberBox.Text, fallback.StationNumber),
        IsUdp = RbTransportUdp.IsChecked == true,
    };

    private static int TryParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static byte TryParseByte(string? s, byte fallback) =>
        byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

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
        // 슬라이더가 10~500/10ms 단위를 보장 — 별도 검증 불필요.
        var scan = (int)ScanSlider.Value;
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

        // 활성 벤더 프로파일 = 검증 통과한 현재 폼 값.
        var activeVendor = _loadedVendor;
        var activeProfile = new PromakerShared.PlcVendorProfile
        {
            Name = NameBox.Text?.Trim() ?? "PLC#1",
            IpAddress = IpBox.Text.Trim(),
            Port = port,
            TimeoutMs = timeout,
            ScanIntervalMs = scan,
            LocalEthernet = LocalEthernetBox.IsChecked == true,
            NetworkNumber = net,
            StationNumber = stn,
            IsUdp = RbTransportUdp.IsChecked == true,
        };
        _workingProfiles[activeVendor.ToString()] = activeProfile;

        // VM commit — VendorProfiles 교체 → Vendor 변경 → 플랫 필드 활성 프로파일로 적용.
        _vm.VendorProfiles.Clear();
        foreach (var kv in _workingProfiles)
            _vm.VendorProfiles[kv.Key] = kv.Value.Clone();
        _vm.Vendor = activeVendor;
        _vm.ApplyProfile(activeProfile);

        // 다음 실행 시에도 같은 값이 채워지도록 영속화.
        _vm.Save();

        // 자동 정합 토글 결과 — 호출자가 SimulationPanelState 에 반영(hub 전파). PlcSettings 와 별개 축.
        AutoDurationCalibrate = AutoCalibrateBox.IsChecked == true;

        DialogResult = true;
        Close();
    }
}
