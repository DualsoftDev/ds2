using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public const int DefaultScanIntervalMs = 100;
    private const int PreviousDefaultScanIntervalMs = 50;

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

    /// <summary>자동 duration 정합 ON/OFF (모니터링 이상판정 기준 — 실측 학습 vs 모델 확정값).
    /// 벤더별이 아니라 PLC 공통 정책이라 플랫 필드. 스캔주기와 동형으로 hub 토글 → 영속화.
    /// 첫 설치 기본 ON(모델값 모름 → 학습부터). 정지 시 "AASX 반영" 선택하면 OFF 로 저장돼 유지된다.</summary>
    public bool AutoDurationCalibrate { get; set; } = true;

    /// <summary>간트 표시 윈도우(분) — 빨간 타임라인(현재시각) 기준 최근 N분만 간트에 보인다.
    /// PLC 설정 슬라이더로 5~300분(5시간) 조정. 그보다 오래된 구간은 스크롤해도 닿지 않는다.
    /// 순수 Promaker 표시 설정이지만 PLC 설정 다이얼로그 묶음이라 같은 파일에 영속화. 기본 300분(5시간).</summary>
    public int GanttWindowMinutes { get; set; } = 300;

    /// <summary>프로젝트 파일(AASX/.sdf)에 저장된 PLC 접속 정보를 로컬 설정보다 우선 적용할지.
    /// 기본 ON — 파일을 다른 PC 로 옮겨도 접속 대상이 따라가게 하는 것이 이 기능의 목적이다.
    /// OFF 로 두면 <see cref="PlcConnectionResolver"/> 가 AASX 단계를 건너뛰어 이 기능 도입 이전과
    /// 완전히 동일하게 동작한다 — 현장에서 재빌드 없이 즉시 되돌리기 위한 킬 스위치.</summary>
    public bool PreferAasxPlcConnection { get; set; } = true;

    /// <summary>벤더 enum 이름 → 해당 벤더의 마지막 입력값. 빈 dict 로 저장된 옛 파일은
    /// <see cref="EnsureProfiles"/> 가 플랫 필드로부터 채워준다.</summary>
    public Dictionary<string, PlcVendorProfile> Profiles { get; set; } = new();

    /// <summary>
    /// 이 값들이 실제 파일에서 왔는가(= 이 PC 에 PLC 설정이 저장된 적 있는가). <b>출처 표식이지 설정이 아니다</b> —
    /// <see cref="JsonIgnoreAttribute"/> 로 직렬화에서 빠지므로 Agent 의 설정 지문에도 영향을 주지 않는다.
    ///
    /// <para>false = 파일이 없어 생성자 기본값을 쓰고 있는 상태. 아무도 고른 적 없는 값이므로
    /// 프로젝트 파일에 기록하면 안 된다(<see cref="PlcConnectionResolver.StampToStore"/>).
    /// 값 비교로는 이 판별을 할 수 없다 — 실제로 192.168.0.10:2004 을 쓰는 현장과 구분되지 않는다.</para>
    /// </summary>
    [JsonIgnore]
    public bool WasPersisted { get; set; }

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
        var persisted = false;
        try
        {
            if (!File.Exists(path)) data = new PlcConnectionSettings();
            else
            {
                var text = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<PlcConnectionSettings>(text, JsonOpts);
                // 손상되어 내용을 못 읽은 파일은 "존재" 하지만 설정을 잃은 상태다. 그걸 저장 이력으로 인정하면
                // 화면에 뜬 생성자 기본값이 프로젝트 파일에 기록된다 — 파일 존재가 아니라 내용이 근거여야 한다.
                data = parsed ?? new PlcConnectionSettings();
                persisted = parsed is not null;
            }
        }
        catch
        {
            data = new PlcConnectionSettings();
        }
        data.WasPersisted = persisted;
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
            WasPersisted = true;
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
