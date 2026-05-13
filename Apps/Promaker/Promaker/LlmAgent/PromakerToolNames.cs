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
        // ── Read tools (store inspect) ────────────────────────────────────────
        "mcp__promaker__list_projects",
        "mcp__promaker__list_systems",
        "mcp__promaker__describe_system",
        "mcp__promaker__describe_subtree",
        "mcp__promaker__find_by_name",
        "mcp__promaker__validate_model",
        // ── doc-level YAML protocol (SSOT: Apps/Promaker/Docs/yaml-protocol-v0.md) ──
        // Wire = JSON object (LLM tool_use native), View = YAML.
        // Phase 5 cleanup 이후 doc-level + read = 10종으로 응축. op-layer 15종 (apply_operations / add_* /
        // remove_entity / rename_entity) 은 일소됨 — patch DSL 로 대체 (yaml-protocol-v0.md §2.6).
        "mcp__promaker__apply_model_doc",
        "mcp__promaker__validate_model_doc",
        "mcp__promaker__export_model_doc",
        "mcp__promaker__json_to_yaml",
    };
}
