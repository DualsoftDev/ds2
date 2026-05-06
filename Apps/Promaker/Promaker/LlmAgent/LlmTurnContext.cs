using System;
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

    /// <summary>turn 당 mutation tool quota. 초과 시 invoker 가 거부.</summary>
    public int MutationQuota { get; init; } = 50;

    /// <summary>validate_model 결과 short-lived cache TTL (ms). spam 방어용 — 너무 길면 store 변경 후
    /// stale 결과 노출 위험, 너무 짧으면 의미 없음. SystemPromptText / ValidateModel description 의
    /// 문구 ("500ms" / "0.5초") 와 동기화 필요.</summary>
    public const int ValidateCacheTtlMs = 500;

    /// <summary>validate_model 결과 short-lived cache. 같은 scope 가 ValidateCacheTtlMs 안 재호출 시 재사용.
    /// dispatcher.InvokeAsync 안에서만 read/write 되므로 별도 lock 불필요 (RunRead 가 work delegate 를
    /// dispatcher 위에서 실행함을 가정 — ModelTools.RunRead 참조).</summary>
    private (string scopeKey, long tickMs, string result)? _validateCache;

    public LlmTurnContext(DsStore store, IUiDispatcher dispatcher)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>mutation tool handler 가 호출 직전 quota 체크.</summary>
    internal void IncrementMutationCount()
    {
        MutationCallCount++;
        if (MutationCallCount > MutationQuota)
            throw new InvalidOperationException($"QUOTA_EXCEEDED: turn 당 mutation tool 호출이 {MutationQuota} 회를 초과했습니다.");
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
