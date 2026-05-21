using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Backend.Plc;
using Ds2.Runtime.IO;
using Promaker.Services;
using PromakerShared = Promaker.Shared;

namespace Promaker.ViewModels;

// PlcVendorChoice — 다이얼로그 ComboBox 가 직접 바인딩하므로 Promaker.ViewModels 네임스페이스에
// 그대로 두되, 실제 정의는 Promaker.Shared 의 POCO 와 동기. 별칭 export.
public enum PlcVendorChoice
{
    LsXgi = PromakerShared.PlcVendorChoice.LsXgi,
    LsXgk = PromakerShared.PlcVendorChoice.LsXgk,
    Mitsubishi = PromakerShared.PlcVendorChoice.Mitsubishi,
}

/// <summary>
/// PLC 연결 정보 MVVM ViewModel. UI 입력 값을 Promaker.Shared.PlcConnectionSettings POCO 로
/// 저장/로드하여 Promaker.Agent (SYSTEM 컨텍스트) 와 동일 파일을 공유한다.
///
/// PLC 게이트웨이 빌드는 PlcGatewayConfigBuilder 에 위임 — Agent 가 동일 로직 재사용.
/// 저장 위치는 SharedPaths.PlcConnectionFilePath (공유 ProgramData) 가 SSOT.
/// </summary>
public partial class PlcSettings : ObservableObject
{
    [ObservableProperty] private PlcVendorChoice _vendor = PlcVendorChoice.LsXgi;
    [ObservableProperty] private string _name = "PLC#1";
    [ObservableProperty] private string _ipAddress = "192.168.0.10";
    [ObservableProperty] private int _port = 2004;        // LS 기본 2004, MX 기본 5007 — Vendor 변경 시 자동 갱신
    [ObservableProperty] private int _timeoutMs = 3000;
    [ObservableProperty] private int _scanIntervalMs = 100;
    [ObservableProperty] private bool _localEthernet = true;     // LS only
    [ObservableProperty] private byte _networkNumber = 0;        // MX only
    [ObservableProperty] private byte _stationNumber = 0xFF;     // MX only

    /// <summary>Mitsubishi 전송 방식 — true=UDP, false=TCP. LS 에서는 무시 (LS 는 항상 TCP).
    /// 미쓰비시 MC 프로토콜은 PLC 측 Ethernet 모듈 파라미터(GX Works)에서 TCP/UDP 를 정해두면
    /// 클라이언트가 그 모드로 붙어야 함 — 모니터링 통신용으로 UDP 를 쓰는 현장이 흔하다.</summary>
    [ObservableProperty] private bool _isUdp = false;

    partial void OnVendorChanged(PlcVendorChoice value)
    {
        // 벤더 전환 시 기본 포트 자동 적용 (이전 값이 다른 벤더 기본값일 때만 덮어써 의도치 않은 손상 방지).
        if (Port == 2004 || Port == 5007)
            Port = value == PlcVendorChoice.Mitsubishi ? 5007 : 2004;
    }

    /// <summary>
    /// SignalIOMap 의 OUT/IN 주소를 그대로 PLC 태그 리스트로 자동 채워 F# PlcGatewayConfig 빌드.
    /// 실제 빌드는 PlcGatewayConfigBuilder 에 위임 — Agent 와 동일 코드 경로.
    /// </summary>
    public PlcGatewayConfig? BuildGatewayConfig(
        SignalIOMap ioMap,
        out List<string> errors,
        IEnumerable<string>? extraAddresses = null)
    {
        return PromakerShared.PlcGatewayConfigBuilder.TryBuild(
            ToPoco(), ioMap, out errors, extraAddresses);
    }

    // ── 영속화 — Promaker.Shared.PlcConnectionSettings 로 위임. ─────────
    // 저장 위치는 SharedPaths.PlcConnectionFilePath (공유 ProgramData) — Agent 와 동일 파일.
    // 옛 경로(%AppData%\Dualsoft\Promaker\Settings\PlcConnection.json) 는 첫 Load 시 자동 마이그레이션.

    /// <summary>저장된 설정을 읽어 새 PlcSettings 인스턴스 생성. 파일 없으면 default.</summary>
    public static PlcSettings LoadOrDefault()
    {
        // 첫 Load 시 옛 위치에 파일이 있으면 신 공유 경로로 1회 복사 — 사용자 설정 보존.
        PromakerShared.PlcConnectionSettings.MigrateLegacyIfNeeded(SettingsPaths.PlcConnection);

        var poco = PromakerShared.PlcConnectionSettings.LoadOrDefault(
            PromakerShared.SharedPaths.PlcConnectionFilePath);
        return FromPoco(poco);
    }

    /// <summary>현재 값을 JSON 으로 저장. 실패해도 throw 없이 조용히 반환 (사용자 흐름 막지 않음).</summary>
    public void Save()
    {
        ToPoco().TrySave(PromakerShared.SharedPaths.PlcConnectionFilePath);
    }

    private PromakerShared.PlcConnectionSettings ToPoco() => new()
    {
        Vendor = Vendor.ToString(),
        Name = Name,
        IpAddress = IpAddress,
        Port = Port,
        TimeoutMs = TimeoutMs,
        ScanIntervalMs = ScanIntervalMs,
        LocalEthernet = LocalEthernet,
        NetworkNumber = NetworkNumber,
        StationNumber = StationNumber,
        IsUdp = IsUdp,
    };

    private static PlcSettings FromPoco(PromakerShared.PlcConnectionSettings d)
    {
        var s = new PlcSettings
        {
            Name = d.Name ?? "PLC#1",
            IpAddress = d.IpAddress ?? "192.168.0.10",
            Port = d.Port > 0 ? d.Port : 2004,
            TimeoutMs = d.TimeoutMs > 0 ? d.TimeoutMs : 3000,
            ScanIntervalMs = d.ScanIntervalMs > 0 ? d.ScanIntervalMs : 100,
            LocalEthernet = d.LocalEthernet,
            NetworkNumber = d.NetworkNumber,
            StationNumber = d.StationNumber,
            IsUdp = d.IsUdp,
        };
        // Vendor 는 setter 가 Port 를 갱신할 수 있으므로 Port 설정 이후 마지막에 적용.
        // 단, JSON 으로 저장된 Port 가 새 벤더의 기본값(2004/5007)과 다르면 그대로 유지하기 위해
        // OnVendorChanged 의 가드 (Port==2004||5007 일 때만 덮어쓰기) 를 신뢰.
        if (System.Enum.TryParse<PlcVendorChoice>(d.Vendor, ignoreCase: true, out var v))
            s.Vendor = v;
        return s;
    }
}
