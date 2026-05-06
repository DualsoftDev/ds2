namespace Promaker.LlmAgent;

/// <summary>
/// Promaker 측 MCP server 가 노출하는 모든 tool 의 fully-qualified 이름 (`mcp__&lt;server&gt;__&lt;snake_case&gt;`).
/// `--allowed-tools` 화이트리스트 인자로 Claude CLI 에 전달.
///
/// **동기화 책임**: `Tools/ModelTools.cs` 에 [McpServerTool] 메서드 추가/제거 시 본 list 도 갱신.
/// drift 시 LLM 측 호출이 조용히 차단되며, 1d-6 의 tool allowlist negative test 가 회귀 검출.
///
/// servername = `McpConfigWriter.Create("promaker", ...)` 의 첫 인자 (`McpHostService` 측 일관).
/// </summary>
public static class PromakerToolNames
{
    public const string ServerName = "promaker";

    public static readonly string[] All =
    {
        "mcp__promaker__list_projects",
        "mcp__promaker__list_systems",
        "mcp__promaker__describe_system",
        "mcp__promaker__describe_subtree",
        "mcp__promaker__find_by_name",
        "mcp__promaker__validate_model",
        "mcp__promaker__add_system",
        "mcp__promaker__add_flow",
        "mcp__promaker__add_work",
        "mcp__promaker__add_call",
        "mcp__promaker__add_api_def",
        "mcp__promaker__add_arrow",
        "mcp__promaker__remove_entity",
        "mcp__promaker__rename_entity",
    };
}
