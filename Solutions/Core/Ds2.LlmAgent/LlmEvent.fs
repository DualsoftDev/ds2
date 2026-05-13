namespace Ds2.LlmAgent

open System.Text.Json

/// MCP server 의 init-time health 상태. Claude CLI 의 stream-json `system/init` 패킷에 포함됨.
type McpServerStatus = {
    Name: string
    Status: string  // "connected" | "needs-auth" | "failed" | ...
}

/// Provider 측에서 ChatPanel 측으로 흘려보내는 stream 이벤트.
///
/// `IAsyncEnumerable<LlmEvent>` 1종으로 통일 (결정 9). Claude CLI 의 stream-json 5종 패킷을
/// 정규화한 형태. Phase 2 의 Codex / OpenAI / Anthropic API provider 도 같은 DU 로 매핑한다.
type LlmEvent =
    /// 첫 패킷 — session 시작. session_id 캡처 후 다음 turn `--resume` 인자로 사용.
    | SessionStarted of sessionId: string * model: string * tools: string list * mcpServers: McpServerStatus list
    /// LLM 응답 텍스트의 한 조각. ChatPanel 에서 누적 표시.
    | AssistantDelta of text: string
    /// LLM 의 thinking block. Phase 1 에선 무시 / phase 2 부터 별도 표시 가능.
    | Thinking of text: string
    /// LLM 의 tool 호출. id 는 toolu_... 형식.
    | ToolUse of id: string * name: string * input: JsonElement
    /// MCP server 가 회신한 tool 결과.
    | ToolResult of toolUseId: string * isError: bool * content: string
    /// 5-hour rate limit 등 보조 이벤트. ChatPanel status bar 표시용.
    | RateLimitEvent of resetsAtUnix: int64 * status: string
    /// turn 종료. 1 turn = 1 ImportPlan apply (phase 1c 부터).
    | SessionEnd of durationMs: int * costUsd: decimal * isError: bool * stopReason: string * permissionDenialCount: int
    /// 이 provider 가 자체적으로 일으킨 오류 (CLI 시작 실패, JSON parse 실패, process 비정상 종료 등).
    | ProviderError of message: string
