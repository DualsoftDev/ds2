using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Api;
using Llm.Shared.Mcp;
namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — **PR-F (todo-lighthouse-index-summary.md §5.1)** KB profile fetch + SSE hook + debounce.
/// <para/>
/// 책임:
/// <list type="bullet">
///   <item>active service 별 <see cref="LightHouseClient.ListCollectionsAsync"/> 응답을 in-memory cache 박제</item>
///   <item><see cref="LightHouseClientHolder.EventReceived"/> 구독 — `collection-*` event 시 cache invalidate + debounce schedule</item>
///   <item><see cref="OnKbProfileChanged"/> = PR-G 의 system prompt swap hook (본 PR 에서는 skeleton)</item>
/// </list>
/// <para/>
/// thread-affinity: cache / acceptedIds dict 의 mutation 은 UI thread (dispatcher) 에서만. SSE callback 은
/// background thread 도착 → <see cref="_dispatcher"/> 로 marshalling 후 mutation.
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>
    /// **PR-F (§5.1)** — KB profile cache. key = ServiceId, value = ListCollectionsAsync 응답을
    /// <see cref="_acceptedCollectionIds"/> 와 교차 filter 한 collection 목록.
    /// <para/>
    /// SSE collection-* event 시 invalidate (entry 단위 또는 전체). 다음 <see cref="FetchKbProfilesAsync"/>
    /// 호출 시 HTTP 재발급.
    /// <para/>
    /// **thread-affinity (review m-4)** — 본 dict 및 <see cref="_acceptedCollectionIds"/> 는 UI thread
    /// (dispatcher) 만 mutate. SSE callback / debounce fire 모두 <see cref="_dispatcher"/> marshalling 후 진입.
    /// concurrent caller path 진입 시 ConcurrentDictionary 또는 lock 으로 승격 의무.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<CollectionInfo>> _kbProfileCache = new();

    /// <summary>
    /// **PR-F (§5.1)** — service 별 session 발급 응답의 acceptedCollectionIds 박제.
    /// <see cref="FetchKbProfilesAsync"/> 의 filter input — accepted 안 든 collection (unknown/unindexable)
    /// 은 digest 에 포함 안 함. <see cref="TryCreateLightHouseSessionsAsync"/> 가 매 session 발급 직후 박제.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _acceptedCollectionIds = new();

    /// <summary>
    /// **PR-F (§5.1)** — SSE collection-* event burst 시 polling 폭주 차단용 debounce CTS.
    /// 매 event 마다 기존 timer cancel + 새 timer schedule. <see cref="DisposeAsync"/> 시 cancel.
    /// </summary>
    private CancellationTokenSource? _kbDigestDebounceCts;

    /// <summary>
    /// **PR-F (§4 미결정 #2)** — debounce window. 잠정 default 750ms (§5.1 권장 500~1000ms 중간값).
    /// test 친화성을 위해 internal setter — test 에서 0ms 박제 가능.
    /// </summary>
    internal int KbDigestDebounceMs { get; set; } = 750;

    /// <summary>
    /// **PR-F (§5.1)** — chat panel lifetime 동안 <see cref="LightHouseClientHolder.EventReceived"/> 에
    /// handler 등록. <see cref="UnsubscribeKbProfileEvents"/> 와 매칭.
    /// </summary>
    private void SubscribeKbProfileEvents()
    {
        LightHouseClientHolder.EventReceived += OnKbSseEventReceived;
    }

    /// <summary>
    /// **PR-F (§5.1)** — handler -= + debounce CTS cancel/dispose. <see cref="DisposeAsync"/> 에서 호출.
    /// 동일 메서드 중복 호출 안전 (CTS null 박제 후 재-Cancel skip).
    /// </summary>
    private void UnsubscribeKbProfileEvents()
    {
        LightHouseClientHolder.EventReceived -= OnKbSseEventReceived;
        SwapDebounceCts(null);
    }

    /// <summary>
    /// **review M-1 fix** — debounce CTS swap helper. 옛 CTS Cancel + Dispose, 새 CTS 박제 (또는 null) 의
    /// 3줄 반복 패턴 SSOT (CLAUDE.md 정합). caller 가 lock-free — UI thread 단일 caller invariant.
    /// </summary>
    private void SwapDebounceCts(CancellationTokenSource? newCts)
    {
        var old = _kbDigestDebounceCts;
        _kbDigestDebounceCts = newCts;
        if (old is null) return;
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        try { old.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// SSE event handler — KB profile invalidate 분류 후 UI thread 로 marshalling.
    /// background thread 도착 가정 — cache mutation 은 항상 dispatcher 안에서.
    /// <para/>
    /// **자가 검열 Major-1 fix + review m-1**: holder 의 invoke 패턴 (LightHouseClientHolder.cs:283
    /// `var handler = EventReceived;`) 이 snapshot 캡처 → -= 시점 race 에서 dispose 된 dispatcher 로
    /// marshalling 시 발생 가능한 두 예외만 한정 흡수 — TaskCanceledException (dispatcher shutdown) /
    /// ObjectDisposedException (dispatcher disposed). 그 외 예외는 fail-fast (root cause 노출 정합).
    /// </summary>
    private void OnKbSseEventReceived(ServerEventDto evt)
    {
        if (!KbProfileExtractor.IsKbProfileEvent(evt)) return;
        try { _ = _dispatcher.InvokeAsync(() => InvalidateAndScheduleRefresh(evt.ServiceId)); }
        catch (TaskCanceledException ex) { Log.Debug($"OnKbSseEventReceived dispatcher shutdown skip: {ex.Message}"); }
        catch (ObjectDisposedException ex) { Log.Debug($"OnKbSseEventReceived dispatcher disposed skip: {ex.Message}"); }
    }

    /// <summary>
    /// cache 의 본 service entry 만 제거 후 debounce schedule. serviceId 빈 값 (legacy event) 시 전체 cache 제거.
    /// 본 chat panel 의 active 가 아닌 service event 는 무시 (다른 panel 의 acceptedIds 박제 분리 정합).
    /// </summary>
    private void InvalidateAndScheduleRefresh(string? serviceId)
    {
        if (string.IsNullOrEmpty(serviceId))
        {
            _kbProfileCache.Clear();
        }
        else if (_acceptedCollectionIds.ContainsKey(serviceId))
        {
            _kbProfileCache.Remove(serviceId);
        }
        else
        {
            return;
        }
        ScheduleKbDigestRefresh();
    }

    /// <summary>
    /// **PR-F (§4 #2) + review M-1 fix** — debounce schedule. 기존 timer 가 진행 중이면 swap helper 로 cancel
    /// 후 새 timer 시작. Task.Delay + CTS — 별 thread 생성 회피 + ObjectDisposedException 도 catch (token
    /// register race 방어).
    /// </summary>
    private void ScheduleKbDigestRefresh()
    {
        var cts = new CancellationTokenSource();
        SwapDebounceCts(cts);
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (KbDigestDebounceMs > 0)
                    await Task.Delay(KbDigestDebounceMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                // **review M-2 fix** — lambda 안의 task 는 RefreshKbDigestAsync 자체가 try/catch 흡수
                // (Log.Warn) → unobserved exception 위험 0. lambda 가 sync Action 이므로 InvokeAsync<T>(Func<T>)
                // 의 Task<Task> trap 회피.
                await _dispatcher.InvokeAsync(() => { _ = RefreshKbDigestAsync(); }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* 의도된 cancel — silent */ }
            catch (ObjectDisposedException) { /* token register race — silent */ }
        });
    }

    /// <summary>
    /// FetchKbProfilesAsync + ApplyPendingKbDigest 묶음. <see cref="InitializeAsync"/> 초기 진입과 debounce fire
    /// 양쪽에서 사용. UI thread 에서 호출 의도. **review M-2 fix** — exception 흡수 + log (caller 의 fire-and-forget
    /// 으로 unobserved task 가 되지 않도록).
    /// </summary>
    private async Task RefreshKbDigestAsync()
    {
        try
        {
            await FetchKbProfilesAsync().ConfigureAwait(true);
            ApplyPendingKbDigest();
            // SSE collection-* invalidate / 초기 fetch path 의 specialized digest(layer E) 동반 갱신. active 셋 변경은
            // specialized digest 변경 가능성 → KB digest 와 동일 시점에 MCP fetch 로 re-apply (attachment_summary
            // includeSpecialized=true). ConfigureAwait(true) — _provider 박제 UI thread invariant.
            await RefreshSpecializedDigestAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("RefreshKbDigestAsync 실패 — KB digest 미갱신, chat 영향 0", ex);
        }
    }

    /// <summary>
    /// **PR-F (§5.1)** — active service 별 KB profile fetch.
    /// <list type="bullet">
    ///   <item>cache hit (per serviceId) → 즉시 박제, HTTP skip</item>
    ///   <item>miss → <see cref="LightHouseClient.ListCollectionsAsync"/> 호출 후 acceptedIds 교차 + cache 박제</item>
    ///   <item>service 실패 (LightHouseAuthException / Timeout 등) → 본 service 만 빈 list, 다른 service 영향 0</item>
    /// </list>
    /// 반환 dict 은 호출 시점 snapshot — caller 가 KbDigestBuilder 등으로 transform.
    /// <para/>
    /// **review m-2 fix** — cancellation 시 partial cache 박제 회피. 임시 dict 에 누적 후 loop 정상 종료 시점에만
    /// _kbProfileCache 일괄 박제. ct cancel 시 본 호출은 부분 결과 폐기 + 다음 호출이 처음부터 재시작.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<CollectionInfo>>> FetchKbProfilesAsync(
        CancellationToken ct = default)
    {
        var pending = new Dictionary<string, IReadOnlyList<CollectionInfo>>();
        foreach (var serviceId in _acceptedCollectionIds.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (_kbProfileCache.TryGetValue(serviceId, out var cached))
            {
                pending[serviceId] = cached;
                continue;
            }
            var client = LightHouseClientHolder.GetClient(serviceId);
            if (client is null)
            {
                pending[serviceId] = Array.Empty<CollectionInfo>();
                continue;
            }
            var filtered = await KbProfileExtractor.FetchForServiceAsync(
                client, _acceptedCollectionIds[serviceId], ct).ConfigureAwait(true);
            pending[serviceId] = filtered;
        }
        // loop 정상 종료 — _kbProfileCache 일괄 박제 (review m-2).
        foreach (var kv in pending) _kbProfileCache[kv.Key] = kv.Value;
        return pending;
    }

    /// <summary>
    /// **PR-G (§5.2 v-b) + review C-1 fix** — cache snapshot → <see cref="KbDigestBuilder.Build"/>
    /// → <see cref="ApiChatProvider.SetPendingSystemPrompt"/> path. 다음 firstTurn 진입 시점에 system message 의
    /// 2번째 TextContent 로 swap (lazy apply, chat-scoped invariant 정합).
    /// <para/>
    /// caller (2 path):
    /// <list type="bullet">
    ///   <item>KB profile fetch/SSE invalidate 완료 시 (<see cref="RefreshKbDigestAsync"/>)</item>
    ///   <item>provider 토글 시 (<see cref="ConfigureProviderAsync"/> 의 _provider = provider; 직후) — new
    ///   ApiChatProvider 의 _kbDigest reset 보호 (review C-1 — Claude/Codex → API 토글 시 SSE 없어도 적용)</item>
    /// </list>
    /// API provider 만 적용 (Claude CLI / Codex CLI 는 별 path — §4 미결정 #3 정합, 본 phase 미적용).
    /// _provider 가 null (init 미완료) 또는 다른 provider 일 때 silent skip.
    /// </summary>
    private void ApplyPendingKbDigest()
    {
        // PR-S1 — KbDigestBuilder 가 wire POCO (Promaker.Knowledge.CollectionInfo) 무지화. caller 1줄 매핑.
        var snapshot = _kbProfileCache.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<KbCollectionDescriptor>)kv.Value
                .Select(c => new KbCollectionDescriptor { DisplayName = c.DisplayName, Description = c.Description, Keywords = c.Keywords })
                .ToArray());
        var digest = KbDigestBuilder.Build(snapshot);
        // CLI(Claude/Codex) + API provider 모두 `ILlmSystemPromptDigestSink` 구현 → 단일 캐스팅 path.
        if (_provider is ILlmSystemPromptDigestSink sink)
            sink.SetPendingSystemPrompt(digest);
        if (Log.IsDebugEnabled)
            Log.Debug($"ApplyPendingKbDigest — digest len={digest.Length} (services={_kbProfileCache.Count}, provider={_provider?.GetType().Name ?? "none"})");
    }
}
