using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Promaker.Shared;

/// <summary>PLC 벤더 선택 — Promaker WPF 다이얼로그와 Agent 양쪽이 공유.</summary>
public enum PlcVendorChoice
{
    LsXgi,
    LsXgk,
    Mitsubishi
}

/// <summary>
/// 벤더별 연결 파라미터 프로파일. <see cref="PlcConnectionSettings.Profiles"/> 에 벤더 enum 이름을
/// 키로 저장돼 사용자가 벤더를 바꿔도 그 벤더에 입력했던 값이 그대로 복원된다.
/// </summary>
public sealed class PlcVendorProfile
{
    public string Name { get; set; } = "PLC#1";
    public string IpAddress { get; set; } = "192.168.0.10";
    public int Port { get; set; } = 2004;
    public int TimeoutMs { get; set; } = 3000;
    public int ScanIntervalMs { get; set; } = PlcConnectionSettings.DefaultScanIntervalMs;
    public bool LocalEthernet { get; set; } = true;
    public byte NetworkNumber { get; set; } = 0;
    public byte StationNumber { get; set; } = 0xFF;
    public bool IsUdp { get; set; } = false;

    public static PlcVendorProfile Defaults(PlcVendorChoice vendor) => vendor switch
    {
        PlcVendorChoice.Mitsubishi => new PlcVendorProfile { Port = 5007 },
        _ => new PlcVendorProfile { Port = 2004 },   // LsXgi, LsXgk
    };

    public PlcVendorProfile Clone() => new()
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
}

/// <summary>
/// PLC 연결 설정 POCO. JSON 직렬화/역직렬화 단일 책임.
/// Promaker WPF 의 PlcSettings(ObservableObject) 와 Promaker.Agent 의 부트스트랩이
/// 모두 이 POCO 를 읽고 쓴다. MVVM 의존성 없음.
///
/// 영속화 경로는 <see cref="SharedPaths.PlcConnectionFilePath"/> 가 기본 — Promaker.Agent (SYSTEM)
/// 가 같은 파일을 보기 위해 사용자 AppData 가 아닌 ProgramData 에 위치.
/// 옛 경로(%AppData%\Dualsoft\Promaker\Settings\PlcConnection.json) 에만 파일이 있으면
/// Load 시 자동 마이그레이션.
///
/// 최상위 플랫 필드(Vendor, IpAddress, Port…) 는 "현재 활성 벤더" 의 값으로 — Agent 와
/// <see cref="PlcGatewayConfigBuilder"/> 가 그대로 읽는다. <see cref="Profiles"/> 는 모든 벤더의
/// 직전 입력값을 보관해, 사용자가 벤더를 토글해도 각 벤더 양식이 복원되도록 한다.
/// </summary>
public sealed class PlcConnectionSettings
{
    public const int DefaultScanIntervalMs = 50;
    private const int PreviousDefaultScanIntervalMs = 100;

    public string Vendor { get; set; } = nameof(PlcVendorChoice.LsXgi);
    public string Name { get; set; } = "PLC#1";
    public string IpAddress { get; set; } = "192.168.0.10";
    public int Port { get; set; } = 2004;
    public int TimeoutMs { get; set; } = 3000;
    public int ScanIntervalMs { get; set; } = DefaultScanIntervalMs;
    public bool LocalEthernet { get; set; } = true;
    public byte NetworkNumber { get; set; } = 0;
    public byte StationNumber { get; set; } = 0xFF;
    public bool IsUdp { get; set; } = false;

    /// <summary>벤더 enum 이름 → 해당 벤더의 마지막 입력값. 빈 dict 로 저장된 옛 파일은
    /// <see cref="EnsureProfiles"/> 가 플랫 필드로부터 채워준다.</summary>
    public Dictionary<string, PlcVendorProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>지정 경로에서 JSON 로드. 없으면 기본값. 손상되면 silent fallback.
    /// 로드 후 <see cref="EnsureProfiles"/> 로 3 벤더 모두 프로파일이 채워진 상태를 보장.</summary>
    public static PlcConnectionSettings LoadOrDefault(string path)
    {
        PlcConnectionSettings data;
        try
        {
            if (!File.Exists(path)) data = new PlcConnectionSettings();
            else
            {
                var text = File.ReadAllText(path);
                data = JsonSerializer.Deserialize<PlcConnectionSettings>(text, JsonOpts)
                       ?? new PlcConnectionSettings();
            }
        }
        catch
        {
            data = new PlcConnectionSettings();
        }
        data.EnsureProfiles();
        data.UpgradeDefaultScanIntervals();
        return data;
    }

    private void UpgradeDefaultScanIntervals()
    {
        ScanIntervalMs = UpgradeDefaultScanInterval(ScanIntervalMs);

        if (Profiles == null)
            return;

        foreach (var profile in Profiles.Values)
            profile.ScanIntervalMs = UpgradeDefaultScanInterval(profile.ScanIntervalMs);
    }

    private static int UpgradeDefaultScanInterval(int value) =>
        value == PreviousDefaultScanIntervalMs ? DefaultScanIntervalMs : value;

    /// <summary>모든 벤더 키에 프로파일이 존재하도록 보장. 활성 벤더 프로파일은 현재 플랫 필드와
    /// 동기화 (저장 시점의 현재값이 SSOT).</summary>
    public void EnsureProfiles()
    {
        Profiles ??= new Dictionary<string, PlcVendorProfile>(StringComparer.OrdinalIgnoreCase);

        // 활성 벤더 프로파일을 플랫 필드 스냅샷으로 갱신 — 옛 파일(profiles 미존재) 마이그레이션 포함.
        Profiles[Vendor] = SnapshotFlatToProfile();

        // 나머지 벤더는 기존 프로파일 유지, 없으면 기본값.
        foreach (PlcVendorChoice v in Enum.GetValues(typeof(PlcVendorChoice)))
        {
            var key = v.ToString();
            if (!Profiles.ContainsKey(key))
                Profiles[key] = PlcVendorProfile.Defaults(v);
        }
    }

    /// <summary>현재 플랫 필드를 PlcVendorProfile 로 스냅샷.</summary>
    public PlcVendorProfile SnapshotFlatToProfile() => new()
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

    /// <summary>지정 벤더 프로파일을 플랫 필드로 적용. 프로파일 없으면 기본값 사용.</summary>
    public void ApplyProfileToFlat(PlcVendorChoice vendor)
    {
        var key = vendor.ToString();
        if (Profiles == null || !Profiles.TryGetValue(key, out var p))
            p = PlcVendorProfile.Defaults(vendor);

        Vendor = key;
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

    /// <summary>지정 경로에 JSON 저장. 디렉터리 자동 생성. 실패해도 throw 없이 false 반환.
    /// 저장 직전 <see cref="EnsureProfiles"/> 로 활성 벤더 프로파일을 최신 플랫 값으로 동기화.</summary>
    public bool TrySave(string path)
    {
        try
        {
            EnsureProfiles();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var text = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(path, text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>옛 경로(%AppData%\Dualsoft\Promaker\Settings\PlcConnection.json) → 신 공유 경로
    /// 1회 마이그레이션. 신 경로에 이미 파일 있으면 no-op.
    /// Promaker WPF 가 첫 Load 직전 호출하면 옛 사용자 설정을 신 위치에서 자연스럽게 보게 된다.</summary>
    public static void MigrateLegacyIfNeeded(string legacyPath)
    {
        try
        {
            var newPath = SharedPaths.PlcConnectionFilePath;
            if (File.Exists(newPath)) return;
            if (!File.Exists(legacyPath)) return;
            var dir = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(legacyPath, newPath, overwrite: false);
        }
        catch { /* best-effort */ }
    }
}
