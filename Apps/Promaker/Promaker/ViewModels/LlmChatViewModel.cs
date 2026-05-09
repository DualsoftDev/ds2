using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.LlmAgent;
using Promaker.LlmAgent;
using Promaker.LlmAgent.Api;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// Phase 2 후속 — 모든 provider 가 ILlmProvider 로 추상화 후 dispatch.
/// CLI 2종 (Claude / Codex) + API 3종 (Anthropic / OpenAI / Ollama).
/// API provider 는 Microsoft.Extensions.AI IChatClient + UseFunctionInvocation + McpClient HTTP self-call 패턴.
/// </summary>
public enum LlmProviderKind
{
    Claude,
    Codex,
    AnthropicApi,
    OpenAiApi,
    Ollama,
    /// <summary>F-1 spike — Groq OpenAI 호환 endpoint. F-4 cleanup 시 정식 schema 로 보존 (이름 동일).</summary>
    GroqApi,
}

/// <summary>
/// Promaker 의 LLM chat ViewModel.
///
/// Phase 1c — system prompt + 단일 mutation/read tool 풀세트 + turn end ApplyImportPlan.
///   1 LLM turn = 1 undo step (결정 7 (d), `Ds2.Editor.ImportPlanApply.applyWithUndo`).
/// Phase 2 — `ILlmProvider` 추상화 + Claude / Codex provider dispatch (UI ComboBox).
/// extend-mcp Phase L3 — Active/Passive System 분리 + Tier 1 device-class helper 4종 (add_cylinder /
///   add_clamp / add_robot / add_device) 으로 PassiveSystem cascade 자동 생성.
/// </summary>
public partial class LlmChatViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmChatViewModel));

    /// <summary>현재 active DsStore. <see cref="MainViewModel.Reset"/> 에서 reassign 시 <see cref="UpdateStore"/> 로 동기화.
    /// readonly 가 아닌 사유: 새 프로젝트 시 MainViewModel._store 가 새 인스턴스로 교체되는데 본 VM 의 reference 가 stale 되면
    /// `Queries.allProjects(store)` 가 empty 반환 → tool 호출 시 "프로젝트가 없습니다" throw (Hot-fix-7).</summary>
    private DsStore _store;
    private readonly IUiDispatcher _dispatcher;
    /// <summary>
    /// EditorEvent subscription 의 onNext 가 dispatcher thread 에서 동기 도착했는지 판단하기 위해 보관.
    /// thread-affinity 검사는 IUiDispatcher 의 책임이 아니므로 WPF Dispatcher 를 직접 참조한다.
    /// dispatcher thread 면 sync 처리 → ApplyImportPlan 동안의 _isLlmApplyingPlan 윈도가 정확히 작동.
    /// </summary>
    private readonly Dispatcher _wpfDispatcher;
    private readonly McpHostService _mcpHost = new();
    private McpConfigWriter? _mcpConfig;
    /// <summary>
    /// Codex 격리 워크스페이스 디렉토리 (`%TEMP%/Promaker/codex-workspace-&lt;guid&gt;/`). lazy 생성 (Codex provider
    /// 첫 선택 시), DisposeAsync 시 재귀 삭제. McpConfigWriter 와 동일 lifecycle 패턴.
    ///
    /// **격리 사유**: codex 0.125 는 `sandbox_mode = "danger-full-access"` 만 MCP tool call 통과 (community
    /// issue 1379772) → file system / network 자유 접근. 의도는 codex 가 promaker MCP tool 만 호출하는 것.
    /// `cd:` 를 사용자 repo 가 아닌 빈 임시 폴더로 두면 codex 의 file 탐색 / git 호출이 사용자 작업물에 닿지 않음.
    /// instructions 파일도 같은 디렉토리 안에 두어 단일 cleanup.
    /// </summary>
    private string? _codexWorkspacePath;
    private string? _codexInstructionsPath;
    private ILlmProvider? _provider;

    /// <summary>Consent + API key + 모델명 + base URL + DefaultProvider 통합 user-scope 설정. DPAPI 로 key 만 암호화.
    /// readonly 가 아닌 사유: <see cref="ReloadConfig"/> 가 설정 다이얼로그 close 후 새 인스턴스로 reassign.</summary>
    private LlmConfig _config = LlmConfig.Load();

    /// <summary>
    /// Provider 전환 race 가드 — `OnSelectedProviderChanged` 가 빠르게 두 번 호출되어도 stale 결과로
    /// IsReady/StatusText 갱신되지 않도록 sequence 비교. async EnsureCli 의 await 후에 검사.
    /// </summary>
    private int _switchCounter;

    private CancellationTokenSource? _cts;
    private ChatTurn? _streamingTurn;

    /// <summary>
    /// AssistantDelta aggregation buffer. Claude CLI 가 30~60Hz 로 fragment 를 보내면
    /// `_streamingTurn.Text +=` 매 호출이 INotifyPropertyChanged → TextBlock invalidate 를 유발해
    /// 깜빡임 + CPU 부하. 50ms 윈도로 묶어 ~20Hz 로 down-sample (사용자 체감 변화 없음).
    /// Codex 는 단일 패킷이라 throttle 효과 없음 (noop) — Claude 와 공유해도 무해.
    /// </summary>
    private readonly System.Text.StringBuilder _pendingAssistant = new();
    private DispatcherTimer? _assistantFlushTimer;
    private const int AssistantFlushIntervalMs = 50;

    /// <summary>
    /// Editor → LLM 변경 알림 채널. <see cref="DsStore.ObserveEvents"/> 구독을 통해 사용자가 GUI 에서
    /// 발생시킨 store 변경을 누적하고, 다음 <see cref="SendAsync"/> 진입 시 user prompt 앞에 한 블럭으로
    /// prepend 한다. 1 turn = 1 prepend → digest clear.
    /// </summary>
    private readonly EditorChangeDigest _editorDigest = new();
    private System.IDisposable? _editorSubscription;

    /// <summary>
    /// Self-loop guard. <see cref="ApplyTurnPlanAsync"/> 의 ApplyImportPlan 호출 직전 set, 완료 후 unset.
    /// EditorEvent subscription 의 onNext 가 동일 dispatcher thread 면 sync 처리되므로 본 flag 가
    /// ApplyImportPlan emit 윈도를 정확히 덮는다 → LLM 자기 turn 의 mutation 결과가 다음 turn 의 digest 로
    /// 다시 inject 되는 noise 차단.
    /// </summary>
    private bool _isLlmApplyingPlan;

    public ObservableCollection<ChatTurn> Turns { get; } = new();

    public IReadOnlyList<LlmProviderKind> AvailableProviders { get; } =
        new[] {
            LlmProviderKind.Claude,
            LlmProviderKind.Codex,
            LlmProviderKind.AnthropicApi,
            LlmProviderKind.OpenAiApi,
            LlmProviderKind.Ollama,
            LlmProviderKind.GroqApi,
        };

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _statusText = "초기화 중…";

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private LlmProviderKind _selectedProvider = LlmProviderKind.Claude;

    public LlmChatViewModel(DsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _wpfDispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher = new WpfDispatcherAdapter(_wpfDispatcher);
        SubscribeEditorEvents();
        HookAttachmentsCollection();
        // PR-A: 시작 시 사용자 default provider 적용. _mcpConfig 미초기화 상태라 OnSelectedProviderChanged 가
        // ConfigureProviderAsync 호출하지 않음 — 안전. InitializeAsync 가 SelectedProvider 값으로 첫 구성.
        if (Enum.TryParse<LlmProviderKind>(_config.DefaultProvider, ignoreCase: true, out var defaultKind))
            SelectedProvider = defaultKind;
        _ = InitializeAsync();
    }

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
            }
            else
            {
                StatusText = $"{kind} 초기화 실패: {result.Message}";
                Turns.Add(new ChatTurn { Role = ChatTurn.Roles.System, Text = result.Message });
            }
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

    private ILlmProvider CreateClaudeProvider()
    {
        var options = new ClaudeCliOptions(
            executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
            mcpConfigPath: Microsoft.FSharp.Core.FSharpOption<string>.Some(_mcpConfig!.Path),
            permissionMode: Microsoft.FSharp.Core.FSharpOption<string>.Some("bypassPermissions"),
            model: Microsoft.FSharp.Core.FSharpOption<string>.None,
            systemPrompt: Microsoft.FSharp.Core.FSharpOption<string>.Some(SystemPromptText.Phase1c),
            strictMcpConfig: true,
            allowedTools: Microsoft.FSharp.Core.FSharpOption<string[]>.Some(PromakerToolNames.All),
            channelCapacity: 256,
            onProcessStarted: Microsoft.FSharp.Core.FSharpOption<Action<System.Diagnostics.Process>>.Some(
                new Action<System.Diagnostics.Process>(ChildProcessTracker.AddProcess)));
        return new ClaudeCliProvider(options);
    }

    /// <summary>
    /// Codex CLI provider. config override 4종 (mcp url + http_headers + approval_policy + sandbox_mode) +
    /// `experimental_instructions_file` 로 system prompt override + `cd:` 격리 워크스페이스.
    ///
    /// **`sandbox_mode = "danger-full-access"` 사유**: codex 0.125 는 read-only / workspace-write 모드에서
    /// MCP tool call 을 "user cancelled" 로 자동 cancel (community issue 1379772). danger-full-access 만 통과.
    /// 부작용 (file system / network 자유 접근) 은 `cd:` 격리로 1차 완화 — codex 가 빈 임시 폴더에서
    /// 실행되어 사용자 git working tree / 작업 디렉토리에 닿지 않음.
    /// </summary>
    private ILlmProvider CreateCodexProvider()
    {
        // Codex 추가 권한 동의 — danger-full-access sandbox + ~/.codex/sessions rollout 이 일반 LLM 동의보다
        // 강한 위임이라 별도 confirm. 거부 시 InvalidOperationException → ConfigureProviderAsync 의 catch 로
        // 떨어져 StatusText / Turns 에 안내 + IsReady=false.
        if (!LlmConfig.EnsureCodexConsent())
            throw new InvalidOperationException(
                "Codex 추가 권한 (danger-full-access sandbox) 동의 미완료 — Codex provider 비활성화. " +
                "다른 provider (Claude / Anthropic API / OpenAI / Ollama) 로 변경하거나 재시도 시 다이얼로그 다시 표시됩니다.");

        // 워크스페이스 디렉토리 + instructions 파일 lazy 생성 (Codex 첫 선택 시점), DisposeAsync 에서 일괄 삭제.
        // experimental_instructions_file 은 path 만 받음 → Phase1c 본문을 워크스페이스 안 .md 파일에 쓰고 path 전달.
        if (_codexWorkspacePath == null)
        {
            _codexWorkspacePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Promaker", $"codex-workspace-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(_codexWorkspacePath);
            _codexInstructionsPath = System.IO.Path.Combine(_codexWorkspacePath, "instructions.md");
            System.IO.File.WriteAllText(_codexInstructionsPath, SystemPromptText.Phase1c, System.Text.Encoding.UTF8);
            // CODEX_HOME 격리는 e2e 검증 결과 인증 정보까지 격리시켜 401 Unauthorized 발생 (codex 의
            // auth 토큰이 ~/.codex/auth.json 에 저장되어 있어 격리 디렉토리에서는 못 찾음). cleanup 정책은
            // 후속 패스에서 (b) Reset() 시 ~/.codex/sessions/<sid>.jsonl 직접 삭제 또는 인증 파일 복사로 전환 예정.
            // 옵션 필드 자체는 보존 (향후 conditional 사용 가능성).
            Log.Info($"Codex 워크스페이스 격리 디렉토리 생성 — {_codexWorkspacePath}");
        }

        var configOverrides = new[]
        {
            new System.Tuple<string, string>(
                "mcp_servers.promaker.url",
                $"\"{_mcpHost.ServerUrl}\""),
            // codex docs 의 공식 예시 (`http_headers = { "X-Figma-Region" = "..." }`) 와 동일 inline table 형식.
            new System.Tuple<string, string>(
                "mcp_servers.promaker.http_headers",
                $"{{ \"X-Promaker-Nonce\" = \"{_mcpHost.HandshakeNonce}\" }}"),
            // 사용자가 chat 명령으로 의도 표현 → mcp tool 호출에 추가 approval 불필요.
            new System.Tuple<string, string>("approval_policy", "\"never\""),
            // codex 0.125 의 mcp tool call 통과 조건 (위 docstring 참조).
            new System.Tuple<string, string>("sandbox_mode", "\"danger-full-access\""),
        };
        var options = new CodexCliOptions(
            executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
            cd: Microsoft.FSharp.Core.FSharpOption<string>.Some(_codexWorkspacePath!),
            model: Microsoft.FSharp.Core.FSharpOption<string>.None,
            json: true,
            // ephemeral=false: thread rollout 을 disk 에 남겨 다음 turn 의 `exec resume <sid>` 가 동작.
            ephemeral: false,
            ignoreUserConfig: true,
            skipGitRepoCheck: true,
            fullAuto: false,
            dangerouslyBypassApprovalsAndSandbox: false,
            configOverrides: Microsoft.FSharp.Core.FSharpOption<System.Tuple<string, string>[]>.Some(configOverrides),
            experimentalInstructionsFile: Microsoft.FSharp.Core.FSharpOption<string>.Some(_codexInstructionsPath!),
            // CODEX_HOME 격리는 인증 정보까지 격리시켜 401 — None 으로 두어 사용자 ~/.codex/ 사용.
            // cleanup 은 후속 패스에서 (b) Reset rollout 직접 삭제 등으로 전환.
            codexHome: Microsoft.FSharp.Core.FSharpOption<string>.None,
            channelCapacity: 256);
        return new CodexCliProvider(options);
    }

    /// <summary>
    /// Anthropic API provider — `Anthropic` SDK + IChatClient + UseFunctionInvocation + McpClient HTTP self-call.
    /// API key 는 LlmApiConfig 에서 DPAPI 복호화. 부재 시 환경변수 ANTHROPIC_API_KEY fallback.
    /// </summary>
    private async Task<ILlmProvider> CreateAnthropicApiProviderAsync()
    {
        var apiKey = _config.GetApiKey(ApiProviderFactory.AnthropicKey)
                     ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                     ?? "";
        var model = _config.AnthropicModel;
        return await ApiProviderFactory.CreateAnthropicAsync(
            apiKey: apiKey,
            model: model,
            systemPrompt: SystemPromptText.Phase1c,
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    /// <summary>OpenAI API provider — 동일 패턴. API key fallback = OPENAI_API_KEY.</summary>
    private async Task<ILlmProvider> CreateOpenAiApiProviderAsync()
    {
        var apiKey = _config.GetApiKey(ApiProviderFactory.OpenAiKey)
                     ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? "";
        var model = _config.OpenAiModel;
        return await ApiProviderFactory.CreateOpenAiAsync(
            apiKey: apiKey,
            model: model,
            systemPrompt: SystemPromptText.Phase1c,
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    /// <summary>Ollama (local) — base URL + model name 만 필요. API key 없음.</summary>
    private async Task<ILlmProvider> CreateOllamaApiProviderAsync()
    {
        var baseUrl = _config.OllamaBaseUrl;
        var model = _config.OllamaModel;
        return await ApiProviderFactory.CreateOllamaAsync(
            baseUrl: baseUrl,
            model: model,
            systemPrompt: SystemPromptText.Phase1c,
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    /// <summary>
    /// F-1 spike — Groq OpenAI 호환 endpoint provider. env-var <c>GROQ_API_KEY</c> 만 사용 (DPAPI 미보관).
    /// 모델은 <c>meta-llama/llama-4-scout-17b-16e-instruct</c> 하드코딩.
    ///
    /// **모델 선택 사유** (F-1 spike 발견 — rev 6 todo 본문 정리 예정):
    /// llama-3.3-70b-versatile = free tier TPM 12,000 → Promaker system prompt + 21 tool descriptions
    /// 합계 ~25K tokens 로 단일 요청이 한도 2배 초과 (HTTP 413). Llama 4 Scout 17B 는 context window
    /// 131K + 더 큰 TPM + tool calling 호환 검증 spike 의의. **모델 ID 는 meta-llama/ prefix 필수**
    /// (prefix 누락 시 HTTP 404 model_not_found — 1차 spike 시도 시 발견).
    /// 모델 변경 시 본 메서드 재컴파일 필요 (F-4 cleanup 시 LlmConfig.GroqModel 로 이전).
    /// </summary>
    private async Task<ILlmProvider> CreateGroqApiProviderAsync()
    {
        var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
        const string model = "meta-llama/llama-4-scout-17b-16e-instruct";
        return await ApiProviderFactory.CreateGroqAsync(
            apiKey: apiKey,
            model: model,
            systemPrompt: SystemPromptText.Phase1c,
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    /// <summary>
    /// 1 turn 종료 시점(= ApplyTurnPlanAsync 까지 완료, IsSending=false 직전) 에 발생.
    /// 측정 자동화(`MainViewModel.LlmChat.cs`) 가 IsSending PropertyChanged + wasSending rising-edge
    /// 로 추정하던 invariant 를 명시적 event 로 보호. SendAsync 가 향후 Task.Run 으로 전환되어도
    /// 측정 path 는 안전하게 1회 호출됨. Early-return(_provider==null/prompt 비어있음) 시에는
    /// 발생하지 않음 — 그 경로는 SendCommand.CanExecute=false 로 차단됨.
    /// </summary>
    public event EventHandler? TurnCompleted;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_provider == null) return;
        var prompt = (Input ?? "").Trim();
        if (prompt.Length == 0) return;

        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.User, Text = prompt });
        // Streaming turn 은 첫 AssistantDelta 시점에 EnsureStreamingTurn 으로 lazy-create —
        // tool_use 가 첫 이벤트로 오는 경우 빈 assistant 버블이 먼저 보이는 깜빡임 회피.
        _streamingTurn = null;

        Input = "";

        // Turn-scoped context — tool method 가 [FromServices] 로 받음.
        // 안전망: 이전 turn 의 EndTurn 누락 방어 (정상 흐름은 finally 에서 종료됨).
        // EndTurn 은 idempotent — current=null 이면 null 반환.
        _mcpHost.TurnProvider.EndTurn();
        var turnCtx = new LlmTurnContext(_store, _dispatcher);
        _mcpHost.TurnProvider.BeginTurn(turnCtx);

        // Editor 변경 digest prepend — 사용자가 GUI 에서 변경한 fact 를 user prompt 앞에 한 블럭으로 inject.
        // digest 가 비어있으면 prompt 그대로. 전송 후 clear → 1 turn = 1 prepend.
        var promptForProvider = prompt;
        if (_editorDigest.HasAny)
        {
            var prefix = _editorDigest.ToContextMessage();
            _editorDigest.Clear();
            if (!string.IsNullOrEmpty(prefix))
                promptForProvider = prefix + "\n\n" + prompt;
        }

        _cts = new CancellationTokenSource();
        try
        {
            // rev 4 (commit-2): `string prompt` → `LlmUserMessage`. 현 단계 첨부 없음 (OfText factory).
            // commit-4..N 에서 Attachments snapshot 채워 wire.
            var stream = _provider.Send(LlmUserMessage.OfText(promptForProvider), _cts.Token);
            await foreach (var evt in stream.ConfigureAwait(true))
            {
                HandleEvent(evt);
            }
        }
        catch (OperationCanceledException) { /* user cancel */ }
        catch (Exception ex)
        {
            Log.Error("LlmChatViewModel.SendAsync 실패", ex);
            AddErrorTurn($"[ERROR] {ex.Message}");
        }
        finally
        {
            // Stream 종료 — throttle 의 잔여 fragment flush + streaming turn 마감.
            EndStreamingTurn();

            // Turn end — plan apply (결정 7 (d): 1 turn = 1 undo step)
            var endedCtx = _mcpHost.TurnProvider.EndTurn();
            if (endedCtx != null && !endedCtx.Plan.IsEmpty)
            {
                try
                {
                    await ApplyTurnPlanAsync(endedCtx, prompt);
                }
                catch (Exception ex)
                {
                    Log.Error("ApplyImportPlan 실패", ex);
                    AddErrorTurn($"[ApplyImportPlan ERROR] {ex.Message}");
                }
            }

            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            _cts?.Dispose();
            _cts = null;

            TurnCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ApplyTurnPlanAsync(LlmTurnContext ctx, string prompt)
    {
        var label = $"{LlmTurnLabelPrefix}{Truncate(prompt, 50)}";
        var plan = ctx.Plan.Build();
        await _dispatcher.InvokeAsync(() =>
        {
            // Self-loop guard: ApplyImportPlan 이 emit 하는 EditorEvent 들은 dispatcher thread 에서 sync 도착 →
            // SubscribeEditorEvents 의 OnEditorEvent 가 본 flag 를 보고 digest 누적을 skip. unset 은 finally 보장.
            _isLlmApplyingPlan = true;
            try { DsStoreImportPlanExtensions.ApplyImportPlan(_store, label, plan); }
            finally { _isLlmApplyingPlan = false; }
        });
        AddToolTurn($"[applied] {plan.Operations.Length} operation(s) committed as 1 undo step.");
    }

    /// <summary>
    /// m8 — `LlmChatViewModel` 의 ApplyImportPlan label prefix. `HistoryPanelItem.IsLlmTurn` 도 같은 prefix 로 식별.
    /// 양쪽 magic string 중복 회피.
    /// </summary>
    public const string LlmTurnLabelPrefix = "LLM: ";

    /// <summary>
    /// commit-4 단계: 텍스트 필수 유지. 정책 16 의 첨부-only 송신 + default prefix 는 commit-6 (race-free SendAsync)
    /// 에서 SendAsync 본체와 함께 묶어 처리 — 그 전까지는 chip UI 만 동작하고 송신은 text 필요.
    /// </summary>
    private bool CanSend() => IsReady && !IsSending && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private bool CanCancel() => IsSending;

    [RelayCommand]
    private void Reset()
    {
        Cancel();
        _provider?.ClearSession();
        Turns.Clear();
        SessionId = null;
        StatusText = "세션 초기화 완료";
    }

    /// <summary>전체 대화를 Markdown 형식으로 clipboard 에 복사 (Role 라벨 + 빈 줄 구분).</summary>
    [RelayCommand]
    private void CopyAll()
    {
        try { System.Windows.Clipboard.SetText(BuildMarkdownTranscript()); }
        catch (Exception ex) { Log.Warn("clipboard 전체 복사 실패", ex); }
    }

    /// <summary>SaveFileDialog 로 사용자 지정 경로 (.md/.txt) 에 전체 대화 저장.</summary>
    [RelayCommand]
    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "LLM 대화 내보내기",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"llm-chat-{DateTime.Now:yyyyMMdd-HHmmss}.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, BuildMarkdownTranscript(), new System.Text.UTF8Encoding(false));
            StatusText = $"대화 내보냄 — {dlg.FileName}";
        }
        catch (Exception ex)
        {
            Log.Error("대화 내보내기 실패", ex);
            StatusText = $"내보내기 실패: {ex.Message}";
        }
    }

    private string BuildMarkdownTranscript()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# LLM Chat — {SelectedProvider}, session={SessionId ?? "(none)"}, exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var t in Turns)
        {
            sb.AppendLine($"## {t.Role}");
            sb.AppendLine();
            sb.AppendLine(t.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// 설정 다이얼로그 close 후 호출. disk 의 새 LlmConfig 를 메모리로 reload + **활성 provider 재구성**.
    /// review (1): Create*ApiProviderAsync 는 ConfigureProviderAsync 시점에만 호출되므로 _config reassign 만으로
    /// 활성 provider (이미 baked-in model/key) 는 재생성되지 않음. 사용자가 모델 / API key 변경해도 다음 turn 까지 옛 값 사용.
    /// → ConfigureProviderAsync(SelectedProvider) 호출하여 활성 provider 도 새 _config 기반으로 재생성. _switchCounter
    /// race 가드가 이미 있어 중복 호출 안전.
    /// DefaultProvider 자체는 OnSelectedProviderChanged 에서 자동 저장되므로 SelectedProvider 동기화 불필요.
    /// </summary>
    public void ReloadConfig()
    {
        _config = LlmConfig.Load();
        if (_mcpConfig != null) _ = ConfigureProviderAsync(SelectedProvider);
    }

    /// <summary>
    /// MainViewModel.Reset() 에서 _store 가 새 DsStore 인스턴스로 교체될 때 호출 (Hot-fix-7).
    /// 진행 중 turn cancel + 기존 session clear + _store reassign — 다음 turn 부터 새 store 의 project 인식.
    ///
    /// 추가: 새 store 에 EditorEvent subscription 재설정 + digest 를 PROJECT_RESET 으로 표식 (다음 turn 의
    /// user prompt 앞에 "이전 모델 가정 무효" 한 줄 prepend). 기존 subscription 은 dispose.
    /// </summary>
    public void UpdateStore(DsStore newStore)
    {
        if (ReferenceEquals(_store, newStore)) return;
        Cancel();
        _provider?.ClearSession();
        _store = newStore;
        SessionId = null;
        StatusText = "프로젝트 변경 — 다음 turn 부터 새 store 사용";

        SubscribeEditorEvents();
        _editorDigest.MarkProjectReset();
    }

    /// <summary>
    /// 현재 <see cref="_store"/> 에 EditorEvent subscription 재설정. 생성자/UpdateStore 모두에서 호출.
    /// 이전 구독은 dispose. onNext 가 dispatcher thread 에서 sync 도착하면 즉시 처리, 아니면 marshalling.
    /// </summary>
    private void SubscribeEditorEvents()
    {
        _editorSubscription?.Dispose();
        var observable = (IObservable<EditorEvent>)_store.ObserveEvents();
        _editorSubscription = observable.Subscribe(new EditorEventObserver(OnEditorEvent));
    }

    private void OnEditorEvent(EditorEvent evt)
    {
        // dispatcher thread 도착 시 sync — _isLlmApplyingPlan 윈도가 ApplyImportPlan 의 sync emit 과 동일 stack frame
        // 에서 검사되므로 self-loop guard 정확. marshalling 경로 (else) 는 BG/non-dispatcher thread 에서 store 가 mutate 되는
        // 가설적 경로 — 결정 8 (mutation 은 dispatcher 경유) 가 깨지지 않는 한 사용자 GUI 직접 동작에서만 발생하므로
        // self-loop 와 무관 (LLM 자기 turn 의 ApplyImportPlan 은 항상 dispatcher 위라 sync 분기로 들어옴).
        if (_wpfDispatcher.CheckAccess()) HandleEditorEventOnDispatcher(evt);
        else _wpfDispatcher.InvokeAsync(() => HandleEditorEventOnDispatcher(evt), DispatcherPriority.Background);
    }

    private void HandleEditorEventOnDispatcher(EditorEvent evt)
    {
        if (_isLlmApplyingPlan) return;
        _editorDigest.Record(evt);
    }

    partial void OnInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsReadyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// ComboBox 변경 시 provider 재구성. 첫 init 이전 (`_mcpConfig == null`) 에는 무시 — InitializeAsync 의
    /// ConfigureProviderAsync 가 SelectedProvider 기본값으로 처리.
    ///
    /// PR-D: 사용자 마지막 선택 = 다음 시작 시 default provider. SelectedProvider 변경 시 _config.DefaultProvider
    /// 갱신 + Save → 별도 "기본 Provider" UI 불필요 (LLM 탭에서 항목 삭제됨).
    /// </summary>
    partial void OnSelectedProviderChanged(LlmProviderKind value)
    {
        if (_mcpConfig == null) return;
        _config.DefaultProvider = value.ToString();
        try { _config.Save(); }
        catch (Exception ex) { Log.Warn("DefaultProvider 저장 실패", ex); }
        _ = ConfigureProviderAsync(value);
    }

    private void HandleEvent(LlmEvent evt)
    {
        switch (evt)
        {
            case LlmEvent.SessionStarted started:
                SessionId = started.sessionId;
                var sidShort = started.sessionId.Length >= 8 ? started.sessionId.Substring(0, 8) : started.sessionId;
                StatusText = $"session={sidShort}… model={started.model} tools={started.tools.Length} mcp={started.mcpServers.Length}";
                break;

            case LlmEvent.AssistantDelta delta:
                EnsureStreamingTurn();
                AppendAssistant(delta.text);
                break;

            case LlmEvent.Thinking think:
                AddThinkingTurn(think.text);
                break;

            case LlmEvent.ToolUse tu:
                AddToolTurn($"[tool_use] {tu.name}");
                break;

            case LlmEvent.ToolResult tr:
                AddToolTurn($"[tool_result] {(tr.isError ? "ERROR " : "")}{Truncate(tr.content, 400)}");
                break;

            case LlmEvent.RateLimitEvent rl:
                StatusText = $"rate-limit: {rl.status} (resets {rl.resetsAtUnix})";
                break;

            case LlmEvent.SessionEnd end:
                StatusText = $"turn 종료 — {end.durationMs}ms, ${end.costUsd:0.0000}, stop={end.stopReason}, denials={end.permissionDenialCount}";
                break;

            case LlmEvent.ProviderError err:
                AddErrorTurn($"[provider error] {err.message}");
                StatusText = err.message;
                break;
        }
    }

    /// <summary>
    /// Streaming assistant turn lazy-create. AssistantDelta / ProviderError / catch 의 ERROR 텍스트가
    /// _streamingTurn=null 상태에서 호출되더라도 새 assistant 버블을 생성한다.
    /// tool_use → tool_result → assistant 순서일 때 사이에 새 assistant 버블이 chronologically 삽입되도록 함.
    /// </summary>
    private void EnsureStreamingTurn()
    {
        if (_streamingTurn != null) return;
        _streamingTurn = new ChatTurn { Role = ChatTurn.Roles.Assistant, Text = "", IsStreaming = true };
        Turns.Add(_streamingTurn);
    }

    /// <summary>현재 streaming turn 의 throttle buffer flush + IsStreaming=false + null 화. 비어있으면 제거.</summary>
    private void EndStreamingTurn()
    {
        _assistantFlushTimer?.Stop();
        FlushAssistantBuffer();
        if (_streamingTurn == null) return;
        _streamingTurn.IsStreaming = false;
        if (string.IsNullOrEmpty(_streamingTurn.Text)) Turns.Remove(_streamingTurn);
        _streamingTurn = null;
    }

    /// <summary>Tool 관련 메시지를 별도 turn 으로 추가 (XAML 의 Role=tool DataTrigger 가 gray 적용).</summary>
    private void AddToolTurn(string text)
    {
        EndStreamingTurn();
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.Tool, Text = text });
    }

    /// <summary>Thinking block 을 별도 turn 으로 추가 (Role=thinking — 기본 어시스턴트 스타일 + 좌측 색띠).</summary>
    private void AddThinkingTurn(string text)
    {
        EndStreamingTurn();
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.Thinking, Text = text });
    }

    /// <summary>에러 메시지를 별도 turn 으로 추가 (Role=error — XAML DataTrigger 가 dark orange 적용).</summary>
    private void AddErrorTurn(string text)
    {
        EndStreamingTurn();
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.Error, Text = text });
    }

    private void AppendAssistant(string fragment)
    {
        if (_streamingTurn == null) return;
        _pendingAssistant.Append(fragment);

        if (_assistantFlushTimer == null)
        {
            _assistantFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(AssistantFlushIntervalMs)
            };
            _assistantFlushTimer.Tick += (_, _) =>
            {
                _assistantFlushTimer!.Stop();
                FlushAssistantBuffer();
            };
        }
        if (!_assistantFlushTimer.IsEnabled) _assistantFlushTimer.Start();
    }

    private void FlushAssistantBuffer()
    {
        if (_pendingAssistant.Length == 0 || _streamingTurn == null) return;
        _streamingTurn.Text += _pendingAssistant.ToString();
        _pendingAssistant.Clear();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _assistantFlushTimer?.Stop();
        _editorSubscription?.Dispose();
        _editorSubscription = null;
        if (_provider is IAsyncDisposable apiAsync)
        {
            try { await apiAsync.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("provider DisposeAsync 실패", ex); }
        }
        _mcpConfig?.Dispose();
        if (_codexWorkspacePath != null)
        {
            try
            {
                if (System.IO.Directory.Exists(_codexWorkspacePath))
                    System.IO.Directory.Delete(_codexWorkspacePath, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Codex 워크스페이스 디렉토리 삭제 실패 — {_codexWorkspacePath}", ex);
            }
        }
        await _mcpHost.DisposeAsync().ConfigureAwait(false);
    }
}

public partial class ChatTurn : ObservableObject
{
    /// <summary>
    /// ChatTurn.Role 의 magic string SSOT. ViewModel 측은 이 const 만 참조.
    /// XAML DataTrigger 의 Value 는 string literal 매칭 특성상 그대로 유지하되, 변경 시 본 const 와 함께 동기화.
    /// </summary>
    public static class Roles
    {
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string System = "system";
        public const string Tool = "tool";
        public const string Thinking = "thinking";
        public const string Error = "error";
    }

    [ObservableProperty]
    private string _role = "";

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isStreaming;
}

file sealed class EditorEventObserver : IObserver<EditorEvent>
{
    private readonly Action<EditorEvent> _onNext;

    public EditorEventObserver(Action<EditorEvent> onNext) => _onNext = onNext;

    public void OnNext(EditorEvent value) => _onNext(value);
    public void OnCompleted() { }
    public void OnError(Exception error) { /* swallow — provider 변경/dispose 시 emit 종료 가능 */ }
}
