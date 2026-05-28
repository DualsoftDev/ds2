using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using log4net;

namespace Llm.Shared.Mcp;

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
    public static McpConfigWriter Create(string serverName, string url, string handshakeNonce) =>
        CreateMulti(new[] { new McpServerEntry(serverName, url, new Dictionary<string, string>
        {
            ["X-Promaker-Nonce"] = handshakeNonce,
        }) });

    /// <summary>
    /// 다중 server 박제 (Phase S5c — promaker loopback + lighthouse LAN 동시 등록).
    /// 항목 0개 또는 server name 중복은 throw. 파일명 / ACL 정책은 단일 server 와 동일.
    /// <see cref="ServerName"/> 은 첫 번째 entry 의 이름 (legacy field).
    /// </summary>
    public static McpConfigWriter CreateMulti(IReadOnlyList<McpServerEntry> servers)
    {
        if (servers is null || servers.Count == 0) throw new ArgumentException("servers 비어 있음.", nameof(servers));
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("server name 빈 값 금지.");
            if (!distinct.Add(s.Name)) throw new ArgumentException($"server name 중복 — {s.Name}");
        }

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Promaker");
        Directory.CreateDirectory(dir);

        var sessionId = Process.GetCurrentProcess().SessionId;
        var pid = Environment.ProcessId;
        var fileName = $"mcp-{sessionId}-{pid}-{Guid.NewGuid():N}.json";
        var path = System.IO.Path.Combine(dir, fileName);

        var mcpServers = new Dictionary<string, object>(servers.Count);
        foreach (var s in servers)
        {
            mcpServers[s.Name] = new
            {
                type = "http",
                url = s.Url,
                headers = s.Headers ?? new Dictionary<string, string>(),
            };
        }

        var doc = new { mcpServers };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

        // M1 — write→ACL race 제거: 파일 생성 시점부터 owner-only ACL 적용 후 write.
        WriteWithOwnerOnlyAcl(path, json);
        Log.Info($"McpConfigWriter 작성 — {path} (servers={servers.Count})");

        return new McpConfigWriter(path, servers[0].Name);
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
    ///
    /// **정책 (2026-05-27 개정)**: 같은 WindowsSessionId 안의 살아있는 Promaker 인스턴스의 pid 집합을
    /// 먼저 수집한 뒤, 그 집합에 속하지 않는 pid 의 파일만 삭제. mtime 임계 폐기 — alive 인스턴스의
    /// idle chat (5분+ 무응답) .mcp-config 가 다른 신규 인스턴스의 startup sweep 에 의해 삭제되어
    /// 다음 turn 의 Claude CLI spawn 이 "MCP config file not found" 로 exit=1 종료되는 회귀 차단.
    ///
    /// **PID reuse 보호**: `Process.GetProcessesByName("Promaker")` 결과만 alive set 에 포함하므로,
    /// 죽은 Promaker pid 가 다른 exe (chrome, explorer 등) 로 재할당된 경우 set 미포함 → sweep.
    ///
    /// **자기 자신 보호**: `Environment.ProcessId` 는 ProcessName 무관 alive set 에 무조건 포함.
    /// 실 Promaker 런타임에서는 자기도 "Promaker" 라 자연히 잡히지만 (예: dotnet test) 다른 host
    /// 프로세스에서 호출 시에도 자기 파일 보호 보장.
    ///
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
            var pattern = $"mcp-{currentSessionId}-*.json";

            var alivePids = new HashSet<int> { Environment.ProcessId };
            foreach (var p in Process.GetProcessesByName("Promaker"))
            {
                try
                {
                    if (p.SessionId == currentSessionId) alivePids.Add(p.Id);
                }
                catch { /* admin / 권한 부족 등 — 보수적 skip (해당 pid 는 sweep 대상으로 흘러가지만 alive 면 다음 startup 까지 무해 leak 1개) */ }
                finally { p.Dispose(); }
            }

            var swept = 0;
            foreach (var path in Directory.EnumerateFiles(dir, pattern))
            {
                if (!TryParsePidFromFileName(System.IO.Path.GetFileName(path), out var filePid)) continue;
                if (alivePids.Contains(filePid)) continue;

                try
                {
                    File.Delete(path);
                    swept++;
                    Log.Info($"McpConfigWriter sweep — {path} (pid={filePid} alive set 외)");
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

}

/// <summary>McpConfigWriter.CreateMulti 입력 — 한 `mcpServers.<Name>` 항목 정의 (Phase S5c).</summary>
public sealed record McpServerEntry(string Name, string Url, IReadOnlyDictionary<string, string>? Headers);
