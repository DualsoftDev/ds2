using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ds2.Core;
using Ds2.LlmAgent;
using log4net;
using ModelContextProtocol.Server;

namespace Promaker.LlmAgent.Tools;

/// <summary>
/// Promaker MCP server 의 tool 핸들러.
///
/// Phase 1c — add_system + list_systems.
/// Phase 1d-1 — add_flow / add_work / add_call / add_arrow / add_api_def 풀세트.
/// Pass 5 — add_project 추가로 Phase 1 의 'GUI 의존' 한계 제거.
/// **Pass 6 — (b) batch tool (`apply_operations`) 채택. (c) variable binding 폐기.**
///   - chain pattern 의 numTurns 부풀림 (multi tool_use → multi internal turn) 해소
///   - 1 LLM message = 1 tool_use (apply_operations) = 1 internal turn = 진짜 round-trip 압축
///   - turn-scoped state (VarCache, cascade flag) 제거 — batch self-contained 처리
///
/// 모든 mutation tool 은 ImportPlanBuilder 에 ImportPlanOperation 누적만. turn end 의 단일
/// ApplyImportPlan 호출이 1 undo step 생성.
/// </summary>
[McpServerToolType]
public static class ModelTools
{
    private static readonly ILog ToolCallLog = LogManager.GetLogger("Promaker.LlmAgent.ToolCall");

    // DI 인자 (e.g. LlmTurnContextProvider) 는 attribute 없이 자동 주입됨.
    // 근거: ModelContextProtocol.AspNetCore 1.2.0 의 AIFunctionMcpServerTool 이 parameter 의 type 을
    // IServiceProviderIsService.IsService(type) 로 검사 → DI 등록된 type 이면 schema 에서 자동 제외 +
    // service provider 에서 binding. McpHostService 가 LlmTurnContextProvider 를 AddSingleton 등록하므로
    // 자동 검출 path 가 동작.

    // ─── 공통 헬퍼 ────────────────────────────────────────────────────────────

    /// <summary>F# sanitizeName 의 메시지를 InvalidOperationException 으로 변환. dispatcher work 안에서 사용.</summary>
    private static void SanitizeOrThrow(string? value, string field, int maxLength = ToolOperations.NameMaxLength)
    {
        var msg = ToolOperations.sanitizeName(value ?? string.Empty, field, maxLength);
        if (!string.IsNullOrEmpty(msg)) throw new InvalidOperationException(msg);
    }

    /// <summary>GUID 문자열 → Guid. parse 실패 시 throw.</summary>
    private static Guid ParseGuidOrThrow(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"VALIDATION_ERROR: {field} 이(가) 비어있습니다.");
        if (!Guid.TryParse(value.Trim(), out var g))
            throw new InvalidOperationException($"VALIDATION_ERROR: {field} 가 유효한 GUID 형식이 아닙니다.");
        return g;
    }

    /// <summary>F# invalidOp 메시지가 이미 카테고리 prefix 포함이면 그대로, 그 외 VALIDATION_ERROR prefix.</summary>
    private static string EnsureErrorPrefix(string message)
    {
        if (message.StartsWith("VALIDATION_ERROR:") || message.StartsWith("BATCH_ERROR:")
            || message.StartsWith("QUOTA_EXCEEDED:") || message.StartsWith("INTERNAL_ERROR:")
            || message.StartsWith("NOT_FOUND:"))
            return message;
        return "VALIDATION_ERROR: " + message;
    }

    /// <summary>
    /// LLM tool 호출 시 발생하는 예외 중, **사용자/모델 입력 오류** 인 것만 catch 하여 LLM 에 회복 단서로 노출.
    /// OOM / StackOverflow / ThreadAbort 등 fatal 은 catch 하지 않고 그대로 전파 (CLAUDE.md fail-fast 정책).
    /// QuotaExceededException 은 InvalidOperationException 의 하위라 본 필터에 자연 포함 — 별도 catch 가 elapsed log 만 분리.
    /// </summary>
    private static bool IsRecoverableToolException(Exception ex) =>
        ex is InvalidOperationException
        || ex is ArgumentException
        || ex is OperationCanceledException
        || ex is FormatException;

    private static async Task<string> RunMutation(
        LlmTurnContextProvider turnProvider, string toolName,
        Func<LlmTurnContext, string> work)
    {
        var ctx = turnProvider.Current ?? throw new InvalidOperationException("활성 turn 이 없습니다.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ctx.IncrementMutationCount();
            var msg = await ctx.Dispatcher.InvokeAsync(() => work(ctx));
            ToolCallLog.Info($"{toolName} ok elapsedMs={sw.ElapsedMilliseconds} planSize={ctx.Plan.Count}");
            return msg;
        }
        catch (QuotaExceededException qex)
        {
            // QUOTA_EXCEEDED 는 system policy — VALIDATION_ERROR 와 prefix 분리해 LLM retry 폭주 방지.
            // ctx.IsQuotaExceeded 가 set 된 후라 동일 turn 의 후속 mutation 호출은 즉시 fast-fail.
            ToolCallLog.Warn($"{toolName} quota exceeded elapsedMs={sw.ElapsedMilliseconds}: {qex.Message}");
            return EnsureErrorPrefix(qex.Message);
        }
        catch (Exception ex) when (IsRecoverableToolException(ex))
        {
            ToolCallLog.Warn($"{toolName} 실패 elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            return EnsureErrorPrefix(ex.Message);
        }
        // OOM / StackOverflow / ThreadAbort 등 fatal 은 catch 안 함 — fail-fast 로 process 종료 (디버깅 용이성).
    }

    /// <summary>
    /// Read tool 진입점. **결정 8 정합성**: read tool 도 dispatcher.InvokeAsync (Background) 경유 —
    /// store dict 가 lock-free 라도 동시 mutation 중 inconsistent snapshot 회피 + write 와 정책 일관.
    /// 누적 latency 가 부담되면 결정 8 자체를 변경한 뒤 본 함수의 dispatcher hop 우회를 별도 PR 로 분리.
    /// </summary>
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
        catch (Exception ex) when (IsRecoverableToolException(ex))
        {
            ToolCallLog.Warn($"{toolName} 실패 elapsedMs={sw.ElapsedMilliseconds}: {ex.Message}");
            var msg = ex.Message;
            return msg.StartsWith("VALIDATION_ERROR:") || msg.StartsWith("NOT_FOUND:")
                ? msg
                : $"INTERNAL_ERROR: {msg}";
        }
        // OOM / StackOverflow / ThreadAbort 등 fatal 은 catch 안 함.
    }

    // ─── Pass 6: Batch tool ──────────────────────────────────────────────────

    [McpServerTool, Description(@"여러 mutation 을 1 round-trip 으로 누적 적용합니다 (권장 — 같은 turn 의 N 개 mutation 은 본 도구 1번 호출로). 같은 batch 안 후속 op 가 직전 op 결과 Guid 를 참조하려면 'ref' 를 부여하고 args 의 Guid 자리에 '@<ref>' 사용. fail-fast — 첫 실패 시 batch 전체 rollback (1 undo step 의미 보장). read tool (list_*, describe_*, validate_model) 은 array 에 포함 불가. 입력 schema = JSON array of {op, ref?, args}, op ∈ {add_project, add_system, add_flow, add_work, add_call, add_api_def, add_arrow, remove_entity, rename_entity}. ref = 같은 batch 안 unique 한 1-32자 식별자. 예: [{""op"":""add_system"", ""ref"":""cyl"", ""args"":{""name"":""Cyl""}}, {""op"":""add_api_def"", ""args"":{""name"":""ADV"", ""systemId"":""@cyl""}}].")]
    public static Task<string> ApplyOperations(
        LlmTurnContextProvider turnProvider,
        [Description("Op 객체 JSON array 의 string 표현. 각 객체: { op: \"add_xxx|remove_entity|rename_entity\", ref?: \"<localName>\", args: {...} }.")] string operations)
    {
        return RunMutation(turnProvider, "apply_operations", ctx =>
        {
            // (review M2) using 으로 ArrayPool 즉시 반환 — JsonElement 의 lifetime 은 queueBatch 동기 완료까지만
            // 필요하므로 work delegate scope 안에서 안전하게 dispose.
            if (string.IsNullOrWhiteSpace(operations))
                throw new InvalidOperationException("VALIDATION_ERROR: operations 이 비어있습니다.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(operations); }
            catch (JsonException jex)
            {
                throw new InvalidOperationException($"VALIDATION_ERROR: operations JSON parse 실패 — {jex.Message}");
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"VALIDATION_ERROR: operations 의 root 가 array 가 아닙니다 (ValueKind={doc.RootElement.ValueKind}).");
                var inputs = BuildBatchOpInputs(doc.RootElement);
                // (review C1) batch 안 op 수만큼 quota 추가 charge — RunMutation 진입에서 +1 했으므로 (length-1) 만 추가.
                // batch 1회 = quota 1 로 두면 100 op 단발 호출이 quota cap 을 우회 → DoS 표면.
                if (inputs.Length > 1)
                    ctx.IncrementMutationCount(inputs.Length - 1);
                var result = ToolOperations.queueBatch(ctx.Plan, ctx.Store, inputs);
                return FormatBatchResult(result, ctx.Plan.Count);
            }
        });
    }

    /// <summary>
    /// JSON array root → BatchOpInput[]. caller (ApplyOperations) 가 JsonDocument 의 using 으로 lifetime 관리.
    /// </summary>
    private static BatchOpInput[] BuildBatchOpInputs(JsonElement root)
    {
        var result = new List<BatchOpInput>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"VALIDATION_ERROR: operations[{result.Count}] 가 object 가 아닙니다.");
            if (!item.TryGetProperty("op", out var opProp) || opProp.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"VALIDATION_ERROR: operations[{result.Count}].op 가 string 이 아닙니다.");
            var op = opProp.GetString() ?? "";
            var refOpt = item.TryGetProperty("ref", out var refProp) && refProp.ValueKind == JsonValueKind.String
                ? Microsoft.FSharp.Core.FSharpOption<string>.Some(refProp.GetString() ?? "")
                : Microsoft.FSharp.Core.FSharpOption<string>.None;
            var args = item.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object
                ? argsProp
                : default;
            result.Add(new BatchOpInput(op, refOpt, args));
        }
        return result.ToArray();
    }

    private static string FormatBatchResult(
        Microsoft.FSharp.Core.FSharpResult<BatchOpResult[], Tuple<int, string, string>> result,
        int planSize)
    {
        if (result.IsOk)
        {
            var ops = result.ResultValue;
            var sb = new StringBuilder();
            sb.Append($"[batch] {ops.Length} op(s) queued (planSize={planSize}):\n");
            foreach (var r in ops)
            {
                var refSuffix = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(r.Ref)
                    ? $" (ref=@{r.Ref.Value})" : "";
                sb.Append($"  [{r.Index}] {r.Display}{refSuffix}\n");
            }
            return sb.ToString().TrimEnd();
        }
        else
        {
            var (idx, opName, msg) = (result.ErrorValue.Item1, result.ErrorValue.Item2, result.ErrorValue.Item3);
            // F# invalidOp 메시지가 이미 VALIDATION_ERROR: 면 그대로, 아니면 prefix 추가
            var detail = EnsureErrorPrefix(msg);
            return $"BATCH_ERROR: op[{idx}] '{opName}' 실패 — {detail} (rollback applied, 0 ops queued in this call)";
        }
    }

    // ─── Single mutation tools (legacy fallback — apply_operations 권장) ────
    //
    // Pass 6 변경: assignVar 인자 + Guid 인자의 '$<varname>' 참조 모두 제거. 다중 op chain 은 apply_operations 사용.
    // 본 도구들은 단일 mutation 이 필요한 경우 (예: 사용자가 1 op 만 지시) 의 편의용으로 유지.

    [McpServerTool, Description("Promaker 에 새 Project 를 추가합니다 (workspace 단위). 빈 store 에서 LLM 이 자율적으로 모델을 시작할 때 사용. 같은 turn 의 후속 add_system 은 첫 project 에 자동 부착됨. 반환: 새 project Id (full GUID). **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddProject(
        LlmTurnContextProvider turnProvider,
        [Description("Project 이름 (1-128자, 다른 project 와 unique). '@' 또는 '$' 시작 금지.")] string name)
    {
        return RunMutation(turnProvider, "add_project", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var projId = ToolOperations.queueAddProject(ctx.Plan, ctx.Store, trimmed);
            return $"[plan] add_project queued: name=\"{trimmed}\", id={projId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker 모델에 새 DsSystem 을 추가합니다 (현재 단순화: 첫 번째 프로젝트에 자동 부착). 반환: 새 system Id (full GUID). **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddSystem(
        LlmTurnContextProvider turnProvider,
        [Description("System 이름 (1-128자, 한 프로젝트 내 unique). '@' 또는 '$' 시작 금지.")] string name,
        [Description("Active 여부. 기본 true.")] bool isActive = true)
    {
        return RunMutation(turnProvider, "add_system", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var sysId = ToolOperations.queueAddSystem(ctx.Plan, ctx.Store, trimmed, isActive);
            return $"[plan] add_system queued: name=\"{trimmed}\", isActive={isActive}, id={sysId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker System 아래에 새 Flow 를 추가합니다. 반환: 새 Flow Id. **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddFlow(
        LlmTurnContextProvider turnProvider,
        [Description("Flow 이름 (1-128자, System 내 unique). '@' 또는 '$' 시작 금지.")] string name,
        [Description("Parent System 의 GUID.")] string systemId)
    {
        return RunMutation(turnProvider, "add_flow", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var sysGuid = ParseGuidOrThrow(systemId, "systemId");
            var flowId = ToolOperations.queueAddFlow(ctx.Plan, ctx.Store, trimmed, sysGuid);
            return $"[plan] add_flow queued: name=\"{trimmed}\", systemId={sysGuid:D}, id={flowId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker Flow 아래에 새 Work 를 추가합니다. Work 표시명 = \"{flow.Name}.{localName}\". 반환: 새 Work Id. **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddWork(
        LlmTurnContextProvider turnProvider,
        [Description("Work LocalName (1-128자, Flow 내 unique). '@' 또는 '$' 시작 금지.")] string localName,
        [Description("Parent Flow 의 GUID.")] string flowId)
    {
        return RunMutation(turnProvider, "add_work", ctx =>
        {
            SanitizeOrThrow(localName, "localName");
            var trimmed = localName.Trim();
            var flowGuid = ParseGuidOrThrow(flowId, "flowId");
            var workId = ToolOperations.queueAddWork(ctx.Plan, ctx.Store, trimmed, flowGuid);
            return $"[plan] add_work queued: localName=\"{trimmed}\", flowId={flowGuid:D}, id={workId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker Work 아래에 새 Call 을 추가합니다. Call 표시명 = \"{devicesAlias}.{apiName}\". 반환: 새 Call Id. **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddCall(
        LlmTurnContextProvider turnProvider,
        [Description("Devices alias (Call 표시명의 앞부분). '@' 또는 '$' 시작 금지.")] string devicesAlias,
        [Description("API 이름 (Call 표시명의 뒷부분). '@' 또는 '$' 시작 금지.")] string apiName,
        [Description("Parent Work 의 GUID.")] string workId)
    {
        return RunMutation(turnProvider, "add_call", ctx =>
        {
            SanitizeOrThrow(devicesAlias, "devicesAlias");
            SanitizeOrThrow(apiName, "apiName");
            var alias = devicesAlias.Trim();
            var api = apiName.Trim();
            var workGuid = ParseGuidOrThrow(workId, "workId");
            var callId = ToolOperations.queueAddCall(ctx.Plan, ctx.Store, alias, api, workGuid);
            return $"[plan] add_call queued: name=\"{alias}.{api}\", workId={workGuid:D}, id={callId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("Promaker System 아래에 새 ApiDef 를 추가합니다. 반환: 새 ApiDef Id. **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddApiDef(
        LlmTurnContextProvider turnProvider,
        [Description("ApiDef 이름 (1-128자, System 내 unique). '@' 또는 '$' 시작 금지.")] string name,
        [Description("Parent System 의 GUID.")] string systemId)
    {
        return RunMutation(turnProvider, "add_api_def", ctx =>
        {
            SanitizeOrThrow(name, "name");
            var trimmed = name.Trim();
            var sysGuid = ParseGuidOrThrow(systemId, "systemId");
            var defId = ToolOperations.queueAddApiDef(ctx.Plan, ctx.Store, trimmed, sysGuid);
            return $"[plan] add_api_def queued: name=\"{trimmed}\", systemId={sysGuid:D}, id={defId:D}, planSize={ctx.Plan.Count}";
        });
    }

    [McpServerTool, Description("두 Work 사이 (같은 System) 또는 두 Call 사이 (같은 Work) 에 Arrow 를 추가합니다. 종류는 자동 판별. 반환: 새 Arrow Id + kind. **N 개 mutation 묶음 시 apply_operations 권장**.")]
    public static Task<string> AddArrow(
        LlmTurnContextProvider turnProvider,
        [Description("Source 의 GUID (Work 또는 Call).")] string sourceId,
        [Description("Target 의 GUID (Source 와 같은 종류).")] string targetId,
        [Description("Arrow type. 허용 값: Unspecified|Start|Reset|StartReset|ResetReset|Group. 기본 Start.")] string arrowType = "Start")
    {
        return RunMutation(turnProvider, "add_arrow", ctx =>
        {
            if (!Enum.TryParse<ArrowType>(arrowType?.Trim(), ignoreCase: true, out var atype))
                throw new InvalidOperationException(
                    $"VALIDATION_ERROR: arrowType 값 '{arrowType}' 이 유효하지 않습니다. 허용: Unspecified|Start|Reset|StartReset|ResetReset|Group.");
            var srcGuid = ParseGuidOrThrow(sourceId, "sourceId");
            var tgtGuid = ParseGuidOrThrow(targetId, "targetId");
            var (arrowId, kind) = ToolOperations.queueAddArrow(ctx.Plan, ctx.Store, srcGuid, tgtGuid, atype);
            return $"[plan] add_arrow queued: kind={kind}, type={atype}, source={srcGuid:D}, target={tgtGuid:D}, id={arrowId:D}, planSize={ctx.Plan.Count}";
        });
    }

    // ─── Remove / Rename ────────────────────────────────────────────────────

    [McpServerTool, Description("entity (Project / System / Flow / Work / Call / ApiDef) 와 모든 자식 + 관련 Arrow 를 cascade 로 제거합니다. EntityKind 는 GUID 로 자동 판별. Arrow 단독 제거는 미지원 (source/target Work/Call 제거 시 자동 cascade). 반환: 판별된 kind + 제거 plan 누적 메시지. **N 개 mutation 묶음 시 apply_operations 권장**.")]
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

    [McpServerTool, Description("System 또는 ApiDef 의 이름을 변경합니다 (Phase 2 는 System/ApiDef 만 지원 — Flow/Work/Call 은 자식 cascade 복잡도로 후속). EntityKind 는 GUID 로 자동 판별. 반환: 판별된 kind + new name. **N 개 mutation 묶음 시 apply_operations 권장**.")]
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
            ToolOperations.formatProjectList(ToolOperations.listProjects(ctx.Store)));
    }

    [McpServerTool, Description("현재 Promaker 모델의 모든 DsSystem 목록을 반환합니다 (모든 프로젝트의 active + passive). full GUID 로 표기. 자식 트리는 미포함 — 자식까지 보려면 describe_system 또는 describe_subtree 호출.")]
    public static Task<string> ListSystems(
        LlmTurnContextProvider turnProvider)
    {
        return RunRead(turnProvider, "list_systems", ctx =>
            ToolOperations.formatSystemList(ToolOperations.listSystems(ctx.Store)));
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
            return ToolOperations.formatFindResults(ToolOperations.findByName(ctx.Store, name.Trim(), fsKind));
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
