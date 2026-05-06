using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Promaker.LlmAgent.Tools;

/// <summary>
/// Phase 1b-c 검증용 dummy tool. spike 의 PingTool 구조 그대로.
/// Phase 1c 진입 시 mutation tool (`add_system` 등) 로 대체될 예정.
/// </summary>
[McpServerToolType]
public static class PingTool
{
    [McpServerTool, Description("Reply with a friendly pong message — phase 1b-c 검증용.")]
    public static string Ping([Description("Optional name")] string? name = null)
        => $"pong from Promaker, name={name ?? "(none)"}";
}
