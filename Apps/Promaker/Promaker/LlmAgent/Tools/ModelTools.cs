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
/// Pass 3 (c) — add_* 5종에 assignVar 인자 + Guid 인자에 '$&lt;varname&gt;' 변수 참조 허용.
///   같은 turn 안 multi tool_use 가 직전 op 의 미래 Guid 를 참조 가능 → ID chain 압축.
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

    /// <summary>
    /// dispatcher work delegate 안에서 호출. F# sanitizeName 의 메시지를 InvalidOperationException 으로
    /// 변환 → RunMutation catch 가 cascade 트리거 + 사용자 응답 메시지 통일.
    /// </summary>
    private static void SanitizeOrThrow(string? value, string field, int maxLength = ToolOperations.NameMaxLength)
    {
        var msg = ToolOperations.sanitizeName(value ?? string.Empty, field, maxLength);
        if (!string.IsNullOrEmpty(msg)) throw new InvalidOperationException(msg);
    }

    /// <summary>add_*: '$&lt;varname&gt;' 또는 GUID 문자열 → Guid. (= resolveGuidOrVar 의 throw 시그니처 wrapper)</summary>
    private static Guid ResolveGuidOrThrow(LlmTurnContext ctx, string? value, string field)
        => ToolOperations.resolveGuidOrVar(ctx.Plan, value ?? string.Empty, field);

    /// <summary>remove/rename: 기존 ParseGuid 의 throw 시그니처. assignVar/$ref 미지원 op 용.</summary>
    private static Guid ParseGuidOrThrow(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"VALIDATION_ERROR: {field} 이(가) 비어있습니다.");
        if (!Guid.TryParse(value.Trim(), out var g))
            throw new InvalidOperationException($"VALIDATION_ERROR: {field} 가 유효한 GUID 형식이 아닙니다.");
        return g;
    }

    /// <summary>"[plan] ... var=$xxx" suffix. assignVar 미사용 시 빈 문자열.</summary>
    private static string VarSuffix(string? assignVar)
        => string.IsNullOrEmpty(assignVar) ? "" : $", var=${assignVar}";

    /// <summary>
    /// Pass 2 spike — 단일 client connection 의 multi tool_use 가 concurrent 진입하는지 검증용.
    /// 각 tool 호출의 진입/종료 thread id + timestamp 로그 → 같은 message 의 4 tool 이 거의 동시에
    /// 진입하면 concurrent (race 위험), 직렬 차례 진입하면 SDK 가 message-boundary 에서 직렬화.
    /// </summary>
    private static long _toolCallSeq = 0;

    /// <summary>
    /// 카테고리 prefix 이미 있으면 그대로, 없으면 "VALIDATION_ERROR: " 추가. (F# invalidOp 메시지가
    /// 이미 prefix 포함하면 중복 회피)
    /// </summary>
    private static string EnsureErrorPrefix(string message)
    {
        if (message.StartsWith("VALIDATION_ERROR:") || message.StartsWith("BATCH_ABORTED:")
            || message.StartsWith("QUOTA_EXCEEDED:") || message.StartsWith("INTERNAL_ERROR:")
            || message.StartsWith("NOT_FOUND:"))
            return message;
        return "VALIDATION_ERROR: " + message;
    }

    private static async Task<string> RunMutation(
        LlmTurnContextProvider turnProvider, string toolName,
        Func<LlmTurnContext, string> work)
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");
        // Pass 3 (c): 같은 turn 안 직전 mutation 이 cascade 트리거 했으면 후속 호출 단락.
        if (ctx.Plan.CascadeFailureFlag)
        {
            ToolCallLog.Warn($"{toolName} BATCH_ABORTED — prior cascade flag set");
            return $"BATCH_ABORTED: 같은 turn 의 이전 mutation 이 실패하여 후속 호출이 중단되었습니다 ({toolName} skipped).";
        }
        var seq = System.Threading.Interlocked.Increment(ref _toolCallSeq);
        var entryT = Environment.CurrentManagedThreadId;
        var entryNanos = System.Diagnostics.Stopwatch.GetTimestamp();
        ToolCallLog.Debug($"{toolName} enter seq={seq} t={entryT} nanos={entryNanos}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ctx.IncrementMutationCount();
            var msg = await ctx.Dispatcher.InvokeAsync(() => work(ctx));
            var exitT = Environment.CurrentManagedThreadId;
            ToolCallLog.Info($"{toolName} ok seq={seq} entryT={entryT} exitT={exitT} elapsedMs={sw.ElapsedMilliseconds} planSize={ctx.Plan.Count}");
            return msg;
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"{toolName} 실패 seq={seq} elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            // Pass 3 (c): cascade 트리거 — flag set + Plan.Clear (turn end 의 ApplyImportPlan skip 가능).
            ctx.Plan.SignalCascadeFailure();
            return EnsureErrorPrefix(ex.Message);
        }
    }

    private static async Task<string> RunRead(
        LlmTurnContextProvider turnProvider, string toolName,
        Func<LlmTurnContext, string> work)
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");
        var seq = System.Threading.Interlocked.Increment(ref _toolCallSeq);
        var entryT = Environment.CurrentManagedThreadId;
        var entryNanos = System.Diagnostics.Stopwatch.GetTimestamp();
        ToolCallLog.Debug($"{toolName} enter seq={seq} t={entryT} nanos={entryNanos}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var msg = await ctx.Dispatcher.InvokeAsync(() => work(ctx));
            var exitT = Environment.CurrentManagedThreadId;
            ToolCallLog.Info($"{toolName} ok seq={seq} entryT={entryT} exitT={exitT} elapsedMs={sw.ElapsedMilliseconds} sizeBytes={msg?.Length ?? 0}");
            return msg ?? "";
        }
        catch (Exception ex)
        {
            ToolCallLog.Warn($"{toolName} 실패 seq={seq} elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            // ParseGuidOrThrow 등이 이미 카테고리 prefix 를 부여한 메시지면 그대로 (이중 prefix 방지),
            // 그 외 (dispatcher / store 자체 오류 = bug 후보) 만 INTERNAL_ERROR 로 분류.
            var msg = ex.Message;
            return msg.StartsWith("VALIDATION_ERROR:") || msg.StartsWith("NOT_FOUND:")
                ? msg
                : $"INTERNAL_ERROR: {msg}";
        }
    }

    // ─── Mutation tools ──────────────────────────────────────────────────────
    //
    // Pass 3 (c): add_* 5종에 `assignVar` 인자 + Guid 인자는 dispatcher work 안에서 resolveGuidOrVar
    // (= GUID 문자열 / '$<varname>' 양쪽 허용). sanitize / parse 검사 모두 work delegate 안에서
    // throw → RunMutation catch 가 cascade 트리거 + 메시지 prefix 통일.

    private const string AssignVarDescription =
        "같은 turn 의 후속 호출이 '$<varname>' 으로 이 entity 의 GUID 를 참조하려면 변수명 부여 (1-32자, "
        + "[a-zA-Z_][a-zA-Z0-9_]*). 미사용 시 null. turn 종료 시 자동 폐기.";

    [McpServerTool, Description("Promaker 모델에 새 DsSystem 을 추가합니다 (현재 단순화: 첫 번째 프로젝트에 자동 부착). 반환: 새 system Id (full GUID).")]
    public static Task<string> AddSystem(
        LlmTurnContextProvider turnProvider,
        [Description("System 이름 (1-128자, 한 프로젝트 내 unique). '@' 또는 '$' 시작 금지.")] string name,
        [Description("Active 여부. 기본 true.")] bool isActive = true,
        [Description(AssignVarDescription)] string? assignVar = null)
    {
        return RunMutation(turnProvider, "add_system", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var sysId = ToolOperations.queueAddSystem(ctx.Plan, ctx.Store, trimmed, isActive);
            ToolOperations.registerVar(ctx.Plan, assignVar ?? string.Empty, sysId);
            return $"[plan] add_system queued: name=\"{trimmed}\", isActive={isActive}, id={sysId:D}{VarSuffix(assignVar)}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker System 아래에 새 Flow 를 추가합니다. 반환: 새 Flow Id.")]
    public static Task<string> AddFlow(
        LlmTurnContextProvider turnProvider,
        [Description("Flow 이름 (1-128자, System 내 unique). '@' 또는 '$' 시작 금지.")] string name,
        [Description("Parent System 의 GUID 또는 같은 turn 의 '$<varname>' (list_systems 결과 / add_system 반환값 / assignVar 등록 변수).")] string systemId,
        [Description(AssignVarDescription)] string? assignVar = null)
    {
        return RunMutation(turnProvider, "add_flow", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var sysGuid = ResolveGuidOrThrow(ctx, systemId, "systemId");
            var flowId = ToolOperations.queueAddFlow(ctx.Plan, ctx.Store, trimmed, sysGuid);
            ToolOperations.registerVar(ctx.Plan, assignVar ?? string.Empty, flowId);
            return $"[plan] add_flow queued: name=\"{trimmed}\", systemId={sysGuid:D}, id={flowId:D}{VarSuffix(assignVar)}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker Flow 아래에 새 Work 를 추가합니다. Work 표시명 = \"{flow.Name}.{localName}\". 반환: 새 Work Id.")]
    public static Task<string> AddWork(
        LlmTurnContextProvider turnProvider,
        [Description("Work LocalName (1-128자, Flow 내 unique). '@' 또는 '$' 시작 금지.")] string localName,
        [Description("Parent Flow 의 GUID 또는 같은 turn 의 '$<varname>'.")] string flowId,
        [Description(AssignVarDescription)] string? assignVar = null)
    {
        return RunMutation(turnProvider, "add_work", ctx =>
        {
            SanitizeOrThrow(localName, "localName");
            var trimmed = localName.Trim();
            var flowGuid = ResolveGuidOrThrow(ctx, flowId, "flowId");
            var workId = ToolOperations.queueAddWork(ctx.Plan, ctx.Store, trimmed, flowGuid);
            ToolOperations.registerVar(ctx.Plan, assignVar ?? string.Empty, workId);
            return $"[plan] add_work queued: localName=\"{trimmed}\", flowId={flowGuid:D}, id={workId:D}{VarSuffix(assignVar)}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker Work 아래에 새 Call 을 추가합니다. Call 표시명 = \"{devicesAlias}.{apiName}\". 반환: 새 Call Id.")]
    public static Task<string> AddCall(
        LlmTurnContextProvider turnProvider,
        [Description("Devices alias (Call 표시명의 앞부분). '@' 또는 '$' 시작 금지.")] string devicesAlias,
        [Description("API 이름 (Call 표시명의 뒷부분). '@' 또는 '$' 시작 금지.")] string apiName,
        [Description("Parent Work 의 GUID 또는 같은 turn 의 '$<varname>'.")] string workId,
        [Description(AssignVarDescription)] string? assignVar = null)
    {
        return RunMutation(turnProvider, "add_call", ctx =>
        {
            SanitizeOrThrow(devicesAlias, "devicesAlias");
            SanitizeOrThrow(apiName, "apiName");
            var alias = devicesAlias.Trim();
            var api = apiName.Trim();
            var workGuid = ResolveGuidOrThrow(ctx, workId, "workId");
            var callId = ToolOperations.queueAddCall(ctx.Plan, ctx.Store, alias, api, workGuid);
            ToolOperations.registerVar(ctx.Plan, assignVar ?? string.Empty, callId);
            return $"[plan] add_call queued: name=\"{alias}.{api}\", workId={workGuid:D}, id={callId:D}{VarSuffix(assignVar)}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker System 아래에 새 ApiDef 를 추가합니다. 반환: 새 ApiDef Id.")]
    public static Task<string> AddApiDef(
        LlmTurnContextProvider turnProvider,
        [Description("ApiDef 이름 (1-128자, System 내 unique). '@' 또는 '$' 시작 금지.")] string name,
        [Description("Parent System 의 GUID 또는 같은 turn 의 '$<varname>'.")] string systemId,
        [Description(AssignVarDescription)] string? assignVar = null)
    {
        return RunMutation(turnProvider, "add_api_def", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var sysGuid = ResolveGuidOrThrow(ctx, systemId, "systemId");
            var defId = ToolOperations.queueAddApiDef(ctx.Plan, ctx.Store, trimmed, sysGuid);
            ToolOperations.registerVar(ctx.Plan, assignVar ?? string.Empty, defId);
            return $"[plan] add_api_def queued: name=\"{trimmed}\", systemId={sysGuid:D}, id={defId:D}{VarSuffix(assignVar)}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("두 Work 사이 (같은 System) 또는 두 Call 사이 (같은 Work) 에 Arrow 를 추가합니다. 종류는 자동 판별. 반환: 새 Arrow Id + kind.")]
    public static Task<string> AddArrow(
        LlmTurnContextProvider turnProvider,
        [Description("Source 의 GUID 또는 '$<varname>' (Work 또는 Call).")] string sourceId,
        [Description("Target 의 GUID 또는 '$<varname>' (Source 와 같은 종류).")] string targetId,
        [Description("Arrow type. 허용 값: Unspecified|Start|Reset|StartReset|ResetReset|Group. 기본 Start.")] string arrowType = "Start",
        [Description(AssignVarDescription)] string? assignVar = null)
    {
        return RunMutation(turnProvider, "add_arrow", ctx =>
        {
            if (!Enum.TryParse<ArrowType>(arrowType?.Trim(), ignoreCase: true, out var atype))
                throw new InvalidOperationException(
                    $"VALIDATION_ERROR: arrowType 값 '{arrowType}' 이 유효하지 않습니다. 허용: Unspecified|Start|Reset|StartReset|ResetReset|Group.");
            var srcGuid = ResolveGuidOrThrow(ctx, sourceId, "sourceId");
            var tgtGuid = ResolveGuidOrThrow(ctx, targetId, "targetId");
            var (arrowId, kind) = ToolOperations.queueAddArrow(ctx.Plan, ctx.Store, srcGuid, tgtGuid, atype);
            ToolOperations.registerVar(ctx.Plan, assignVar ?? string.Empty, arrowId);
            return $"[plan] add_arrow queued: kind={kind}, type={atype}, source={srcGuid:D}, target={tgtGuid:D}, id={arrowId:D}{VarSuffix(assignVar)}, planSize={ctx.Plan.Count}";
        });
    }

    // ─── Remove / Rename (Phase 2) ───────────────────────────────────────────
    //
    // assignVar / '$<var>' 미지원 — Pass 3 (c) 는 add_* 한정. 같은 turn 안 add 직후 remove 흐름은
    // queueRemoveEntity 가 store 만 검색하므로 의미 약함 (ToolOperations.fs:209-210 참조).

    [McpServerTool, Description("entity (Project / System / Flow / Work / Call / ApiDef) 와 모든 자식 + 관련 Arrow 를 cascade 로 제거합니다. EntityKind 는 GUID 로 자동 판별. Arrow 단독 제거는 미지원 (source/target Work/Call 제거 시 자동 cascade). 반환: 판별된 kind + 제거 plan 누적 메시지.")]
    public static Task<string> RemoveEntity(
        LlmTurnContextProvider turnProvider,
        [Description("제거할 entity 의 GUID. Project/System/Flow/Work/Call/ApiDef 자동 판별.")] string entityId)
    {
        return RunMutation(turnProvider, "remove_entity", ctx =>
        {
            var gid = ParseGuidOrThrow(entityId, "entityId");
            var kind = ToolOperations.queueRemoveEntity(ctx.Plan, ctx.Store, gid);
            return $"[plan] remove_entity queued: kind={kind}, id={gid:D}, planSize={ctx.Plan.Count} (cascade 는 turn end 의 ApplyImportPlan 시점에 적용)";
        });
    }

    [McpServerTool, Description("System 또는 ApiDef 의 이름을 변경합니다 (Phase 2 는 System/ApiDef 만 지원 — Flow/Work/Call 은 자식 cascade 복잡도로 후속). EntityKind 는 GUID 로 자동 판별. 반환: 판별된 kind + new name.")]
    public static Task<string> RenameEntity(
        LlmTurnContextProvider turnProvider,
        [Description("이름 변경할 entity 의 GUID. System 또는 ApiDef 만 허용.")] string entityId,
        [Description("새 이름 (1-128자, 같은 parent 안 unique). '@' 또는 '$' 시작 금지.")] string newName)
    {
        return RunMutation(turnProvider, "rename_entity", ctx =>
        {
            SanitizeOrThrow(newName, "newName");
            var trimmed = newName.Trim();
            var gid = ParseGuidOrThrow(entityId, "entityId");
            var kind = ToolOperations.queueRenameEntity(ctx.Plan, ctx.Store, gid, trimmed);
            return $"[plan] rename_entity queued: kind={kind}, id={gid:D}, newName=\"{trimmed}\", planSize={ctx.Plan.Count}";
        });
    }

    // ─── Read tools ──────────────────────────────────────────────────────────

    [McpServerTool, Description("현재 Promaker 의 모든 Project 목록 + 각 project 의 system 합계 (active + passive). 빈 결과 (no projects) 는 프로젝트 자체 부재 — list_systems 의 빈 결과 (어느 프로젝트에도 system 없음) 와 구분. add_system 의 첫 project 자동 부착이 어느 project 인지 확인 시 사용.")]
    public static Task<string> ListProjects(
        LlmTurnContextProvider turnProvider)
    {
        return RunRead(turnProvider, "list_projects", ctx =>
        {
            var rows = ToolOperations.listProjects(ctx.Store);
            if (rows.Length == 0) return "(no projects)";
            var sb = new StringBuilder();
            foreach (var (id, name, total) in rows)
                sb.AppendLine($"- {name} (id={id:D}, systems={total})");
            return sb.ToString().TrimEnd();
        });
    }

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
        return RunRead(turnProvider, "describe_system", ctx =>
        {
            var sysGuid = ParseGuidOrThrow(systemId, "systemId");
            return ToolOperations.describeSystem(ctx.Store, sysGuid, deep);
        });
    }

    [McpServerTool, Description("rootId (Project / System / Flow / Work GUID) 의 부분 트리를 indented text 로 반환합니다. depth = 추가 깊이 (0~5). 50 entity 초과 시 truncated 표기. 여러 system 을 한 번에 batch 조회할 때 사용 — 단일 describe_system 의 N+1 호출보다 token 효율 ↑.")]
    public static Task<string> DescribeSubtree(
        LlmTurnContextProvider turnProvider,
        [Description("Root entity 의 GUID. EntityKind 는 자동 판별 (Project/System/Flow/Work).")] string rootId,
        [Description("Root 기준 추가 깊이 (0=root만, 1=직접 자식, ..., 최대 5).")] int depth = 2)
    {
        return RunRead(turnProvider, "describe_subtree", ctx =>
        {
            var rootGuid = ParseGuidOrThrow(rootId, "rootId");
            return ToolOperations.describeSubtree(ctx.Store, rootGuid, depth);
        });
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
