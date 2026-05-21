using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Promaker.LlmAgent.Api;

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
        try { _kbDigestDebounceCts?.Cancel(); } catch (ObjectDisposedException) { }
        _kbDigestDebounceCts?.Dispose();
        _kbDigestDebounceCts = null;
    }

    /// <summary>
    /// SSE event handler — KB profile invalidate 분류 후 UI thread 로 marshalling.
    /// background thread 도착 가정 — cache mutation 은 항상 dispatcher 안에서.
    /// <para/>
    /// **자가 검열 Major-1 fix**: holder 의 invoke 패턴 (LightHouseClientHolder.cs:283 `var handler = EventReceived;`)
    /// 이 snapshot 캡처 → -= 시점 race 에서 dispose 된 dispatcher 로 marshalling 시 TaskCanceledException 발생 가능.
    /// holder 가 invoke try/catch 흡수 (process crash 없음) 하지만 noise log 회피 위해 본 메서드에서도 흡수.
    /// </summary>
    private void OnKbSseEventReceived(ServerEventDto evt)
    {
        if (!KbProfileExtractor.IsKbProfileEvent(evt)) return;
        try { _ = _dispatcher.InvokeAsync(() => InvalidateAndScheduleRefresh(evt.ServiceId)); }
        catch (Exception ex) { Log.Debug($"OnKbSseEventReceived marshalling skip (dispose race?): {ex.Message}"); }
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
    /// **PR-F (§4 #2)** — debounce schedule. 기존 timer 가 진행 중이면 cancel 후 새 timer 시작.
    /// Task.Delay + CTS — 별 thread 생성 회피 + 의도된 cancel 시 OCE silent.
    /// </summary>
    private void ScheduleKbDigestRefresh()
    {
        try { _kbDigestDebounceCts?.Cancel(); } catch (ObjectDisposedException) { }
        _kbDigestDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _kbDigestDebounceCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (KbDigestDebounceMs > 0)
                    await Task.Delay(KbDigestDebounceMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                await _dispatcher.InvokeAsync(() => _ = RefreshKbDigestAsync()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* 의도된 cancel — silent */ }
        });
    }

    /// <summary>
    /// FetchKbProfilesAsync + OnKbProfileChanged 묶음. <see cref="InitializeAsync"/> 초기 진입과 debounce fire
    /// 양쪽에서 사용. UI thread 에서 호출 의도.
    /// </summary>
    private async Task RefreshKbDigestAsync()
    {
        await FetchKbProfilesAsync().ConfigureAwait(true);
        OnKbProfileChanged();
    }

    /// <summary>
    /// **PR-F (§5.1)** — active service 별 KB profile fetch.
    /// <list type="bullet">
    ///   <item>cache hit (per serviceId) → 즉시 박제, HTTP skip</item>
    ///   <item>miss → <see cref="LightHouseClient.ListCollectionsAsync"/> 호출 후 acceptedIds 교차 + cache 박제</item>
    ///   <item>service 실패 (LightHouseAuthException / Timeout 등) → 본 service 만 빈 list, 다른 service 영향 0</item>
    /// </list>
    /// 반환 dict 은 호출 시점 snapshot — caller 가 KbDigestBuilder 등으로 transform.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<CollectionInfo>>> FetchKbProfilesAsync(
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<CollectionInfo>>();
        foreach (var serviceId in _acceptedCollectionIds.Keys.ToList())
        {
            if (ct.IsCancellationRequested) break;
            if (_kbProfileCache.TryGetValue(serviceId, out var cached))
            {
                result[serviceId] = cached;
                continue;
            }
            var client = LightHouseClientHolder.GetClient(serviceId);
            if (client is null)
            {
                _kbProfileCache[serviceId] = Array.Empty<CollectionInfo>();
                result[serviceId] = Array.Empty<CollectionInfo>();
                continue;
            }
            var filtered = await KbProfileExtractor.FetchForServiceAsync(
                client, _acceptedCollectionIds[serviceId], ct).ConfigureAwait(true);
            _kbProfileCache[serviceId] = filtered;
            result[serviceId] = filtered;
        }
        return result;
    }

    /// <summary>
    /// **PR-G (§5.2 v-b)** — KB profile fetch 완료 시 호출. cache snapshot → <see cref="KbDigestBuilder.Build"/>
    /// → <see cref="ApiChatProvider.SetPendingSystemPrompt"/> path. 다음 firstTurn 진입 시점에 system message 의
    /// 2번째 TextContent 로 swap (lazy apply, chat-scoped invariant 정합).
    /// <para/>
    /// API provider 만 적용 (Claude CLI / Codex CLI 는 별 path — §4 미결정 #3 정합, 본 phase 미적용).
    /// _provider 가 null (init 미완료) 또는 다른 provider 일 때 silent skip.
    /// </summary>
    private void OnKbProfileChanged()
    {
        var snapshot = new Dictionary<string, IReadOnlyList<CollectionInfo>>(_kbProfileCache);
        var digest = KbDigestBuilder.Build(snapshot);
        if (_provider is ApiChatProvider api)
            api.SetPendingSystemPrompt(digest);
        if (Log.IsDebugEnabled)
            Log.Debug($"OnKbProfileChanged — digest len={digest.Length} (services={_kbProfileCache.Count}, provider={_provider?.GetType().Name ?? "none"})");
    }
}
