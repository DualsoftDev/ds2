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
using Promaker.Knowledge;
using Promaker.LlmAgent;

namespace Promaker.Dialogs;

/// <summary>
/// LightHouse KB collection 관리 — folder picker + 색인+upload + active 토글 + 제거 + 재업로드
/// (todo-lighthouse-kb-server.md §4.2 Phase S5 / §3.6 consent / §3.8 active set / §3.9 API surface).
///
/// 진입: LLM Chat 패널 의 "📚 KB 관리" 버튼 (Phase S5b).
///
/// 책임:
/// - server `GET /collections` 호출 + `LlmConfig.KbCollections` 와 sync (Q1 lazy reject driven)
/// - 신규 folder 등록 시 **consent dialog 의무** (§6 m2 SSOT — multi-tenant T1 flat PII 위험 안내)
/// - Active 토글은 즉시 LlmConfig.Save (server 무영향, 다음 chat 부터 반영 — chip 안내 §3.8 L1)
/// - cancel button → CancellationToken trigger → ingest 중단 + staging cleanup (AttachmentIngestService 책임)
/// </summary>
public partial class KbManagerDialog : Window
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(KbManagerDialog));

    private readonly LlmConfig _config;
    private readonly ObservableCollection<CollectionRow> _rows = new();
    private CancellationTokenSource? _cts;
    /// <summary>review M2 — in-flight ingest task. OnClosed 가 await 후 종료 (s5c 변경: client dispose 는 holder 책임).</summary>
    private Task? _inflightIngestTask;

    /// <summary>다이얼로그 닫힐 때 호출자에게 LlmConfig.KbCollections 변경 알림 (active 토글 / 등록 / 제거).</summary>
    public bool ConfigChanged { get; private set; }

    public KbManagerDialog()
    {
        InitializeComponent();
        _config = LlmConfig.Load();

        // Phase S5c — LightHouseClient 는 process singleton (LightHouseClientHolder SSOT, §3.8 L2-2 정합).
        // **review s5c M1**: client/ingest 를 필드로 잡지 않고 매 사용 시점 holder.Current 재조회 —
        // Settings dialog 가 BaseUrl/PSK 변경 시 holder.Invalidate 호출 후 본 다이얼로그가 stale instance 를
        // 잡고 있으면 ObjectDisposedException. modal 가정에 의존하지 않는 1차 안전망.
        LightHouseClientHolder.EnsureCreated(_config);
        if (LightHouseClientHolder.Current is null)
        {
            StatusChip.Text = "⚠ LightHouse Service 미설정 — 설정 > LLM 탭에서 BaseUrl + PSK 입력 후 \"연결 테스트\" 통과 필요.";
        }

        CollectionsList.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>
    /// 매 사용 시점 holder.Current 재조회 (review s5c M1). null = LightHouseService 미설정 또는 Invalidate 후.
    /// caller 가 null 분기 (chip 안내) 처리. holder.EnsureCreated 도 안전 — config 변경 시 자동 재생성.
    /// </summary>
    private LightHouseClient? CurrentClient => LightHouseClientHolder.EnsureCreated(_config);

    /// <summary>
    /// AttachmentIngestService 도 매 호출 시점 신규 생성 (cost 미미 — HttpClient 는 holder 공유).
    /// review s5c M1 정합 — caller 의 stale 참조 제거.
    /// </summary>
    private AttachmentIngestService? CurrentIngest =>
        CurrentClient is { } c ? new AttachmentIngestService(c, Environment.UserName, _config) : null;

    // ── refresh / sync ────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        var client = CurrentClient;
        if (client is null) return;
        StatusChip.Text = "registry 조회 중…";
        try
        {
            var resp = await client.ListCollectionsAsync().ConfigureAwait(true);
            StatusChip.Text = $"✅ 연결됨 — server registry {resp.Collections.Count}건";
            RebuildRows(resp.Collections);
            ReconcileLlmConfig(resp.Collections);
        }
        catch (LightHouseAuthException)
        {
            StatusChip.Text = "❌ 인증 실패 — PSK 확인 필요. 설정 > LLM 탭의 \"연결 테스트\" 로 검증.";
        }
        catch (Exception ex)
        {
            StatusChip.Text = $"❌ registry 조회 실패 — {ex.Message}";
        }
    }

    private void RebuildRows(IEnumerable<CollectionInfo> serverRows)
    {
        _rows.Clear();
        foreach (var info in serverRows)
        {
            var active = _config.KbCollections.FirstOrDefault(k =>
                string.Equals(k.CollectionId, info.Id, StringComparison.OrdinalIgnoreCase))?.Active ?? false;
            _rows.Add(new CollectionRow
            {
                CollectionId = info.Id,
                DisplayName = info.DisplayName,
                Status = info.Status,
                FileCount = info.FileCount,
                IndexerVersion = info.IndexerVersion,
                Active = active,
            });
        }
    }

    /// <summary>
    /// server registry 와 LlmConfig.KbCollections 양방향 sync (Q1 / Q4):
    /// - server 에 없는 entry 는 폐기 (server 가 영구 제거)
    /// - server 의 새 entry 는 LlmConfig 에 추가 (DisplayName / Active=false 로)
    /// </summary>
    private void ReconcileLlmConfig(List<CollectionInfo> serverRows)
    {
        var serverIds = new HashSet<string>(serverRows.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var changed = false;

        var stale = _config.KbCollections.Where(k => !serverIds.Contains(k.CollectionId)).ToList();
        if (stale.Count > 0)
        {
            foreach (var s in stale) _config.KbCollections.Remove(s);
            changed = true;
        }

        var localIds = new HashSet<string>(_config.KbCollections.Select(k => k.CollectionId), StringComparer.OrdinalIgnoreCase);
        foreach (var info in serverRows.Where(c => !localIds.Contains(c.Id)))
        {
            _config.KbCollections.Add(new KbCollectionEntry
            {
                CollectionId = info.Id,
                DisplayName = info.DisplayName,
                Active = false,
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

    // ── active toggle ─────────────────────────────────────────────────────────

    private void ActiveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not CollectionRow row) return;
        var entry = _config.KbCollections.FirstOrDefault(k => k.CollectionId == row.CollectionId);
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
        // review M3 (s5b) — `_client` 단일 기준 null check.
        // review s5c M1 — holder.Current 매 사용 시점 재조회.
        var ingest = CurrentIngest;
        if (ingest is null)
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

        // §6 m2 SSOT — multi-tenant T1 flat 의 PII 위험 안내. 매 등록마다 의무.
        var consent = MessageBox.Show(this,
            "이 collection 은 LightHouse Service 의 모든 사용자가 검색 가능합니다 (T1 flat 정책).\n\n" +
            "비밀 문서 / 개인 정보 / 회사 기밀 등이 포함된 폴더는 등록하지 마십시오.\n\n" +
            $"폴더: {folder}\n이름: {title}\n\n계속 진행하시겠습니까?",
            "Knowledge Base 등록 동의 (T1 flat)",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        await RunIngestAsync(async (ct, progress) =>
        {
            var collectionId = await ingest.IngestAndUploadAsync(folder, title, progress, ct).ConfigureAwait(true);
            _config.KbCollections.Add(new KbCollectionEntry
            {
                CollectionId = collectionId,
                DisplayName = title,
                Active = true,
            });
            _config.Save();
            ConfigChanged = true;
            StatusChip.Text = $"✅ 등록 완료 — {title} ({collectionId})";
            NewFolderBox.Text = "";
            NewTitleBox.Text = "";
            await RefreshAsync().ConfigureAwait(true);
        });
    }

    // ── 재업로드 / 제거 ───────────────────────────────────────────────────────

    private async void Reupload_Click(object sender, RoutedEventArgs e)
    {
        var ingest = CurrentIngest;
        if (ingest is null || sender is not Button btn || btn.Tag is not CollectionRow row) return;
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
            StatusChip.Text = $"✅ 재업로드 완료 — {row.DisplayName}";
            await RefreshAsync().ConfigureAwait(true);
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var client = CurrentClient;
        if (client is null || sender is not Button btn || btn.Tag is not CollectionRow row) return;
        var consent = MessageBox.Show(this,
            $"\"{row.DisplayName}\" 를 server 에서 영구 제거합니다.\n등록 디렉토리 (`Collections\\<id>\\`) 가 디스크에서 purge 됩니다.\n\n계속하시겠습니까?",
            "Collection 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        try
        {
            RegisterButton.IsEnabled = false;
            await client.DeleteCollectionAsync(row.CollectionId).ConfigureAwait(true);
            _config.KbCollections.RemoveAll(k => k.CollectionId == row.CollectionId);
            _config.Save();
            ConfigChanged = true;
            StatusChip.Text = $"✅ 제거 완료 — {row.DisplayName}";
            await RefreshAsync().ConfigureAwait(true);
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

    /// <summary>
    /// ingest 실행 wrapper — Progress reporter / Cancel / progress bar 표시 + finally cleanup.
    /// caller (Register_Click / Reupload_Click) 는 본문만 작성.
    /// </summary>
    private async Task RunIngestAsync(Func<CancellationToken, IProgress<IngestStageProgress>, Task> body)
    {
        _cts = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        RegisterButton.IsEnabled = false;
        // review M1 — Total 변동 (Indexer walk 후 결정) 중 bar 후퇴 회피.
        ProgressBarMain.IsIndeterminate = true;
        ProgressBarMain.Value = 0;
        ProgressLabel.Text = "준비 중…";

        var progress = new Progress<IngestStageProgress>(UpdateProgress);
        // review M2 — _inflightIngestTask 박제로 OnClosed 가 await 가능. caller (Register/Reupload_Click) 는
        // body 의 await 결과를 그대로 받음.
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
            // s6-r20 (E-i): captionGen 의 VisionCostGate.Consume 누적이 disk 에 반영되도록 1회 save.
            // 자가 검열 m3 정합 — Debug.WriteLine → log4net Log.Warn.
            //
            // **--review M4 정합 (s6-r21) — cap 덮어쓰기 1 시나리오만 해결**:
            //   ApplicationSettingsDialog 가 동시에 열려 cost gate cap (DailyTokenCap) 을 변경 후 저장한 경우,
            //   stale snapshot 덮어쓰기 차단. disk 의 최신 LlmConfig 를 reload 한 후 cost gate 의 *누적 필드*
            //   (TokensUsedToday / LastResetUtc) 만 본 인스턴스 값으로 덮어쓰고 Save.
            //
            // **잔여 race (자가 검열 s6-r21 MJ1)** — `Load` → modify → `Save` 사이가 `_saveLock` 로 보호 안 됨
            //   (`_saveLock` 는 `Save()` 의 file write 만 직렬화). 두 인스턴스가 거의 동시에 Load 하면
            //   `TokensUsedToday` 누적분 중 일부 손실. 본질 해결 = file lock 또는 LlmConfig 전체 transaction —
            //   별 turn backlog (MJ1).
            //
            // **MJ2 정합** — `LastResetUtc` 는 두 값 중 더 최근 UTC date (사전순 max) 채택. 본 인스턴스가
            // 오늘 자정 전 시작 + disk 가 자정 후 reload 된 시나리오에서 어제 값으로 덮어쓰기 회귀 차단.
            try
            {
                var latest = LlmConfig.Load();
                latest.VisionCostGate.TokensUsedToday = _config.VisionCostGate.TokensUsedToday;
                latest.VisionCostGate.LastResetUtc =
                    string.CompareOrdinal(latest.VisionCostGate.LastResetUtc, _config.VisionCostGate.LastResetUtc) >= 0
                        ? latest.VisionCostGate.LastResetUtc
                        : _config.VisionCostGate.LastResetUtc;
                latest.Save();
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
            // review M1 — Total 보고 시점부터 determinate 전환. 이전 stage 의 잔여 후퇴 회피 위해 Max 누적.
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
        // review M2 (s5b-r0) — cancel 신호 → in-flight ingest 종료까지 await. _client 는 본 dialog 가 소유 안 함
        // (Phase S5c 변경 — LightHouseClientHolder 가 process singleton).
        _cts?.Cancel();
        if (_inflightIngestTask is { } t)
        {
            try { await t.ConfigureAwait(true); } catch { /* swallow — body 의 try/catch 가 이미 처리 */ }
        }
        base.OnClosed(e);
    }

    /// <summary>ListView 의 한 행 — server CollectionInfo + LlmConfig 의 Active 플래그 join.</summary>
    public sealed class CollectionRow
    {
        public string CollectionId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public int FileCount { get; set; }
        public string IndexerVersion { get; set; } = "";
        public bool Active { get; set; }
    }
}
