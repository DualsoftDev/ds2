using System;
using System.IO;

namespace Promaker.Shared;

/// <summary>
/// Promaker · Promaker.Agent · DSPilot 가 공동으로 접근하는 고정 경로 단일 출처.
/// 세 앱이 같은 머신에 설치되어 데이터를 주고받을 때 어디에 무엇이 있는지 한 곳에서 정의.
/// DSPilot 측 SharedPaths (Apps/DSPilot/DSPilot/Infrastructure/SharedPaths.cs) 와 반드시
/// AasxFilePath 경로가 동일해야 한다 — 한쪽만 변경 시 동기화 깨짐.
/// </summary>
public static class SharedPaths
{
    /// <summary>systemd/운영 환경에서 공유 디렉터리를 오버라이드하는 환경변수 이름.
    /// DSPilot.Infrastructure.SharedPaths 의 같은 상수와 반드시 동일해야 한다(두 앱 같은 폴더 정합).</summary>
    public const string SharedDirEnvVar = "DUALSOFT_SHARED_DIR";

    /// <summary>공유 루트. Windows = %ProgramData%\DualSoft\Shared, Linux = /var/lib/dualsoft/Shared
    /// (systemd 가변 상태 디렉터리 표준 — Linux 에서 CommonApplicationData 는 /usr/share 로 root 전용이라
    /// 서비스 계정이 못 쓴다). <see cref="SharedDirEnvVar"/>(DUALSOFT_SHARED_DIR) 가 있으면 OS 무관 최우선
    /// (systemd 유닛이 주입하는 값 — 코드 기본값과 동일 경로). DSPilot.Infrastructure.SharedPaths 와 동일
    /// 로직 — Agent·DSPilot 이 같은 폴더(project.aasx / PlcConnection.json / active.flag …)를 본다.
    /// 디렉터리 생성·소유권(서비스 계정)·권한은 install.sh 가 보장한다.</summary>
    public static string SharedDirectory { get; } = ResolveSharedDirectory();

    private static string ResolveSharedDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(SharedDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath.Trim();
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DualSoft", "Shared");
        return "/var/lib/dualsoft/Shared";
    }

    /// <summary>Promaker → DSPilot · Agent 가 공유하는 런타임 모델. Promaker 가 Hub 모드 진입
    /// 직전 publish, Agent 와 DSPilot 이 파일 변경 감지로 재구독.</summary>
    public static string AasxFilePath { get; } = Path.Combine(SharedDirectory, "project.aasx");

    /// <summary>실 PLC 연결 설정. 옛 위치 (%AppData%\Dualsoft\Promaker\Settings\PlcConnection.json) 에서
    /// 공유 위치로 옮긴 새 위치 — Promaker WPF (사용자 컨텍스트) 와 Promaker.Agent (SYSTEM 컨텍스트) 가
    /// 동일 파일을 봐야 하므로 사용자 AppData 가 아닌 ProgramData 에 둔다.
    /// 마이그레이션: 옛 경로에만 파일이 있으면 첫 Load 시 자동 복사.</summary>
    public static string PlcConnectionFilePath { get; } = Path.Combine(SharedDirectory, "PlcConnection.json");

    /// <summary>Agent 전용 작업 디렉터리 — active.flag, session.json, agent 로그 등.</summary>
    public static string AgentDirectory { get; } = Path.Combine(SharedDirectory, "agent");

    /// <summary>Agent active 신호. 존재하면 Agent 가 모니터링 활성, 부재하면 idle.
    /// WPF PLAY 진입 시 생성, "모니터링 중지" 시 삭제.</summary>
    public static string AgentActiveFlagPath { get; } = Path.Combine(AgentDirectory, "active.flag");

    /// <summary>Agent active 세션 메타 — { aasxPath, plcConnectionPath, activatedAt, requestedBy }.
    /// active.flag 와 함께 갱신 (둘 다 있어야 Agent 가 부팅 후 자동 재개).</summary>
    public static string AgentSessionJsonPath { get; } = Path.Combine(AgentDirectory, "session.json");

    /// <summary>Agent가 소유하는 OPC UA 서버 설정. WPF 사용자 AppData 설정과 분리해
    /// Windows Service(SYSTEM)와 Linux 서비스 계정이 동일한 파일을 읽도록 한다.</summary>
    public static string AgentOpcUaSettingsPath { get; } = Path.Combine(AgentDirectory, "OpcUaServer.json");

    /// <summary>Agent OPC UA 인증서와 결정론적 namespace 상태 저장 루트.</summary>
    public static string AgentOpcUaDataDirectory { get; } = Path.Combine(AgentDirectory, "opcua");

    /// <summary>실측 duration 확정 상태 사이드카 — Work 별 "Min 실측 확정(minMeasured)" + 확정 시점 AASX 해시.
    /// AASX 모델은 건드리지 않고 런타임 확정 메타만 분리 보관한다. ActionUnder(시간 미만) 판정 게이트의 SSOT —
    /// 해시가 현재 AASX 와 다르면(모델 변경) 그 확정은 stale 로 간주되어 재확정 전까지 ActionUnder 가 비활성.</summary>
    public static string CalibrationStateJsonPath { get; } = Path.Combine(AgentDirectory, "calibration-state.json");

    /// <summary>공유 AASX/사이드카 동시 쓰기 직렬화용 cross-process 락 파일. 저장 주체(Promaker 실측반영 /
    /// DSPilot 실측반영 / Agent 업로드 수신)가 쓰기 전 원자적 생성으로 획득하고 끝나면 삭제한다.
    /// named Mutex 는 머신 로컬이라 같은 공유 폴더를 보는 cross-machine/cross-process 동시 쓰기를 못 막는다.</summary>
    public static string SharedWriteLockPath { get; } = Path.Combine(AgentDirectory, ".shared-write.lock");
}
