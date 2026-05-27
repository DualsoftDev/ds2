using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
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

    /// <summary>fallback BaseUrl — config.json read 실패 시 사용. install-service.ps1 의 default ListenUrl 정합.</summary>
    public const string DefaultLocalBaseUrl = "https://127.0.0.1:8443";

    private readonly LlmConfig _llmConfig;

    public LightHouseLocalInstaller(LlmConfig llmConfig)
    {
        _llmConfig = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
    }

    /// <summary>
    /// **B4 (2026-05-27) — SecureString 정공 path**. enable-ai.ps1 호출 → Promaker 측 PSK DPAPI 박제.
    /// 성공 시 <see cref="EnableResult.ServiceId"/> 가 LlmConfig.LightHouseServices 의 Local entry 식별자.
    /// <para/>
    /// **PSK / cert PFX password 평문 lifetime 최소화**: SecureString → UTF-8 byte[] (lock 보호) → File.WriteAllBytes
    /// → finally Array.Clear(bytes) + Marshal.ZeroFreeGlobalAllocUnicode. 임시 파일은 ps1 read 직후 wipe + 본 메서드도
    /// finally wipe (이중 안전). PSK 의 DPAPI 박제 단계만 LlmConfig.SetLightHousePsk(string) 시그니처 제약으로 1회
    /// managed string 변환 (lifetime = SetLightHousePsk 호출 안에 한정).
    /// <para/>
    /// **caller (EnableLocalServiceDialog)** 가 SecureString 소유권 이양. 본 메서드는 사용 후 Dispose 의무 가짐.
    /// </summary>
    /// <param name="psk">사용자 입력 PSK SecureString. 본 메서드가 Dispose (early-throw 경로 포함).</param>
    /// <param name="certPwd">사용자 입력 cert PFX password SecureString. 본 메서드가 Dispose (early-throw 경로 포함).</param>
    public async Task<EnableResult> EnableAsync(SecureString psk, SecureString certPwd, CancellationToken ct = default)
    {
        // **B4 자가 검열 Critical-1 fix (2026-05-27)** — Dispose 의무 박제는 진입 직후부터 보장.
        // (이전 구현: Argument 검사 / ResolveDeployment / File.Exists throw 가 try 블록 *바깥* 이라 SecureString 누수.)
        var pskIn  = Path.Combine(Path.GetTempPath(), $"promaker-psk-{Guid.NewGuid():N}.tmp");
        var cpwdIn = Path.Combine(Path.GetTempPath(), $"promaker-cpw-{Guid.NewGuid():N}.tmp");
        var logOut = Path.Combine(Path.GetTempPath(), $"promaker-ai-{Guid.NewGuid():N}.log");

        byte[]? pskBytes = null;
        byte[]? cpwBytes = null;
        try
        {
            if (psk is null) throw new ArgumentNullException(nameof(psk));
            if (certPwd is null) throw new ArgumentNullException(nameof(certPwd));
            if (psk.Length == 0) throw new ArgumentException("PSK 비어있음.", nameof(psk));
            if (certPwd.Length == 0) throw new ArgumentException("Cert PFX password 비어있음.", nameof(certPwd));

            var deployment = ResolveDeployment()
                ?? throw new InvalidOperationException(
                    "LightHouseService 배포 파일 미발견 — installer 의 'AI 기능 활성화' 컴포넌트 체크 후 재설치 (개발 환경은 'make publish-lighthouse' 필요).");

            var enableScript = Path.Combine(deployment.ScriptsDir, "enable-ai.ps1");
            if (!File.Exists(enableScript))
                throw new FileNotFoundException("enable-ai.ps1 미발견 — installer 갱신 또는 'make publish-lighthouse' 후 재시도.", enableScript);

            // **B4 (2026-05-27)** — SecureString → UTF-8 byte[]. Owner-only ACL 은 default Temp 디렉터리 권한에 의존
            // (Windows 의 %TEMP% 가 사용자별 분리 박제 — 다른 사용자 read 불가, admin elevation 시 read 가능 — UAC 후 자기 process 가 read).
            pskBytes = SecureStringToUtf8Bytes(psk);
            cpwBytes = SecureStringToUtf8Bytes(certPwd);
            File.WriteAllBytes(pskIn, pskBytes);
            File.WriteAllBytes(cpwdIn, cpwBytes);

            var psArgs = string.Join(' ', new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", Quote(enableScript),
                "-ExePath", Quote(deployment.ExePath),
                "-PskInputPath", Quote(pskIn),
                "-CertPasswordInputPath", Quote(cpwdIn),
                "-LogPath", Quote(logOut),
            });

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = psArgs,
                UseShellExecute = true,   // Verb=runas 와 정합 (UAC 프롬프트)
                Verb = "runas",
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

            // **B4 (2026-05-27)** — PSK 의 DPAPI 박제 단계만 SecureString → managed string 1회 변환 (lifetime 한정).
            // LlmConfig.SetLightHousePsk(string) 시그니처 제약 — 별 backlog 로 SecureString overload 박제 권장.
            var serviceId = PersistLocalEntryFromSecureString(psk);
            Log.Info($"LightHouse Local entry 박제 완료 — ServiceId={serviceId}");

            var startOk = TryParseStartResult(logOut);
            return new EnableResult(serviceId, logOut, startOk);
        }
        finally
        {
            // **B4 (2026-05-27)** — heap 평문 wipe (Array.Clear) + temp file wipe (이중 안전).
            if (pskBytes is not null) Array.Clear(pskBytes, 0, pskBytes.Length);
            if (cpwBytes is not null) Array.Clear(cpwBytes, 0, cpwBytes.Length);
            TryWipeFile(pskIn);
            TryWipeFile(cpwdIn);
            // null-safe — ArgumentNullException 분기 진입 시 한쪽이 null 일 수 있음.
            psk?.Dispose();
            certPwd?.Dispose();
            // log 는 caller 가 결과 표시 후 직접 cleanup. (실패 시 진단 자료로 유용.)
        }
    }

    /// <summary>
    /// **B4 (2026-05-27) deprecated** — managed string 시그니처 보존 (외부 caller 호환). 신규 caller 는 SecureString
    /// overload 사용 의무 (heap 평문 lifetime 최소화 SSOT). 본 overload 는 평문 string → SecureString 1회 wrapping
    /// 후 정공 path 호출 — string 자체는 immutable 이라 wipe 불가, caller 평문 잔존 risk 그대로.
    /// </summary>
    [Obsolete("B4 (2026-05-27): SecureString overload 사용 권장. managed string path 는 heap 평문 잔존 risk.")]
    public Task<EnableResult> EnableAsync(string pskPlain, string certPwdPlain, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pskPlain)) throw new ArgumentException("PSK 평문 비어있음.", nameof(pskPlain));
        if (string.IsNullOrEmpty(certPwdPlain)) throw new ArgumentException("Cert PFX password 평문 비어있음.", nameof(certPwdPlain));
        var psk = StringToSecureString(pskPlain);
        var cpw = StringToSecureString(certPwdPlain);
        return EnableAsync(psk, cpw, ct);
    }

    private static SecureString StringToSecureString(string s)
    {
        var ss = new SecureString();
        foreach (var ch in s) ss.AppendChar(ch);
        ss.MakeReadOnly();
        return ss;
    }

    /// <summary>**B4 (2026-05-27)** — SecureString → UTF-8 byte[]. 중간 char[] 도 Array.Clear 로 wipe.
    /// caller 가 반환 byte[] 의 사용 후 Array.Clear 의무 (finally). internal — 보안 정합 회귀 가드 (LightHouseLocalInstallerTests).</summary>
    internal static byte[] SecureStringToUtf8Bytes(SecureString ss)
    {
        IntPtr unicodePtr = IntPtr.Zero;
        char[]? chars = null;
        try
        {
            unicodePtr = Marshal.SecureStringToGlobalAllocUnicode(ss);
            chars = new char[ss.Length];
            Marshal.Copy(unicodePtr, chars, 0, ss.Length);
            return Encoding.UTF8.GetBytes(chars);
        }
        finally
        {
            if (chars is not null) Array.Clear(chars, 0, chars.Length);
            if (unicodePtr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(unicodePtr);
        }
    }

    /// <summary>**B4 (2026-05-27)** — DPAPI 박제 직전 1회 평문 변환 (SetLightHousePsk(string) 시그니처 제약).
    /// 본 helper 의 string 결과는 GC heap 에 잔존 — LlmConfig 의 DPAPI 박제 후 GC 가 회수할 때까지 lifetime
    /// 단축 의도. 별 backlog 로 SetLightHousePsk(SecureString) overload 박제 후 본 helper 폐기 권장.</summary>
    private string PersistLocalEntryFromSecureString(SecureString psk)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = Marshal.SecureStringToGlobalAllocUnicode(psk);
            var pskPlain = Marshal.PtrToStringUni(ptr) ?? "";
            return PersistLocalEntry(pskPlain);
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    /// <summary>PSK 평문을 DPAPI 박제. Local LightHouse entry 가 없으면 신규, 있으면 PSK 만 update.
    /// 단일 active 정합 (LlmConfig <see cref="LightHouseServiceConfig.Active"/> XML doc 의 결정 D-S7-3 #3) —
    /// 활성화 시점에 다른 entry 의 <c>Active</c> 를 false 로 강제 (사용자가 다시 명시 토글 가능).</summary>
    private string PersistLocalEntry(string pskPlain)
    {
        var entry = _llmConfig.LightHouseServices.FirstOrDefault(s =>
            IsSameLocalEndpoint(s.BaseUrl, DefaultLocalBaseUrl));

        if (entry is null)
        {
            entry = new LightHouseServiceConfig
            {
                ServiceId = Guid.NewGuid().ToString(),
                DisplayName = "Local LightHouse",
                BaseUrl = DefaultLocalBaseUrl,
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

    /// <summary>두 BaseUrl 이 같은 endpoint 인지 판정 — scheme / host(localhost↔127.0.0.1 정규화) / port 비교.
    /// LlmConfig 의 LightHouseServices 중복 검사 (UI 의 "Local 항목 추가" + "+ Add Service" 양쪽) SSOT.</summary>
    public static bool IsSameLocalEndpoint(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua)) return false;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var ub)) return false;
        static string Norm(string h) => h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : h;
        return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
            && Norm(ua.Host) == Norm(ub.Host)
            && ua.Port == ub.Port;
    }

    /// <summary>로컬 LightHouseService 의 client target BaseUrl 결정 —
    /// <c>%PROGRAMDATA%\Dualsoft\LightHouseService\config.json</c> 의 <c>listenUrl</c> 박제값 우선,
    /// read 실패 (ACL 거부 / 파일 미존재 / parse 실패) 시 <see cref="DefaultLocalBaseUrl"/> fallback.
    /// <para/>
    /// 정규화 — listenUrl 의 host 가 <c>0.0.0.0</c> 이면 <c>127.0.0.1</c> 로 치환 (client connect target 부적합).
    /// IPv6 <c>[::]</c> 도 동일.
    /// <para/>
    /// 본 helper 는 read-only — DPAPI 복호화 / 평문 노출 0. listenUrl 만 추출.
    /// </summary>
    public static string ResolveLocalBaseUrl()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Dualsoft", "LightHouseService", "config.json");
            if (!File.Exists(configPath)) return DefaultLocalBaseUrl;

            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("listenUrl", out var lu)) return DefaultLocalBaseUrl;
            var raw = lu.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return DefaultLocalBaseUrl;

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return DefaultLocalBaseUrl;
            // bind-only host (client target 부적합) → loopback 치환. install-service.ps1 default 가 IPv4 loopback
            // 이므로 IPv4 unspecified 만 처리 — IPv6 unspecified (`[::]`) 는 별 backlog 박제 의무
            // (Uri.Host 가 IPv6 의 경우 full-expand 형태 "0000:..." 로 정규화되어 단순 비교 안 통함).
            var host = u.Host == "0.0.0.0" ? "127.0.0.1" : u.Host;
            // UriBuilder 가 default port 를 -1 로 표기 + ToString() 에서 생략 — 명시 port 유지를 위해 직접 조립.
            return $"{u.Scheme}://{host}:{u.Port}";
        }
        catch (Exception ex)
        {
            Log.Warn($"config.json listenUrl 추출 실패 — fallback {DefaultLocalBaseUrl} ({ex.Message})");
            return DefaultLocalBaseUrl;
        }
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
