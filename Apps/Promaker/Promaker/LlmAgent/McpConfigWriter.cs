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
        File.WriteAllText(path, json, new UTF8Encoding(false));
        ApplyOwnerOnlyAcl(path);
        Log.Info($"McpConfigWriter 작성 — {path}");

        return new McpConfigWriter(path, serverName);
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
    private static bool TryParsePidFromFileName(string fileName, out int pid)
    {
        pid = 0;
        var parts = fileName.Split('-');
        // ["mcp", sessionId, pid, "guid.json"]
        if (parts.Length < 4) return false;
        return int.TryParse(parts[2], out pid);
    }

    private static bool IsProcessDead(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid == null)
            {
                Log.Warn($"WindowsIdentity.GetCurrent().User == null — ACL skip ({path})");
                return;
            }

            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();

            sec.SetOwner(sid);
            sec.SetAccessRuleProtection(true, false);

            var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule r in rules)
                sec.RemoveAccessRule(r);

            sec.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            fi.SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            Log.Warn($"McpConfigWriter ACL 적용 실패 — {path}", ex);
        }
    }
}
