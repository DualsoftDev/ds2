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

    /// <summary>Agent 가 호스팅할 runtime engine 모드 — "Control"(read-write, OUT→PLC 쓰기) 또는
    /// "Monitoring"(read-only). WPF Control PLAY → "Control", Monitoring+실PLC PLAY → "Monitoring".
    /// 기본 "Monitoring" (하위호환: 구 session.json 에 필드 없으면 Monitoring 으로 간주).</summary>
    public string RuntimeMode { get; set; } = "Monitoring";

    /// <summary>"실제 PLC 연결" 여부 — Promaker 런타임 모드 설정 UI 의 기존 체크박스
    /// (SimulationPanelState.IsRealPlcConnected) 값을 그대로 전달. 이 값이 Agent 의 직접/위임 스캔을 가른다:
    ///   true  = 직접 스캔 (Agent 가 PlcScanService 로 현장 PLC 에 직접 접속 — 올인원/현장 로컬).
    ///   false = 위임 스캔 (Agent 는 PLC 에 안 붙고, 분리된 Pi5 수집기가 스캔→WriteTags push → Agent 엔진 구동).
    /// 클라우드 인스턴스처럼 Agent 가 현장 PLC 에 네트워크상 못 붙는 환경에서 false(위임)로 두면
    /// 모델 IP 무한 접속실패/CommBlackout 이 원천 차단된다(§10.10 ①).
    /// **기본 true(직접)** — 구 session.json 에 필드가 없으면 기존(직접 스캔) 동작 유지 → 올인원 회귀 0.</summary>
    public bool IsRealPlcConnected { get; set; } = true;

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
            if (!TrySave(SharedPaths.AgentSessionJsonPath)) return false;
            // flag 는 비어 있는 marker. 존재 여부만 의미가 있고 내용은 무시.
            var flagTemp = SharedPaths.AgentActiveFlagPath + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(flagTemp, ActivatedAtUtc);
            File.Move(flagTemp, SharedPaths.AgentActiveFlagPath, overwrite: true);
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
        => TryLoad(SharedPaths.AgentSessionJsonPath);

    /// <summary>지정한 경로에서 세션을 읽는다. active.flag 상태에는 영향을 주지 않는다.</summary>
    public static AgentSession? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AgentSession>(text, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Agent 운영 경로용 엄격 로드. 손상/미지원 스키마를 기본값으로 숨기지 않는다.</summary>
    public static bool TryLoadExact(string path, out AgentSession? session, out string error)
    {
        session = null;
        error = "";
        try
        {
            if (!File.Exists(path))
            {
                error = $"Session file does not exist: {path}";
                return false;
            }
            session = JsonSerializer.Deserialize<AgentSession>(File.ReadAllText(path), JsonOpts);
            if (session is null)
            {
                error = "Session JSON is empty.";
                return false;
            }
            if (!session.TryValidate(out error))
            {
                session = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Session JSON is invalid: {ex.Message}";
            session = null;
            return false;
        }
    }

    public bool TryValidate(out string error)
    {
        if (SchemaVersion != 1)
            error = $"Unsupported session schemaVersion '{SchemaVersion}'.";
        else if (string.IsNullOrWhiteSpace(AasxPath))
            error = "session.aasxPath is required.";
        else if (!string.Equals(RuntimeMode, "Monitoring", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(RuntimeMode, "Control", StringComparison.OrdinalIgnoreCase))
            error = "session.runtimeMode must be 'Monitoring' or 'Control'.";
        else if (!DateTimeOffset.TryParse(ActivatedAtUtc, out _))
            error = "session.activatedAtUtc must be an ISO-8601 timestamp.";
        else if (string.IsNullOrWhiteSpace(RequestedBy) || RequestedBy.Length > 128)
            error = "session.requestedBy is required and must be at most 128 characters.";
        else
        {
            RuntimeMode = string.Equals(RuntimeMode, "Control", StringComparison.OrdinalIgnoreCase)
                ? "Control"
                : "Monitoring";
            error = "";
            return true;
        }
        return false;
    }

    /// <summary>세션 JSON만 지정 경로에 원자적으로 저장한다. active.flag는 만들지 않는다.</summary>
    public bool TrySave(string path)
    {
        string? temp = null;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            temp = path + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    /// <summary>현재 시점 + 기본 경로로 채운 세션 인스턴스 생성 helper.
    /// <paramref name="isRealPlcConnected"/> = 런타임 모드 설정 UI 의 "실제 PLC 연결" 체크값(기본 true=직접 스캔).</summary>
    public static AgentSession ForCurrentDefaults(string requestedBy, string runtimeMode = "Monitoring",
                                                  bool isRealPlcConnected = true) => new()
    {
        AasxPath = SharedPaths.AasxFilePath,
        PlcConnectionPath = SharedPaths.PlcConnectionFilePath,
        ActivatedAtUtc = DateTime.UtcNow.ToString("o"),
        RequestedBy = requestedBy,
        RuntimeMode = runtimeMode,
        IsRealPlcConnected = isRealPlcConnected,
    };
}
