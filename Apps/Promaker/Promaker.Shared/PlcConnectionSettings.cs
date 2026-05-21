using System;
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
/// PLC 연결 설정 POCO. JSON 직렬화/역직렬화 단일 책임.
/// Promaker WPF 의 PlcSettings(ObservableObject) 와 Promaker.Agent 의 부트스트랩이
/// 모두 이 POCO 를 읽고 쓴다. MVVM 의존성 없음.
///
/// 영속화 경로는 <see cref="SharedPaths.PlcConnectionFilePath"/> 가 기본 — Promaker.Agent (SYSTEM)
/// 가 같은 파일을 보기 위해 사용자 AppData 가 아닌 ProgramData 에 위치.
/// 옛 경로(%AppData%\Dualsoft\Promaker\Settings\PlcConnection.json) 에만 파일이 있으면
/// Load 시 자동 마이그레이션.
/// </summary>
public sealed class PlcConnectionSettings
{
    public string Vendor { get; set; } = nameof(PlcVendorChoice.LsXgi);
    public string Name { get; set; } = "PLC#1";
    public string IpAddress { get; set; } = "192.168.0.10";
    public int Port { get; set; } = 2004;
    public int TimeoutMs { get; set; } = 3000;
    public int ScanIntervalMs { get; set; } = 100;
    public bool LocalEthernet { get; set; } = true;
    public byte NetworkNumber { get; set; } = 0;
    public byte StationNumber { get; set; } = 0xFF;
    public bool IsUdp { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>지정 경로에서 JSON 로드. 없으면 기본값. 손상되면 silent fallback.</summary>
    public static PlcConnectionSettings LoadOrDefault(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PlcConnectionSettings();
            var text = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PlcConnectionSettings>(text, JsonOpts);
            return data ?? new PlcConnectionSettings();
        }
        catch
        {
            return new PlcConnectionSettings();
        }
    }

    /// <summary>지정 경로에 JSON 저장. 디렉터리 자동 생성. 실패해도 throw 없이 false 반환.</summary>
    public bool TrySave(string path)
    {
        try
        {
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
