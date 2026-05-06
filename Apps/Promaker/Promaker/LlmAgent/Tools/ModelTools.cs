using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using Ds2.Core;
using Ds2.LlmAgent;
using log4net;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Promaker.LlmAgent.Tools;

/// <summary>
/// Phase 1c — add_system + list_systems.
/// Phase 1d-1 — add_flow / add_work / add_call / add_arrow / add_api_def 풀세트 추가.
///
/// 모든 mutation tool 은 ImportPlanBuilder 에 ImportPlanOperation 누적만. turn end 의 단일
/// ApplyImportPlan 호출이 1 undo step 생성. 같은 turn 안의 ID chaining (add_flow 의 반환 id 를
/// 다음 add_work 의 flowId 로 사용) 은 ToolOperations 가 plan + store 합산 lookup 으로 지원.
/// </summary>
[McpServerToolType]
public static class ModelTools
{
    private static readonly ILog ToolCallLog = LogManager.GetLogger("Promaker.LlmAgent.ToolCall");

    // DI 주입은 [FromKeyedServices(null)] 로 명시. ASP.NET Core MVC 의 [FromServices] 는 본 SDK
    // (ModelContextProtocol.AspNetCore 1.2.0) 에서 인식되지 않음 → MCP binder 가 "DI 인자" 와 "tool JSON 인자"
    // 를 구분하기 위해 keyed services API (unkeyed = null key) 를 사용. 본 attribute 가 없으면 LLM 의
    // tool 인자로 잘못 매핑됨.

    // ─── 공통 헬퍼 ────────────────────────────────────────────────────────────

    private const int NameMaxLength = 128;

    private static string? Sanitize(string? value, string field, int maxLength = NameMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"VALIDATION_ERROR: {field} 이(가) 비어있습니다.";
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            return $"VALIDATION_ERROR: {field} 길이 {trimmed.Length} > {maxLength}.";
        return null;
    }

    private static (Guid? id, string? error) ParseGuid(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, $"VALIDATION_ERROR: {field} 이(가) 비어있습니다.");
        if (!Guid.TryParse(value.Trim(), out var g))
            return (null, $"VALIDATION_ERROR: {field} 가 유효한 GUID 형식이 아닙니다 ({value}).");
        return (g, null);
    }

    private static async Task<string> RunMutation(
        LlmTurnContextProvider turnProvider, string toolName,
        Func<LlmTurnContext, string> work)
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");
        ctx.IncrementMutationCount();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var msg = await ctx.Dispatcher.InvokeAsync(() => work(ctx));
            ToolCallLog.Info($"{toolName} ok elapsedMs={sw.ElapsedMilliseconds} planSize={ctx.Plan.Count}");
            return msg;
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"{toolName} 실패 elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            return $"VALIDATION_ERROR: {ex.Message}";
        }
    }

    // ─── Mutation tools ──────────────────────────────────────────────────────

    [McpServerTool, Description("Promaker 모델에 새 DsSystem 을 추가합니다 (현재 단순화: 첫 번째 프로젝트에 자동 부착). 반환: 새 system Id (full GUID).")]
    public static Task<string> AddSystem(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("System 이름 (1-128자, 한 프로젝트 내 unique).")] string name,
        [Description("Active 여부. 기본 true.")] bool isActive = true)
    {
        var err = Sanitize(name, "name");
        if (err != null) return Task.FromResult(err);
        name = name.Trim();
        return RunMutation(turnProvider, "add_system", ctx =>
        {
            var sysId = ToolOperations.queueAddSystem(ctx.Plan, ctx.Store, name, isActive);
            return $"[plan] add_system queued: name=\"{name}\", isActive={isActive}, id={sysId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker System 아래에 새 Flow 를 추가합니다. 반환: 새 Flow Id.")]
    public static Task<string> AddFlow(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("Flow 이름 (1-128자, System 내 unique).")] string name,
        [Description("Parent System 의 GUID (list_systems 결과 또는 같은 turn 의 add_system 반환값).")] string systemId)
    {
        var err = Sanitize(name, "name");
        if (err != null) return Task.FromResult(err);
        var (sysGuid, gerr) = ParseGuid(systemId, "systemId");
        if (gerr != null) return Task.FromResult(gerr);
        name = name.Trim();
        return RunMutation(turnProvider, "add_flow", ctx =>
        {
            var flowId = ToolOperations.queueAddFlow(ctx.Plan, ctx.Store, name, sysGuid!.Value);
            return $"[plan] add_flow queued: name=\"{name}\", systemId={sysGuid:D}, id={flowId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker Flow 아래에 새 Work 를 추가합니다. Work 표시명 = \"{flow.Name}.{localName}\". 반환: 새 Work Id.")]
    public static Task<string> AddWork(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("Work LocalName (1-128자, Flow 내 unique).")] string localName,
        [Description("Parent Flow 의 GUID.")] string flowId)
    {
        var err = Sanitize(localName, "localName");
        if (err != null) return Task.FromResult(err);
        var (flowGuid, gerr) = ParseGuid(flowId, "flowId");
        if (gerr != null) return Task.FromResult(gerr);
        localName = localName.Trim();
        return RunMutation(turnProvider, "add_work", ctx =>
        {
            var workId = ToolOperations.queueAddWork(ctx.Plan, ctx.Store, localName, flowGuid!.Value);
            return $"[plan] add_work queued: localName=\"{localName}\", flowId={flowGuid:D}, id={workId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker Work 아래에 새 Call 을 추가합니다. Call 표시명 = \"{devicesAlias}.{apiName}\". 반환: 새 Call Id.")]
    public static Task<string> AddCall(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("Devices alias (Call 표시명의 앞부분).")] string devicesAlias,
        [Description("API 이름 (Call 표시명의 뒷부분).")] string apiName,
        [Description("Parent Work 의 GUID.")] string workId)
    {
        var err = Sanitize(devicesAlias, "devicesAlias") ?? Sanitize(apiName, "apiName");
        if (err != null) return Task.FromResult(err);
        var (workGuid, gerr) = ParseGuid(workId, "workId");
        if (gerr != null) return Task.FromResult(gerr);
        devicesAlias = devicesAlias.Trim();
        apiName = apiName.Trim();
        return RunMutation(turnProvider, "add_call", ctx =>
        {
            var callId = ToolOperations.queueAddCall(ctx.Plan, ctx.Store, devicesAlias, apiName, workGuid!.Value);
            return $"[plan] add_call queued: name=\"{devicesAlias}.{apiName}\", workId={workGuid:D}, id={callId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker System 아래에 새 ApiDef 를 추가합니다. 반환: 새 ApiDef Id.")]
    public static Task<string> AddApiDef(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("ApiDef 이름 (1-128자, System 내 unique).")] string name,
        [Description("Parent System 의 GUID.")] string systemId)
    {
        var err = Sanitize(name, "name");
        if (err != null) return Task.FromResult(err);
        var (sysGuid, gerr) = ParseGuid(systemId, "systemId");
        if (gerr != null) return Task.FromResult(gerr);
        name = name.Trim();
        return RunMutation(turnProvider, "add_api_def", ctx =>
        {
            var defId = ToolOperations.queueAddApiDef(ctx.Plan, ctx.Store, name, sysGuid!.Value);
            return $"[plan] add_api_def queued: name=\"{name}\", systemId={sysGuid:D}, id={defId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("두 Work 사이 (같은 System) 또는 두 Call 사이 (같은 Work) 에 Arrow 를 추가합니다. 종류는 자동 판별. 반환: 새 Arrow Id + kind.")]
    public static Task<string> AddArrow(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider,
        [Description("Source 의 GUID (Work 또는 Call).")] string sourceId,
        [Description("Target 의 GUID (Source 와 같은 종류).")] string targetId,
        [Description("Arrow type. 허용 값: Unspecified|Start|Reset|StartReset|ResetReset|Group. 기본 Start.")] string arrowType = "Start")
    {
        var (srcGuid, serr) = ParseGuid(sourceId, "sourceId");
        if (serr != null) return Task.FromResult(serr);
        var (tgtGuid, terr) = ParseGuid(targetId, "targetId");
        if (terr != null) return Task.FromResult(terr);
        if (!Enum.TryParse<ArrowType>(arrowType?.Trim(), ignoreCase: true, out var atype))
            return Task.FromResult($"VALIDATION_ERROR: arrowType 값 '{arrowType}' 이 유효하지 않습니다. 허용: Unspecified|Start|Reset|StartReset|ResetReset|Group.");
        return RunMutation(turnProvider, "add_arrow", ctx =>
        {
            var (arrowId, kind) = ToolOperations.queueAddArrow(ctx.Plan, ctx.Store, srcGuid!.Value, tgtGuid!.Value, atype);
            return $"[plan] add_arrow queued: kind={kind}, type={atype}, source={srcGuid:D}, target={tgtGuid:D}, id={arrowId:D}, planSize={ctx.Plan.Count}";
        });
    }

    // ─── Read tools ──────────────────────────────────────────────────────────

    [McpServerTool, Description("현재 Promaker 모델의 모든 DsSystem 목록을 반환합니다 (모든 프로젝트의 active + passive). full GUID 로 표기.")]
    public static async Task<string> ListSystems(
        [FromKeyedServices(null)] LlmTurnContextProvider turnProvider)
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
                sb.AppendLine($"- {name} (id={id:D}, {(isActive ? "active" : "passive")})");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"list_systems 실패 elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            return $"INTERNAL_ERROR: {ex.Message}";
        }
    }
}
