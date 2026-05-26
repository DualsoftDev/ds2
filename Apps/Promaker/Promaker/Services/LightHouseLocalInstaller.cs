using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Promaker.LlmAgent;

namespace Promaker.Services;

/// <summary>
/// 로컬 LightHouseService 활성화 — UAC elevated PowerShell 호출 + PSK capture + LlmConfig 박제.
/// 인스톨러가 배치한 scripts/enable-ai.ps1 을 trigger 하고 결과(PSK 평문)를 임시 파일에서 흡수해
/// DPAPI 로 LlmConfig.LightHouseServices 의 Local entry 에 박제한다.
///
/// 운영 / 개발 환경 모두 동작:
///   운영 = {app}\LightHouseService\scripts\enable-ai.ps1  +  {app}\LightHouseService\Ds2.LightHouseService.exe
///   개발 = repo/Solutions/Tools/Ds2.LightHouseService/scripts/enable-ai.ps1  +  .../bin/Release/net9.0/publish/Ds2.LightHouseService.exe
/// </summary>
public sealed class LightHouseLocalInstaller
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LightHouseLocalInstaller));

    private const string LocalBaseUrl = "https://127.0.0.1:8443";

    private readonly LlmConfig _llmConfig;

    public LightHouseLocalInstaller(LlmConfig llmConfig)
    {
        _llmConfig = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
    }

    /// <summary>
    /// enable-ai.ps1 호출 → 4단계 (Ollama / cert / sc create + PSK / firewall + start) → PSK 박제.
    /// 성공 시 <see cref="EnableResult.ServiceId"/> 가 LlmConfig.LightHouseServices 의 Local entry 식별자.
    /// </summary>
    public async Task<EnableResult> EnableAsync(CancellationToken ct = default)
    {
        var deployment = ResolveDeployment()
            ?? throw new InvalidOperationException(
                "LightHouseService 배포 파일 미발견 — installer 의 'AI 기능 활성화' 컴포넌트 체크 후 재설치 (개발 환경은 'make publish-lighthouse' 필요).");

        var enableScript = Path.Combine(deployment.ScriptsDir, "enable-ai.ps1");
        if (!File.Exists(enableScript))
            throw new FileNotFoundException("enable-ai.ps1 미발견 — installer 갱신 또는 'make publish-lighthouse' 후 재시도.", enableScript);

        var pskOut = Path.Combine(Path.GetTempPath(), $"promaker-psk-{Guid.NewGuid():N}.tmp");
        var logOut = Path.Combine(Path.GetTempPath(), $"promaker-ai-{Guid.NewGuid():N}.log");

        try
        {
            var psArgs = string.Join(' ', new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", Quote(enableScript),
                "-ExePath", Quote(deployment.ExePath),
                "-PskOutputPath", Quote(pskOut),
                "-LogPath", Quote(logOut),
            });

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = psArgs,
                UseShellExecute = true,   // Verb=runas 와 정합 (UAC 프롬프트)
                Verb = "runas",
                // 사용자 progress feedback — 진행 화면을 console 로 표시 (수 분 소요 + UAC 후 무화면 회피).
                // PSK 평문은 install-service.ps1 가 -PskOutputPath 로 직접 박제 → console / transcript 미경유.
                WindowStyle = ProcessWindowStyle.Normal,
            };

            Log.Info($"LightHouse enable-ai.ps1 호출 — log={logOut}");
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell 시작 실패.");
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var tail = TryReadTail(logOut, 4096);
                throw new InvalidOperationException(
                    $"enable-ai.ps1 실패 (exit {proc.ExitCode}). log 끝부분:{Environment.NewLine}{tail}{Environment.NewLine}전체 log: {logOut}");
            }

            if (!File.Exists(pskOut))
                throw new InvalidOperationException($"PSK output 미생성 — enable-ai.ps1 가 PSK 박제하지 못함. log: {logOut}");

            var pskPlain = File.ReadAllText(pskOut).Trim();
            if (string.IsNullOrEmpty(pskPlain))
                throw new InvalidOperationException($"PSK output 비어 있음. log: {logOut}");

            var serviceId = PersistLocalEntry(pskPlain);
            Log.Info($"LightHouse Local entry 박제 완료 — ServiceId={serviceId}");

            // enable-ai.ps1 의 마지막 단계가 출력한 `START_RESULT=exit=<n> health=<true|false>` 라인을 파싱.
            // log 파일에는 transcript 가 박혀 있고, install-service.ps1 가 PSK 평문을 stdout 미경유로
            // 파일 박제하므로 (자가검열 C2 정합) — log 파일에 PSK 평문 잔존 없음.
            var startOk = TryParseStartResult(logOut);
            return new EnableResult(serviceId, logOut, startOk);
        }
        finally
        {
            TryWipeFile(pskOut);
            // log 는 caller 가 결과 표시 후 직접 cleanup. (실패 시 진단 자료로 유용.)
        }
    }

    /// <summary>PSK 평문을 DPAPI 박제. Local LightHouse entry 가 없으면 신규, 있으면 PSK 만 update.
    /// 단일 active 정합 (LlmConfig <see cref="LightHouseServiceConfig.Active"/> XML doc 의 결정 D-S7-3 #3) —
    /// 활성화 시점에 다른 entry 의 <c>Active</c> 를 false 로 강제 (사용자가 다시 명시 토글 가능).</summary>
    private string PersistLocalEntry(string pskPlain)
    {
        var entry = _llmConfig.LightHouseServices.FirstOrDefault(s =>
            IsSameLocalEndpoint(s.BaseUrl, LocalBaseUrl));

        if (entry is null)
        {
            entry = new LightHouseServiceConfig
            {
                ServiceId = Guid.NewGuid().ToString(),
                DisplayName = "Local LightHouse",
                BaseUrl = LocalBaseUrl,
                Active = true,
            };
            _llmConfig.LightHouseServices.Add(entry);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(entry.DisplayName))
                entry.DisplayName = "Local LightHouse";
        }

        // 단일 active 정합 — Local 이 방금 활성화되었으므로 다른 모든 entry 비활성.
        foreach (var s in _llmConfig.LightHouseServices)
            s.Active = ReferenceEquals(s, entry);

        _llmConfig.SetLightHousePsk(entry.ServiceId, pskPlain);
        _llmConfig.Save();
        return entry.ServiceId;
    }

    /// <summary>운영 (`{AppContext.BaseDirectory}\LightHouseService\`) → 개발 (repo/Solutions/Tools/...) 순으로 탐색.</summary>
    public static Deployment? ResolveDeployment()
    {
        // 운영 — installer 가 배치한 위치.
        var opsRoot = Path.Combine(AppContext.BaseDirectory, "LightHouseService");
        var opsScripts = Path.Combine(opsRoot, "scripts");
        var opsExe = Path.Combine(opsRoot, "Ds2.LightHouseService.exe");
        if (Directory.Exists(opsScripts) && File.Exists(opsExe))
            return new Deployment(opsScripts, opsExe, IsOperational: true);

        // 개발 — repo root 검색 (.git 디렉터리 탐색).
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            var devRoot = Path.Combine(repoRoot, "Solutions", "Tools", "Ds2.LightHouseService");
            var devScripts = Path.Combine(devRoot, "scripts");
            var devExe = Path.Combine(devRoot, "bin", "Release", "net9.0", "publish", "Ds2.LightHouseService.exe");
            if (Directory.Exists(devScripts) && File.Exists(devExe))
                return new Deployment(devScripts, devExe, IsOperational: false);
        }

        return null;
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            // worktree / submodule 환경에서는 .git 가 파일 (`gitdir: ...`). 둘 다 인식.
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static bool IsSameLocalEndpoint(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua)) return false;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var ub)) return false;
        static string Norm(string h) => h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : h;
        return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
            && Norm(ua.Host) == Norm(ub.Host)
            && ua.Port == ub.Port;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string TryReadTail(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path)) return "(log 미생성)";
            var text = File.ReadAllText(path);
            return text.Length <= maxBytes ? text : text[^maxBytes..];
        }
        catch (Exception ex)
        {
            return $"(log read 실패: {ex.Message})";
        }
    }

    private static void TryWipeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var len = new FileInfo(path).Length;
            if (len > 0)
            {
                using (var fs = File.OpenWrite(path))
                {
                    var zero = new byte[Math.Min(len, 4096)];
                    long written = 0;
                    while (written < len)
                    {
                        var chunk = (int)Math.Min(zero.Length, len - written);
                        fs.Write(zero, 0, chunk);
                        written += chunk;
                    }
                    fs.Flush();
                }
            }
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"PSK 임시 파일 wipe 실패 ({path}): {ex.Message}");
        }
    }

    /// <summary>enable-ai.ps1 transcript 에서 `START_RESULT=exit=<n> health=<true|false>` 파싱.
    /// 미발견 시 null (사용자에게 "결과 불확실" 안내). exit==0 && health==true 시 true.</summary>
    private static bool? TryParseStartResult(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            foreach (var line in File.ReadLines(logPath))
            {
                var idx = line.IndexOf("START_RESULT=", StringComparison.Ordinal);
                if (idx < 0) continue;
                var rest = line[(idx + "START_RESULT=".Length)..];
                // 예: "exit=0 health=True"
                var exitOk = rest.Contains("exit=0", StringComparison.OrdinalIgnoreCase);
                var healthOk = rest.Contains("health=True", StringComparison.OrdinalIgnoreCase);
                return exitOk && healthOk;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"START_RESULT parse 실패: {ex.Message}");
        }
        return null;
    }

    public sealed record Deployment(string ScriptsDir, string ExePath, bool IsOperational);
    public readonly record struct EnableResult(string ServiceId, string LogPath, bool? Healthy);
}
