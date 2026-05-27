namespace Promaker.LlmAgent;

/// <summary>
/// Promaker 측 MCP server 가 노출하는 모든 tool 의 fully-qualified 이름 (`mcp__&lt;server&gt;__&lt;snake_case&gt;`).
/// `--allowed-tools` 화이트리스트 인자로 Claude CLI 에 전달.
///
/// **동기화 책임**: `Tools/ModelTools.cs` / `Tools/LightHouseTools.cs` 에 [McpServerTool] 메서드 추가/제거 시
/// 본 list 도 갱신. drift 시 LLM 측 호출이 조용히 차단되며, 1d-6 의 tool allowlist negative test 가 회귀 검출.
///
/// **Plan B (2026-05-27)** — 12종 정적. Promaker 6 (`ModelTools`) + LightHouse 6 (`LightHouseTools` wrapper).
/// 모두 `mcp__promaker__*` prefix. lighthouse server 별 동적 명명 path 폐기 — Anthropic Claude CLI 의 mcp HTTP/SSE
/// transport 가 OAuth 2.1 강제라 lighthouse 직접 박제 불가, wrapper 우회. wrapper 가 multi-service fan-out 자체 처리.
///
/// servername = `McpConfigWriter.Create("promaker", ...)` 의 첫 인자 (`McpHostService` 측 일관).
/// </summary>
public static class PromakerToolNames
{
    public const string ServerName = "promaker";

    public static readonly string[] All =
    {
        // ── ModelTools (Ds2 모델 편집) — 6종 ──────────────────
        "mcp__promaker__find_by_name",
        "mcp__promaker__validate_model",
        "mcp__promaker__apply_model_doc",
        "mcp__promaker__validate_model_doc",
        "mcp__promaker__export_model_doc",
        "mcp__promaker__json_to_yaml",
        // ── LightHouseTools (KB 검색 wrapper, multi-service fan-out) — 6종 ──────────────────
        "mcp__promaker__lighthouse_list",
        "mcp__promaker__lighthouse_search",
        "mcp__promaker__lighthouse_outline",
        "mcp__promaker__lighthouse_read",
        "mcp__promaker__lighthouse_fulltext",
        "mcp__promaker__lighthouse_summary",
    };
}
