namespace DSPilot.Infrastructure;

/// <summary>
/// DSPilot · Promaker 공동 운영 시 두 앱이 동일하게 접근하는 고정 경로.
/// Windows 서비스 계정(SYSTEM)과 일반 사용자 양쪽에서 동일하게 해석되도록
/// <see cref="Environment.SpecialFolder.CommonApplicationData"/> (= %ProgramData%) 하위에 둔다.
/// </summary>
public static class SharedPaths
{
    public static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DualSoft", "Shared");

    public static string AasxFilePath { get; } = Path.Combine(SharedDirectory, "project.aasx");
}
