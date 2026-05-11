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
        "mcp__promaker__apply_operations",
        "mcp__promaker__add_project",
        "mcp__promaker__add_active_system",
        "mcp__promaker__add_passive_system",
        "mcp__promaker__add_flow",
        "mcp__promaker__add_work",
        "mcp__promaker__add_call",
        "mcp__promaker__add_api_def",
        "mcp__promaker__add_arrow",
        "mcp__promaker__add_cylinder",
        "mcp__promaker__add_clamp",
        "mcp__promaker__add_robot",
        "mcp__promaker__add_device",
        "mcp__promaker__remove_entity",
        "mcp__promaker__rename_entity",
        // ── Phase 1 YAML protocol (SSOT: Apps/Promaker/Docs/yaml-protocol-v0.md) ──
        // Wire = JSON object (LLM tool_use native), View = YAML.
        // 기존 validate_model (consistency check) 과 별개 도구 — `_doc` 접미사로 차별화.
        "mcp__promaker__apply_model_doc",
        "mcp__promaker__validate_model_doc",
        "mcp__promaker__export_model_doc",
        "mcp__promaker__json_to_yaml",
    };
}
