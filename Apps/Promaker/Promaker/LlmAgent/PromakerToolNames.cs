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
        // ── Read tools (store inspect) — Phase 6 후 2종 (list_*/describe_* 4종은 export_model_doc 의
        //    path?/depth? 인자로 흡수, find_by_name 은 path 출력 격상, validate_model 은 path scope) ──
        "mcp__promaker__find_by_name",
        "mcp__promaker__validate_model",
        // ── doc-level YAML protocol (SSOT: Apps/Promaker/Docs/yaml-protocol-v0.md) ──
        // Wire = JSON object (LLM tool_use native), View = YAML.
        // Phase 5 (op-layer 15종 일소) + Phase 6 (read 4종 흡수) 누적으로 풀세트 = 6종.
        "mcp__promaker__apply_model_doc",
        "mcp__promaker__validate_model_doc",
        "mcp__promaker__export_model_doc",
        "mcp__promaker__json_to_yaml",
    };
}
