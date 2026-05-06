using System;
using System.IO;
using System.Text;
using System.Text.Json;
using log4net;

namespace Promaker.LlmAgent;

/// <summary>
/// `.mcp-config` 임시 JSON 파일 작성 / 정리.
///
/// 결정 5.0: %TEMP%\Promaker\mcp-&lt;WindowsSessionId&gt;-&lt;guid&gt;.json. WindowsSessionId 격리로 RDP / Fast User Switching 충돌 방지.
/// (현재 Phase 1b-c 골격 — ACL 강화 / stale sweep 은 phase 1d).
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
    public static McpConfigWriter Create(string serverName, string url, string handshakeNonce)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Promaker");
        Directory.CreateDirectory(dir);

        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var fileName = $"mcp-{sessionId}-{Guid.NewGuid():N}.json";
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
}
