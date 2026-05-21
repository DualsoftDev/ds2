using System.Text;
using Promaker.LlmAgent;

namespace Promaker.Knowledge;

/// <summary>
/// **D-S7-3b (s6-r30) — MCP server entry 이름 SSOT**.
/// <para/>
/// 단일 (active=1) 또는 multi (active≥2) 시점 일관 패턴 — `lighthouse-{sanitize(displayName)}`.
/// MCP client (Claude / Codex / Anthropic API) 가 본 이름으로 tool prefix 인식
/// (예: `lighthouse-hq.attachment_search`).
/// <para/>
/// **sanitize 규칙**: 영숫자(ASCII a-z / A-Z / 0-9) + 하이픈만 허용. whitespace / underscore (`_`) / 하이픈은
/// 단일 하이픈으로 normalize, 그 외 (한글 / 특수문자) 모두 silent drop. 연속 하이픈은 1개로 압축. leading /
/// trailing 하이픈 제거. sanitize 결과 빈 문자열이면 fallback = ServiceId 의 처음 8 문자 (Guid v4 의 앞 8 자).
/// <para/>
/// **충돌 정책 (본 turn)**: 사용자가 동일 displayName 의 2 개 active service 박제 시 sanitize 결과 동일 →
/// <see cref="McpConfigWriter.CreateMulti"/> 가 throw. D-S7-3c UI dialog 에서 displayName uniqueness 검증
/// 차단 의무 (별 turn). 본 phase 의 caller (LlmChatViewModel) 는 catch 후 chip 안내 + 부분 활성화.
/// </summary>
public static class LightHouseServerNaming
{
    /// <summary>MCP entry name 의 lighthouse prefix.</summary>
    public const string EntryPrefix = "lighthouse-";

    /// <summary>
    /// **D-S7-3b (s6-r30)** — service entry 의 MCP server name 산정. 본 메서드의 결과가 `.mcp-config` 의
    /// servers key 로 박제되며 LLM tool prefix 도 동일.
    /// </summary>
    public static string McpEntryName(LightHouseServiceConfig service)
    {
        var sanitized = SanitizeDisplayName(service.DisplayName);
        if (sanitized.Length == 0)
        {
            // displayName 빈 값 / sanitize 결과 빈 값 → ServiceId 의 첫 8 자 (Guid v4 의 12345678-... 앞 8).
            var fallback = service.ServiceId.Length >= 8
                ? service.ServiceId.Substring(0, 8)
                : service.ServiceId;
            return EntryPrefix + (string.IsNullOrEmpty(fallback) ? "unknown" : fallback);
        }
        return EntryPrefix + sanitized;
    }

    /// <summary>
    /// **internal for tests** — sanitize 로직 단독 검증 위해 노출. 영숫자/하이픈만 허용 + 연속 하이픈 압축 +
    /// leading/trailing 하이픈 제거.
    /// </summary>
    internal static string SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length);
        var prevHyphen = false;
        foreach (var ch in name)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                prevHyphen = false;
            }
            else if (ch == '-' || ch == '_' || char.IsWhiteSpace(ch))
            {
                if (!prevHyphen && sb.Length > 0) { sb.Append('-'); prevHyphen = true; }
            }
            // 그 외 (한글 / 특수문자) silent drop.
        }
        // trailing hyphen 제거.
        while (sb.Length > 0 && sb[sb.Length - 1] == '-') sb.Length--;
        return sb.ToString();
    }
}
