// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Infrastructure;

/// <summary>
/// DSPilot · Promaker 공동 운영 시 두 앱이 동일하게 접근하는 고정 경로.
/// Windows 서비스 계정(SYSTEM)과 일반 사용자 양쪽에서 동일하게 해석되도록
/// <see cref="Environment.SpecialFolder.CommonApplicationData"/> (= %ProgramData%) 하위에 둔다.
///
/// Linux: <see cref="Environment.SpecialFolder.CommonApplicationData"/> 는 /usr/share(루트 전용 쓰기)로
/// 해석되어 서비스 계정이 쓸 수 없다. 설치본은 <c>DUALSOFT_SHARED_DIR</c> 를 /etc/dualsoft/dualsoft.env(SSOT)에
/// 두고 systemd 유닛이 EnvironmentFile 로 읽어 우선한다(install.sh).
/// 이 단일 소스를 plc.db/oee.db(연결 문자열 기본값)와 project.aasx 가 함께 사용해 항상 같은 폴더에 정합된다.
/// </summary>
public static class SharedPaths
{
    /// <summary>systemd/운영 환경에서 공유 디렉터리를 오버라이드하는 환경변수 이름.</summary>
    public const string SharedDirEnvVar = "DUALSOFT_SHARED_DIR";

    public static string SharedDirectory { get; } = ResolveSharedDirectory();

    public static string AasxFilePath { get; } = Path.Combine(SharedDirectory, "project.aasx");

    /// <summary>Agent 전용 작업 디렉터리 — active.flag, session.json, calibration-state.json 등.
    /// Promaker.Shared.SharedPaths.AgentDirectory 와 동일 경로(SharedDirectory/agent)여야 세 앱이 같은 사이드카를 본다.</summary>
    public static string AgentDirectory { get; } = Path.Combine(SharedDirectory, "agent");

    /// <summary>실측 duration 확정 상태 사이드카 — Work 별 "Min 실측 확정(minMeasured)" + 확정 시점 AASX 해시.
    /// ActionUnder(시간 미만) 판정 게이트의 SSOT. DSPilot 실측 보정(FillMin)이 여기에 기록하면 Agent 어댑터가 읽어 게이트를 연다.
    /// Promaker.Shared.SharedPaths.CalibrationStateJsonPath 와 동일 경로여야 한다.</summary>
    public static string CalibrationStateJsonPath { get; } = Path.Combine(AgentDirectory, "calibration-state.json");

    /// <summary>공유 AASX/사이드카 동시 쓰기 직렬화용 cross-process 락 파일. 저장 주체(Promaker 실측반영 /
    /// DSPilot 실측반영 / Agent 업로드 수신)가 쓰기 전 원자적 생성으로 획득하고 끝나면 삭제한다.
    /// Promaker.Shared.SharedPaths.SharedWriteLockPath 와 동일 경로여야 한다.</summary>
    public static string SharedWriteLockPath { get; } = Path.Combine(AgentDirectory, ".shared-write.lock");

    private static string ResolveSharedDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(SharedDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath.Trim();

        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DualSoft", "Shared");
        // Linux/macOS: CommonApplicationData(/usr/share)는 root 전용이라 서비스 계정이 못 쓴다.
        // systemd 가변 상태 디렉터리 표준 위치 — install.sh 가 서비스 계정 소유로 생성·권한 부여.
        // 대문자 "Shared" 고정: Linux 는 경로 대소문자를 구분하므로 Promaker.Shared.SharedPaths 의
        // Linux 기본값(대문자 Shared)과 글자까지 동일해야 두 앱이 같은 폴더를 본다(env 변수 누락 시 폴백 정합).
        return "/var/lib/dualsoft/Shared";
    }
}
