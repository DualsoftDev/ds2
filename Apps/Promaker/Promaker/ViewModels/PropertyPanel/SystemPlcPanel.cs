using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using PromakerShared = Promaker.Shared;

namespace Promaker.ViewModels;

/// <summary>
/// System 속성 패널의 "PLC 연결" 섹션 — System(=PLC 1대)별 접속을 트리 컨텍스트에서 바로
/// 편집한다. (구) PLC 설정 다이얼로그의 System별 접속 폼 승계.
/// Passive(수동/디바이스) 시스템은 PLC 가 없으므로 섹션 숨김.
///
/// 저장 경로가 벤더에 따라 갈린다 — AID InterfaceXGT endpoint 의 CpuModel 이 Xgi|Xgk|Xgb
/// 닫힌 DU 이기 때문이다.
///
/// * LS(XGI/XGK/XGB) → SavePlcEndpointForSystem → store(AID) 기록 → 파일 저장 시 AASX 에 실림
/// * 그 외(MICREX-SX·Mitsubishi) → SavePlcConnectionGlobally → 전역 PlcConnection.json
///
/// 후자를 AASX 에 실을 수 없는 것은 규격 구조 때문이지만, Promaker 가 스캔에 실제로 쓰는 값은
/// 전역 PlcSettings 이므로(BuildPlcGatewayConfig) 동작에는 문제가 없다. 예전에는 이 경우
/// 저장이 조용히 거부되고 "입력값을 확인하세요" 만 나와, 어떤 값을 넣어도 안 되는 막다른
/// 길이었다 — 그래서 SX 는 UI 로 설정할 방법이 아예 없었다.
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

    // ── MICREX-SX 전용 입력 ────────────────────────────────────────────────
    // 이 두 값은 AID InterfaceXGT 에 실을 자리가 없어 전역 PLC 연결(PlcConnection.json)에
    // 저장된다. 그래도 여기서 편집하게 두는 이유: Promaker 에는 전역 연결 편집 UI 가 따로
    // 없고(실행 설정에서 상태만 보여준다), 사용자는 이미 이 패널에서 IP·포트를 넣고 있다.
    // 다른 화면으로 보내면 "어디서 설정하나" 가 되고, 그게 실제로 일어난 일이다.

    /// <summary>D300win 프로젝트에서 뽑은 I/O 매핑표 경로. 비우면 네이티브 주소만 쓴다.</summary>
    [ObservableProperty] private string _plcSxIoMapPath = string.Empty;

    /// <summary>쓰기 허용 — 켜면 사용자 메모리(M1)에만 쓴다. 해제면 읽기 전용이다.
    ///
    /// 영역별 체크박스 세 개(M1/IO/M10)를 두었더니 무엇을 켜야 하는지가 되물어졌다.
    /// I/O 이미지와 시스템 메모리는 잘못 쓰면 설비를 움직이는 영역이라 화면에서 고를 값이
    /// 아니다 — 필요한 현장은 PlcConnection.json 에 직접 적는다(아래 요약이 그 상태를 보여준다).</summary>
    [ObservableProperty] private bool _plcSxWriteAllow;

    /// <summary>모니터링 모드에서는 쓰기를 고를 수 없다 — 읽어서 상태만 추적하는 모드다.
    /// 나머지 모드(제어·가상플랜트·시뮬레이션)에서는 선택 가능.</summary>
    public bool IsPlcSxWriteSelectable =>
        _host.Simulation.SelectedRuntimeMode != RuntimeMode.Monitoring;

    /// <summary>설정 파일에 M1 외의 영역이 들어 있었으면 그 사실을 잊지 않기 위해 보관한다.
    /// 화면에서 껐다 켜는 것으로 조용히 사라지면 안 되는 정보다.</summary>
    private List<string> _plcSxExtraAreas = new();

    /// <summary>토글 → SX 커넥터가 받는 영역 이름 목록. 빈 목록 = 쓰기 원천 차단.</summary>
    private List<string> SxWritableAreas() =>
        PlcSxWriteAllow ? new List<string> { "M1" } : new List<string>();

    /// <summary>쓰기 허용 상태를 한 줄로 — 잠김 여부가 화면에서 바로 보여야 한다.</summary>
    public string PlcSxWriteSummary
    {
        get
        {
            if (!IsPlcSxWriteSelectable)
                return "모니터링 모드 — 쓰기가 원천 차단됩니다 (읽어서 상태만 추적)";
            if (!PlcSxWriteAllow)
                return "읽기 전용 — 쓰기가 원천 차단됩니다";
            if (_plcSxExtraAreas.Count > 0)
                return $"쓰기 허용: M1 사용자 메모리  ⚠ 설정 파일에는 {string.Join("/", _plcSxExtraAreas)} 도 있습니다 — 저장하면 M1 만 남습니다";
            return "쓰기 허용: M1 사용자 메모리";
        }
    }

    /// <summary>런타임 모드가 바뀌면 쓰기 선택 가능 여부가 달라진다. 모니터링으로 들어가면
    /// 자동으로 선택을 해제한다 — 비활성만 하고 체크를 남겨 두면 저장 시 쓰기가 열린다.</summary>
    internal void OnRuntimeModeChangedForPlc()
    {
        if (!IsPlcSxWriteSelectable && PlcSxWriteAllow)
            PlcSxWriteAllow = false;
        OnPropertyChanged(nameof(IsPlcSxWriteSelectable));
        OnPropertyChanged(nameof(PlcSxWriteSummary));
    }

    public IReadOnlyList<PlcVendorChoice> PlcVendorChoices { get; } =
        (PlcVendorChoice[])Enum.GetValues(typeof(PlcVendorChoice));

    public bool IsPlcVendorMx => PlcVendor == PlcVendorChoice.Mitsubishi;
    public bool IsPlcVendorSx => PlcVendor == PlcVendorChoice.MicrexSx;
    public bool IsPlcVendorLs =>
        PromakerShared.PlcVendorProfile.IsAidXgtVendor((PromakerShared.PlcVendorChoice)PlcVendor);

    public string SystemPlcHeader =>
        // Mitsubishi 는 아직 AID 표현이 없어 전역 연결에만 저장된다 — "⚠ 미지정" 으로 보이면
        // 설정이 안 된 줄 안다. SX 는 InterfaceMicrexSx 로 AASX 에 실리므로 여기 해당하지 않는다.
        IsPlcVendorMx       ? $"PLC 연결 · {PlcVendor} 전역 · 주소 {PlcAddressCount}개"
        : PlcIsLegacyEndpoint ? $"PLC 연결 · 구버전 — 저장 시 이 System 에 귀속 · 주소 {PlcAddressCount}개"
        : PlcHasEndpoint    ? $"PLC 연결 · 주소 {PlcAddressCount}개"
                            : $"PLC 연결 · ⚠ 미지정 · 주소 {PlcAddressCount}개";

    /// <summary>패널 로드 시 원본 스냅샷 — dirty 판정 기준. Refresh 중 재발화 방지용 suppress 와 짝.</summary>
    private (PlcVendorChoice Vendor, string Ip, int Port, int Timeout, int Scan,
             bool Eth, int Net, int Stn, bool Udp) _plcOriginal;
    /// <summary>SX 전용 값의 원본 스냅샷 — 전역 연결에서 읽어 온 값이 기준이다.</summary>
    private (string IoMap, bool WriteAllow) _plcSxOriginal;
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
            || PlcIsUdp != _plcOriginal.Udp
            || (IsPlcVendorSx && SxSettingsChanged());
    }

    private bool SxSettingsChanged() =>
        !string.Equals((PlcSxIoMapPath ?? "").Trim(), _plcSxOriginal.IoMap, StringComparison.OrdinalIgnoreCase)
        || PlcSxWriteAllow != _plcSxOriginal.WriteAllow;

    partial void OnPlcVendorChanged(PlcVendorChoice value)
    {
        OnPropertyChanged(nameof(IsPlcVendorMx));
        OnPropertyChanged(nameof(IsPlcVendorSx));
        OnPropertyChanged(nameof(IsPlcVendorLs));
        OnPropertyChanged(nameof(SystemPlcHeader));

        // 벤더를 바꿨는데 포트가 그대로면 틀린 포트로 붙는다 — SX 를 골라도 LS 의 2004 가
        // 남아 있었다. 사용자가 직접 넣은 포트는 건드리지 않고, "어떤 벤더의 기본 포트"
        // 상태일 때만 새 벤더의 기본값으로 옮긴다. Refresh 중에는 저장된 값이 기준이므로 제외.
        if (!_suppressPlcDirty)
        {
            if (PromakerShared.PlcVendorProfile.IsAnyVendorDefaultPort(PlcPort))
                PlcPort = PromakerShared.PlcVendorProfile.Defaults(
                    (PromakerShared.PlcVendorChoice)value).Port;
        }

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
    partial void OnPlcSxIoMapPathChanged(string value) => UpdatePlcDirty();
    partial void OnPlcSxWriteAllowChanged(bool value)
    {
        OnPropertyChanged(nameof(PlcSxWriteSummary));
        UpdatePlcDirty();
    }
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
        var global = sim.PlcSettings;
        // SX 는 InterfaceMicrexSx endpoint 가 정본이다. 이것을 XGT 보다 먼저 보는 이유:
        // 벤더를 SX 로 저장하면 상대 바인딩이 지워지지만, 읽는 순서를 정해 두지 않으면
        // 과거 파일처럼 둘이 함께 있는 경우 화면이 흔들린다.
        var sxConn = PromakerShared.AidMicrexSxEndpointSynchronizer.TryReadFromStore(Store, systemId);
        // Mitsubishi 는 아직 AID 표현이 없어 전역 연결이 유일한 저장소다.
        var globalIsMxOnly = global.Vendor == PlcVendorChoice.Mitsubishi && sxConn is null;

        _suppressPlcDirty = true;
        try
        {
            if (sxConn is not null)
            {
                PlcHasEndpoint = true;
                PlcIsLegacyEndpoint = false;
                PlcVendor = PlcVendorChoice.MicrexSx;
                PlcIpAddress = sxConn.IpAddress;
                PlcPort = sxConn.Port;
                PlcTimeoutMs = sxConn.TimeoutMs > 0 ? sxConn.TimeoutMs : 3000;
                PlcScanIntervalMs = sxConn.ScanIntervalMs > 0 ? sxConn.ScanIntervalMs : 100;
                PlcLocalEthernet = true;
                PlcNetworkNumber = 0;
                PlcStationNumber = 0;
                PlcIsUdp = false;
            }
            else if (globalIsMxOnly)
            {
                var defaults = PromakerShared.PlcVendorProfile.Defaults(
                    (PromakerShared.PlcVendorChoice)global.Vendor);
                PlcHasEndpoint = false;
                PlcIsLegacyEndpoint = false;
                PlcVendor = global.Vendor;
                PlcIpAddress = global.IpAddress ?? string.Empty;
                PlcPort = global.Port > 0 ? global.Port : defaults.Port;
                PlcTimeoutMs = global.TimeoutMs > 0 ? global.TimeoutMs : defaults.TimeoutMs;
                PlcScanIntervalMs = global.ScanIntervalMs > 0 ? global.ScanIntervalMs : defaults.ScanIntervalMs;
                PlcLocalEthernet = global.LocalEthernet;
                PlcNetworkNumber = global.NetworkNumber;
                PlcStationNumber = global.StationNumber;
                PlcIsUdp = global.IsUdp;
            }
            else if (conn is not null
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

            // SX 전용 값의 정본은 endpoint 다. endpoint 가 아직 없으면(벤더를 방금 SX 로 바꾼
            // 경우) 전역 연결의 값을 초기값으로 보여 준다 — 직전에 입력하던 값이 사라지지 않는다.
            PlcSxIoMapPath = sxConn is not null
                ? (sxConn.IoMapPath ?? string.Empty)
                : (global.SxIoMapPath ?? string.Empty);
            var writable = sxConn is not null
                ? sxConn.WritableAreas.ToList()
                : (global.SxWritableAreas ?? new List<string>());
            _plcSxExtraAreas = writable
                .Where(a => !string.Equals(a, "M1", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // 모니터링 모드에서는 저장된 값이 무엇이든 화면에서 쓰기를 켜 두지 않는다.
            PlcSxWriteAllow = writable.Count > 0 && IsPlcSxWriteSelectable;
            _plcSxOriginal = ((PlcSxIoMapPath ?? "").Trim(), PlcSxWriteAllow);
            OnPropertyChanged(nameof(IsPlcSxWriteSelectable));
            OnPropertyChanged(nameof(PlcSxWriteSummary));

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

    /// <summary>I/O 매핑표(io_map.json) 파일 선택. 경로를 손으로 적게 하면 오타 하나로
    /// IEC 원격 주소가 전부 실패한다.</summary>
    [RelayCommand]
    private void BrowseSxIoMap()
    {
        var dlg = new OpenFileDialog
        {
            Title = "MICREX-SX I/O 매핑표 선택",
            Filter = "I/O 매핑표 (*.json)|*.json|모든 파일 (*.*)|*.*",
            DefaultExt = "json",
        };
        var current = (PlcSxIoMapPath ?? "").Trim();
        if (current.Length > 0)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
                dlg.FileName = System.IO.Path.GetFileName(current);
            }
            catch (ArgumentException) { /* 잘못된 경로 문자 — 초기 위치 없이 연다 */ }
        }

        if (dlg.ShowDialog() == true)
            PlcSxIoMapPath = dlg.FileName;
    }

    /// <summary>매핑표 지정 해제 — 네이티브 주소(M1.2000.0)만 쓰는 상태로 되돌린다.</summary>
    [RelayCommand]
    private void ClearSxIoMap() => PlcSxIoMapPath = string.Empty;

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

        if (!_host.Simulation.SavePlcEndpointForSystem(
                systemNode.Id, PlcVendor, profile, (PlcSxIoMapPath ?? "").Trim(), SxWritableAreas()))
        {
            _host.ShowWarning(
                IsPlcVendorMx
                    ? "전역 PLC 연결 저장에 실패했습니다 (PlcConnection.json 쓰기 실패)."
                    : "PLC 접속 저장에 실패했습니다. 입력값을 확인하세요.");
            return;
        }

        var writeState = IsPlcVendorSx
            ? (SxWritableAreas().Count == 0 ? " · 읽기 전용" : $" · 쓰기 {string.Join("/", SxWritableAreas())}")
            : "";
        // Mitsubishi 만 AID 표현이 없어 AASX 에 실리지 않는다.
        var destination = IsPlcVendorMx
            ? "전역 PLC 연결 · AASX 에는 기록되지 않습니다"
            : "파일 저장 시 AASX 에 기록";
        _host.SetStatusText(
            $"'{systemNode.Name}' PLC 접속 저장됨 — {PlcVendor} {ip}:{PlcPort}{writeState} ({destination})");
        RefreshSystemPlcPanel(systemNode.Id, isPassive: false);
    }
}
