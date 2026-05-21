using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LightHouse;
using Ds2.LightHouse.Extractors;
using log4net;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Promaker.LlmAgent;
// F# 도구 — IExtractor list (ListModule.OfArray), IngestProgress -> unit (FuncConvert.FromAction),
// IngestProgress.CurrentFile : Option<string> (FSharpOption<string>.get_IsSome).

namespace Promaker.Knowledge;

/// <summary>
/// 사용자 폴더 → staging dir 사본 → in-process <see cref="Indexer"/> → <see cref="CollectionPackager"/> →
/// <see cref="LightHouseClient"/> 의 upload orchestration (done-lighthouse-kb-server.md §4.2 Phase S5 / §3.3 / §3.8).
///
/// **lifetime per ingest**: 본 service 의 한 호출 = 한 collection 등록. staging dir / 임시 zip 은 호출 종료 시
/// finally 에서 cleanup. `CancellationToken` 통과 — 사용자가 KbManagerDialog 의 cancel 버튼 누르면 즉시 중단.
///
/// 진행률 reporting: <see cref="IngestStage"/> + 단계별 metric 을 <see cref="IProgress{T}"/> 로 통합. KbManagerDialog
/// 의 ProgressBar 가 본 model 만 watch.
/// </summary>
public sealed class AttachmentIngestService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AttachmentIngestService));

    private readonly LightHouseClient _client;
    private readonly string _userIdentity;
    private readonly LlmConfig _llmConfig;

    /// <summary>
    /// process singleton HttpClient — VLM caption 호출용. Indexer 의 captionGen closure 가 capture.
    /// per-request HttpClient 생성은 socket exhaustion risk (Phase 2 task D-iii s6-r20 박제 정합).
    /// </summary>
    private static readonly HttpClient _vlmHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// s6-r20 (D-iii): LlmConfig 주입 — captionGen build 의 VLM provider / API key / model 결정 SSOT.
    /// caller (KbManagerDialog) 가 매 ingest 시점 생성. cost gate hard cutoff 처리 (E-i) 도 본 단원 책임.
    /// </summary>
    public AttachmentIngestService(LightHouseClient client, string userIdentity, LlmConfig llmConfig)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _userIdentity = string.IsNullOrWhiteSpace(userIdentity)
            ? throw new ArgumentException("userIdentity 필수.", nameof(userIdentity))
            : userIdentity;
        _llmConfig = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
    }

    /// <summary>
    /// 신규 collection 등록 — `sourceFolder` 색인 + upload. 반환 = server 가 발급한 collection guid (D3).
    /// </summary>
    public async Task<string> IngestAndUploadAsync(
        string sourceFolder,
        string title,
        IProgress<IngestStageProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) throw new ArgumentException("sourceFolder 필수.", nameof(sourceFolder));
        if (!Directory.Exists(sourceFolder)) throw new DirectoryNotFoundException($"폴더 없음 — {sourceFolder}");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title 필수.", nameof(title));

        var session = PrepareStaging(sourceFolder, progress, ct);
        try
        {
            await RunIndexerAsync(session.StagingDir, progress, ct).ConfigureAwait(false);
            using var zipStream = PackagePayload(session, title, sourceFolder, progress, ct);
            return await UploadAsync(zipStream, title, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            CleanupSession(session);
        }
    }

    /// <summary>
    /// 기존 collection 의 payload swap (D5 / `POST /collections/{id}/payload` or `/uploads-rs/{id}/finalize` swap path).
    /// title 변경은 본 호출과 무관 — caller (KbManagerDialog) 가 displayName 변경 시 별도 endpoint 필요 시 추후 추가.
    /// <para/>
    /// **D-S7-5 phase 4 (s6-r74 b1)** — zip size 가 `LightHouseClient.ResumableUploadThresholdBytes` (256 MiB) 초과 시
    /// chunked path 자동 진입 (`UploadCollectionResumableAsync` 에 `swapTargetCollectionId` 박제). 작은 zip 은 기존
    /// multipart single-shot 유지 (회귀 0).
    /// </summary>
    public async Task ReingestAndReuploadAsync(
        string collectionId,
        string sourceFolder,
        string title,
        IProgress<IngestStageProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(collectionId)) throw new ArgumentException("collectionId 필수.", nameof(collectionId));
        if (string.IsNullOrWhiteSpace(sourceFolder)) throw new ArgumentException("sourceFolder 필수.", nameof(sourceFolder));
        if (!Directory.Exists(sourceFolder)) throw new DirectoryNotFoundException($"폴더 없음 — {sourceFolder}");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title 필수.", nameof(title));

        var session = PrepareStaging(sourceFolder, progress, ct);
        try
        {
            await RunIndexerAsync(session.StagingDir, progress, ct).ConfigureAwait(false);
            using var zipStream = PackagePayload(session, title, sourceFolder, progress, ct);

            var totalBytes = zipStream.Length;
            if (totalBytes > LightHouseClient.ResumableUploadThresholdBytes)
            {
                Log.Info($"ReingestAndReuploadAsync: zip size {totalBytes:N0} bytes > threshold {LightHouseClient.ResumableUploadThresholdBytes:N0} → chunked swap path 자동 진입 (target={collectionId})");
                var totalKib = (int)(totalBytes / 1024L);
                progress?.Report(new IngestStageProgress(IngestStage.Uploading, 0, totalKib, $"chunked 재업로드 시작 → {collectionId}"));
                var chunkedProgress = progress is null
                    ? null
                    : new Progress<(long sent, long total)>(p =>
                    {
                        var sentKib = (int)(p.sent / 1024L);
                        var pct = p.sent * 100L / Math.Max(1L, p.total);
                        progress.Report(new IngestStageProgress(IngestStage.Uploading, sentKib, totalKib, $"chunked → {collectionId} ({pct}%)"));
                    });
                var swappedId = await _client.UploadCollectionResumableAsync(
                    title, zipStream,
                    progress: chunkedProgress,
                    swapTargetCollectionId: collectionId,
                    ct: ct).ConfigureAwait(false);
                progress?.Report(new IngestStageProgress(IngestStage.Done, totalKib, totalKib, $"재업로드 완료 (chunked) — {swappedId}"));
            }
            else
            {
                progress?.Report(new IngestStageProgress(IngestStage.Uploading, 0, 1, $"재업로드 → {collectionId}"));
                await _client.ReuploadCollectionPayloadAsync(collectionId, zipStream, ct).ConfigureAwait(false);
                progress?.Report(new IngestStageProgress(IngestStage.Done, 1, 1, $"재업로드 완료 — {collectionId}"));
            }
        }
        finally
        {
            CleanupSession(session);
        }
    }

    // ── stage helpers ──────────────────────────────────────────────────────────

    private static StagingSession PrepareStaging(string sourceFolder, IProgress<IngestStageProgress>? progress, CancellationToken ct)
    {
        var session = new StagingSession();
        try
        {
            Directory.CreateDirectory(session.StagingDir);
            var dstSource = Path.Combine(session.StagingDir, CollectionPackager.SourceFolderName);
            Directory.CreateDirectory(dstSource);
            CopyTree(sourceFolder, dstSource, progress, ct);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Indexer.ingest 는 F# 동기 함수 — UI freeze 회피 위해 본 service 가 `Task.Run` wrap (review M4).
    /// 본 책임을 caller (KbManagerDialog) 가 매번 wrap 하면 누락 회귀 위험 → SSOT 측 단일 진입점.
    /// <para/>
    /// **Phase 4 P4-C.2 (s6-r38)** — embedder 주입 분기. caller (`_llmConfig` 의 active LightHouseService 의
    /// EmbeddingProviderConfig) 가 Enabled=true 시 OllamaEmbedder 생성 후 색인 시점 주입. lifecycle = 본
    /// RunIndexerAsync 의 한 호출 안 (사용자 결정 정합 — per-ingest, service singleton 아님). try/finally 로
    /// HttpClient 자원 회수. backend 검증 (daemon up / 모델 미설치) 은 색인 첫 chunk 호출 시점 lazy fail-fast.
    /// </summary>
    private Task RunIndexerAsync(string stagingDir, IProgress<IngestStageProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new IngestStageProgress(IngestStage.Indexing, 0, 0, "색인 준비 중"));

        return Task.Run(() =>
        {
            var extractors = ListModule.OfArray(new IExtractor[]
            {
                new TextExtractor(),
                new PdfExtractor(),
                new OoxmlExtractor(),
            });

            var lastReportedFile = -1;
            Action<IngestProgress> onProgress = p =>
            {
                // F# IngestProgress 의 CurrentFile 은 FSharpOption<string> — None / Some 분기.
                string? currentFile = FSharpOption<string>.get_IsSome(p.CurrentFile)
                    ? Path.GetFileName(p.CurrentFile.Value)
                    : null;
                if (p.CompletedFiles != lastReportedFile)
                {
                    lastReportedFile = p.CompletedFiles;
                    progress?.Report(new IngestStageProgress(
                        IngestStage.Indexing, p.CompletedFiles, p.TotalFiles, currentFile));
                }
            };
            var fsProgress = FuncConvert.FromAction(onProgress);

            // s6-r19: captionGen 인자 추가 (D-2-2 eager). s6-r20 (D-iii): noop → BuildCaptionGen 실 치환.
            // VLM 비활성 (provider="none" / API key 미박제) 시 자동 noop fallback — Phase 1 회귀 0.
            var captionGen = BuildCaptionGen(ct);

            // Phase 4 P4-C.2 (s6-r38): active LightHouseService 의 EmbeddingProviderConfig 기반 embedder 생성.
            // Enabled=false / config null / active service 없음 → None (BM25-only fallback).
            IEmbeddingProvider? embedder = TryCreateEmbedder();
            var embedderOpt = embedder is null
                ? Microsoft.FSharp.Core.FSharpOption<IEmbeddingProvider>.None
                : Microsoft.FSharp.Core.FSharpOption<IEmbeddingProvider>.Some(embedder);
            try
            {
                var results = Indexer.ingest(stagingDir, extractors, captionGen, embedderOpt, fsProgress, ct);
                Log.Info($"AttachmentIngestService: 색인 완료 — files={results.Length} staging={stagingDir} embedder={embedder?.GetType().Name ?? "None"}");
            }
            finally
            {
                embedder?.Dispose();
            }
        }, ct);
    }

    /// <summary>
    /// **Phase 4 P4-C.2 (s6-r38)** — active LightHouseService 의 EmbeddingProviderConfig 기반 OllamaEmbedder 생성.
    /// <para/>
    /// 반환 null = BM25-only fallback (caller 가 FSharpOption.None 박제). 조건:
    /// <list type="bullet">
    ///   <item>active LightHouseService 가 없음 (모든 service Active=false 또는 0건)</item>
    ///   <item>active service 의 Embedding config 가 null</item>
    ///   <item>Embedding.Enabled = false</item>
    ///   <item>BaseUrl / Model / Dimension validation 실패 (warn log + null 반환, 색인 자체 abort 없음)</item>
    /// </list>
    /// </summary>
    private IEmbeddingProvider? TryCreateEmbedder()
    {
        var activeService = _llmConfig.LightHouseServices.FirstOrDefault(s => s.Active);
        var cfg = activeService?.Embedding;
        if (cfg is null || !cfg.Enabled)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model) || cfg.Dimension <= 0)
        {
            Log.Warn($"AttachmentIngestService: EmbeddingProviderConfig validation 실패 (BaseUrl='{cfg.BaseUrl}' Model='{cfg.Model}' Dim={cfg.Dimension}) — BM25-only fallback");
            return null;
        }
        try
        {
            return new Ds2.LightHouse.Ollama.OllamaEmbedder(cfg.BaseUrl, cfg.Model, cfg.Dimension);
        }
        catch (Exception ex)
        {
            Log.Warn($"AttachmentIngestService: OllamaEmbedder 생성 실패 — {ex.GetType().Name}: {ex.Message}. BM25-only fallback");
            return null;
        }
    }

    /// <summary>
    /// s6-r20 (D-iii / E-i): LlmConfig 의 VLM 설정 → Indexer.ingestImagesIntoStore 에 전달할 captionGen closure.
    ///
    /// 정책 (D-2-1 / D-2-2 / D-2-5 / E-i MR4):
    /// - VLM 비활성 (`IsVlmEnabled() = false`) → CaptionGenerator.noop 그대로
    /// - cost gate hard cap 도달 (`VisionCostGate.CanAfford(300) = false`) → SkippedCaption "cost gate"
    /// - 정상 경로 → `CaptionGenerator.callAnthropic` 호출 + 성공 시 `VisionCostGate.Consume(300)`
    /// - per-image fail-safe (D-2-4) → callAnthropic 의 FailedCaption 반환 그대로 (NULL 유지)
    ///
    /// 사후 save: cost gate 상태 변동 시 caller 가 LlmConfig.Save 책임. 본 closure 가 매 image 마다 save 면
    /// disk write 증폭 → caller 가 RunIndexerAsync 종료 시점 1회 save. (cost gate snapshot 다중 process race 는
    /// 본 정책 의도 외 — single Promaker instance 가정.)
    /// </summary>
    private FSharpFunc<byte[], FSharpFunc<ImageFormat, CaptionResult>> BuildCaptionGen(CancellationToken ct)
        => BuildCaptionGenStatic(_llmConfig, _vlmHttp, ct);

    /// <summary>
    /// **s6-r23 묶음 4** — `BuildCaptionGen` 의 static 형태 (instance fields `_llmConfig` / `_vlmHttp` 를 method arg 로 노출).
    /// Promaker.Tests 가 mock <see cref="HttpClient"/> + 임의 <see cref="LlmConfig"/> 박제로 cost gate hard cap /
    /// disabled / FailedCaption / Captioned 분기 회귀 차단 fact 박제 가능.
    /// <para>policy SSOT (s6-r20 M2 정합)</para>
    /// <list type="bullet">
    ///   <item>VLM disabled → <see cref="CaptionGenerator.noop"/></item>
    ///   <item>cost gate hard cap → <c>SkippedCaption("cost gate hard cap")</c> (no http call)</item>
    ///   <item>callAnthropic 의 Captioned → <c>VisionCostGate.Consume(perImageTokens)</c></item>
    ///   <item>callAnthropic 의 FailedCaption / SkippedCaption → cost 0 (정책 의도)</item>
    /// </list>
    /// </summary>
    internal static FSharpFunc<byte[], FSharpFunc<ImageFormat, CaptionResult>> BuildCaptionGenStatic(
        LlmConfig llmConfig, HttpClient http, CancellationToken ct)
    {
        if (!llmConfig.IsVlmEnabled())
        {
            return CaptionGenerator.noop;
        }
        // **B18 (s6-r90, 15-reviewer Major)** — apiKey/model 매 호출 재조회. 이전 박제는 closure 캡쳐 (process lifetime
        // 평문 잔존) — Indexer 의 long-running 색인 (~수시간) 동안 GC 회수 불가능. 매 호출 LlmConfig 조회 = 색인 cost
        // (~100ms+ Anthropic API) 대비 무시 가능 + Indexer 진행 중 사용자가 LlmConfig 갱신 (예: API key rotation) 시 즉시 반영.
        if (string.IsNullOrWhiteSpace(llmConfig.GetVlmApiKey()) || string.IsNullOrWhiteSpace(llmConfig.VlmModel))
        {
            return CaptionGenerator.noop;
        }
        var gate = llmConfig.VisionCostGate;
        var perImageTokens = VisionCostGate.AverageTokensPerImage;
        return FuncConvert.FromFunc<byte[], ImageFormat, CaptionResult>(
            (Func<byte[], ImageFormat, CaptionResult>)((bytes, fmt) =>
            {
                if (!gate.CanAfford(perImageTokens))
                {
                    return CaptionResult.NewSkippedCaption("cost gate hard cap");
                }
                // B18: 매 호출 재조회 (closure 캡쳐 회피).
                var apiKey = llmConfig.GetVlmApiKey();
                var model = llmConfig.VlmModel;
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
                {
                    return CaptionResult.NewSkippedCaption("vlm credential 결함 (mid-run)");
                }
                var result = CaptionGenerator.callAnthropic(http, apiKey, model, bytes, fmt, ct);
                // 자가 검열 M2 정책 SSOT (s6-r20): Captioned 분기만 Consume. FailedCaption / SkippedCaption 시 cost 0.
                // Anthropic 측 실 차감과의 drift 가능성 인지함 (4xx/5xx 가 token billing 되는 경우 등) — 정책상
                // "응답 미수신 / 사용 불가" 결과에 사용자 cost 부담 면제 의도. 정밀 회계는 Phase 4 (cost gate v2) backlog.
                if (result is CaptionResult.Captioned)
                {
                    gate.Consume(perImageTokens);
                }
                return result;
            }));
    }

    private FileStream PackagePayload(
        StagingSession session,
        string title,
        string sourceFolder,
        IProgress<IngestStageProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new IngestStageProgress(IngestStage.Packaging, 0, 1, "zip 패키징 중"));
        var inputs = new PackagerInputs
        {
            Title = title,
            IndexerVersion = Ds2.LightHouse.IndexerVersion.Current,
            SourcePathHint = sourceFolder,
            ClientHost = Environment.MachineName,
            ClientUser = _userIdentity,
        };
        var stream = CollectionPackager.Package(session.StagingDir, session.ZipPath, inputs, ct);
        progress?.Report(new IngestStageProgress(IngestStage.Packaging, 1, 1, "zip 패키징 완료"));
        return stream;
    }

    /// <summary>
    /// **D-S7-5 phase 3 (s6-r67)** — zip size 가 임계값 (`LightHouseClient.ResumableUploadThresholdBytes`, 256 MiB) 초과 시
    /// chunked path 자동 진입. 작은 zip 은 기존 single-shot multipart 유지 (회귀 0).
    /// <para/>
    /// chunked path 의 progress 는 byte 단위 — IngestStageProgress 의 (current, total) 로 매핑하여 KbManagerDialog ProgressBar
    /// 가 동일 model watch. chunkSize default (4 MiB) 그대로 사용 — server-side `maxUploadBytes` 정합 의무.
    /// <para/>
    /// swap path (`ReingestAndReuploadAsync` → `POST /collections/{id}/payload`) 는 phase 3 대상 아님 (D-S7-5 phase 2 가
    /// 신규 등록만 지원). 기존 큰 zip 의 swap chunked 진입은 별 phase (backlog).
    /// </summary>
    private async Task<string> UploadAsync(
        FileStream zipStream,
        string title,
        IProgress<IngestStageProgress>? progress,
        CancellationToken ct)
    {
        var totalBytes = zipStream.Length;
        if (totalBytes > LightHouseClient.ResumableUploadThresholdBytes)
        {
            Log.Info($"UploadAsync: zip size {totalBytes:N0} bytes > threshold {LightHouseClient.ResumableUploadThresholdBytes:N0} → chunked path 자동 진입");
            // IngestStageProgress 의 Completed/Total 이 int — byte 단위 (10 GB 한도) 가 int.MaxValue (~2 GiB) 초과 가능
            // → KiB 단위로 cast 후 박제 (int 안전, ~2 TiB 까지 표현). UI 가 % 비율만 사용하므로 단위 변환 의미 동일.
            var totalKib = (int)(totalBytes / 1024L);
            progress?.Report(new IngestStageProgress(IngestStage.Uploading, 0, totalKib, $"chunked upload 시작 → {title}"));
            var chunkedProgress = progress is null
                ? null
                : new Progress<(long sent, long total)>(p =>
                {
                    var sentKib = (int)(p.sent / 1024L);
                    var pct = p.sent * 100L / Math.Max(1L, p.total);
                    progress.Report(new IngestStageProgress(IngestStage.Uploading, sentKib, totalKib, $"chunked → {title} ({pct}%)"));
                });
            var chunkedId = await _client.UploadCollectionResumableAsync(
                title, zipStream, progress: chunkedProgress, ct: ct).ConfigureAwait(false);
            progress?.Report(new IngestStageProgress(IngestStage.Done, totalKib, totalKib, $"등록 완료 (chunked) — {chunkedId}"));
            return chunkedId;
        }
        progress?.Report(new IngestStageProgress(IngestStage.Uploading, 0, 1, $"upload → {title}"));
        var collectionId = await _client.UploadCollectionAsync(title, zipStream, ct).ConfigureAwait(false);
        progress?.Report(new IngestStageProgress(IngestStage.Done, 1, 1, $"등록 완료 — {collectionId}"));
        return collectionId;
    }

    private static void CleanupSession(StagingSession session) => session.Dispose();

    private static void CopyTree(string src, string dst, IProgress<IngestStageProgress>? progress, CancellationToken ct)
    {
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
        var srcRoot = src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(srcRoot, files[i]);
            var dstPath = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
            File.Copy(files[i], dstPath, overwrite: true);
            progress?.Report(new IngestStageProgress(IngestStage.Copying, i + 1, files.Length, Path.GetFileName(files[i])));
        }
    }

    /// <summary>임시 staging dir + zip 의 lifetime owner. ingest 호출당 1개 생성.
    /// <para/>
    /// **B6 (M12 보안 sweep, s6-r71+)** — staging dir 안 사용자 원본 평문 보관. `%TEMP%` 의 default ACL 은
    /// user-specific 이지만 동일 machine 의 다른 user / admin 이 접근 가능 → 사내 IT 환경 (관리자 권한 공유) 에서
    /// 평문 leak risk. 본 fix 는 dir 생성 직후 explicit DirectorySecurity 박제 — current user FullControl + inherit
    /// 명시, 다른 user / Authenticated Users 의 default ACL inheritance 차단.
    /// </summary>
    private sealed class StagingSession : IDisposable
    {
        public string Root { get; }
        public string StagingDir => Path.Combine(Root, "staging");
        public string ZipPath => Path.Combine(Root, "payload.zip");

        public StagingSession()
        {
            Root = Path.Combine(Path.GetTempPath(), "promaker-kb-" + Guid.NewGuid().ToString("N"));
            // M12 — explicit current-user-only ACL 박제. Windows 한정 (System.Security.AccessControl).
            // 다른 OS 환경에서는 try/catch 로 silent fail (Promaker 자체가 WPF 라 Windows 한정 = 본 fix 도 Windows 가정).
            var info = Directory.CreateDirectory(Root);
            TryApplyOwnerOnlyAcl(info);
        }

        private static void TryApplyOwnerOnlyAcl(DirectoryInfo dir)
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return;
                var security = new System.Security.AccessControl.DirectorySecurity();
                // Protect ACL — 부모 inheritance 차단 (Authenticated Users default 막음).
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (currentUser is not null)
                {
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        currentUser,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                        System.Security.AccessControl.PropagationFlags.None,
                        System.Security.AccessControl.AccessControlType.Allow));
                }
                dir.SetAccessControl(security);
            }
            catch (Exception ex)
            {
                // best-effort — ACL 박제 실패 시도 staging 자체는 계속 동작 (default %TEMP% ACL fallback).
                Log.Warn($"StagingSession ACL 박제 실패 (best-effort) — {dir.FullName}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"StagingSession cleanup 실패 (best-effort) — {Root}: {ex.Message}");
            }
        }
    }
}

/// <summary>ingest pipeline 의 단계 (KbManagerDialog 의 진행률 UI label).</summary>
public enum IngestStage
{
    Copying,
    Indexing,
    Packaging,
    Uploading,
    Done,
}

/// <summary>
/// IProgress payload — UI thread 가 ProgressBar + label 갱신에 사용. CurrentItem 은 단계별 의미 다름
/// (Copying/Indexing = 파일명, Packaging = "zip 패키징 중", Uploading = collection id 또는 title).
/// </summary>
public sealed record IngestStageProgress(
    IngestStage Stage,
    int Completed,
    int Total,
    string? CurrentItem);
