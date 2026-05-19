using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    // Round-trip 최적화 — doc: Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md
    // 본 파일 안의 `§3` / `§5.1` / `§C1` / `§M1` 는 모두 위 doc 의 섹션/이슈 ID.
    /// <summary>
    /// 마지막 성공 송신 시점의 <see cref="DsStore.Revision"/>. delta-only snapshot 첨부 정책 (§3) —
    /// 현재 store.Revision 과 다르면 다음 송신에 snapshot 재첨부, 같으면 침묵 (marker 도 미부착).
    /// in-memory only — app restart / chat history 복원 시 첫 송신에 자동 재첨부 (null 시작).
    ///
    /// **갱신 시점**: 송신 성공 (await foreach 정상 종료) 직후. 실패 / cancel 시에는 갱신 안 함 → 재시도 시 재첨부.
    /// **reset 시점**: <see cref="Reset"/>, <see cref="UpdateStore"/>, provider switch (<see cref="ConfigureProviderAsync"/>),
    ///   ApplyImportPlan 실패 (round-trip §M1).
    /// TODO(roundtrip): message edit / regenerate 기능 추가 시 해당 진입점에서도 reset 필요.
    /// </summary>
    private int? _lastSentRevision;

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


    /// <summary>
    /// 1 turn 종료 시점(= ApplyTurnPlanAsync 까지 완료, IsSending=false 직전) 에 발생.
    /// 측정 자동화(`MainViewModel.LlmChat.cs`) 가 IsSending PropertyChanged + wasSending rising-edge
    /// 로 추정하던 invariant 를 명시적 event 로 보호. SendAsync 가 향후 Task.Run 으로 전환되어도
    /// 측정 path 는 안전하게 1회 호출됨. Early-return(_provider==null/prompt 비어있음) 시에는
    /// 발생하지 않음 — 그 경로는 SendCommand.CanExecute=false 로 차단됨.
    /// </summary>
    public event EventHandler? TurnCompleted;


    /// <summary>
    /// m8 — `LlmChatViewModel` 의 ApplyImportPlan label prefix. `HistoryPanelItem.IsLlmTurn` 도 같은 prefix 로 식별.
    /// 양쪽 magic string 중복 회피.
    /// </summary>
    public const string LlmTurnLabelPrefix = "LLM: ";

    /// <summary>
    /// 정책 16 (commit-6): 텍스트 또는 첨부 중 하나만 있어도 송신. 둘 다 없으면 차단.
    /// 첨부-only 시 SendAsync 가 default prefix ("첨부된 N개 파일을 검토해 주세요") 자동 부여.
    /// </summary>
    private bool CanSend() => IsReady && !IsSending && (!string.IsNullOrWhiteSpace(Input) || HasAttachments);

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
        // review F4: Reset 은 세션/대화 초기화 의미 — chip / notice 도 함께 정리해 stale 상태 회피.
        Attachments.Clear();
        AttachmentNotice = "";
        SessionId = null;
        LastClosedProjectPath = null;
        // round-trip §3 — 세션 초기화 시 snapshot 재첨부 강제 (새 history 의 첫 turn 에 무조건 snapshot 보냄).
        _lastSentRevision = null;
        StatusText = "세션 초기화 완료";
    }

    /// <summary>전체 대화를 Markdown 형식으로 clipboard 에 복사 (Role 라벨 + 빈 줄 구분).</summary>
    [RelayCommand]
    private void CopyAll()
    {
        try { System.Windows.Clipboard.SetText(BuildMarkdownTranscript()); }
        catch (Exception ex) { Log.Warn("clipboard 전체 복사 실패", ex); }
    }

    /// <summary>chat-ui boost: ModelDocButton chat bubble 클릭 → ModelDocPreviewDialog 띄우기.
    /// XAML 의 Button.Command binding 으로 호출. CommandParameter = ChatTurn.Payload (yaml 본문).</summary>
    [RelayCommand]
    private void OpenModelDocPreview(string? yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return;
        try
        {
            var dlg = new Promaker.Dialogs.ModelDocPreviewDialog(yaml)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            dlg.ShowDialog();
        }
        catch (Exception ex) { Log.Warn("ModelDocPreviewDialog 띄우기 실패", ex); }
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
    /// 사용자가 user prompts dir 의 *.md 를 편집한 신호. provider 재구성은 불필요 — system prompt 텍스트만 다음 호출 시
    /// 자동 반영 (SystemPromptText.Phase1c 가 매 호출 LoadComposed → 디스크 재읽기).
    ///
    /// Codex provider 만 별도 처리 필요: <see cref="_codexInstructionsPath"/> 에 1회 write 한 파일을 사용하므로
    /// Refresh 시점에 동일 파일을 새 Phase1c 로 덮어쓰지 않으면 Codex 세션이 옛 prompt 그대로 사용 (provider 비대칭).
    /// </summary>
    public void RefreshPrompts()
    {
        if (_codexInstructionsPath != null && System.IO.File.Exists(_codexInstructionsPath))
        {
            System.IO.File.WriteAllText(_codexInstructionsPath, SystemPromptText.Phase1c, System.Text.Encoding.UTF8);
            Log.Info($"Codex instructions.md 재기록 (user prompts 변경 반영) — {_codexInstructionsPath}");
        }
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
        // round-trip §3 — store 인스턴스 자체가 바뀌면 이전 store 의 revision 비교가 의미 없음. 새 store 의 첫 송신에 snapshot 무조건 첨부.
        _lastSentRevision = null;
        StatusText = "프로젝트 변경 — 다음 turn 부터 새 store 사용";

        SubscribeEditorEvents();
        _editorDigest.MarkProjectReset();
    }

    /// <summary>
    /// 현재 <see cref="_store"/> 에 EditorEvent subscription 재설정. 생성자/UpdateStore 모두에서 호출.
    /// 이전 구독은 dispose. onNext 가 dispatcher thread 에서 sync 도착하면 즉시 처리, 아니면 marshalling.
    /// </summary>

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
        /// <summary>chat-ui boost: `apply_model_doc` 발행 doc bubble — 항상 button.
        /// Text = label ("발행 doc 보기 (N lines, X KB)"), Payload = yaml 본문.
        /// 클릭 시 ModelDocPreviewDialog 열기. XAML DataTrigger 동기화.</summary>
        public const string ModelDocButton = "model-doc-button";
    }

    [ObservableProperty]
    private string _role = "";

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>chat-ui boost: ModelDocButton role 의 yaml 본문 payload. 클릭 시 dialog 에 전달.
    /// 다른 role 에서는 빈 string. 별도 DataTemplate 분기.</summary>
    [ObservableProperty]
    private string _payload = "";
}

