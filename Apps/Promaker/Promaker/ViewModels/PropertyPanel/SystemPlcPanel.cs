using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using PromakerShared = Promaker.Shared;

namespace Promaker.ViewModels;

/// <summary>
/// System 속성 패널의 "PLC 연결" 섹션 — System(=PLC 1대)별 AID InterfaceXGT endpoint 를
/// 트리 컨텍스트에서 바로 편집한다. (구) PLC 설정 다이얼로그의 System별 접속 폼 승계.
/// 저장 = SimulationPanelState.SavePlcEndpointForSystem → store(AID) 기록 → 파일 저장 시 AASX 에 실림.
/// Passive(수동/디바이스) 시스템은 PLC 가 없으므로 섹션 숨김.
/// </summary>
public partial class PropertyPanelState
{
    [ObservableProperty] private bool _showSystemPlc;
    [ObservableProperty] private bool _plcHasEndpoint;
    /// <summary>구버전(systemRef 없는) endpoint 표시 중 — 저장하면 이 System 으로 귀속(claim)된다.</summary>
    [ObservableProperty] private bool _plcIsLegacyEndpoint;
    [ObservableProperty] private int _plcAddressCount;
    [ObservableProperty] private PlcVendorChoice _plcVendor = PlcVendorChoice.LsXgi;
    [ObservableProperty] private string _plcIpAddress = string.Empty;
    [ObservableProperty] private int _plcPort = 2004;
    [ObservableProperty] private int _plcTimeoutMs = 3000;
    [ObservableProperty] private int _plcScanIntervalMs = 100;
    [ObservableProperty] private bool _plcLocalEthernet = true;
    [ObservableProperty] private int _plcNetworkNumber;
    [ObservableProperty] private int _plcStationNumber = 0xFF;
    [ObservableProperty] private bool _plcIsUdp;
    [ObservableProperty] private bool _isPlcDirty;

    public IReadOnlyList<PlcVendorChoice> PlcVendorChoices { get; } =
        (PlcVendorChoice[])Enum.GetValues(typeof(PlcVendorChoice));

    public bool IsPlcVendorMx => PlcVendor == PlcVendorChoice.Mitsubishi;
    public bool IsPlcVendorLs => !IsPlcVendorMx;

    public string SystemPlcHeader =>
        PlcIsLegacyEndpoint ? $"PLC 연결 · 구버전 — 저장 시 이 System 에 귀속 · 주소 {PlcAddressCount}개"
        : PlcHasEndpoint   ? $"PLC 연결 · 주소 {PlcAddressCount}개"
                           : $"PLC 연결 · ⚠ 미지정 · 주소 {PlcAddressCount}개";

    /// <summary>패널 로드 시 원본 스냅샷 — dirty 판정 기준. Refresh 중 재발화 방지용 suppress 와 짝.</summary>
    private (PlcVendorChoice Vendor, string Ip, int Port, int Timeout, int Scan,
             bool Eth, int Net, int Stn, bool Udp) _plcOriginal;
    private bool _suppressPlcDirty;

    private void UpdatePlcDirty()
    {
        if (_suppressPlcDirty) return;
        // 구버전(무주인) endpoint 는 값이 같아도 저장할 변경(systemRef 귀속)이 남아 있다 — 항상 저장 가능.
        IsPlcDirty =
            PlcIsLegacyEndpoint
            || PlcVendor != _plcOriginal.Vendor
            || !string.Equals((PlcIpAddress ?? "").Trim(), _plcOriginal.Ip, StringComparison.OrdinalIgnoreCase)
            || PlcPort != _plcOriginal.Port
            || PlcTimeoutMs != _plcOriginal.Timeout
            || PlcScanIntervalMs != _plcOriginal.Scan
            || PlcLocalEthernet != _plcOriginal.Eth
            || PlcNetworkNumber != _plcOriginal.Net
            || PlcStationNumber != _plcOriginal.Stn
            || PlcIsUdp != _plcOriginal.Udp;
    }

    partial void OnPlcVendorChanged(PlcVendorChoice value)
    {
        OnPropertyChanged(nameof(IsPlcVendorMx));
        OnPropertyChanged(nameof(IsPlcVendorLs));
        UpdatePlcDirty();
    }
    partial void OnPlcIpAddressChanged(string value) => UpdatePlcDirty();
    partial void OnPlcPortChanged(int value) => UpdatePlcDirty();
    partial void OnPlcTimeoutMsChanged(int value) => UpdatePlcDirty();
    partial void OnPlcScanIntervalMsChanged(int value) => UpdatePlcDirty();
    partial void OnPlcLocalEthernetChanged(bool value) => UpdatePlcDirty();
    partial void OnPlcNetworkNumberChanged(int value) => UpdatePlcDirty();
    partial void OnPlcStationNumberChanged(int value) => UpdatePlcDirty();
    partial void OnPlcIsUdpChanged(bool value) => UpdatePlcDirty();
    partial void OnPlcHasEndpointChanged(bool value) => OnPropertyChanged(nameof(SystemPlcHeader));
    partial void OnPlcIsLegacyEndpointChanged(bool value) => OnPropertyChanged(nameof(SystemPlcHeader));
    partial void OnPlcAddressCountChanged(int value) => OnPropertyChanged(nameof(SystemPlcHeader));

    /// <summary>선택된 System 의 AID endpoint 를 섹션 필드로 로드. Passive 면 섹션 숨김.</summary>
    private void RefreshSystemPlcPanel(Guid systemId, bool isPassive)
    {
        ShowSystemPlc = !isPassive;
        if (!ShowSystemPlc)
        {
            IsPlcDirty = false;
            return;
        }

        var sim = _host.Simulation;
        PlcAddressCount = sim.EnumeratePlcAddressesForSystem(systemId).Count;

        var conn = PromakerShared.AidXgtEndpointSynchronizer.TryReadFromStore(Store, systemId);
        var legacyUnassigned = false;
        if (conn is null)
        {
            // 구버전(8/5~8/20, systemRef 없는) endpoint 표시 폴백 — 단일 System 프로젝트만(소유 모호성 없음).
            // 저장하면 EnsureBindingForSystem 의 "무주인 endpoint 1개 claim" 규칙이 이 System 으로 귀속시킨다.
            conn = PromakerShared.AidXgtEndpointSynchronizer.TryReadLegacyUnassigned(Store);
            legacyUnassigned = conn is not null;
        }
        _suppressPlcDirty = true;
        try
        {
            if (conn is not null
                && Enum.TryParse<PlcVendorChoice>(conn.Vendor, ignoreCase: true, out var vendor))
            {
                PlcHasEndpoint = !legacyUnassigned;
                PlcIsLegacyEndpoint = legacyUnassigned;
                PlcVendor = vendor;
                PlcIpAddress = conn.IpAddress;
                PlcPort = conn.Port;
                PlcTimeoutMs = conn.TimeoutMs > 0 ? conn.TimeoutMs : 3000;
                PlcScanIntervalMs = conn.ScanIntervalMs > 0 ? conn.ScanIntervalMs : 100;
                PlcLocalEthernet = conn.LocalEthernet;
                PlcNetworkNumber = conn.NetworkNumber;
                PlcStationNumber = conn.StationNumber;
                PlcIsUdp = conn.IsUdp;
            }
            else
            {
                // endpoint 미보유 — 현재 화면 벤더의 기본 프로파일로 시작하되 IP 는 비워
                // 사용자가 명시 입력해야만 저장되게 한다(기본 IP 로 endpoint 가 생기는 사고 방지).
                PlcHasEndpoint = false;
                PlcIsLegacyEndpoint = false;
                var fallbackVendor = sim.PlcSettings.Vendor;
                var defaults = PromakerShared.PlcVendorProfile.Defaults(
                    (PromakerShared.PlcVendorChoice)fallbackVendor);
                PlcVendor = fallbackVendor;
                PlcIpAddress = string.Empty;
                PlcPort = defaults.Port;
                PlcTimeoutMs = defaults.TimeoutMs;
                PlcScanIntervalMs = defaults.ScanIntervalMs;
                PlcLocalEthernet = defaults.LocalEthernet;
                PlcNetworkNumber = defaults.NetworkNumber;
                PlcStationNumber = defaults.StationNumber;
                PlcIsUdp = defaults.IsUdp;
            }

            _plcOriginal = (PlcVendor, (PlcIpAddress ?? "").Trim(), PlcPort, PlcTimeoutMs,
                            PlcScanIntervalMs, PlcLocalEthernet, PlcNetworkNumber,
                            PlcStationNumber, PlcIsUdp);
            // 구버전 endpoint 는 값 동일해도 귀속(claim) 커밋이 남아 있어 저장 버튼을 열어 둔다.
            IsPlcDirty = PlcIsLegacyEndpoint;
        }
        finally
        {
            _suppressPlcDirty = false;
        }
    }

    private void ClearSystemPlcPanel()
    {
        ShowSystemPlc = false;
        PlcIsLegacyEndpoint = false;
        IsPlcDirty = false;
    }

    [RelayCommand]
    private void ApplySystemPlc()
    {
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;
        if (!GuardSimulationSemanticEdit("PLC 접속 편집")) return;

        var ip = (PlcIpAddress ?? "").Trim();
        if (ip.Length == 0)
        {
            _host.ShowWarning("IP 주소를 입력하세요.");
            return;
        }
        if (PlcPort is <= 0 or > 65535)
        {
            _host.ShowWarning("Port 는 1–65535 범위 정수여야 합니다.");
            return;
        }
        if (PlcTimeoutMs <= 0 || PlcScanIntervalMs <= 0)
        {
            _host.ShowWarning("Timeout/Scan(ms) 은 양의 정수여야 합니다.");
            return;
        }
        if (PlcNetworkNumber is < 0 or > 255 || PlcStationNumber is < 0 or > 255)
        {
            _host.ShowWarning("Network/Station No. 는 0–255 범위여야 합니다.");
            return;
        }

        var profile = new PromakerShared.PlcVendorProfile
        {
            Name = systemNode.Name,
            IpAddress = ip,
            Port = PlcPort,
            TimeoutMs = PlcTimeoutMs,
            ScanIntervalMs = PlcScanIntervalMs,
            LocalEthernet = PlcLocalEthernet,
            NetworkNumber = (byte)PlcNetworkNumber,
            StationNumber = (byte)PlcStationNumber,
            IsUdp = PlcIsUdp,
        };

        if (!_host.Simulation.SavePlcEndpointForSystem(systemNode.Id, PlcVendor, profile))
        {
            _host.ShowWarning("PLC 접속 저장에 실패했습니다. 입력값을 확인하세요.");
            return;
        }

        _host.SetStatusText(
            $"'{systemNode.Name}' PLC 접속 저장됨 — {PlcVendor} {ip}:{PlcPort} (파일 저장 시 AASX 에 기록)");
        RefreshSystemPlcPanel(systemNode.Id, isPassive: false);
    }
}
