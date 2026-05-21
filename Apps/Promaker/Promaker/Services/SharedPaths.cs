namespace Promaker.Services;

/// <summary>
/// 공유 경로 forwarder — 실제 정의는 Promaker.Shared.SharedPaths 가 SSOT.
/// Promaker WPF, Promaker.Agent (SYSTEM 서비스), DSPilot 이 동일 경로를 봐야 하므로
/// 신규 코드는 Promaker.Shared.SharedPaths 를 직접 참조 권장. 본 forwarder 는 기존
/// Promaker.Services.SharedPaths 호출자(Save.cs / Open.cs / MainViewModel.cs 등) 호환용.
/// </summary>
public static class SharedPaths
{
    public static string SharedDirectory => Promaker.Shared.SharedPaths.SharedDirectory;
    public static string AasxFilePath    => Promaker.Shared.SharedPaths.AasxFilePath;
}
