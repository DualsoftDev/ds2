using System;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using Promaker.LlmAgent;

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
            await _mcpHost.StartAsync().ConfigureAwait(true);
            _mcpConfig = McpConfigWriter.Create("promaker", _mcpHost.ServerUrl, _mcpHost.HandshakeNonce);

            await ConfigureProviderAsync(SelectedProvider).ConfigureAwait(true);
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
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider"),
            };

            _provider = provider;
            IsReady = false;
            SessionId = null;
            StatusText = $"{kind} CLI 검출 중…";
            SendCommand.NotifyCanExecuteChanged();

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
