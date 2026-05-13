using System;
using System.Collections.Generic;
using Ds2.Core.Store;
using Ds2.LlmAgent;

namespace Promaker.LlmAgent;

/// <summary>
/// LLM 1 turn 동안의 context. SendAsync 시작 시 BeginTurn → tool handler 들이 본 인스턴스를 통해
/// store / dispatcher / plan 접근 → SendAsync 종료 시 EndTurn → ApplyImportPlan(plan).
///
/// 결정 7 (d): mutation tool 은 Plan 누적만, turn end 1회 ApplyImportPlan 호출.
/// 결정 8: store/plan 접근은 모두 dispatcher.InvokeAsync(Background) 안에서.
/// </summary>
public sealed class LlmTurnContext
{
    public DsStore Store { get; }
    public IUiDispatcher Dispatcher { get; }
    public ImportPlanBuilder Plan { get; } = new();

    /// <summary>turn 당 mutation tool 호출 횟수 (runaway / injection 방어).</summary>
    public int MutationCallCount { get; private set; }

    /// <summary>turn 당 mutation tool quota. 초과 시 invoker 가 거부.
    /// **SSOT** — 본 default 값 변경 시 다음을 동시 갱신:
    /// `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs:57` `MutationQuotaSync` (helper 사전 reject 산식 기준),
    /// `Solutions/Tests/Ds2.LlmAgent.Tests/LlmTurnContextQuotaTests.fs` (sync literal + 임계 케이스),
    /// `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` (LLM 안내 + safety margin 20%),
    /// `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` (한 줄 sync 주석).</summary>
    public int MutationQuota { get; init; } = 2000;

    /// <summary>quota 한 번 초과되면 같은 turn 의 후속 mutation 호출을 fast-fail. retry 폭주 방어.
    /// 단 <see cref="DecrementMutationCount"/> 에 의한 사전 charge revert 후 counter 가 한도 이하로
    /// 떨어지면 자동 해제 (1차 validation 실패 후 정상 재시도 정당성).</summary>
    public bool IsQuotaExceeded { get; private set; }

    /// <summary>validate_model 결과 short-lived cache TTL (ms). spam 방어용 — 너무 길면 store 변경 후
    /// stale 결과 노출 위험, 너무 짧으면 의미 없음. SystemPromptText / ValidateModel description 의
    /// 문구 ("500ms" / "0.5초") 와 동기화 필요.</summary>
    public const int ValidateCacheTtlMs = 500;

    /// <summary>validate_model 결과 short-lived cache. 같은 scope 가 ValidateCacheTtlMs 안 재호출 시 재사용.
    /// dispatcher.InvokeAsync 안에서만 read/write 되므로 별도 lock 불필요 (RunRead 가 work delegate 를
    /// dispatcher 위에서 실행함을 가정 — ModelTools.RunRead 참조).
    /// **scopeKey sentinel (Phase 6)**: empty string "" = global (전체 scope). 그 외 = dotted-path 문자열
    /// (예: ".Proj1.SysA"). 'global' literal / GUID 형식은 Phase 6 에서 폐기 (ModelTools.ValidateModel
    /// 참조). null/sentinel literal collision 회피 — `string.IsNullOrEmpty` 한 줄로 분기.</summary>
    private (string scopeKey, long tickMs, string result)? _validateCache;

    /// <summary>
    /// 본 turn 안 `apply_model_doc` 호출들이 받은 model 의 YAML view (발행 doc display 용).
    /// turn end 시 ViewModel 이 본 list 의 각 entry 를 chat bubble (or 큰 모델이면 button → dialog) 로 노출.
    /// LLM output/input tokens 변화 0 — server↔client display channel.
    /// </summary>
    private readonly List<string> _modelDocsYaml = new();

    public LlmTurnContext(DsStore store, IUiDispatcher dispatcher)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// mutation tool handler 가 호출 직전 quota 체크. 초과 시 <see cref="QuotaExceededException"/>.
    /// <paramref name="delta"/> = 이번 호출이 소비할 mutation op 갯수. 단일 tool = 1, batch tool = batch 안 op 수.
    /// (review C1) batch 1회 = quota 1 로 간주하면 대량 op 단발 호출이 quota cap 을 우회 → DoS 표면.
    /// </summary>
    internal void IncrementMutationCount(int delta = 1)
    {
        if (delta < 1) throw new ArgumentOutOfRangeException(nameof(delta), "delta 는 1 이상이어야 합니다.");
        if (IsQuotaExceeded)
            throw new QuotaExceededException(
                $"QUOTA_EXCEEDED: 이 turn 은 이미 mutation tool 호출 quota ({MutationQuota}) 를 초과했습니다. 추가 mutation 시도는 거부됩니다 — 응답을 마치고 다음 turn 에서 작업을 분할하세요.");
        MutationCallCount += delta;
        if (MutationCallCount > MutationQuota)
        {
            IsQuotaExceeded = true;
            throw new QuotaExceededException(
                $"QUOTA_EXCEEDED: turn 당 mutation op 누적이 quota ({MutationQuota}) 를 초과했습니다 (현재 {MutationCallCount}). 이 turn 의 후속 mutation 은 거부됩니다 — 응답을 마치고 다음 turn 에서 작업을 분할하세요.");
        }
    }

    /// <summary>
    /// 사전 charge 후 후속 작업이 throw (validation / sanitize / dispatcher 실패) 시 quota counter 를 revert.
    /// 1차 시도 실패가 quota 를 먹어버려 정상 재시도가 차단되는 회귀를 방어.
    /// `ModelTools.RunMutation` 진입 +1 의 catch path 에서 호출 (path-symmetric).
    /// <paramref name="delta"/> &lt; 1 은 silent no-op (호출자 단순화 — <see cref="IncrementMutationCount"/>
    /// 와 짝이 되는 invariant 가 아닌 *revert* 의미라 음수/0 은 의도된 부작용 없음).
    /// quota 초과 flag 도 revert 후 한도 이하이면 해제 (재시도 정당).
    /// </summary>
    internal void DecrementMutationCount(int delta)
    {
        if (delta < 1) return;
        MutationCallCount = Math.Max(0, MutationCallCount - delta);
        if (IsQuotaExceeded && MutationCallCount <= MutationQuota)
            IsQuotaExceeded = false;
    }

    /// <summary>validate_model cache lookup. 만료 또는 다른 scope 면 null.</summary>
    internal string? TryGetValidateCache(string scopeKey)
    {
        if (_validateCache is null) return null;
        var entry = _validateCache.Value;
        if (entry.scopeKey != scopeKey) return null;
        if (Environment.TickCount64 - entry.tickMs > ValidateCacheTtlMs) return null;
        return entry.result;
    }

    internal void SetValidateCache(string scopeKey, string result)
        => _validateCache = (scopeKey, Environment.TickCount64, result);

    /// <summary>
    /// `apply_model_doc` 발행 doc 의 YAML view 1건을 turn 의 list 에 append. 호출 순서 보존.
    /// 호출자 = `ModelTools.ApplyModelDoc` (성공 / 부분 성공 무관 받은 model 의 yaml 1건). HasErrors 로
    /// rollback 된 경우 본 entry 의 표시 여부는 ViewModel 책임 (현 정책: turn end 시 모두 표시).
    /// </summary>
    public void AppendModelDocYaml(string yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return;
        _modelDocsYaml.Add(yaml);
    }

    /// <summary>본 turn 안 누적된 발행 doc yaml view 들. turn end 시 ViewModel 이 1회 조회.</summary>
    public IReadOnlyList<string> ModelDocsYaml => _modelDocsYaml;
}

/// <summary>
/// turn 당 mutation tool quota 초과. <see cref="InvalidOperationException"/> 을 상속해 기존 catch path 와
/// 호환하되, ModelTools.RunMutation 가 본 type 을 별도 catch 하여 VALIDATION_ERROR 가 아닌
/// QUOTA_EXCEEDED prefix 로 LLM 에 노출 (system policy / validation error 분리).
/// </summary>
public sealed class QuotaExceededException : InvalidOperationException
{
    public QuotaExceededException(string message) : base(message) { }
}

/// <summary>
/// MCP host DI singleton. ChatViewModel 가 SendAsync 시점에 BeginTurn → EndTurn.
/// Tool method 가 [FromServices] LlmTurnContextProvider 로 인스턴스 주입받아 .Current 액세스.
///
/// Phase 1 가정: 동시 turn 1개. SendAsync 가 in-flight 동안 다음 Send 는 ViewModel 측 IsSending flag 로 차단.
/// </summary>
public sealed class LlmTurnContextProvider
{
    private LlmTurnContext? _current;

    /// <summary>현재 활성 turn. 없으면 null.</summary>
    public LlmTurnContext? Current => _current;

    public LlmTurnContext BeginTurn(LlmTurnContext ctx)
    {
        if (_current != null)
            throw new InvalidOperationException("이전 turn 이 종료되지 않았습니다 (EndTurn 누락).");
        _current = ctx;
        return ctx;
    }

    /// <summary>Turn 종료. plan 을 반환 (호출자가 ApplyImportPlan 결정).</summary>
    public LlmTurnContext? EndTurn()
    {
        var ctx = _current;
        _current = null;
        return ctx;
    }
}
