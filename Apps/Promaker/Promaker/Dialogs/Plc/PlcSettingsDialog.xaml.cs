using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>다중 System(멀티 PLC) 편집 대상 — null 또는 1개면 기존 단일 화면 동작.</summary>
    private readonly IReadOnlyList<PlcSystemEndpointEntry>? _systems;

    /// <summary>System별 endpoint 저장 콜백 (systemId, vendor, profile) → 성공 여부. VM 이 AID 에 기록.</summary>
    private readonly Func<Guid, PlcVendorChoice, PromakerShared.PlcVendorProfile, bool>? _saveEndpoint;

    /// <summary>System별 편집본 — 콤보 전환 시 폼을 스냅샷/복원. Apply 에서 변경분을 저장.</summary>
    private readonly Dictionary<Guid, (PlcVendorChoice Vendor, PromakerShared.PlcVendorProfile Profile)> _systemEdits = new();

    private Guid _currentSystemId;
    private bool _suppressSystemSelection;

    private bool MultiSystem => _systems is { Count: > 1 };

    // 간트 표시 윈도우 → 간트 차트 헤더 드롭다운, 자동 duration 정합 → 런타임 세팅으로 각각 이사.
    // 이 다이얼로그는 이제 System별 PLC 접속 정보만 다룬다.

    public PlcSettingsDialog(
        PlcSettings settings,
        int? autoImportedTagCount = null,
        IReadOnlyList<PlcSystemEndpointEntry>? systems = null,
        Func<Guid, PlcVendorChoice, PromakerShared.PlcVendorProfile, bool>? saveEndpoint = null)
    {
        _vm = settings;
        _systems = systems;
        _saveEndpoint = saveEndpoint;
        InitializeComponent();

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
            case PlcVendorChoice.LsXgb: RbLsXgb.IsChecked = true; break;
            case PlcVendorChoice.Mitsubishi: RbMx.IsChecked = true; break;
        }
        LoadProfileToForm(_workingProfiles[_loadedVendor.ToString()]);

        TagSummaryText.Text = autoImportedTagCount switch
        {
            null => "현재 IO 매핑은 PLAY 시점에 빌드되어 자동 import 됩니다.",
            0    => "⚠ AASX IO 매핑에서 주소가 발견되지 않았습니다. ApiCall 의 OutTag/InTag 주소를 먼저 설정하세요.",
            int n => $"AASX IO 매핑에서 {n}개 주소가 자동 import 됩니다."
        };

        // 다중 System(멀티 PLC) — 콤보 노출 + 첫 System 의 편집본을 폼에 로드.
        // 단일 System 은 기존 흐름 그대로 (VM 값 로드, Save.cs 가 AID 동기화).
        if (MultiSystem)
        {
            foreach (var entry in _systems!)
            {
                _systemEdits[entry.SystemId] = (entry.Vendor, entry.Profile.Clone());
                SystemCombo.Items.Add(entry.SystemName);
            }
            SystemSelectPanel.Visibility = Visibility.Visible;

            _suppressSystemSelection = true;
            SystemCombo.SelectedIndex = 0;
            _suppressSystemSelection = false;

            _currentSystemId = _systems[0].SystemId;
            LoadSystemEditToForm(_currentSystemId);
        }

        UpdateVendorSpecificPanels();
    }

    /// <summary>System 편집본을 폼에 로드 — 벤더 라디오 + 벤더별 작업본을 그 System 기준으로 재설정.</summary>
    private void LoadSystemEditToForm(Guid systemId)
    {
        var (vendor, profile) = _systemEdits[systemId];

        // 태그 안내도 선택 System 기준으로 — 주소의 네임스페이스는 System(PLC)이다.
        var entry = _systems?.FirstOrDefault(s => s.SystemId == systemId);
        if (entry is not null)
        {
            TagSummaryText.Text = entry.AddressCount > 0
                ? $"{entry.SystemName}: AASX IO 매핑에서 {entry.AddressCount}개 주소가 자동 import 됩니다."
                : $"⚠ {entry.SystemName}: 이 System 의 IO 매핑에서 주소가 발견되지 않았습니다. ApiCall 의 OutTag/InTag 주소를 먼저 설정하세요.";
        }

        // 벤더별 작업본을 이 System 스코프로 리셋 — 이전 System 의 벤더 토글 잔상이 새지 않게.
        _workingProfiles.Clear();
        foreach (PlcVendorChoice v in System.Enum.GetValues(typeof(PlcVendorChoice)))
            _workingProfiles[v.ToString()] = PromakerShared.PlcVendorProfile.Defaults(
                (PromakerShared.PlcVendorChoice)v);
        _workingProfiles[vendor.ToString()] = profile.Clone();

        // _loadedVendor 를 먼저 맞춰야 라디오 Checked 핸들러가 스냅샷 경로를 타지 않는다.
        _loadedVendor = vendor;
        switch (vendor)
        {
            case PlcVendorChoice.LsXgi: RbLsXgi.IsChecked = true; break;
            case PlcVendorChoice.LsXgk: RbLsXgk.IsChecked = true; break;
            case PlcVendorChoice.LsXgb: RbLsXgb.IsChecked = true; break;
            case PlcVendorChoice.Mitsubishi: RbMx.IsChecked = true; break;
        }
        LoadProfileToForm(profile);
        UpdateVendorSpecificPanels();
    }

    /// <summary>현재 폼 값을 현재 System 의 편집본으로 스냅샷.</summary>
    private void SnapshotCurrentSystemEdit()
    {
        if (!_systemEdits.TryGetValue(_currentSystemId, out var current)) return;
        _systemEdits[_currentSystemId] = (_loadedVendor, CaptureFormLenient(current.Profile));
    }

    private void SystemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSystemSelection || !MultiSystem) return;
        var index = SystemCombo.SelectedIndex;
        if (index < 0) return;

        var next = _systems![index];
        if (next.SystemId == _currentSystemId) return;

        SnapshotCurrentSystemEdit();
        _currentSystemId = next.SystemId;
        LoadSystemEditToForm(_currentSystemId);
    }

    private static bool ProfilesEqual(PromakerShared.PlcVendorProfile a, PromakerShared.PlcVendorProfile b) =>
        string.Equals(a.Name, b.Name, System.StringComparison.Ordinal)
        && string.Equals(a.IpAddress, b.IpAddress, System.StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && a.TimeoutMs == b.TimeoutMs
        && a.ScanIntervalMs == b.ScanIntervalMs
        && a.LocalEthernet == b.LocalEthernet
        && a.NetworkNumber == b.NetworkNumber
        && a.StationNumber == b.StationNumber
        && a.IsUdp == b.IsUdp;

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
          : RbLsXgb.IsChecked == true ? PlcVendorChoice.LsXgb
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

        // 다중 System — 현재 폼을 현재 System 편집본으로 확정하고 System별 AID endpoint 저장.
        // 실패한 System 이 있으면 다이얼로그를 유지해 바로 고칠 수 있게 한다.
        if (MultiSystem)
        {
            _systemEdits[_currentSystemId] = (activeVendor, activeProfile.Clone());

            var failed = new List<string>();
            foreach (var entry in _systems!)
            {
                var edit = _systemEdits[entry.SystemId];
                var changed = edit.Vendor != entry.Vendor || !ProfilesEqual(edit.Profile, entry.Profile);
                // 손대지 않은 '미보유' System 은 건드리지 않는다 — 기본값 IP 로 endpoint 가 생기는 사고 방지.
                if (!entry.HasEndpoint && !changed) continue;
                if (_saveEndpoint?.Invoke(entry.SystemId, edit.Vendor, edit.Profile) != true)
                    failed.Add(entry.SystemName);
            }
            if (failed.Count > 0)
            {
                DialogHelpers.Warn(
                    $"다음 System 의 PLC 접속 저장에 실패했습니다: {string.Join(", ", failed)}\nIP 등 필수값을 확인하세요.");
                return;
            }
        }

        // VM commit — VendorProfiles 교체 → Vendor 변경 → 플랫 필드 활성 프로파일로 적용.
        _vm.VendorProfiles.Clear();
        foreach (var kv in _workingProfiles)
            _vm.VendorProfiles[kv.Key] = kv.Value.Clone();
        _vm.Vendor = activeVendor;
        _vm.ApplyProfile(activeProfile);

        // 다음 실행 시에도 같은 값이 채워지도록 영속화.
        _vm.Save();

        DialogResult = true;
        Close();
    }
}
