using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Promaker.LlmAgent.Tools;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Api;
using Llm.Shared.Mcp;
namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — InitializeAsync (consent + MCP host + 초기 provider) + ConfigureProviderAsync
/// (provider switch lifecycle + stale switch race 차단).
/// 본체와의 share: _config / _mcpHost / _mcpConfig / _cts / _provider / _switchCounter / _lastSentRevision
/// + Status/IsReady/SessionId/SelectedProvider + Turns / SendCommand / Log.
/// </summary>
public partial class LlmChatViewModel
{
    private async Task InitializeAsync()
    {
        // Defense-in-depth (1d-4 E): OpenLlmChat 진입점이 1차 차단하나 다른 진입점 추가 시 안전망.
        // 거부 상태에서는 MCP host 도 띄우지 않아 LLM tool 호출 자체가 불가.
        if (!_config.IsConsentGranted())
        {
            StatusText = "LLM 데이터 전송 동의 미완료 — LLM Chat 메뉴 재진입 시 다이얼로그 표시";
            Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = StatusText });
            return;
        }

        try
        {
            // McpHostService 가 Llm.Shared 로 분리된 후 (PR-S1) WithToolsFromAssembly 의 calling-assembly
            // default 가 Llm.Shared 를 가리키게 되어 ModelTools (Promaker assembly) 가 silently 누락됨.
            // 호출 시 typeof(ModelTools).Assembly 명시로 tools/list 응답에 mcp__promaker__* 6개 복원.
            await _mcpHost.StartAsync(typeof(ModelTools).Assembly).ConfigureAwait(true);

            // Phase S5c → D-S7-3b — N 개 active service 별 session 발급 시도 (KbDigest / SSE 등 의존).
            // **Plan B (2026-05-27)** — `.mcp-config` 박제 안 함. mcp__promaker__lighthouse_* wrapper 사용.
            await TryCreateLightHouseSessionsAsync().ConfigureAwait(true);

            _mcpConfig = BuildMcpConfig();
            await ConfigureProviderAsync(SelectedProvider).ConfigureAwait(true);

            // PR-F (§5.1) — KB profile subscribe (SSE collection-* invalidate) + 초기 fetch.
            // chat panel lifetime 동안 _acceptedCollectionIds 와 holder event 가 sync.
            // 한 service 실패 ≠ chat 차단 (FetchKbProfilesAsync 가 try/catch 흡수).
            // **review M-2** — RefreshKbDigestAsync 자체가 Exception 흡수 (Log.Warn) → unobserved 0.
            SubscribeKbProfileEvents();
            // KB digest + specialized digest(layer E) 초기 fetch. RefreshKbDigestAsync 안에서 specialized digest 도
            // 동반 갱신 (KbProfile.cs). fire-and-forget — RefreshKbDigestAsync 자체 try/catch 흡수로 unobserved 0.
            // layer E 는 MCP attachment_summary(includeSpecialized=true) fetch (로컬 SourceFolder read 폐기,
            // 사용자 지시 2026-06-01) — 로컬/원격 service 모두 지원, collection 식별자(_acceptedCollectionIds)만 의존.
            _ = RefreshKbDigestAsync();
        }
        catch (Exception ex)
        {
            Log.Error("LlmChatViewModel 초기화 실패", ex);
            StatusText = $"초기화 실패: {ex.Message}";
            Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"초기화 실패: {ex.Message}" });
            // McpHostService.WaitReadyAsync timeout 등으로 throw 시 _app 은 이미 set 된 상태.
            // panel close 까지 DisposeAsync 가 지연되면 background Kestrel + ephemeral port leak →
            // defense-in-depth 로 즉시 stop. StopAsync 자체가 _app == null 이면 noop 이라 idempotent.
            await _mcpHost.StopAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// **D-S7-3b (s6-r30) — multi-service N session 발급**.
    /// <para/>
    /// 모든 active service 별로 session 발급 + unknownIds/unindexableIds lazy sync (todo §3.8 Q4). 일부 service
    /// 실패 시 부분 활성화 (결정 #1) — 성공한 service 만 반환 리스트에 박제. 어떤 실패도 chat 자체를 막지 않음.
    /// <para/>
    /// KbCollections.GroupBy(ServiceId) — 각 service 는 본인 소속 collection 만 routing 의 collectionIds 로 발행.
    /// </summary>
    private async Task TryCreateLightHouseSessionsAsync()
    {
        var clients = LightHouseClientHolder.EnsureCreated(_config);
        if (clients.Count == 0)
        {
            // active LightHouse service 미설정 — 정상 분기 (Knowledge Base 비활성). chip 안내 없음 (정보 과잉 회피).
            return;
        }

        // ServiceId → 본인 소속 collection 의 active id 셋 (D-S7-3a path — KbCollections.ServiceId 정합).
        var collectionsByServiceId = _config.KbCollections
            .Where(k => k.Active && !string.IsNullOrEmpty(k.ServiceId))
            .GroupBy(k => k.ServiceId)
            .ToDictionary(g => g.Key, g => g.Select(k => k.CollectionId).ToList());

        // **자가 검열 Major-3 (s6-r30 review) + D-S7-3c helper SSOT (s6-r31)** — KbCollectionOrphanHelper 호출.
        var orphanCount = KbCollectionOrphanHelper.CountActiveOrphans(_config);
        if (orphanCount > 0)
        {
            Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"⚠ 소속 service 없는 collection {orphanCount}건 (Settings 에서 service 삭제됨) — 정리 권장." });
        }

        var changedConfig = false;
        foreach (var svc in _config.LightHouseServices.Where(s => s.Active))
        {
            var client = LightHouseClientHolder.GetClient(svc.ServiceId);
            if (client is null) continue;  // Holder 가 BaseUrl/PSK 검증 후 entry 누락 — 정상 분기.

            var psk = _config.GetLightHousePsk(svc.ServiceId);
            if (string.IsNullOrEmpty(psk))
            {
                Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"⚠ LightHouse [{svc.DisplayName}] PSK 복호화 실패 — 본 service 비활성." });
                continue;
            }

            var activeIds = collectionsByServiceId.TryGetValue(svc.ServiceId, out var ids) ? ids : new List<string>();

            try
            {
                var resp = await client.CreateSessionAsync(activeIds).ConfigureAwait(true);
                _lightHouseSessions[svc.ServiceId] = resp.Token;
                LightHouseClientHolder.RegisterSession(svc.ServiceId, resp.Token);
                // PR-F (§5.1) — server 가 박제한 accepted 셋만 본 panel 의 KB digest filter input.
                // unknown/unindexable 은 filter 단계에서 제외 (resp.UnknownIds 가 이미 _config 에서 제거됨).
                _acceptedCollectionIds[svc.ServiceId] = resp.AcceptedCollectionIds;

                if (resp.UnknownIds.Count > 0)
                {
                    foreach (var id in resp.UnknownIds)
                        _config.KbCollections.RemoveAll(k =>
                            string.Equals(k.CollectionId, id, StringComparison.OrdinalIgnoreCase)
                            && k.ServiceId == svc.ServiceId);
                    changedConfig = true;
                    Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"⚠ [{svc.DisplayName}] server 에 없는 collection {resp.UnknownIds.Count}건 제거." });
                }
                if (resp.UnindexableIds.Count > 0)
                {
                    Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"⚠ [{svc.DisplayName}] 색인 실패 collection {resp.UnindexableIds.Count}건 제외 (재시도 가능)." });
                }

                // **Plan B (2026-05-27)** — lighthouse 의 `.mcp-config` entry 박제 폐기. Anthropic Claude CLI 의
                // mcp HTTP/SSE transport 가 OAuth 2.1 path 강제 (`_authThenStart`) 라 단순 Bearer PSK 와 호환 안 됨.
                // 대신 `LightHouseTools` (mcp__promaker__lighthouse_*) wrapper 가 LightHouseClient 의 raw JSON-RPC
                // (CallMcpToolAsync) 로 fan-out 호출. Claude CLI 는 promaker MCP 1개만 직접 통신.
                // 본 분기는 session 발급 + accepted/unknown/unindexable lazy sync + SSE register 로직만 유지.
            }
            catch (LightHouseAuthException)
            {
                Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"⚠ LightHouse [{svc.DisplayName}] 인증 실패 (PSK 확인 필요) — 본 service 비활성." });
            }
            catch (Exception ex)
            {
                Log.Warn($"LightHouse [{svc.DisplayName}] session 발급 실패: {ex.Message}");
                Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"⚠ LightHouse [{svc.DisplayName}] session 발급 실패 — 본 service 비활성 ({ex.Message})." });
            }
        }

        if (changedConfig) _config.Save();
    }

    /// <summary>
    /// `.mcp-config` 작성 — promaker 1개만. **Plan B (2026-05-27)**: Anthropic Claude CLI 의 mcp HTTP/SSE transport
    /// OAuth 2.1 강제 사고로 lighthouse entry 박제 폐기. LLM 은 `mcp__promaker__lighthouse_*` wrapper 6종으로
    /// LightHouseService 의 attachment_* 도구를 fan-out 호출 (multi-service 지원).
    /// </summary>
    private McpConfigWriter BuildMcpConfig()
    {
        var promaker = new McpServerEntry("promaker", _mcpHost.ServerUrl,
            new Dictionary<string, string> { ["X-Promaker-Nonce"] = _mcpHost.HandshakeNonce });
        return McpConfigWriter.CreateMulti(new List<McpServerEntry> { promaker });
    }

    /// <summary>
    /// Provider 생성 + EnsureCli 검증. SelectedProvider 변경 시 / 초기화 시 호출.
    /// stale switch race = `_switchCounter` 증가 후 await 경계 뒤에서 비교.
    ///
    /// **try/catch 사유**: `OnSelectedProviderChanged` 의 `_ = ConfigureProviderAsync(...)` fire-and-forget
    /// 경로에서 unobserved task exception 이 발생하면 GC finalizer 까지 노출이 지연되어 디버깅 어려움.
    /// `InitializeAsync` 가 동일 try/catch 패턴이므로 일관성 + StatusText/Turns 에 사용자 가시화. provider
    /// ctor / dispatcher 호출 / collection 수정 등의 동기 예외도 본 catch 가 흡수.
    /// </summary>
    private async Task ConfigureProviderAsync(LlmProviderKind kind)
    {
        var myCounter = Interlocked.Increment(ref _switchCounter);

        try
        {
            // 진행 중 turn 취소 + 기존 provider session 정리. API provider 는 IAsyncDisposable 라
            // McpClient + HttpClient 회수까지 같이.
            _cts?.Cancel();
            _provider?.ClearSession();
            // C6 — provider switch 는 새 session 동등 → citation cache 도 cleanup.
            ClearCitationCache();
            // round-trip §3 — provider switch 는 새 history 시작과 동치 → 새 provider 의 첫 송신에 snapshot 무조건 첨부.
            _lastSentRevision = null;
            if (_provider is IAsyncDisposable prevAsync)
            {
                try { await prevAsync.DisposeAsync().ConfigureAwait(true); }
                catch (Exception ex) { Log.Warn("이전 provider DisposeAsync 실패", ex); }
            }

            ILlmProvider provider = kind switch
            {
                LlmProviderKind.Claude => CreateClaudeProvider(),
                LlmProviderKind.Codex => CreateCodexProvider(),
                LlmProviderKind.AnthropicApi => await CreateAnthropicApiProviderAsync().ConfigureAwait(true),
                LlmProviderKind.OpenAiApi => await CreateOpenAiApiProviderAsync().ConfigureAwait(true),
                LlmProviderKind.Ollama => await CreateOllamaApiProviderAsync().ConfigureAwait(true),
                LlmProviderKind.GroqApi => await CreateGroqApiProviderAsync().ConfigureAwait(true),
                LlmProviderKind.HkmcHChatClaude => await CreateHkmcHChatClaudeProviderAsync().ConfigureAwait(true),
                LlmProviderKind.HkmcHChatOpenAi => await CreateHkmcHChatOpenAiProviderAsync().ConfigureAwait(true),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider"),
            };

            _provider = provider;
            IsReady = false;
            SessionId = null;
            StatusText = $"{kind} CLI 검출 중…";
            SendCommand.NotifyCanExecuteChanged();

            // PR-G review C-1 fix — provider 토글 시 새 ApiChatProvider 의 _kbDigest 가 "" 박제로 reset 되므로
            // 현재 cache snapshot 으로 즉시 re-apply. SSE event 없이도 다음 firstTurn 에 KB digest 박제 보장.
            ApplyPendingKbDigest();
            // provider 토글 시 새 ApiChatProvider 의 _specializedDigest 도 "" reset → layer E 재 fetch 로 re-apply.
            // MCP attachment_summary(includeSpecialized=true) fetch (async) — fire-and-forget (RefreshSpecializedDigestAsync
            // 자체 try/catch 흡수). 빈 active 셋 → SetPendingSpecializedDigest("") → cache breakpoint 3 skip (회귀 0).
            _ = RefreshSpecializedDigestAsync();

            var result = await Task.Run(() => provider.EnsureCli()).ConfigureAwait(true);

            // stale 결과 무시 (다른 switch 가 더 늦게 들어와 _switchCounter 증가시켰으면).
            // API provider 는 IAsyncDisposable 라 stale 방어 시 leak 방지로 즉시 dispose.
            if (myCounter != _switchCounter)
            {
                if (provider is IAsyncDisposable staleAsync)
                {
                    try { await staleAsync.DisposeAsync().ConfigureAwait(true); }
                    catch (Exception ex) { Log.Warn("stale provider DisposeAsync 실패", ex); }
                }
                return;
            }

            if (result.IsValid)
            {
                StatusText = $"준비 완료 — {kind}, MCP {_mcpHost.ServerUrl}, CLI {result.VersionString}";
                IsReady = true;
                // commit-5: 새 provider capability 로 chip 재검증 — 미지원 첨부 강제 제거 + 1줄 안내 (정책 9 / 3.4).
                ReevaluateAttachmentsForProvider();
            }
            else
            {
                StatusText = $"{kind} 초기화 실패: {result.Message}";
                Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = result.Message });
            }
            SendCommand.NotifyCanExecuteChanged();
        }
        catch (LlmProviderDeclinedException ex)
        {
            // 사용자가 동의 다이얼로그에서 "거부" — 정상 흐름. Error 톤 (Log.Error / Error role) 으로 표시하지 않음.
            if (myCounter != _switchCounter) return;
            Log.Info($"ConfigureProviderAsync({kind}) declined — {ex.Message}");
            StatusText = ex.Message;
            Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = ex.Message });
            IsReady = false;
            SendCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            if (myCounter != _switchCounter) return;
            Log.Error($"ConfigureProviderAsync({kind}) 실패", ex);
            StatusText = $"{kind} 초기화 실패: {ex.Message}";
            Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = $"{kind} 초기화 실패: {ex.Message}" });
            IsReady = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }
}
