using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Promaker.Shared;

/// <summary>
/// WPF ↔ Agent 핸드셰이크 프로토콜.
/// WPF Monitoring + RealPLC + PLAY 시 <see cref="Write"/>, "모니터링 중지" 시 <see cref="Clear"/>.
/// Agent 부팅 시 + FileSystemWatcher 변화 시 <see cref="TryLoad"/> 로 상태 확인.
///
/// active.flag 가 존재해야 Agent 가 active 모드로 진입한다. session.json 만 있고 flag 가 없으면
/// idle 로 간주 — 깔끔한 정지 의도 표현 (예: 일시적으로 멈춰두고 설정 유지).
/// </summary>
public sealed class AgentSession
{
    /// <summary>모니터링 대상 모델 — 일반적으로 <see cref="SharedPaths.AasxFilePath"/> 와 동일.
    /// 명시 저장하는 이유: 추후 다른 위치의 AASX 를 가리키는 옵션 확장 여지.</summary>
    public string AasxPath { get; set; } = "";

    /// <summary>PLC 설정 경로 — 일반적으로 <see cref="SharedPaths.PlcConnectionFilePath"/> 와 동일.</summary>
    public string PlcConnectionPath { get; set; } = "";

    /// <summary>WPF 가 PLAY 를 눌렀거나 Agent 가 자동 재개한 마지막 시점 (UTC ISO 8601).</summary>
    public string ActivatedAtUtc { get; set; } = "";

    /// <summary>기록 주체 — "promaker" (WPF PLAY) 또는 "agent" (자동 재개) 등. 로그/디버깅 보조.</summary>
    public string RequestedBy { get; set; } = "";

    /// <summary>스키마 버전 — 향후 필드 추가 시 호환성 가드.</summary>
    public int SchemaVersion { get; set; } = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>active.flag + session.json 동시 기록 (Agent 활성 신호).
    /// session.json 을 먼저 쓰고 마지막에 flag 를 만들어 Agent 가 부분-읽기로 깨진 상태를 보지 않게 한다.
    /// 디렉터리 자동 생성. 실패 시 false 반환 — 호출자가 사용자에게 경고할 책임.</summary>
    public bool TryWrite()
    {
        try
        {
            Directory.CreateDirectory(SharedPaths.AgentDirectory);
            File.WriteAllText(SharedPaths.AgentSessionJsonPath,
                JsonSerializer.Serialize(this, JsonOpts));
            // flag 는 비어 있는 marker. 존재 여부만 의미가 있고 내용은 무시.
            File.WriteAllText(SharedPaths.AgentActiveFlagPath, ActivatedAtUtc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>active.flag 만 삭제 (모니터링 중지 신호). session.json 은 유지 — 다음 활성화 시
    /// 마지막 설정 자동 복원 가능. 완전 초기화를 원하면 <see cref="ClearAll"/>.</summary>
    public static bool TryDeactivate()
    {
        try
        {
            if (File.Exists(SharedPaths.AgentActiveFlagPath))
                File.Delete(SharedPaths.AgentActiveFlagPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>flag + session.json 모두 삭제 (완전 초기화).</summary>
    public static bool ClearAll()
    {
        var ok = true;
        try { if (File.Exists(SharedPaths.AgentActiveFlagPath)) File.Delete(SharedPaths.AgentActiveFlagPath); }
        catch { ok = false; }
        try { if (File.Exists(SharedPaths.AgentSessionJsonPath)) File.Delete(SharedPaths.AgentSessionJsonPath); }
        catch { ok = false; }
        return ok;
    }

    /// <summary>active.flag 존재 여부 — Agent 의 부팅/재구독 분기 판단용.</summary>
    public static bool IsActive() => File.Exists(SharedPaths.AgentActiveFlagPath);

    /// <summary>session.json 을 읽어 반환. 파일 없거나 손상되면 null.
    /// flag 와 무관 — flag 만 확인하려면 <see cref="IsActive"/>.</summary>
    public static AgentSession? TryLoad()
    {
        try
        {
            if (!File.Exists(SharedPaths.AgentSessionJsonPath)) return null;
            var text = File.ReadAllText(SharedPaths.AgentSessionJsonPath);
            return JsonSerializer.Deserialize<AgentSession>(text, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>현재 시점 + 기본 경로로 채운 세션 인스턴스 생성 helper.</summary>
    public static AgentSession ForCurrentDefaults(string requestedBy) => new()
    {
        AasxPath = SharedPaths.AasxFilePath,
        PlcConnectionPath = SharedPaths.PlcConnectionFilePath,
        ActivatedAtUtc = DateTime.UtcNow.ToString("o"),
        RequestedBy = requestedBy,
    };
}
