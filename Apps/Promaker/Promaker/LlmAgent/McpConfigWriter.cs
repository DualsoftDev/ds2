using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using log4net;

namespace Promaker.LlmAgent;

/// <summary>
/// `.mcp-config` 임시 JSON 파일 작성 / 정리.
///
/// 결정 5.0/5.3/5.4: %TEMP%\Promaker\mcp-&lt;WindowsSessionId&gt;-&lt;pid&gt;-&lt;guid&gt;.json.
/// WindowsSessionId 격리로 RDP / Fast User Switching 충돌 방지. 파일명에 spawn 한 process 의 PID 포함 →
/// stale sweep 의 dead-pid 검사 가능 (SweepStale 호출 시점의 자기 자신 보호).
///
/// ACL (1d-5): Owner = current user, DACL = current user FullControl only, inheritance 차단.
/// 같은 user 의 다른 logon session 또는 악성 프로세스의 read 차단.
/// </summary>
public sealed class McpConfigWriter : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(McpConfigWriter));

    /// <summary>Sweep 시 dead 가 아닌 alive 프로세스의 파일이 stale 로 간주되기 위한 mtime 임계 (분). dead pid 면 즉시 sweep.</summary>
    public const int StaleMinutes = 5;

    public string Path { get; }
    public string ServerName { get; }

    private McpConfigWriter(string path, string serverName)
    {
        Path = path;
        ServerName = serverName;
    }

    /// <summary>
    /// `mcpServers.<serverName>.{type:http, url, headers:{X-Promaker-Nonce: nonce}}` 항목 1개를 가진 임시 파일을 작성.
    /// 호출자가 Dispose 시 파일 삭제.
    /// </summary>
    public static McpConfigWriter Create(string serverName, string url, string handshakeNonce)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Promaker");
        Directory.CreateDirectory(dir);

        var sessionId = Process.GetCurrentProcess().SessionId;
        var pid = Environment.ProcessId;
        var fileName = $"mcp-{sessionId}-{pid}-{Guid.NewGuid():N}.json";
        var path = System.IO.Path.Combine(dir, fileName);

        var doc = new
        {
            mcpServers = new System.Collections.Generic.Dictionary<string, object>
            {
                [serverName] = new
                {
                    type = "http",
                    url = url,
                    headers = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["X-Promaker-Nonce"] = handshakeNonce,
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

        // M1 — write→ACL race 제거: 파일 생성 시점부터 owner-only ACL 적용 후 write.
        // 같은 user 의 다른 process 가 nonce 평문을 ms window 안에 read 하는 경로 차단.
        WriteWithOwnerOnlyAcl(path, json);
        Log.Info($"McpConfigWriter 작성 — {path}");

        return new McpConfigWriter(path, serverName);
    }

    internal static void WriteWithOwnerOnlyAcl(string path, string json)
    {
        var bytes = new UTF8Encoding(false).GetBytes(json);

        if (!OperatingSystem.IsWindows())
        {
            // 비-Windows 는 process owner / file mode 의 OS 기본 격리에 의존 (Linux: 600, mac: 600 등 umask).
            // FileSecurity API 자체가 Windows 전용이므로 별도 ACL 적용 없음.
            File.WriteAllBytes(path, bytes);
            return;
        }

        var sec = TryBuildOwnerOnlySecurity();
        if (sec == null)
        {
            // SID 조회 실패 — 1d-5 보안 격리 기준 미충족 (loopback bind + nonce 가 1차 방어이지만
            // .mcp-config 의 nonce 평문이 같은 user 의 다른 process 에 노출될 수 있는 ms-window race 발생).
            // 정책: fallback write 보다 fail-fast. 호출자 (LlmChatViewModel.InitializeAsync) 가 catch 하여
            // StatusText 로 사용자에게 "보안 격리 불가 — LLM 비활성화" 명시.
            throw new InvalidOperationException(
                "보안 격리 불가 — WindowsIdentity SID 조회 실패로 .mcp-config 의 Owner-only ACL 을 적용할 수 없습니다. " +
                "LLM 비활성화. (가상화 / sandboxed 환경 또는 비표준 user identity 가능성)");
        }

        // FileStream ctor 의 FileSecurity overload — 파일 생성 atomic 하게 ACL 적용.
        using var fs = FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.Create,
            FileSystemRights.WriteData | FileSystemRights.ReadData,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            sec);
        fs.Write(bytes, 0, bytes.Length);
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity? TryBuildOwnerOnlySecurity()
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid == null) return null;
        var sec = new FileSecurity();
        sec.SetOwner(sid);
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return sec;
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
                Log.Info($"McpConfigWriter 삭제 — {Path}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"McpConfigWriter 삭제 실패 — {Path}", ex);
        }
    }

    /// <summary>
    /// %TEMP%/Promaker 의 stale `.mcp-config` 파일을 정리 (1d-5 / 결정 5.4).
    /// 자기 WindowsSessionId 안의 파일만 + (dead pid OR mtime > StaleMinutes) 조건 — 자기 자신 (current pid) 은 절대 sweep 안 함.
    /// 다른 session 의 파일은 건드리지 않음 (RDP / Fast User Switching 격리).
    /// Promaker 시작 시 1회 호출 (App / MainViewModel ctor).
    /// </summary>
    public static void SweepStale()
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Promaker");
            if (!Directory.Exists(dir)) return;

            var currentSessionId = Process.GetCurrentProcess().SessionId;
            var currentPid = Environment.ProcessId;
            var pattern = $"mcp-{currentSessionId}-*.json";
            var threshold = DateTime.UtcNow.AddMinutes(-StaleMinutes);
            var swept = 0;

            foreach (var path in Directory.EnumerateFiles(dir, pattern))
            {
                if (!TryParsePidFromFileName(System.IO.Path.GetFileName(path), out var filePid)) continue;
                if (filePid == currentPid) continue;

                var dead = IsProcessDead(filePid);
                var oldEnough = File.GetLastWriteTimeUtc(path) < threshold;
                if (!dead && !oldEnough) continue;

                try
                {
                    File.Delete(path);
                    swept++;
                    Log.Info($"McpConfigWriter sweep — {path} (pid={filePid}, dead={dead}, oldEnough={oldEnough})");
                }
                catch (Exception ex)
                {
                    Log.Warn($"McpConfigWriter sweep 실패 — {path}", ex);
                }
            }

            if (swept > 0) Log.Info($"McpConfigWriter sweep 완료 — {swept}개 정리");
        }
        catch (Exception ex)
        {
            Log.Warn("McpConfigWriter.SweepStale 예외", ex);
        }
    }

    /// <summary>
    /// 파일명 `mcp-{sessionId}-{pid}-{guid}.json` 에서 pid 추출.
    /// </summary>
    internal static bool TryParsePidFromFileName(string fileName, out int pid)
    {
        pid = 0;
        var parts = fileName.Split('-');
        // ["mcp", sessionId, pid, "guid.json"]
        if (parts.Length < 4) return false;
        return int.TryParse(parts[2], out pid);
    }

    /// <summary>
    /// m2 — Win32Exception (admin process 조회 실패 / PID reuse) 도 fail-safe 로 감쌈.
    /// 죽은 게 확실하지 않으면 false (alive) 로 보수적 보고 → mtime 임계로 자연 sweep.
    /// </summary>
    private static bool IsProcessDead(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
            return false;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Exception) { return false; }
    }

}
