using System.Collections.Generic;
using System.Linq;
using Promaker.LlmAgent;

namespace Promaker.Knowledge;

/// <summary>
/// **D-S7-3c (s6-r31) — LightHouse service 검증 SSOT helper**
/// (todo-lighthouse-kb-server.md §3.16.8).
/// <para/>
/// dialog (ApplicationSettingsDialog) + Promaker.Tests 양쪽이 동일 검증 로직 호출 — drift 회피.
/// </summary>
public static class LightHouseServiceValidator
{
    /// <summary>
    /// **D-S7-3c (s6-r31)** — displayName uniqueness 검증. 결과 = (ok, duplicateName).
    /// trim 후 대소문자 구분 비교. 빈 displayName 도 invalid 로 간주 (caller 가 사전 UI 검증 의무, 본 helper 는 빈 값 1건도 invalid 처리).
    /// <para/>
    /// ok=false 시 duplicateName 은 충돌 이름 (또는 "(빈 값)" 표시). 첫 충돌 1건만 반환 — 사용자 수정 후 재검증 의도.
    /// </summary>
    public static (bool Ok, string? Reason) ValidateDisplayNames(IEnumerable<LightHouseServiceConfig> services)
    {
        var counts = new Dictionary<string, int>();
        foreach (var s in services)
        {
            var name = (s.DisplayName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                return (false, "(빈 값)");
            }
            counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
        }
        var dup = counts.FirstOrDefault(kv => kv.Value > 1);
        if (dup.Value > 1) return (false, dup.Key);
        return (true, null);
    }
}
