using System;
using System.IO;

namespace Promaker.Services;

/// <summary>
/// Promaker · DSPilot 공동 운영 시 두 앱이 동일하게 접근하는 고정 경로.
/// DSPilot 측의 같은 이름 클래스(Apps/DSPilot/DSPilot/Infrastructure/SharedPaths.cs)와
/// 반드시 동일한 경로를 유지해야 함 — 한쪽만 변경 시 동기화 깨짐.
/// </summary>
public static class SharedPaths
{
    public static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DualSoft", "Shared");

    public static string AasxFilePath { get; } = Path.Combine(SharedDirectory, "project.aasx");
}
