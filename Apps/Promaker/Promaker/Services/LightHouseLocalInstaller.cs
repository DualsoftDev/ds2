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
/// **설치본 기준만 사용 (2026-05-30 사용자 결정)** — dev repo 빌드 출력은 service binPath 로 부적합
/// (clean 시 소멸 / 버전 불일치 / 배포 미검증) 하므로 탐색 대상에서 제외. <see cref="ResolveDeployment"/> 는
/// 설치된 Promaker 의 {app}\LightHouseService\ 만 탐색한다:
///   ① 실행 디렉터리 {AppContext.BaseDirectory}\LightHouseService  (설치본을 직접 실행 중인 경우)
///   ② 레지스트리 InstallLocation\LightHouseService  (개발 빌드를 실행 중이어도 설치된 Promaker 를 사용)
/// 설치본 미발견 시 <see cref="EnableAsync(SecureString, SecureString, CancellationToken)"/> 가 명시 throw
/// → 사용자에게 Promaker installer ('AI 기능 활성화' 컴포넌트) 설치 유도.
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
    /// finally wipe (이중 안전).
    /// <para/>
    /// **B5 (2026-05-27)** — DPAPI 박제 단계도 SecureString overload 직접 호출 (B4 의 PtrToStringUni 마지막 1지점 leak 제거).
    /// <para/>
    /// **caller (EnableLocalServiceDialog)** 가 SecureString 소유권 이양. 본 메서드는 사용 후 Dispose 의무 가짐.
    /// <para/>
    /// **단일 비밀번호 통합 (2026-05-30)** — 이전 (psk, certPwd) 2개 인자를 단일 <paramref name="password"/> 로 통합.
    /// 같은 byte[] 를 PSK / cert PFX password 양쪽 임시 파일에 박제 → enable-ai.ps1 의 -PskInputPath /
    /// -CertPasswordInputPath 계약은 그대로 유지 (ps1 무변경).
    /// </summary>
    /// <param name="password">사용자 입력 비밀번호 SecureString (PSK / cert PFX password 공용). 본 메서드가 Dispose (early-throw 경로 포함).</param>
    public async Task<EnableResult> EnableAsync(SecureString password, CancellationToken ct = default)
    {
        // **B4 자가 검열 Critical-1 fix (2026-05-27)** — Dispose 의무 박제는 진입 직후부터 보장.
        // (이전 구현: Argument 검사 / ResolveDeployment / File.Exists throw 가 try 블록 *바깥* 이라 SecureString 누수.)
        var pskIn  = Path.Combine(Path.GetTempPath(), $"promaker-psk-{Guid.NewGuid():N}.tmp");
        var cpwdIn = Path.Combine(Path.GetTempPath(), $"promaker-cpw-{Guid.NewGuid():N}.tmp");
        var logOut = Path.Combine(Path.GetTempPath(), $"promaker-ai-{Guid.NewGuid():N}.log");

        byte[]? pwdBytes = null;
        try
        {
            if (password is null) throw new ArgumentNullException(nameof(password));
            if (password.Length == 0) throw new ArgumentException("비밀번호 비어있음.", nameof(password));

            var deployment = ResolveDeployment()
                ?? throw new InvalidOperationException(
                    "설치된 Promaker 의 LightHouseService 를 찾지 못했습니다. " +
                    "Promaker installer 로 'AI 기능 활성화' 컴포넌트를 포함해 설치한 뒤 다시 시도하십시오 " +
                    "(개발 환경에서도 설치본 기준으로만 service 를 등록합니다).");

            var enableScript = Path.Combine(deployment.ScriptsDir, "enable-ai.ps1");
            if (!File.Exists(enableScript))
                throw new FileNotFoundException("enable-ai.ps1 미발견 — Promaker installer 로 'AI 기능 활성화' 컴포넌트를 포함해 재설치 후 재시도하십시오.", enableScript);

            // **B4 (2026-05-27)** — SecureString → UTF-8 byte[]. Owner-only ACL 은 default Temp 디렉터리 권한에 의존
            // (Windows 의 %TEMP% 가 사용자별 분리 박제 — 다른 사용자 read 불가, admin elevation 시 read 가능 — UAC 후 자기 process 가 read).
            // **단일 비밀번호 통합 (2026-05-30)** — 같은 byte[] 를 PSK / cert PFX password 양쪽 파일에 박제.
            pwdBytes = SecureStringToUtf8Bytes(password);
            File.WriteAllBytes(pskIn, pwdBytes);
            File.WriteAllBytes(cpwdIn, pwdBytes);

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

            // **B5 (2026-05-27)** — LlmConfig.SetLightHousePsk(SecureString) overload 박제 완료. SecureString 직접 박제 path —
            // managed string 변환 0 (B4 의 PtrToStringUni 마지막 1지점 leak 제거).
            var serviceId = PersistLocalEntry(password);
            Log.Info($"LightHouse Local entry 박제 완료 — ServiceId={serviceId}");

            var startOk = TryParseStartResult(logOut);
            return new EnableResult(serviceId, logOut, startOk);
        }
        finally
        {
            // **B4 (2026-05-27)** — heap 평문 wipe (Array.Clear) + temp file wipe (이중 안전).
            // **단일 비밀번호 통합 (2026-05-30)** — 단일 byte[] / SecureString 만 정리.
            if (pwdBytes is not null) Array.Clear(pwdBytes, 0, pwdBytes.Length);
            TryWipeFile(pskIn);
            TryWipeFile(cpwdIn);
            // null-safe — ArgumentNullException 분기 진입 시 null 일 수 있음.
            password?.Dispose();
            // log 는 caller 가 결과 표시 후 직접 cleanup. (실패 시 진단 자료로 유용.)
        }
    }

    /// <summary>
    /// **B4 (2026-05-27) deprecated** — managed string 시그니처 보존 (외부 caller 호환). 신규 caller 는 SecureString
    /// overload 사용 의무 (heap 평문 lifetime 최소화 SSOT). 본 overload 는 평문 string → SecureString 1회 wrapping
    /// 후 정공 path 호출 — string 자체는 immutable 이라 wipe 불가, caller 평문 잔존 risk 그대로.
    /// </summary>
    [Obsolete("B4 (2026-05-27): SecureString overload 사용 권장. managed string path 는 heap 평문 잔존 risk.")]
    public Task<EnableResult> EnableAsync(string passwordPlain, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(passwordPlain)) throw new ArgumentException("비밀번호 평문 비어있음.", nameof(passwordPlain));
        var pwd = StringToSecureString(passwordPlain);
        return EnableAsync(pwd, ct);
    }

    private static SecureString StringToSecureString(string s)
    {
        var ss = new SecureString();
        foreach (var ch in s) ss.AppendChar(ch);
        ss.MakeReadOnly();
        return ss;
    }

    /// <summary>**PR2 (2026-05-27)** — SecureString → managed string 1회 변환 (LightHouseClient PSK provider `Func&lt;string?&gt;` 인터페이스 보존 의무).
    /// ApplicationSettings 의 LhTestConnection 이 사용 — pending PSK 의 1회 wire-up. 본 변환 결과 string 은 immutable 이라 GC 회수 전까지 heap 잔존
    /// (LightHouseClient.PSK provider 가 SecureString 박제 migrate 시 본 helper 도 폐기 가능 — 별 backlog).</summary>
    internal static string SecureStringToManagedString(SecureString ss)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = Marshal.SecureStringToGlobalAllocUnicode(ss);
            return Marshal.PtrToStringUni(ptr) ?? "";
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    /// <summary>**B4 (2026-05-27)** — SecureString → UTF-8 byte[]. 중간 char[] 도 Array.Clear 로 wipe.
    /// caller 가 반환 byte[] 의 사용 후 Array.Clear 의무 (finally).
    /// <para/>**B5 (2026-05-27)** — LlmConfig.SetLightHousePsk(SecureString) 도 본 helper SSOT 호출 (중복 제거).</summary>
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

    /// <summary>**B5 (2026-05-27) — SecureString 정공 path**. SecureString → DPAPI 직접 박제 — managed string 변환 0.
    /// <see cref="LlmConfig.SetLightHousePsk(string, SecureString?)"/> overload 사용. PtrToStringUni leak 제거.</summary>
    private string PersistLocalEntry(SecureString psk)
    {
        var entry = EnsureLocalEntry();
        _llmConfig.SetLightHousePsk(entry.ServiceId, psk);
        _llmConfig.Save();
        return entry.ServiceId;
    }

    /// <summary>**B5 자가 검열 M2 fix (2026-05-27)** — entry 박제 / 단일 active 정합 SSOT. SecureString overload 만 호출 (string overload caller 0 — dead code 제거).</summary>
    private LightHouseServiceConfig EnsureLocalEntry()
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
        else if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            entry.DisplayName = "Local LightHouse";
        }

        // 단일 active 정합 — Local 이 방금 활성화되었으므로 다른 모든 entry 비활성.
        foreach (var s in _llmConfig.LightHouseServices)
            s.Active = ReferenceEquals(s, entry);

        return entry;
    }

    /// <summary>설치된 Promaker 의 {app}\LightHouseService\ 만 탐색 — dev repo fallback 없음 (2026-05-30 사용자 결정).
    /// ① 실행 디렉터리(설치본 직접 실행) → ② 레지스트리 InstallLocation(개발 빌드 실행이어도 설치본 사용) 순.
    /// 두 후보 모두 scripts\ + Ds2.LightHouseService.exe 가 함께 있어야 유효. 미발견 시 null.</summary>
    public static Deployment? ResolveDeployment()
    {
        foreach (var root in EnumerateInstalledServiceRoots())
        {
            var scripts = Path.Combine(root, "scripts");
            var exe = Path.Combine(root, "Ds2.LightHouseService.exe");
            if (Directory.Exists(scripts) && File.Exists(exe))
                return new Deployment(scripts, exe);
        }
        return null;
    }

    /// <summary>설치본의 LightHouseService 폴더 후보를 우선순위 순으로 산출. exe + scripts 묶음 정합을 위해
    /// scripts 와 exe 는 항상 동일 root 에서 가져온다 (enable-ai.ps1 이 같은 폴더의 install-service.ps1 등을 상대 참조).</summary>
    private static IEnumerable<string> EnumerateInstalledServiceRoots()
    {
        // ① 설치본을 직접 실행 중 — installer 가 {app}\LightHouseService\ 로 번들.
        yield return Path.Combine(AppContext.BaseDirectory, "LightHouseService");

        // ② 개발 빌드를 실행 중이어도 설치된 Promaker 의 service 를 사용 — Inno Setup InstallLocation.
        var installRoot = ResolveInstalledPromakerRoot();
        if (installRoot is not null)
            yield return Path.Combine(installRoot, "LightHouseService");
    }

    /// <summary>설치된 Promaker 의 app 루트(InstallLocation) 를 Inno Setup uninstall 레지스트리에서 조회.
    /// 키의 GUID 는 Installer/Setup.iss 의 [Setup] AppId SSOT — AppId 는 업그레이드 호환을 위해 영구 불변.
    /// 64-bit 모드 설치(ArchitecturesInstallIn64BitMode)이므로 64-bit view 의 HKLM 만 조회. 미설치 / 값 없음 시 null.</summary>
    private static string? ResolveInstalledPromakerRoot()
    {
        // Setup.iss: AppId={{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31} → uninstall 키는 "<GUID>_is1".
        const string subKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31}_is1";
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey);
        var loc = key?.GetValue("InstallLocation") as string;
        return string.IsNullOrWhiteSpace(loc) ? null : loc.TrimEnd('\\', '/');
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

    // 설치본 전용 — dev fallback 제거 후 "운영/개발" 구분 플래그(IsOperational)는 의미를 잃어 제거 (2026-05-30).
    public sealed record Deployment(string ScriptsDir, string ExePath);
    public readonly record struct EnableResult(string ServiceId, string LogPath, bool? Healthy);
}
