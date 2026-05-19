using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.LlmAgent;
using Promaker.LlmAgent;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — SendAsync (1 turn lifecycle) + ApplyTurnPlanAsync (LLM 결정 → store 반영) + AddModelDocTurn (yaml bubble).
/// 본체와의 share: _provider / _cts / _streamingTurn / _editorDigest / _store / _mcpHost.TurnProvider / _lastSentRevision / _isLlmApplyingPlan / _dispatcher.
/// </summary>
public partial class LlmChatViewModel
{
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_provider == null) return;

        // 정책 20 race-free snapshot — fire-and-forget paste / drag-drop 이 SendAsync 진행 중 Attachments 갱신해도
        // 본 turn 은 진입 시점 sn 으로 완료. snapshotProvider 도 OnSelectedProviderChanged 가 cancel 시켜도
        // 진행 중 스트림은 캡처된 provider 로 끝남.
        var attachmentsSnapshot = Attachments.ToArray();
        var snapshotProvider = _provider;

        var rawPrompt = (Input ?? "").Trim();
        var hasAttachments = attachmentsSnapshot.Length > 0;
        if (rawPrompt.Length == 0 && !hasAttachments) return;

        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        // 정책 16 default prefix — 텍스트 비어있고 첨부만 있을 때 자동 보충 (user-facing turn 도 동일 텍스트).
        var prompt = rawPrompt.Length == 0
            ? $"첨부된 {attachmentsSnapshot.Length}개 파일을 검토해 주세요."
            : rawPrompt;

        // 정책 16 송신 후 처리 — chip 즉시 비움 + notice 비움. 실패 시 복원 안 함 (race-free 우선; 사용자가 재첨부 가능).
        Attachments.Clear();
        AttachmentNotice = "";

        // user turn 표시 — 첨부 있으면 summary 라벨 prepend (정책 17 history summary 와 동일 형식)
        var userTurnText = prompt;
        if (hasAttachments)
        {
            var summaries = string.Join(" ",
                attachmentsSnapshot.Select(a => AttachmentRendering.summarize(a.Source)));
            userTurnText = summaries + "\n" + prompt;
        }
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.User, Text = userTurnText });
        _streamingTurn = null;

        Input = "";

        // Turn-scoped context
        _mcpHost.TurnProvider.EndTurn();
        var turnCtx = new LlmTurnContext(_store, _dispatcher);
        _mcpHost.TurnProvider.BeginTurn(turnCtx);

        // Editor digest prepend
        var promptForProvider = prompt;
        if (_editorDigest.HasAny)
        {
            var prefix = _editorDigest.ToContextMessage();
            _editorDigest.Clear();
            if (!string.IsNullOrEmpty(prefix))
                promptForProvider = prefix + "\n\n" + promptForProvider;
        }

        // LastClosedProjectPath hint — 직전 닫힌 프로젝트 경로를 LLM 에 1회성 주입.
        // 토큰 절약을 위해 주입 직후 null clear — session/history 에 이미 들어가 LLM 이 인지하므로 다음 turn 부터 다시 prefix 안 붙임.
        // EditorChangeDigest.MarkProjectReset 자동 발화와 분리된 별도 분기 (UpdateStore 와 무관, 사용자가 직접 닫기 명령 호출했을 때만).
        if (!string.IsNullOrEmpty(LastClosedProjectPath))
        {
            var hint = $"<closed_project>\n직전 세션 닫힌 프로젝트 경로: {LastClosedProjectPath}\n사용자가 이 프로젝트를 다시 참조하면 해당 파일을 읽어 컨텍스트를 재구축하세요.\n</closed_project>";
            promptForProvider = hint + "\n\n" + promptForProvider;
            LastClosedProjectPath = null;
        }

        // 정책 15 텍스트 첨부 inline — fenced wrapper. 이미지/PDF 는 LlmUserMessage.Attachments 로 wire (commit-6b).
        var textInlines = attachmentsSnapshot
            .Where(a => a.Source.IsTextFile)
            .Select(a => AttachmentRendering.toInlineString(a.Source))
            .ToArray();
        if (textInlines.Length > 0)
            promptForProvider = string.Join("\n\n", textInlines) + "\n\n" + promptForProvider;

        // round-trip §3 / §5.1 — store snapshot delta-only 첨부. revision 변화 시점에만 별도 envelope 으로 첨부.
        // §C1 — snapshot 은 promptForProvider 본문에 prepend 하지 않고 LlmUserMessage.SnapshotPrefix 로 분리 전달:
        //   - In-process IChatClient (ApiChatProvider) 가 _history 누적에서 분리하고 본 turn 호출 시점에만
        //     별도 TextContent 로 prepend (Anthropic 시 cache_control 부착).
        //   - CLI provider (Claude/Codex) 는 prompt 본문 앞에 단순 prepend (CLI 자체가 history 관리).
        // §J6 — revision read 와 envelope 빌드 사이 BumpRevision race 차단을 위해 RenderSnapshotEnvelopeAtomic 사용
        //   (rev, body) 단일 호출 내 캡쳐. 비교용 _store.Revision 도 한 번 더 read 하지만 attach 결정만 하므로 무해.
        // retry-safe: send 성공 직후에만 _lastSentRevision 갱신 (정상 종료 path 의 finally 직전).
        var attachSnapshot = _lastSentRevision != _store.Revision;
        int revisionAtSend = _lastSentRevision ?? -1;
        string? snapshotEnvelope = null;
        if (attachSnapshot)
        {
            var (rev, body) = _store.RenderSnapshotEnvelopeAtomic();
            revisionAtSend = rev;
            snapshotEnvelope = body;
            Log.Info($"[snapshot] attached revision={rev} prev={_lastSentRevision?.ToString() ?? "null"} length={body.Length}");
        }

        // 비텍스트 첨부 array — provider wire 대상 (commit-6b 까지는 LlmUserMessageOps.WarnUnsupported only).
        var nonTextAttachments = attachmentsSnapshot
            .Where(a => !a.Source.IsTextFile)
            .Select(a => a.Source)
            .ToArray();

        // status 진행 표시
        if (hasAttachments)
        {
            var totalBytes = 0L;
            foreach (var a in attachmentsSnapshot) totalBytes += a.ByteSize;
            StatusText = $"첨부 {attachmentsSnapshot.Length}개 ({AttachmentRendering.formatBytes(totalBytes)}) 송신 중…";
        }

        // ImportPlan label fallback (정책 16) — 사용자 입력 + 첨부 시 [첨부 N개] prefix 라벨링.
        var labelPrompt = (hasAttachments && rawPrompt.Length > 0)
            ? $"[첨부 {attachmentsSnapshot.Length}개] {prompt}"
            : prompt;

        _cts = new CancellationTokenSource();
        try
        {
            // §C1 — snapshotEnvelope=null 이면 LlmUserMessage.Create overload 가 자동으로 SnapshotPrefix=None 처리.
            var msg = LlmUserMessage.Create(promptForProvider, nonTextAttachments, snapshotEnvelope);
            var stream = snapshotProvider.Send(msg, _cts.Token);
            await foreach (var evt in stream.ConfigureAwait(true))
            {
                HandleEvent(evt);
            }
            // round-trip §3 retry-safe — stream 정상 종료 시점에만 commit. catch / cancel path 에서는 미갱신 → 다음 송신에 동일 snapshot 재첨부.
            if (attachSnapshot)
                _lastSentRevision = revisionAtSend;
        }
        catch (OperationCanceledException) { /* user cancel — _lastSentRevision 미갱신 */ }
        catch (Exception ex)
        {
            Log.Error("LlmChatViewModel.SendAsync 실패", ex);
            // commit-6b Minor-5 — 413 메시지 통합. error turn / StatusText 둘 다 같은 한국어 안내.
            if (ex is System.Net.Http.HttpRequestException hre && (int?)hre.StatusCode == 413)
            {
                const string msg413 = "첨부 크기가 provider 한도를 초과했습니다 (HTTP 413)";
                AddErrorTurn($"[ERROR] {msg413}");
                StatusText = msg413;
            }
            else
            {
                AddErrorTurn($"[ERROR] {ex.Message}");
            }
        }
        finally
        {
            EndStreamingTurn();

            // Turn end — plan apply (결정 7 (d): 1 turn = 1 undo step). label = labelPrompt.
            var endedCtx = _mcpHost.TurnProvider.EndTurn();
            if (endedCtx != null && !endedCtx.Plan.IsEmpty)
            {
                try
                {
                    await ApplyTurnPlanAsync(endedCtx, labelPrompt);
                }
                catch (Exception ex)
                {
                    Log.Error("ApplyImportPlan 실패", ex);
                    AddErrorTurn($"[ApplyImportPlan ERROR] {ex.Message}");
                    // round-trip §M1 — apply 실패 시 LLM 측은 mutation 성공 가정으로 다음 turn 진입할 수 있는데
                    // store 는 갱신 안 됨 → BumpRevision 미발동 → _lastSentRevision 비교 무변경 → 다음 turn snapshot
                    // 미첨부 → LLM 이 stale 가정 그대로. 강제 재첨부로 stale 차단.
                    _lastSentRevision = null;
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
        // chat-ui boost: 발행 doc yaml view 가 생성되면 line 수 무관 항상 button bubble (클릭 시 dialog).
        // LLM output/input token 변화 0 — turn 안 누적된 yaml 들을 ViewModel local 로 display.
        foreach (var yaml in ctx.ModelDocsYaml)
            AddModelDocTurn(yaml);
    }

    /// <summary>chat-ui boost: 발행 doc yaml view 1건 → button bubble 추가. 클릭 시 dialog.</summary>
    private void AddModelDocTurn(string yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return;
        // review m1: YamlDotNet emitter 가 trailing '\n' 으로 끝나면 Split 결과 마지막 빈 string 1개 추가됨 →
        // 라벨의 line 카운트가 실제보다 1 많게 표시되어 보정 필요. TrimEnd 로 정정.
        var lineCount = yaml.TrimEnd('\n').Split('\n').Length;
        EndStreamingTurn();
        // Text = 사용자 보이는 label, Payload = yaml 본문. 클릭 시 ModelDocPreviewDialog 열기.
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(yaml);
        var label = $"발행 doc 보기 ({lineCount} lines, {byteCount / 1024.0:F1} KB)";
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.ModelDocButton, Text = label, Payload = yaml });
    }
}
