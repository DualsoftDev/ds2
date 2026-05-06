using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using log4net;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Promaker.LlmAgent.Tools;

/// <summary>
/// Phase 1c — 첫 mutation tool (`add_system`) + read tool (`list_systems`).
/// </summary>
[McpServerToolType]
public static class ModelTools
{
    private static readonly ILog ToolCallLog = LogManager.GetLogger("Promaker.LlmAgent.ToolCall");

    // DI 주입은 [FromKeyedServices(null)] 로 명시. ASP.NET Core MVC 의 [FromServices] 는 본 SDK
    // (ModelContextProtocol.AspNetCore 1.2.0) 에서 인식되지 않음 → MCP binder 가 "DI 인자" 와 "tool JSON 인자"
    // 를 구분하기 위해 keyed services API (unkeyed = null key) 를 사용. 본 attribute 가 없으면 LLM 의
    // tool 인자로 잘못 매핑됨.

    [McpServerTool, Description("Promaker 모델에 새 DsSystem 을 추가합니다 (현재 phase 1c 단순화: 첫 번째 프로젝트에 자동 부착). 반환: 새 system Id.")]
    public static async Task<string> AddSystem(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("System 이름 (1-128자, 한 프로젝트 내 unique).")] string name,
        [Description("Active 여부. 기본 true.")] bool isActive = true)
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");

        // sanitize
        if (string.IsNullOrWhiteSpace(name))
            return "VALIDATION_ERROR: name 이 비어있습니다.";
        name = name.Trim();
        if (name.Length > 128)
            return $"VALIDATION_ERROR: name 길이 {name.Length} > 128.";

        ctx.IncrementMutationCount();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var sysId = await ctx.Dispatcher.InvokeAsync(() =>
                ToolOperations.queueAddSystem(ctx.Plan, ctx.Store, name, isActive));
            var sidShort = sysId.ToString("N").Substring(0, 8);
            var msg = $"[plan] add_system queued: name=\"{name}\", isActive={isActive}, id={sidShort}…, planSize={ctx.Plan.Count}";
            ToolCallLog.Info($"add_system ok name=\"{name}\" elapsedMs={sw.ElapsedMilliseconds} planSize={ctx.Plan.Count}");
            return msg;
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"add_system 실패 name=\"{name}\" elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            return $"VALIDATION_ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("현재 Promaker 모델의 모든 DsSystem 목록을 반환합니다 (모든 프로젝트의 active + passive).")]
    public static async Task<string> ListSystems(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider)  // DI marker — 위 AddSystem 주석 참조
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var rows = await ctx.Dispatcher.InvokeAsync(() => ToolOperations.listSystems(ctx.Store));
            ToolCallLog.Info($"list_systems ok count={rows.Length} elapsedMs={sw.ElapsedMilliseconds}");
            if (rows.Length == 0) return "(no systems)";
            var sb = new StringBuilder();
            foreach (var (id, name, isActive) in rows)
            {
                var sidShort = id.ToString("N").Substring(0, 8);
                sb.AppendLine($"- {name} (id={sidShort}…, {(isActive ? "active" : "passive")})");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"list_systems 실패 elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            return $"INTERNAL_ERROR: {ex.Message}";
        }
    }
}
