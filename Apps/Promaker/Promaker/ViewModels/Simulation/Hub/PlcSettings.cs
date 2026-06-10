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
///
/// 플랫 필드는 "현재 활성 벤더" 의 값을 항상 반영한다. <see cref="VendorProfiles"/> 는
/// 세 벤더 (LsXgi, LsXgk, Mitsubishi) 각각의 마지막 입력값을 보관해, 벤더를 토글해도
/// 양식이 복원된다.
/// </summary>
public partial class PlcSettings : ObservableObject
{
    [ObservableProperty] private PlcVendorChoice _vendor = PlcVendorChoice.LsXgi;
    [ObservableProperty] private string _name = "PLC#1";
    [ObservableProperty] private string _ipAddress = "192.168.0.10";
    [ObservableProperty] private int _port = 2004;        // LS 기본 2004, MX 기본 5007
    [ObservableProperty] private int _timeoutMs = 3000;
    [ObservableProperty] private int _scanIntervalMs = PromakerShared.PlcConnectionSettings.DefaultScanIntervalMs;
    [ObservableProperty] private bool _localEthernet = true;     // LS only
    [ObservableProperty] private byte _networkNumber = 0;        // MX only
    [ObservableProperty] private byte _stationNumber = 0xFF;     // MX only

    /// <summary>Mitsubishi 전송 방식 — true=UDP, false=TCP. LS 에서는 무시 (LS 는 항상 TCP).
    /// 미쓰비시 MC 프로토콜은 PLC 측 Ethernet 모듈 파라미터(GX Works)에서 TCP/UDP 를 정해두면
    /// 클라이언트가 그 모드로 붙어야 함 — 모니터링 통신용으로 UDP 를 쓰는 현장이 흔하다.</summary>
    [ObservableProperty] private bool _isUdp = false;

    /// <summary>벤더 enum 이름 → 해당 벤더에서 마지막으로 입력했던 프로파일. POCO 와 동일 dict 를
    /// 보유해 다이얼로그 / Save 시점에 동기화. 직접 노출돼 다이얼로그가 토글 중 swap 가능.</summary>
    public Dictionary<string, PromakerShared.PlcVendorProfile> VendorProfiles { get; private set; }
        = new();

    /// <summary>플랫 필드 값을 PlcVendorProfile 로 캡처.</summary>
    public PromakerShared.PlcVendorProfile CaptureActiveProfile() => new()
    {
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

    /// <summary>지정 프로파일을 플랫 필드로 적용 (활성 벤더는 별도 인자로 받지 않고 호출자가 Vendor 를
    /// 미리 셋업했다고 가정).</summary>
    public void ApplyProfile(PromakerShared.PlcVendorProfile p)
    {
        Name = p.Name;
        IpAddress = p.IpAddress;
        Port = p.Port;
        TimeoutMs = p.TimeoutMs;
        ScanIntervalMs = p.ScanIntervalMs;
        LocalEthernet = p.LocalEthernet;
        NetworkNumber = p.NetworkNumber;
        StationNumber = p.StationNumber;
        IsUdp = p.IsUdp;
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

    /// <summary>현재 값을 JSON 으로 저장. 실패해도 throw 없이 조용히 반환 (사용자 흐름 막지 않음).
    /// 저장 직전 활성 벤더 프로파일을 현재 플랫 값으로 갱신.</summary>
    public void Save()
    {
        VendorProfiles[Vendor.ToString()] = CaptureActiveProfile();
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
        Profiles = VendorProfiles,
    };

    private static PlcSettings FromPoco(PromakerShared.PlcConnectionSettings d)
    {
        // POCO 의 EnsureProfiles 가 LoadOrDefault 안에서 호출돼 세 벤더 프로파일이 모두 채워져 있음.
        var s = new PlcSettings { VendorProfiles = d.Profiles };

        if (System.Enum.TryParse<PlcVendorChoice>(d.Vendor, ignoreCase: true, out var v))
            s.Vendor = v;

        // 활성 벤더 프로파일 (= POCO 의 플랫 필드와 동기) 을 플랫 필드로 적용.
        var key = s.Vendor.ToString();
        var profile = s.VendorProfiles.TryGetValue(key, out var p)
            ? p
            : PromakerShared.PlcVendorProfile.Defaults((PromakerShared.PlcVendorChoice)s.Vendor);
        s.ApplyProfile(profile);
        return s;
    }
}
