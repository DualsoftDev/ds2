using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ds2.Core;
using Ds2.LlmAgent;
using log4net;
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

    // DI 인자 (e.g. LlmTurnContextProvider) 는 attribute 없이 자동 주입됨.
    // 근거: ModelContextProtocol.AspNetCore 1.2.0 의 AIFunctionMcpServerTool 이 parameter 의 type 을
    // IServiceProviderIsService.IsService(type) 로 검사 → DI 등록된 type 이면 schema 에서 자동 제외 +
    // service provider 에서 binding. McpHostService 가 LlmTurnContextProvider 를 AddSingleton 등록하므로
    // 자동 검출 path 가 동작. (Pass D — 이전 [FromKeyedServices(null)] 우회 제거)

    // ─── 공통 헬퍼 ────────────────────────────────────────────────────────────

    private const int NameMaxLength = 128;

    /// <summary>
    /// Tool 인자 sanitize. 1d-4 강화 — 길이 + 제어 문자 (Cc) + format 문자 (Cf, BiDi override 등) 차단.
    /// 정상적인 entity 이름은 영문/한글/숫자/공백/일부 기호로 충분하며, 제어·format 문자가 들어올 일은
    /// prompt injection 또는 unicode bomb 시도뿐. 발견 시 codepoint 를 메시지에 포함해 LLM 회복 단서 제공.
    /// </summary>
    private static string? Sanitize(string? value, string field, int maxLength = NameMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"VALIDATION_ERROR: {field} 이(가) 비어있습니다.";
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            return $"VALIDATION_ERROR: {field} 길이 {trimmed.Length} > {maxLength}.";

        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.Control ||
                cat == System.Globalization.UnicodeCategory.Format)
                return $"VALIDATION_ERROR: {field} 에 허용되지 않은 제어/format 문자 (U+{(int)c:X4}) 가 포함되어 있습니다.";
        }
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

    private static async Task<string> RunRead(
        LlmTurnContextProvider turnProvider, string toolName,
        Func<LlmTurnContext, string> work)
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var msg = await ctx.Dispatcher.InvokeAsync(() => work(ctx));
            ToolCallLog.Info($"{toolName} ok elapsedMs={sw.ElapsedMilliseconds} sizeBytes={msg?.Length ?? 0}");
            return msg ?? "";
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"{toolName} 실패 elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            return $"INTERNAL_ERROR: {ex.Message}";
        }
    }

    // ─── Mutation tools ──────────────────────────────────────────────────────

    [McpServerTool, Description("Promaker 모델에 새 DsSystem 을 추가합니다 (현재 단순화: 첫 번째 프로젝트에 자동 부착). 반환: 새 system Id (full GUID).")]
    public static Task<string> AddSystem(
        LlmTurnContextProvider turnProvider,
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
        LlmTurnContextProvider turnProvider,
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
        LlmTurnContextProvider turnProvider,
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
        LlmTurnContextProvider turnProvider,
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
        LlmTurnContextProvider turnProvider,
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
        LlmTurnContextProvider turnProvider,
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

    [McpServerTool, Description("현재 Promaker 모델의 모든 DsSystem 목록을 반환합니다 (모든 프로젝트의 active + passive). full GUID 로 표기. 자식 트리는 미포함 — 자식까지 보려면 describe_system 또는 describe_subtree 호출.")]
    public static Task<string> ListSystems(
        LlmTurnContextProvider turnProvider)
    {
        return RunRead(turnProvider, "list_systems", ctx =>
        {
            var rows = ToolOperations.listSystems(ctx.Store);
            if (rows.Length == 0) return "(no systems)";
            var sb = new StringBuilder();
            foreach (var (id, name, isActive) in rows)
                sb.AppendLine($"- {name} (id={id:D}, {(isActive ? "active" : "passive")})");
            return sb.ToString().TrimEnd();
        });
    }

    [McpServerTool, Description("특정 DsSystem 의 직계 자식 (Flow / ApiDef) 또는 깊은 트리 (Flow → Work → Call + Arrows) 를 반환합니다. deep=false (기본) 가 token 절약. 여러 system 을 한 번에 보려면 describe_subtree 사용.")]
    public static Task<string> DescribeSystem(
        LlmTurnContextProvider turnProvider,
        [Description("DsSystem 의 GUID.")] string systemId,
        [Description("true 면 Work / Call / Arrows 까지 깊게. 기본 false (Flow / ApiDef 이름만).")] bool deep = false)
    {
        var (sysGuid, gerr) = ParseGuid(systemId, "systemId");
        if (gerr != null) return Task.FromResult(gerr);
        return RunRead(turnProvider, "describe_system", ctx =>
            ToolOperations.describeSystem(ctx.Store, sysGuid!.Value, deep));
    }

    [McpServerTool, Description("rootId (Project / System / Flow / Work GUID) 의 부분 트리를 indented text 로 반환합니다. depth = 추가 깊이 (0~5). 50 entity 초과 시 truncated 표기. 여러 system 을 한 번에 batch 조회할 때 사용 — 단일 describe_system 의 N+1 호출보다 token 효율 ↑.")]
    public static Task<string> DescribeSubtree(
        LlmTurnContextProvider turnProvider,
        [Description("Root entity 의 GUID. EntityKind 는 자동 판별 (Project/System/Flow/Work).")] string rootId,
        [Description("Root 기준 추가 깊이 (0=root만, 1=직접 자식, ..., 최대 5).")] int depth = 2)
    {
        var (rootGuid, gerr) = ParseGuid(rootId, "rootId");
        if (gerr != null) return Task.FromResult(gerr);
        return RunRead(turnProvider, "describe_subtree", ctx =>
            ToolOperations.describeSubtree(ctx.Store, rootGuid!.Value, depth));
    }

    [McpServerTool, Description("이름으로 entity 검색 (대소문자 무관 substring). kind 미지정 시 모든 종류. 결과 50개 제한.")]
    public static Task<string> FindByName(
        LlmTurnContextProvider turnProvider,
        [Description("검색어 (대소문자 무관 substring).")] string name,
        [Description("필터 종류. 허용: Project|System|Flow|Work|Call|ApiDef. 미지정 시 모두.")] string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult("VALIDATION_ERROR: name 이 비어있습니다.");

        Ds2.Core.Store.EntityKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<Ds2.Core.Store.EntityKind>(kind.Trim(), ignoreCase: true, out var k))
                return Task.FromResult($"VALIDATION_ERROR: kind '{kind}' 가 유효하지 않습니다. 허용: Project|System|Flow|Work|Call|ApiDef.");
            kindFilter = k;
        }

        return RunRead(turnProvider, "find_by_name", ctx =>
        {
            var fsKind = kindFilter.HasValue
                ? Microsoft.FSharp.Core.FSharpOption<Ds2.Core.Store.EntityKind>.Some(kindFilter.Value)
                : Microsoft.FSharp.Core.FSharpOption<Ds2.Core.Store.EntityKind>.None;
            var rows = ToolOperations.findByName(ctx.Store, name.Trim(), fsKind);
            if (rows.Length == 0) return "(no matches)";
            var truncated = rows.Length > 50;
            var sb = new StringBuilder();
            foreach (var (k, id, n) in truncated ? rows.Take(50) : rows)
                sb.AppendLine($"- {k} \"{n}\" (id={id:D})");
            if (truncated) sb.AppendLine("... (truncated at 50; refine the name)");
            return sb.ToString().TrimEnd();
        });
    }

    [McpServerTool, Description("현재 모델의 일관성을 검사합니다. 카테고리: Orphan / DanglingArrow / EmptyFlow / EmptyWork / DuplicateName / TodoPlaceholder. 위반 없으면 (no issues; scope=...). turn 종료 직전 1회 호출 권장 — 같은 scope 로 0.5초 안 재호출 시 캐시 결과 반환.")]
    public static Task<string> ValidateModel(
        LlmTurnContextProvider turnProvider,
        [Description("검사 범위. 'global' (또는 미지정) = 모든 System. GUID 면 Project/System/Flow 중 자동 판별.")] string? scope = null)
    {
        var trimmed = scope?.Trim();
        Guid? rootGuid = null;
        string scopeKey;
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "global", StringComparison.OrdinalIgnoreCase))
        {
            scopeKey = "global";
        }
        else
        {
            if (!Guid.TryParse(trimmed, out var g))
                return Task.FromResult($"VALIDATION_ERROR: scope '{scope}' 가 'global' 또는 GUID 형식이 아닙니다.");
            rootGuid = g;
            scopeKey = g.ToString("D");
        }

        return RunRead(turnProvider, "validate_model", ctx =>
        {
            var cached = ctx.TryGetValidateCache(scopeKey);
            if (cached is not null) return cached + $"\n(cached, <{LlmTurnContext.ValidateCacheTtlMs}ms)";

            var fsRoot = rootGuid.HasValue
                ? Microsoft.FSharp.Core.FSharpOption<Guid>.Some(rootGuid.Value)
                : Microsoft.FSharp.Core.FSharpOption<Guid>.None;
            var result = ToolOperations.validateModelByGuid(ctx.Store, fsRoot);
            ctx.SetValidateCache(scopeKey, result);
            return result;
        });
    }
}
