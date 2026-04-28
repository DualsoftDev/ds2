using System.Reflection;

namespace Promaker.Services;

/// <summary>
/// 애플리케이션 버전/타이틀 정보 — 어셈블리 버전을 런타임에 추출해 노출.
/// </summary>
public static class AppInfo
{
    /// <summary>실행중 어셈블리의 4-part 버전 (예: "1.0.0.0").</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            // AssemblyInformationalVersion 우선 (GitVersion 등 커스텀 태그 반영), 없으면 AssemblyVersion
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // "1.0.0.0+abcdef" 형태에서 빌드 메타데이터 제거
                var plus = info.IndexOf('+');
                return plus > 0 ? info.Substring(0, plus) : info;
            }
            return asm.GetName().Version?.ToString() ?? "0.0.0.0";
        }
    }

    /// <summary>윈도우 제목 기본 문자열 — "Promaker v1.0.0.0" 형식.</summary>
    public static string TitleBase => $"Promaker v{Version}";
}
