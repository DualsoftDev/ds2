using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ds2.Core.StandardSubmodels;

namespace Ds2.Hub.Shared;

/// <summary>
/// PLC 연결 설정 POCO. JSON 직렬화/역직렬화 단일 책임.
/// Ds2.Hub 의 부트스트랩이 읽고 쓴다. 벤더·프로파일 타입은 Ds2.Core(PlcVendor.fs) 의 것이다.
///
/// 영속화 경로는 <see cref="SharedPaths.PlcConnectionFilePath"/> 가 기본 — Ds2.Hub (SYSTEM)
/// 가 같은 파일을 보기 위해 사용자 AppData 가 아닌 ProgramData 에 위치.
/// 옛 경로(%AppData%\Dualsoft\Promaker\Settings\PlcConnection.json) 에만 파일이 있으면
/// Load 시 자동 마이그레이션.
///
/// 최상위 플랫 필드(Vendor, IpAddress, Port…) 는 "현재 활성 벤더" 의 값이고, <see cref="Profiles"/> 는
/// 모든 벤더의 직전 입력값을 보관해 사용자가 벤더를 토글해도 각 벤더 양식이 복원되도록 한다.
/// 런타임이 실제로 스캔에 쓰는 접속은 이 파일이 아니라 AASX 의 AID endpoint 다(Agent 가 AID 에서만
/// 게이트웨이를 조립한다) — 이 파일은 UI 입력 보존과 Agent 설정 지문·스캔주기 영속에 쓰인다.
/// </summary>
public sealed class PlcConnectionSettings
{
    public const int DefaultScanIntervalMs = PlcVendorDefaults.ScanIntervalMs;
    private const int PreviousDefaultScanIntervalMs = 50;

    public string Vendor { get; set; } = nameof(PlcVendorChoice.LsXgi);

    /// <summary>
    /// MICREX-SX 전용 — D300win 프로젝트에서 뽑은 I/O 매핑표 경로.
    /// 비워 두면 네이티브 주소만 쓴다(<c>M1.2000.0</c>, <c>IO.42.4</c>).
    /// IEC 원격 주소(<c>%QX5.43.0.04</c>)를 쓰려면 이 표가 있어야 한다.
    /// </summary>
    public string SxIoMapPath { get; set; } = string.Empty;

    /// <summary>
    /// MICREX-SX 전용 — 쓰기를 허용할 영역. 비어 있으면 <b>읽기 전용</b>이다.
    /// "M1"(사용자 메모리), "IO"(I/O 이미지), "M10"(시스템 메모리).
    /// 기본을 잠금으로 두는 이유: 현장 PLC 의 메모리 배치는 설비마다 다르고,
    /// 잘못된 주소에 쓰면 설비를 오동작시킨다.
    /// </summary>
    public List<string> SxWritableAreas { get; set; } = new();
    public string Name { get; set; } = "PLC#1";
    public string IpAddress { get; set; } = "192.168.0.10";
    public int Port { get; set; } = 2004;
    public int TimeoutMs { get; set; } = 3000;
    public int ScanIntervalMs { get; set; } = DefaultScanIntervalMs;
    public bool LocalEthernet { get; set; } = true;
    public byte NetworkNumber { get; set; } = 0;
    public byte StationNumber { get; set; } = 0xFF;
    /// <summary>접속 매체 — <see cref="PlcTransports"/> 참조.</summary>
    public string Transport { get; set; } = PlcTransports.Tcp;
    /// <summary>USB 전용 장치 선택 키. <see cref="PlcVendorProfile.UsbDeviceSelector"/> 참조.</summary>
    public string UsbDeviceSelector { get; set; } = string.Empty;

    /// <summary>자동 duration 정합 ON/OFF (모니터링 이상판정 기준 — 실측 학습 vs 모델 확정값).
    /// 벤더별이 아니라 PLC 공통 정책이라 플랫 필드. 스캔주기와 동형으로 hub 토글 → 영속화.
    /// 첫 설치 기본 ON(모델값 모름 → 학습부터). 정지 시 "AASX 반영" 선택하면 OFF 로 저장돼 유지된다.</summary>
    public bool AutoDurationCalibrate { get; set; } = true;

    // 간트 표시 윈도우(GanttWindowMinutes)는 순수 뷰 설정이라 Promaker 앱 설정(ganttWindowMinutes.txt)으로
    // 이사 — 이 파일은 Agent 업로드에 포함되므로 UI 취향값을 싣지 않는다. 구 json 의 잔여 필드는 역직렬화 시 무시됨.

    /// <summary>벤더 enum 이름 → 해당 벤더의 마지막 입력값. 빈 dict 로 저장된 옛 파일은
    /// <see cref="EnsureProfiles"/> 가 플랫 필드로부터 채워준다.</summary>
    public Dictionary<string, PlcVendorProfile> Profiles { get; set; } = new();

    /// <summary>
    /// 이 값들이 실제 파일에서 왔는가(= 이 PC 에 PLC 설정이 저장된 적 있는가). <b>출처 표식이지 설정이 아니다</b> —
    /// <see cref="JsonIgnoreAttribute"/> 로 직렬화에서 빠지므로 Agent 의 설정 지문에도 영향을 주지 않는다.
    ///
    /// <para>false = 파일이 없어 생성자 기본값을 쓰고 있는 상태. 아무도 고른 적 없는 값이다.
    /// 값 비교로는 이 판별을 할 수 없다 — 실제로 192.168.0.10:2004 을 쓰는 현장과 구분되지 않는다.</para>
    /// </summary>
    [JsonIgnore]
    public bool WasPersisted { get; set; }

    /// <summary>사람이 읽는 현재 활성 접속 표기.</summary>
    [JsonIgnore]
    public string EndpointLabel => PlcEndpointLabel.format(Transport, IpAddress ?? string.Empty, Port, UsbDeviceSelector ?? string.Empty);

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

    /// <summary>존재하는 설정 파일의 손상을 기본값으로 숨기지 않고 읽는다.</summary>
    public static bool TryLoadExact(string path, out PlcConnectionSettings? settings, out string error)
    {
        settings = null;
        error = "";
        try
        {
            if (!File.Exists(path))
            {
                error = $"PLC settings not found at '{path}'.";
                return false;
            }
            settings = JsonSerializer.Deserialize<PlcConnectionSettings>(File.ReadAllText(path), JsonOpts);
            if (settings is null)
            {
                error = "PLC settings JSON was empty.";
                return false;
            }
            settings.WasPersisted = true;
            settings.EnsureProfiles();
            settings.UpgradeDefaultScanIntervals();
            return true;
        }
        catch (Exception ex)
        {
            error = $"PLC settings JSON is invalid: {ex.Message}";
            return false;
        }
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
        Transport = Transport,
        UsbDeviceSelector = UsbDeviceSelector,
    };

    /// <summary>프로파일 값을 플랫 필드로 적용 — <see cref="SnapshotFlatToProfile"/> 의 역방향.
    /// System 속성 패널 저장·AASX 박제(Save.StampPlcConnection)·벤더 전환이 전부 이 한 곳을 쓴다.
    /// 벤더는 바꾸지 않는다(호출자가 정한다).</summary>
    public void ApplyProfile(PlcVendorProfile p)
    {
        Name = p.Name;
        IpAddress = p.IpAddress;
        Port = p.Port;
        TimeoutMs = p.TimeoutMs;
        ScanIntervalMs = p.ScanIntervalMs;
        LocalEthernet = p.LocalEthernet;
        NetworkNumber = p.NetworkNumber;
        StationNumber = p.StationNumber;
        Transport = PlcTransports.Normalize(p.Transport);
        UsbDeviceSelector = p.UsbDeviceSelector ?? string.Empty;
    }

    /// <summary>지정 벤더 프로파일을 플랫 필드로 적용. 프로파일 없으면 기본값 사용.</summary>
    public void ApplyProfileToFlat(PlcVendorChoice vendor)
    {
        var key = vendor.ToString();
        if (Profiles == null || !Profiles.TryGetValue(key, out var p))
            p = PlcVendorProfile.Defaults(vendor);

        Vendor = key;
        ApplyProfile(p);
    }

    /// <summary>지정 경로에 JSON 저장. 디렉터리 자동 생성. 실패해도 throw 없이 false 반환.
    /// 저장 직전 <see cref="EnsureProfiles"/> 로 활성 벤더 프로파일을 최신 플랫 값으로 동기화.</summary>
    public bool TrySave(string path)
    {
        string? temp = null;
        try
        {
            EnsureProfiles();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var text = JsonSerializer.Serialize(this, JsonOpts);
            temp = path + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            WasPersisted = true;
            return true;
        }
        catch
        {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { }
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
