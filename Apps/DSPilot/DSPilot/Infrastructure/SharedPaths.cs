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
/// 해석되어 서비스 계정이 쓸 수 없다. systemd 유닛이 <c>DUALSOFT_SHARED_DIR</c> 환경변수로
/// 쓰기 가능한 공유 디렉터리(예: /var/lib/dualsoft/Shared)를 지정하면 이를 우선한다.
/// 이 단일 소스를 plc.db/oee.db(연결 문자열 기본값)와 project.aasx 가 함께 사용해 항상 같은 폴더에 정합된다.
/// </summary>
public static class SharedPaths
{
    /// <summary>systemd/운영 환경에서 공유 디렉터리를 오버라이드하는 환경변수 이름.</summary>
    public const string SharedDirEnvVar = "DUALSOFT_SHARED_DIR";

    public static string SharedDirectory { get; } = ResolveSharedDirectory();

    public static string AasxFilePath { get; } = Path.Combine(SharedDirectory, "project.aasx");

    private static string ResolveSharedDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(SharedDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath.Trim();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DualSoft", "Shared");
    }
}
