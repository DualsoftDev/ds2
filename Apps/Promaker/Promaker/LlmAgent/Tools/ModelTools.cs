using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Ds2.Core.Store;
using Ds2.LlmAgent;
using log4net;
using ModelContextProtocol.Server;

namespace Promaker.LlmAgent.Tools;

/// <summary>
/// Promaker MCP server 의 tool 핸들러.
///
/// **Phase 5 cleanup**: op-layer 도구 (apply_operations + add_* + remove_entity + rename_entity, 총 15종) 일소.
/// 주력 진입점 = doc-level (`apply_model_doc` / `validate_model_doc` / `export_model_doc` / `json_to_yaml`) +
/// read tools (`list_*` / `describe_*` / `find_by_name` / `validate_model`). SSOT = `yaml-protocol-v0.md` §2.
///
/// 모든 mutation tool 은 ImportPlanBuilder 에 ImportPlanOperation 누적만. turn end 의 단일
/// ApplyImportPlan 호출이 1 undo step 생성.
/// </summary>
[McpServerToolType]
public static class ModelTools
{
    private static readonly ILog ToolCallLog = LogManager.GetLogger("Promaker.LlmAgent.ToolCall");

    // mutation tool_result self-explanatory 보강 — architectural invariant (1 LLM turn = 1 undo step, mutation 은
    // turn end 에 일괄 apply) 가 작은 모델에서 잘 안 보일 때, 같은 turn 안 read 재조회가 turn-시작 snapshot 만 반환
    // → "queue 안 됨" 으로 잘못 추론 → 동일 mutation 중복 호출 회귀. 본 suffix 가 visibility invariant 를 LLM 에 직접
    // 알리는 single line. `3.tooling.md` 의 운영 규칙 wording 과 sync 유지.
    private const string PlanVisibilityHint = " (반영은 turn 종료 후 — 같은 turn 안에서 재조회 금지)";

    // DI 인자 (e.g. LlmTurnContextProvider) 는 attribute 없이 자동 주입됨.
    // 근거: ModelContextProtocol.AspNetCore 1.2.0 의 AIFunctionMcpServerTool 이 parameter 의 type 을
    // IServiceProviderIsService.IsService(type) 로 검사 → DI 등록된 type 이면 schema 에서 자동 제외 +
    // service provider 에서 binding. McpHostService 가 LlmTurnContextProvider 를 AddSingleton 등록하므로
    // 자동 검출 path 가 동작.

    // ─── 공통 헬퍼 ────────────────────────────────────────────────────────────
    //
    // 본문 entity 이름 sanitize (control char / RTL override / @·$ prefix 거부) 는 doc-level dispatcher 의
    // 책임으로 옮길 예정 — 본 파일 surface 의 sanitize 진입점 (구 SanitizeOrThrow) 은 Phase 5 cleanup 으로 제거.
    // 후속 cycle: ModelProtocol.fs 의 dispatcher 가 systems/flow/work 이름 발견 시점에 ToolOperations.sanitizeName
    // 직접 호출하여 동일 정책 보장 (현재는 미적용 — 별개 cycle 권고).

    /// <summary>F# invalidOp 메시지가 이미 카테고리 prefix 포함이면 그대로, 그 외 VALIDATION_ERROR prefix.</summary>
    private static string EnsureErrorPrefix(string message)
    {
        if (message.StartsWith("VALIDATION_ERROR:")
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
        // (A path-symmetric) 진입 +1 도 work delegate 가 throw 하면 revert — single helper / batch 의 cascade revert 와 일관.
        // IncrementMutationCount 가 quota 초과로 throw 한 경우 entryCharged=false 라 revert 0 (M-B 회귀 차단).
        bool entryCharged = false;
        try
        {
            ctx.IncrementMutationCount();
            entryCharged = true;
            var msg = await ctx.Dispatcher.InvokeAsync(() => work(ctx));
            ToolCallLog.Info($"{toolName} ok elapsedMs={sw.ElapsedMilliseconds} planSize={ctx.Plan.Count}");
            return msg;
        }
        catch (QuotaExceededException qex)
        {
            // QUOTA_EXCEEDED 는 system policy — VALIDATION_ERROR 와 prefix 분리해 LLM retry 폭주 방지.
            // ctx.IsQuotaExceeded 가 set 된 후라 동일 turn 의 후속 mutation 호출은 즉시 fast-fail.
            // revert 후 counter 가 한도 이하이면 IsQuotaExceeded 자동 해제 (재시도 정당) — DecrementMutationCount 참조.
            if (entryCharged) ctx.DecrementMutationCount(1);
            ToolCallLog.Warn($"{toolName} quota exceeded elapsedMs={sw.ElapsedMilliseconds}: {qex.Message}");
            return EnsureErrorPrefix(qex.Message);
        }
        catch (Exception ex) when (IsRecoverableToolException(ex))
        {
            if (entryCharged) ctx.DecrementMutationCount(1);
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


    // ─── doc-level YAML protocol entry ───────────────────────────────────────
    //
    // SSOT: Apps/Promaker/Docs/yaml-protocol-v0.md
    // Wire = JSON object (LLM tool_use native, escape 0). View = YAML (사용자 미리보기 / 디스크 SSOT).
    //
    // 자연어 → 선언적 모델 변환을 LLM 한 번의 tool_use 로 압축. Phase 5 cleanup 이후 op-layer 의 batch / single
    // mutation surface 는 모두 제거 — doc-level 4종 + read 6종 = 10종이 풀세트.
    //
    // 이름 정책: 기존 ValidateModel (consistency check) 과 충돌 회피로 'Doc' 접미사 (snake_case = `_doc`).

    private static string FormatDiagnostics(ModelProtocol.Diagnostics diag)
    {
        var msg = diag.Format();
        return string.IsNullOrEmpty(msg) ? "(no diagnostics)" : msg;
    }

    [McpServerTool, Description("doc-level YAML 프로토콜의 주력 진입점. JSON object 로 선언적 모델 입력 → MCP 가 entity graph 변환 + cascade + 검증 + 트랜잭션 일괄 처리. **GUID 노출 없음** — 이름 기반 dotted-path 만 사용. 입력 schema = `{protocol:'promaker/v0', project?:..., systems?:[...], patch?:{...}}`. 자세한 schema = Apps/Promaker/Docs/yaml-protocol-v0.md §2. 같은 turn 안 visibility 규칙은 op-layer 와 동일 (turn 종료 후 반영).")]
    public static Task<string> ApplyModelDoc(
        LlmTurnContextProvider turnProvider,
        [Description("schema v0 의 JSON object string. 최상단 키: protocol(MUST='promaker/v0'), project?, systems?, patch?.")] string model)
    {
        return RunMutation(turnProvider, "apply_model_doc", ctx =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("VALIDATION_ERROR: model 이 비어있습니다.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(model); }
            catch (JsonException jex)
            {
                throw new InvalidOperationException($"VALIDATION_ERROR: model JSON parse 실패: {jex.Message}");
            }
            using (doc)
            {
                var (diag, refs) = ModelProtocol.apply(ctx.Plan, ctx.Store, doc.RootElement);
                if (diag.HasErrors)
                {
                    throw new InvalidOperationException(diag.Format());
                }
                // chat-ui boost: 발행 doc 의 yaml view 를 turn context 에 append → turn end 시 ViewModel 이
                // chat bubble (≤30 라인) 또는 button-dialog (>30) 로 노출. LLM output/input token 변화 0.
                ctx.AppendModelDocYaml(ModelProtocolYaml.jsonElementToYaml(doc.RootElement));
                return $"[plan] apply_model_doc queued: refs={refs.Count}, planSize={ctx.Plan.Count}{PlanVisibilityHint}";
            }
        });
    }

    [McpServerTool, Description("apply_model_doc 의 dry-run. mutation 누적 없이 schema 검증 + 가까운 후보 제안만 반환. LLM 이 자체 검증 후 apply_model_doc 호출 권장. 기존 mcp__promaker__validate_model (consistency check) 와 별개 도구.")]
    public static Task<string> ValidateModelDoc(
        LlmTurnContextProvider turnProvider,
        [Description("schema v0 의 JSON object string. apply_model_doc 와 동일 schema.")] string model)
    {
        return RunRead(turnProvider, "validate_model_doc", ctx =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("VALIDATION_ERROR: model 이 비어있습니다.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(model); }
            catch (JsonException jex)
            {
                throw new InvalidOperationException($"VALIDATION_ERROR: model JSON parse 실패: {jex.Message}");
            }
            using (doc)
            {
                var diag = ModelProtocol.validate(ctx.Store, doc.RootElement);
                return FormatDiagnostics(diag);
            }
        });
    }

    [McpServerTool, Description("현재 store 의 entity graph 를 schema v0 의 선언적 표현으로 export. format=yaml(default, 사람 친화 view) | json(wire 와 동일). round-trip 검증의 SSOT — apply(export(model)) ≡ model.")]
    public static Task<string> ExportModelDoc(
        LlmTurnContextProvider turnProvider,
        [Description("출력 형식. 'yaml' (default) 또는 'json'.")] string format = "yaml")
    {
        return RunRead(turnProvider, "export_model_doc", ctx =>
        {
            using var jdoc = ModelProtocol.exportToJson(ctx.Store);
            var fmt = string.IsNullOrWhiteSpace(format) ? "yaml" : format.Trim().ToLowerInvariant();
            return fmt switch
            {
                "json" => jdoc.RootElement.GetRawText(),
                "yaml" => ModelProtocolYaml.jsonElementToYaml(jdoc.RootElement),
                _ => throw new InvalidOperationException($"VALIDATION_ERROR: format '{format}' 미지원. 'yaml' 또는 'json' 사용.")
            };
        });
    }

    [McpServerTool, Description("JSON object string 을 YAML 문자열로 변환 (사용자 미리보기 / 디스크 SSOT 용). schema 검증 없음 — 순수 transformer. apply_model_doc 응답을 YAML 로 보고 싶을 때 사용.")]
    public static Task<string> JsonToYaml(
        LlmTurnContextProvider turnProvider,
        [Description("YAML 으로 변환할 JSON 문자열.")] string json)
    {
        return RunRead(turnProvider, "json_to_yaml", _ =>
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("VALIDATION_ERROR: json 이 비어있습니다.");
            return ModelProtocolYaml.jsonToYaml(json);
        });
    }

    // ─── Read tools (Phase 6 후 2종 — list_*/describe_* 는 export_model_doc(path?, depth?) 으로 흡수) ──

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
            // SSOT yaml-protocol-v0.md §2.5.1 / §4.5 (Phase 6 todo-read-surface-guid-cleanup.md closure #3 v4):
            // 출력 = `[ {kind, path} ]` 목록. ModelProtocol.pathOf 가 entity → leading `.` + dot path 합성
            // (root 까지 parent chain).
            var truncated = rows.Length > 50;
            var visible = truncated ? Microsoft.FSharp.Collections.ListModule.Truncate(50, rows) : rows;
            var sb = new System.Text.StringBuilder();
            foreach (var triple in visible)
            {
                var path = ModelProtocol.pathOf(ctx.Store, triple.Item1, triple.Item2);
                sb.AppendLine($"- {triple.Item1} (path={path})");
            }
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
