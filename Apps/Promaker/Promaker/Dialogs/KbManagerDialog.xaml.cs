using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using log4net;
using Ds2.LightHouse.Protocol;
using Promaker.Knowledge;
using Promaker.LlmAgent;

namespace Promaker.Dialogs;

/// <summary>
/// LightHouse KB collection 관리 — multi-service TabControl + per-tab ListView (D-S7-3c, s6-r31).
///
/// **D-S7-3c 변경**: 단일 ListView → TabControl (각 tab = active service) + 단일 CollectionsList (선택 tab 의
/// service 의 collection 만 표시). per-service `_rowsByServiceId` dict 로 다중 service 의 row 분리. SSE event 의
/// `evt.ServiceId` 로 source service 식별 후 해당 service 의 rows 만 refresh.
///
/// orphan KbCollection (소속 service 가 Settings 에서 삭제된 entry) 정리 버튼 — 동의 후 일괄 제거.
///
/// 진입: LLM Chat 패널 의 "📚 KB 관리" 버튼 (Phase S5b).
/// </summary>
public partial class KbManagerDialog : Window
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(KbManagerDialog));

    private readonly LlmConfig _config;

    /// <summary>**D-S7-3c (s6-r31)** — per-service collection rows. key = ServiceId.</summary>
    private readonly Dictionary<string, ObservableCollection<CollectionRow>> _rowsByServiceId = new();

    private CancellationTokenSource? _cts;
    private Task? _inflightIngestTask;
    private int _tokensUsedAtIngestStart;

    /// <summary>다이얼로그 닫힐 때 호출자에게 LlmConfig.KbCollections 변경 알림 (active 토글 / 등록 / 제거).</summary>
    public bool ConfigChanged { get; private set; }

    public KbManagerDialog()
    {
        InitializeComponent();
        _config = LlmConfig.Load();

        // D-S7-3c — multi-instance Holder 의 모든 active service client 보장.
        var clients = LightHouseClientHolder.EnsureCreated(_config);
        if (clients.Count == 0)
        {
            StatusChip.Text = "⚠ LightHouse Service 미설정 — 설정 > LLM 탭에서 BaseUrl + PSK 입력 후 \"연결 테스트\" 통과 필요.";
        }

        BuildServiceTabs();
        Loaded += async (_, _) => await RefreshAsync();

        // D-S7-2b (s6-r28) + D-S7-3c (s6-r31, ServiceId tagging) — SSE event 수신 시 해당 service 의 rows refresh.
        LightHouseClientHolder.EventReceived += OnSseEventReceived;
    }

    /// <summary>
    /// **D-S7-3c (s6-r31)** — active service 별 TabItem 동적 생성. Tab.Header = DisplayName, Tab.Tag = ServiceId.
    /// SelectionChanged 가 CollectionsList.ItemsSource 갱신.
    /// </summary>
    private void BuildServiceTabs()
    {
        ServicesTabControl.Items.Clear();
        var active = _config.LightHouseServices.Where(s => s.Active).ToList();
        foreach (var svc in active)
        {
            ServicesTabControl.Items.Add(new TabItem
            {
                Header = string.IsNullOrEmpty(svc.DisplayName) ? "(unnamed)" : svc.DisplayName,
                Tag = svc.ServiceId,
            });
            if (!_rowsByServiceId.ContainsKey(svc.ServiceId))
                _rowsByServiceId[svc.ServiceId] = new ObservableCollection<CollectionRow>();
        }
        if (ServicesTabControl.Items.Count > 0) ServicesTabControl.SelectedIndex = 0;
    }

    /// <summary>**D-S7-3c (s6-r31)** — 선택된 tab 의 ServiceId. tab 0개일 때 빈 문자열.</summary>
    private string SelectedServiceId =>
        (ServicesTabControl.SelectedItem is TabItem ti ? ti.Tag as string : null) ?? "";

    /// <summary>**D-S7-3c (s6-r31)** — 선택된 tab 의 service client. null = service 미설정 또는 holder skip.</summary>
    private LightHouseClient? SelectedClient
    {
        get
        {
            var sid = SelectedServiceId;
            return string.IsNullOrEmpty(sid) ? null : LightHouseClientHolder.GetClient(sid);
        }
    }

    /// <summary>**D-S7-3c (s6-r31)** — 선택된 tab 의 service 의 AttachmentIngestService.</summary>
    private AttachmentIngestService? SelectedIngest =>
        SelectedClient is { } c ? new AttachmentIngestService(c, Environment.UserName, _config) : null;

    /// <summary>
    /// SSE event handler — evt.ServiceId 로 source service 식별 후 해당 service 의 rows 만 refresh.
    /// </summary>
    private void OnSseEventReceived(ServerEventDto evt)
    {
        if (evt is null || string.IsNullOrEmpty(evt.Event)) return;
        if (evt.Event == ServerEventNames.Keepalive) return;
        var sourceServiceId = evt.ServiceId;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!IsLoaded) return;
            try
            {
                if (!string.IsNullOrEmpty(sourceServiceId))
                    await RefreshServiceAsync(sourceServiceId);
                else
                    await RefreshAsync();
            }
            catch (Exception ex) { StatusChip.Text = $"⚠ SSE refresh 결함 — {ex.Message}"; }
        }));
    }

    private void ServiceTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sid = SelectedServiceId;
        if (string.IsNullOrEmpty(sid))
        {
            CollectionsList.ItemsSource = null;
            return;
        }
        if (!_rowsByServiceId.TryGetValue(sid, out var rows))
        {
            rows = new ObservableCollection<CollectionRow>();
            _rowsByServiceId[sid] = rows;
        }
        CollectionsList.ItemsSource = rows;
    }

    // ── refresh / sync ────────────────────────────────────────────────────────

    /// <summary>**D-S7-3c (s6-r31)** — 모든 active service refresh. status chip 은 첫 결함 한 줄만 표시.</summary>
    private async Task RefreshAsync()
    {
        var active = _config.LightHouseServices.Where(s => s.Active).ToList();
        if (active.Count == 0)
        {
            StatusChip.Text = "⚠ LightHouse Service 미설정 — 설정 > LLM 탭에서 추가하세요.";
            UpdateOrphanButton();
            return;
        }
        StatusChip.Text = $"registry 조회 중… ({active.Count} services)";
        var failures = new List<string>();
        foreach (var svc in active)
        {
            try { await RefreshServiceAsync(svc.ServiceId); }
            catch (Exception ex) { failures.Add($"[{svc.DisplayName}] {ex.Message}"); }
        }
        StatusChip.Text = failures.Count == 0
            ? $"✅ 연결됨 — {active.Count} services"
            : $"⚠ {active.Count - failures.Count}/{active.Count} services 정상 / 결함: {failures[0]}";
        UpdateOrphanButton();
    }

    /// <summary>**D-S7-3c (s6-r31)** — 단일 service refresh. ListCollectionsAsync + per-service rows 갱신 + KbCollections sync.</summary>
    private async Task RefreshServiceAsync(string serviceId)
    {
        var client = LightHouseClientHolder.GetClient(serviceId);
        if (client is null) return;
        var resp = await client.ListCollectionsAsync().ConfigureAwait(true);
        if (!_rowsByServiceId.TryGetValue(serviceId, out var rows))
        {
            rows = new ObservableCollection<CollectionRow>();
            _rowsByServiceId[serviceId] = rows;
        }
        RebuildServiceRows(serviceId, rows, resp.Collections);
        ReconcileServiceLlmConfig(serviceId, resp.Collections);
        // 선택된 tab 이면 ListView 갱신 — ObservableCollection 변경은 자동 반영, ItemsSource 가 본 dict 인지 확인.
        if (SelectedServiceId == serviceId && CollectionsList.ItemsSource != rows)
        {
            CollectionsList.ItemsSource = rows;
        }
    }

    private void RebuildServiceRows(string serviceId, ObservableCollection<CollectionRow> rows, IEnumerable<CollectionInfo> serverRows)
    {
        rows.Clear();
        foreach (var info in serverRows)
        {
            var entry = _config.KbCollections.FirstOrDefault(k =>
                string.Equals(k.CollectionId, info.Id, StringComparison.OrdinalIgnoreCase)
                && k.ServiceId == serviceId);
            rows.Add(new CollectionRow
            {
                CollectionId = info.Id,
                DisplayName = info.DisplayName,
                Status = info.Status,
                FileCount = info.FileCount,
                IndexerVersion = info.IndexerVersion,
                Active = entry?.Active ?? false,
                ServiceId = serviceId,
            });
        }
    }

    /// <summary>**D-S7-3c (s6-r31)** — server registry 와 LlmConfig.KbCollections 의 per-service sync.</summary>
    private void ReconcileServiceLlmConfig(string serviceId, List<CollectionInfo> serverRows)
    {
        var serverIds = new HashSet<string>(serverRows.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var changed = false;

        var stale = _config.KbCollections
            .Where(k => k.ServiceId == serviceId && !serverIds.Contains(k.CollectionId))
            .ToList();
        if (stale.Count > 0)
        {
            foreach (var s in stale) _config.KbCollections.Remove(s);
            changed = true;
        }

        var localIds = new HashSet<string>(
            _config.KbCollections.Where(k => k.ServiceId == serviceId).Select(k => k.CollectionId),
            StringComparer.OrdinalIgnoreCase);
        foreach (var info in serverRows.Where(c => !localIds.Contains(c.Id)))
        {
            _config.KbCollections.Add(new KbCollectionEntry
            {
                CollectionId = info.Id,
                DisplayName = info.DisplayName,
                Active = false,
                ServiceId = serviceId,
            });
            changed = true;
        }

        if (changed)
        {
            _config.Save();
            ConfigChanged = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ── orphan cleanup (D-S7-3c, s6-r31) ──────────────────────────────────────

    /// <summary>orphan 탐지 — KbCollection.ServiceId 가 현재 active service 와 match 안 되는 entry 수. 0 이면 버튼 숨김.</summary>
    private void UpdateOrphanButton()
    {
        var orphanCount = CountOrphans();
        OrphanCleanupButton.Visibility = orphanCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (orphanCount > 0)
        {
            OrphanCleanupButton.Content = $"orphan 정리 ({orphanCount})";
        }
    }

    private int CountOrphans() => KbCollectionOrphanHelper.CountOrphans(_config);

    private void OrphanCleanup_Click(object sender, RoutedEventArgs e)
    {
        var orphans = KbCollectionOrphanHelper.FindOrphans(_config);
        if (orphans.Count == 0)
        {
            UpdateOrphanButton();
            return;
        }
        var preview = string.Join("\n", orphans.Take(10).Select(o => $"  • {o.DisplayName} ({o.CollectionId[..Math.Min(8, o.CollectionId.Length)]}…)"));
        var more = orphans.Count > 10 ? $"\n  …외 {orphans.Count - 10}건" : "";
        var consent = MessageBox.Show(this,
            $"다음 {orphans.Count} 건의 collection 의 소속 service 가 삭제되었습니다.\n" +
            "LlmConfig.KbCollections 에서 제거하시겠습니까? (server-side 의 collection 자체는 영향 없음)\n\n" +
            preview + more,
            "orphan collection 정리", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (consent != MessageBoxResult.Yes) return;

        foreach (var o in orphans) _config.KbCollections.Remove(o);
        _config.Save();
        ConfigChanged = true;
        var count = orphans.Count;
        StatusChip.Text = $"✅ orphan {count}건 정리 완료.";
        UpdateOrphanButton();
    }

    // ── active toggle ─────────────────────────────────────────────────────────

    private void ActiveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not CollectionRow row) return;
        var entry = _config.KbCollections.FirstOrDefault(k =>
            k.CollectionId == row.CollectionId && k.ServiceId == row.ServiceId);
        if (entry is null) return;
        if (entry.Active == row.Active) return;
        entry.Active = row.Active;
        _config.Save();
        ConfigChanged = true;
        StatusChip.Text = "active 토글 변경 — 현재 chat 은 영향 없음, 다음 chat 부터 반영.";
    }

    // ── 신규 등록 ─────────────────────────────────────────────────────────────

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Knowledge Base 등록할 폴더 선택",
            InitialDirectory = string.IsNullOrEmpty(NewFolderBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : NewFolderBox.Text,
        };
        if (picker.ShowDialog(this) == true)
        {
            NewFolderBox.Text = picker.FolderName;
            if (string.IsNullOrWhiteSpace(NewTitleBox.Text))
                NewTitleBox.Text = Path.GetFileName(picker.FolderName.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var sid = SelectedServiceId;
        var ingest = SelectedIngest;
        if (ingest is null || string.IsNullOrEmpty(sid))
        {
            MessageBox.Show(this, "LightHouse Service 미설정 — 설정 > LLM 탭에서 BaseUrl/PSK 입력 후 재시도.",
                "등록 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var folder = (NewFolderBox.Text ?? "").Trim();
        var title = (NewTitleBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(this, "유효한 폴더 경로 필요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show(this, "표시 이름 필수.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var svcDisplayName = ((TabItem?)ServicesTabControl.SelectedItem)?.Header?.ToString() ?? "(unnamed)";
        var consent = MessageBox.Show(this,
            $"이 collection 은 LightHouse Service [{svcDisplayName}] 의 모든 사용자가 검색 가능합니다 (T1 flat 정책).\n\n" +
            "비밀 문서 / 개인 정보 / 회사 기밀 등이 포함된 폴더는 등록하지 마십시오.\n\n" +
            $"폴더: {folder}\n이름: {title}\n\n계속 진행하시겠습니까?",
            "Knowledge Base 등록 동의 (T1 flat)",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        await RunIngestAsync(async (ct, progress) =>
        {
            var collectionId = await ingest.IngestAndUploadAsync(folder, title, progress, ct).ConfigureAwait(true);
            // 옵션 A (2026-05-30) — collectionId = sanitized 폴더명 (멱등). 같은 이름 재등록 시 server 가 기존 것을
            // overwrite 하므로, client config 도 기존 동일 (CollectionId, ServiceId) entry 제거 후 재추가 (중복 row 방지).
            _config.KbCollections.RemoveAll(k =>
                k.CollectionId == collectionId && k.ServiceId == sid);
            _config.KbCollections.Add(new KbCollectionEntry
            {
                CollectionId = collectionId,
                DisplayName = title,
                Active = true,
                ServiceId = sid,
                // Backlog A — 본 collection 의 local source root 박제. LlmChatViewModel.GetActiveCollectionSourceRoots
                // 가 본 값을 KbSpecializedDigestFetcher.FetchMany 입력으로 사용 (specialized digest cache breakpoint 3).
                SourceFolder = folder,
            });
            _config.Save();
            ConfigChanged = true;
            StatusChip.Text = $"✅ 등록 완료 — [{svcDisplayName}] {title} ({collectionId})";
            NewFolderBox.Text = "";
            NewTitleBox.Text = "";
            await RefreshServiceAsync(sid).ConfigureAwait(true);
        });
    }

    // ── 재업로드 / 제거 ───────────────────────────────────────────────────────

    private async void Reupload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CollectionRow row) return;
        var client = LightHouseClientHolder.GetClient(row.ServiceId);
        if (client is null) return;
        var ingest = new AttachmentIngestService(client, Environment.UserName, _config);
        var picker = new OpenFolderDialog
        {
            Title = $"\"{row.DisplayName}\" 의 새 폴더 선택 (payload swap)",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (picker.ShowDialog(this) != true) return;

        var consent = MessageBox.Show(this,
            $"\"{row.DisplayName}\" 의 payload 를 다음 폴더로 교체:\n  {picker.FolderName}\n\n계속하시겠습니까?",
            "재업로드 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (consent != MessageBoxResult.Yes) return;

        await RunIngestAsync(async (ct, progress) =>
        {
            await ingest.ReingestAndReuploadAsync(row.CollectionId, picker.FolderName, row.DisplayName, progress, ct)
                .ConfigureAwait(true);
            // Backlog A — payload swap 시 SourceFolder 도 새 폴더로 갱신. 다음 chat 의 specialized digest fetch 정합.
            var entry = _config.KbCollections.FirstOrDefault(k =>
                k.CollectionId == row.CollectionId && k.ServiceId == row.ServiceId);
            if (entry is not null)
            {
                entry.SourceFolder = picker.FolderName;
                _config.Save();
                ConfigChanged = true;
            }
            StatusChip.Text = $"✅ 재업로드 완료 — {row.DisplayName}";
            await RefreshServiceAsync(row.ServiceId).ConfigureAwait(true);
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CollectionRow row) return;
        var client = LightHouseClientHolder.GetClient(row.ServiceId);
        if (client is null) return;
        var consent = MessageBox.Show(this,
            $"\"{row.DisplayName}\" 를 server 에서 영구 제거합니다.\n등록 디렉토리 (`Collections\\<id>\\`) 가 디스크에서 purge 됩니다.\n\n계속하시겠습니까?",
            "Collection 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        try
        {
            RegisterButton.IsEnabled = false;
            await client.DeleteCollectionAsync(row.CollectionId).ConfigureAwait(true);
            _config.KbCollections.RemoveAll(k =>
                k.CollectionId == row.CollectionId && k.ServiceId == row.ServiceId);
            _config.Save();
            ConfigChanged = true;
            StatusChip.Text = $"✅ 제거 완료 — {row.DisplayName}";
            await RefreshServiceAsync(row.ServiceId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"제거 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RegisterButton.IsEnabled = true;
        }
    }

    // ── ingest 진행 ───────────────────────────────────────────────────────────

    private async Task RunIngestAsync(Func<CancellationToken, IProgress<IngestStageProgress>, Task> body)
    {
        _cts = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        RegisterButton.IsEnabled = false;
        ProgressBarMain.IsIndeterminate = true;
        ProgressBarMain.Value = 0;
        ProgressLabel.Text = "준비 중…";

        _tokensUsedAtIngestStart = _config.VisionCostGate.TokensUsedToday;

        var progress = new Progress<IngestStageProgress>(UpdateProgress);
        var task = body(_cts.Token, progress);
        _inflightIngestTask = task;
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusChip.Text = "⚠ 사용자가 취소함.";
        }
        catch (Exception ex)
        {
            StatusChip.Text = $"❌ 등록 실패 — {ex.Message}";
            MessageBox.Show(this, ex.ToString(), "등록 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // s6-r24 작업 2 (MJ1) — cost gate merge-save (delta 누적 + day rollover SSOT 보존).
            try
            {
                var deltaTokens = _config.VisionCostGate.TokensUsedToday - _tokensUsedAtIngestStart;
                var instanceLastReset = _config.VisionCostGate.LastResetUtc;
                LlmConfig.ModifyWithLock(latest =>
                {
                    if (string.CompareOrdinal(instanceLastReset, latest.VisionCostGate.LastResetUtc) > 0)
                    {
                        latest.VisionCostGate.LastResetUtc = instanceLastReset;
                        latest.VisionCostGate.TokensUsedToday = 0;
                    }
                    if (deltaTokens > 0)
                    {
                        latest.VisionCostGate.TokensUsedToday += deltaTokens;
                    }
                });
            }
            catch (Exception ex) { Log.Warn($"KbManagerDialog: cost gate merge-save 실패 (best-effort) — {ex.Message}"); }
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressBarMain.IsIndeterminate = false;
            RegisterButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
            _inflightIngestTask = null;
        }
    }

    private void UpdateProgress(IngestStageProgress p)
    {
        var stage = p.Stage switch
        {
            IngestStage.Copying => "복사",
            IngestStage.Indexing => "색인",
            IngestStage.Packaging => "패키징",
            IngestStage.Uploading => "업로드",
            IngestStage.Done => "완료",
            _ => p.Stage.ToString(),
        };
        if (p.Total > 0)
        {
            ProgressBarMain.IsIndeterminate = false;
            var newVal = (double)p.Completed / p.Total;
            if (newVal > ProgressBarMain.Value) ProgressBarMain.Value = newVal;
            ProgressLabel.Text = $"[{stage}] {p.Completed}/{p.Total}" + (p.CurrentItem is null ? "" : $" — {p.CurrentItem}");
        }
        else
        {
            ProgressBarMain.IsIndeterminate = true;
            ProgressLabel.Text = $"[{stage}] {p.CurrentItem ?? "..."}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = ConfigChanged;

    protected override async void OnClosed(EventArgs e)
    {
        LightHouseClientHolder.EventReceived -= OnSseEventReceived;

        _cts?.Cancel();
        if (_inflightIngestTask is { } t)
        {
            try { await t.ConfigureAwait(true); } catch { /* swallow */ }
        }
        base.OnClosed(e);
    }

    /// <summary>**D-S7-3c (s6-r31)** — ListView 의 한 행 + ServiceId (소속 service 식별).</summary>
    public sealed class CollectionRow
    {
        public string CollectionId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public int FileCount { get; set; }
        public string IndexerVersion { get; set; } = "";
        public bool Active { get; set; }
        public string ServiceId { get; set; } = "";
    }
}
