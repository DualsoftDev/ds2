using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.LlmAgent;

namespace Promaker.ViewModels;

/// <summary>
/// LLM Chat 첨부 (drag-drop / Ctrl+V) 인프라 — Phase 3a commit-4.
///
/// 본 partial 의 책임:
///   - <see cref="Attachments"/> ObservableCollection + chip add/remove
///   - drag-drop / paste 진입점에서 호출되는 <see cref="AddPathsAsync"/> + 이미지 bytes 진입점
///   - capability 비교 + size cap + turn cap 검증
///   - chip 영역 상단 1줄 안내 (<see cref="AttachmentNotice"/>) — 거부 / cap 초과 / provider 전환 단일화
///
/// 정책 4: filename chip 만 (썸네일 없음). 정책 6: turn 당 10개 cap.
/// 정책 18: STA sync = 경량 검증 (확장자 / 개수 / Length), Background <see cref="Task.Run"/> = bytes 로드 + 이미지 dim.
/// 정책 19: 분류는 F# <c>AttachmentClassifier</c> SSOT 만 사용.
///
/// commit-4 범위: 이미지 + 텍스트/코드. Phase 3b (rev 15): PDF 활성 — capability + size + 페이지 cap 검증.
/// 페이지 cap 검증은 background 단계 (PdfPig 파싱) — STA sync 는 size cap 만 (정책 18).
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>turn 당 첨부 개수 cap (정책 6). SSOT = F# <c>CapabilityPresets.DefaultMaxAttachmentCount</c>.
    /// review m1 — F# literal 을 가져옴 (값 변경 시 한 곳만 수정).</summary>
    public const int MaxAttachmentCount = Ds2.LlmAgent.CapabilityPresets.DefaultMaxAttachmentCount;

    /// <summary>텍스트 첨부 단일 파일 size cap (정책 6: 1MB). capability 별 분기 없음.</summary>
    private const long MaxTextBytes = 1024L * 1024L;

    /// <summary>PDF 미지원 provider (Codex/Ollama) 의 sync 단계 size cap fallback default — capability 의 MaxPdfBytes 가
    /// None 일 때 적용. 32MB = Anthropic native 한도와 동일. 큰 파일 로드 + PdfPig 파싱 비용 차단용 sync 가드.
    /// 추출된 텍스트의 1MB 가드 (deferred C-2 fallback D) 는 background <see cref="LoadAcceptedAttachments"/> 단계에서 별도 적용.</summary>
    private const long DefaultPdfSizeCap = 32L * 1024L * 1024L;

    /// <summary>chip 영역 상단 1줄 안내 (거부 / cap / provider 전환 등 단일화 — 정책 8/9/MI-5).</summary>
    [ObservableProperty]
    private string _attachmentNotice = "";

    /// <summary>현재 turn 의 첨부 chip 목록. <see cref="SendAsync"/> 의 ToArray snapshot 대상 (commit-6 race-free).</summary>
    public ObservableCollection<AttachmentChipVm> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// constructor 에서 한 번 호출 — Attachments.CollectionChanged hook 등록.
    /// HasAttachments 변동 시 PropertyChanged + SendCommand.CanExecute 재평가.
    /// </summary>
    private void HookAttachmentsCollection()
    {
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>
    /// 사용자 path 다중 진입점 (drag-drop / file paste). STA sync 단계에서 호출.
    /// 본 메서드 안에서 sync 검증 → background bytes 로드 → dispatcher chip 추가.
    ///
    /// 정책 18: 이미지 dim 측정은 <see cref="BitmapDecoder"/> metadata-only 경로 (full decode 회피).
    /// </summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return;

        var caps = _provider?.Capabilities ?? Capabilities.TextOnly;
        var notices = new List<string>();
        var accepted = new List<(string Path, Classification Cls, long Size)>();
        var remainingSlots = Math.Max(0, MaxAttachmentCount - Attachments.Count);

        foreach (var p in paths)
        {
            if (accepted.Count >= remainingSlots)
            {
                notices.Add($"첨부 cap {MaxAttachmentCount}개 초과 — 일부 파일 거부");
                break;
            }
            ClassifyPathSync(p, caps, accepted, notices);
        }

        if (notices.Count > 0)
            AttachmentNotice = string.Join(" / ", notices);
        else
            AttachmentNotice = "";

        if (accepted.Count == 0) return;

        var (loaded, loadNotices) = await Task.Run(() => LoadAcceptedAttachments(accepted, caps)).ConfigureAwait(true);

        // race 재검증 (review F2): fire-and-forget AddPathsAsync 가 겹치면 sync 단계의 remainingSlots 가 stale 상태에서
        // 양쪽 다 통과 → cap 초과 가능. dispatcher thread (single-threaded) 에서 add 직전 재검증.
        var capOverflow = false;
        foreach (var chip in loaded)
        {
            if (Attachments.Count >= MaxAttachmentCount)
            {
                capOverflow = true;
                break;
            }
            Attachments.Add(chip);
        }
        if (capOverflow)
            loadNotices.Add($"첨부 cap {MaxAttachmentCount}개 초과 — 일부 파일 거부");

        // background 단계 notice (페이지 cap 초과 / PDF 파싱 실패 등) 머지.
        if (loadNotices.Count > 0)
        {
            var merged = string.IsNullOrEmpty(AttachmentNotice)
                ? string.Join(" / ", loadNotices)
                : AttachmentNotice + " / " + string.Join(" / ", loadNotices);
            AttachmentNotice = merged;
        }
    }

    /// <summary>
    /// 클립보드 image (CF_PNG/CF_DIB) 진입점 — commit-5 의 AddPastingHandler 가 호출.
    /// 이미 PNG bytes 로 인코딩된 상태로 진입. dim 측정은 bytes stream 으로.
    /// rev 18 m3: 시그니처 mime: string → format: ImageFormat 일원화.
    /// </summary>
    public async Task AddImageBytesAsync(byte[] bytes, ImageFormat format, string suggestedName)
    {
        if (bytes == null || bytes.Length == 0) return;

        var caps = _provider?.Capabilities ?? Capabilities.TextOnly;
        if (caps.ImageFormats.IsEmpty)
        {
            AttachmentNotice = $"{suggestedName}: 현재 provider 가 이미지 미지원";
            return;
        }
        if (!caps.ImageFormats.Contains(format))
        {
            AttachmentNotice = $"{suggestedName}: 현재 provider 가 {ExtOf(format)} 미지원";
            return;
        }
        if (caps.MaxImageBytes != null && bytes.Length > caps.MaxImageBytes.Value)
        {
            AttachmentNotice = $"{suggestedName}: 이미지 크기 cap {caps.MaxImageBytes.Value / 1024 / 1024}MB 초과";
            return;
        }
        if (Attachments.Count >= MaxAttachmentCount)
        {
            AttachmentNotice = $"첨부 cap {MaxAttachmentCount}개 초과 — 추가 거부";
            return;
        }

        var chip = await Task.Run(() =>
        {
            var (w, h) = LlmImageDimReader.TryFromBytes(bytes);
            var tok = TokenEstimator.anthropicImageTokens(w, h, TokenEstimator.opus47ImageCap);
            var att = Attachment.NewImage(suggestedName, bytes, format);
            return new AttachmentChipVm(suggestedName, bytes.Length, tok.Item1, att);
        }).ConfigureAwait(true);

        // race 재검증 (review F2): paste / drop 동시 발생 시 cap 초과 방어.
        if (Attachments.Count >= MaxAttachmentCount)
        {
            AttachmentNotice = $"첨부 cap {MaxAttachmentCount}개 초과 — 추가 거부";
            return;
        }
        AttachmentNotice = "";
        Attachments.Add(chip);
    }

    /// <summary>chip × 버튼 — XAML 의 RelativeSource binding 으로 호출.</summary>
    [RelayCommand]
    private void RemoveAttachment(AttachmentChipVm? chip)
    {
        if (chip == null) return;
        Attachments.Remove(chip);
        AttachmentNotice = "";
    }

    /// <summary>
    /// commit-5 정책 9 / 3.4: provider 전환 후 호출. 새 provider 의 capability 와 현재 chip 비교 →
    /// 미지원 첨부 강제 제거 + 1줄 안내. <see cref="ConfigureProviderAsync"/> 의 IsReady=true 직후 진입.
    /// commit-6 m2: image format 별 세분 비교 (PNG 만 지원하는 provider 에서 JPEG chip 은 제거).
    /// rev 18 m3: chip 의 <see cref="Attachment"/> 에서 직접 ImageFormat 추출 (`AttachmentInfo.tryGetImageFormat`)
    /// — mime 문자열 역추론 helper (`ImageFormatFromMime`) 폐기.
    /// </summary>
    public void ReevaluateAttachmentsForProvider()
    {
        if (_provider == null) return;
        if (Attachments.Count == 0) return;
        var caps = _provider.Capabilities;
        var removed = new System.Collections.Generic.List<string>();
        for (int i = Attachments.Count - 1; i >= 0; i--)
        {
            var src = Attachments[i].Source;
            bool keep;
            // rev 20 (F3 외부 review): format/native 외 size cap 도 검사 — 10MB OpenAI 이미지 → Claude (5MB)
            // 전환 시 silent 통과 회귀 차단. F# `EnforceCapabilityOrFail` 의 strict throw 를 UI 가 사전 흡수.
            if (src.IsImage)
            {
                var fmtOpt = AttachmentInfo.tryGetImageFormat(src);
                if (fmtOpt == null || !caps.ImageFormats.Contains(fmtOpt.Value))
                {
                    keep = false;
                }
                else
                {
                    var imgOpt = AttachmentInfo.tryGetImage(src);
                    keep = imgOpt == null
                        || caps.MaxImageBytes == null
                        || imgOpt.Value.Bytes.LongLength <= caps.MaxImageBytes.Value;
                }
            }
            else if (src.IsPdf)
            {
                if (!caps.SupportsPdfNative)
                {
                    keep = false;
                }
                else
                {
                    var pdfOpt = AttachmentInfo.tryGetPdf(src);
                    keep = pdfOpt == null
                        || caps.MaxPdfBytes == null
                        || pdfOpt.Value.Bytes.LongLength <= caps.MaxPdfBytes.Value;
                }
            }
            else
            {
                // TextFile 은 inline 으로 wire — capability 무관.
                keep = true;
            }
            if (!keep)
            {
                removed.Add(Attachments[i].FileName);
                Attachments.RemoveAt(i);
            }
        }
        if (removed.Count > 0)
            AttachmentNotice = $"provider 변경 — 미지원/cap 초과 첨부 {removed.Count}개 제거 ({string.Join(", ", removed)})";
    }

    /// <summary>
    /// 단일 path 에 대한 sync 검증 — 확장자 / capability / size cap. 통과 시 <paramref name="accepted"/> 에 push,
    /// 거부 시 <paramref name="notices"/> 에 한 줄 추가. PDF 는 capability 통과해도 commit-4 단계 (Phase 3b 미구현)
    /// 라 거부.
    /// </summary>
    private static void ClassifyPathSync(
        string path,
        Capabilities caps,
        List<(string Path, Classification Cls, long Size)> accepted,
        List<string> notices)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            notices.Add($"{name}: 파일 없음");
            return;
        }

        var cls = AttachmentClassifier.classify(path);
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception ex) { notices.Add($"{name}: 크기 조회 실패 — {ex.Message}"); return; }

        if (cls is Classification.AcceptImage img)
        {
            if (caps.ImageFormats.IsEmpty)
            {
                notices.Add($"{name}: 현재 provider 가 이미지 미지원");
                return;
            }
            if (!caps.ImageFormats.Contains(img.Item))
            {
                notices.Add($"{name}: 현재 provider 가 {ExtOf(img.Item)} 미지원");
                return;
            }
            if (caps.MaxImageBytes != null && size > caps.MaxImageBytes.Value)
            {
                notices.Add($"{name}: 이미지 크기 cap {caps.MaxImageBytes.Value / 1024 / 1024}MB 초과");
                return;
            }
            accepted.Add((path, cls, size));
            return;
        }

        if (cls.IsAcceptText)
        {
            if (size > MaxTextBytes)
            {
                notices.Add($"{name}: 텍스트 1MB 초과");
                return;
            }
            accepted.Add((path, cls, size));
            return;
        }

        if (cls.IsAcceptPdf)
        {
            // Phase 3b (rev 15): capability + size cap 검증. 페이지 cap 검증은 background (PdfPig 파싱).
            // deferred C-2 fallback D (rev 17): native 미지원 provider (Codex/Ollama) 도 거부 대신 background 에서
            // 텍스트 추출 → TextFile chip 변환. sync 단계는 size cap 만 적용 — native 면 caps.MaxPdfBytes,
            // 미지원이면 DefaultPdfSizeCap (32MB) fallback. 양쪽 다 큰 파일의 PdfPig 파싱 비용 차단용.
            var pdfSizeCap = caps.SupportsPdfNative
                ? (caps.MaxPdfBytes != null ? caps.MaxPdfBytes.Value : DefaultPdfSizeCap)
                : DefaultPdfSizeCap;
            if (size > pdfSizeCap)
            {
                notices.Add($"{name}: PDF 크기 cap {pdfSizeCap / 1024 / 1024}MB 초과");
                return;
            }
            accepted.Add((path, cls, size));
            return;
        }

        if (cls.IsRejectExtension)
        {
            // F# DU named field C# interop 회피 — 확장자 재추출이 단순.
            var ext = Path.GetExtension(path).ToLowerInvariant();
            notices.Add($"{name}: 거부된 확장자 {ext}");
            return;
        }

        // RejectUnknown
        notices.Add($"{name}: 지원하지 않는 형식");
    }

    /// <summary>
    /// background thread 진입 — accepted 목록의 bytes 로드 + 이미지 dim + PDF 페이지 수 + 토큰 추정.
    /// 단일 파일 실패는 swallow + log (다른 파일 chip 추가 계속).
    /// 반환 = (chips, notices) — 페이지 cap 초과 / PDF 파싱 실패는 notices 에 1줄 추가 + chip skip.
    /// </summary>
    private static (List<AttachmentChipVm> chips, List<string> notices) LoadAcceptedAttachments(
        List<(string Path, Classification Cls, long Size)> accepted,
        Capabilities caps)
    {
        var chips = new List<AttachmentChipVm>(accepted.Count);
        var bgNotices = new List<string>();  // sync 단계 caller 의 notices 와 lifecycle 분리 — review M-1
        foreach (var (path, cls, size) in accepted)
        {
            var name = Path.GetFileName(path);
            try
            {
                if (cls is Classification.AcceptImage img)
                {
                    var bytes = File.ReadAllBytes(path);
                    var (w, h) = LlmImageDimReader.TryFromPath(path);
                    var tok = TokenEstimator.anthropicImageTokens(w, h, TokenEstimator.opus47ImageCap);
                    // rev 18 m3: ImageFormat 직접 보유 — mime 변환은 wire 시점 (Attachment.mimeOf SSOT).
                    var att = Attachment.NewImage(name, bytes, img.Item);
                    chips.Add(new AttachmentChipVm(name, size, tok.Item1, att));
                }
                else if (cls.IsAcceptText)
                {
                    var raw = File.ReadAllBytes(path);
                    var det = AttachmentClassifier.detectEncoding(raw);
                    var content = det.Encoding.GetString(raw);
                    var ratio = TokenEstimator.estimateKoreanRatio(content);
                    var tok = TokenEstimator.textTokens(raw.Length, ratio);
                    var att = Attachment.NewTextFile(name, content);
                    chips.Add(new AttachmentChipVm(name, size, tok, att));
                }
                else if (cls.IsAcceptPdf)
                {
                    var bytes = File.ReadAllBytes(path);
                    UglyToad.PdfPig.PdfDocument doc;
                    try { doc = UglyToad.PdfPig.PdfDocument.Open(bytes); }
                    catch (Exception ex)
                    {
                        Log.Warn($"PDF 파싱 실패: {path}", ex);
                        bgNotices.Add($"{name}: PDF 파싱 실패 ({ex.GetType().Name})");
                        continue;
                    }
                    using (doc)
                    {
                        var pages = doc.NumberOfPages;
                        if (caps.SupportsPdfNative)
                        {
                            // native wire: PDF bytes 그대로 chip 에 보유 → 송신 시 multipart content block.
                            if (caps.MaxPdfPages != null && pages > caps.MaxPdfPages.Value)
                            {
                                bgNotices.Add($"{name}: PDF 페이지 cap {caps.MaxPdfPages.Value}p 초과 ({pages}p)");
                                continue;
                            }
                            var (_, tokHigh) = TokenEstimator.pdfTokensRange(pages);
                            var att = Attachment.NewPdf(name, bytes);
                            chips.Add(new AttachmentChipVm(name, size, tokHigh, att));
                        }
                        else
                        {
                            // deferred C-2 fallback D (rev 17): native 미지원 provider (Codex/Ollama) — PdfPig 로 텍스트
                            // 추출 후 TextFile chip 으로 변환. 1MB 가드 (MaxTextBytes) 로 토큰 폭증 차단.
                            // chip FileName 은 원본 PDF 이름 유지 → 사용자 인지 + bgNotice 1줄 안내로 변환 사실 노출.
                            var text = ExtractPdfText(doc);
                            var byteLen = Encoding.UTF8.GetByteCount(text);
                            if (byteLen > MaxTextBytes)
                            {
                                bgNotices.Add($"{name}: PDF 추출 텍스트 {byteLen / 1024 / 1024}MB 가 {MaxTextBytes / 1024 / 1024}MB 초과 — 거부");
                                continue;
                            }
                            var ratio = TokenEstimator.estimateKoreanRatio(text);
                            var tok = TokenEstimator.textTokens(byteLen, ratio);
                            var att = Attachment.NewTextFile(name, text);
                            chips.Add(new AttachmentChipVm(name, size, tok, att));
                            bgNotices.Add($"{name}: PDF 미지원 → 텍스트 {pages}페이지 추출됨");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // log4net 에 기록 — main partial 의 Log 재사용. 단일 파일 실패는 계속.
                // UX 보강 (rev 16): user-visible 안내 1줄 — PDF inner catch 와 일관 패턴.
                Log.Warn($"첨부 로드 실패: {path}", ex);
                bgNotices.Add($"{name}: 로드 실패 ({ex.GetType().Name})");
            }
        }
        return (chips, bgNotices);
    }

    /// <summary>
    /// deferred C-2 fallback D — PdfPig 로 페이지별 텍스트 추출 후 concat. 페이지 사이 빈 줄 1개로 구분.
    /// PdfPig 가 PDF 의 vector text 스트림을 추출 (OCR 아님 — 스캔 PDF 는 빈 결과).
    /// </summary>
    private static string ExtractPdfText(UglyToad.PdfPig.PdfDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // 이미지 dim metadata-only 읽기는 LlmImageDimReader.TryFromPath / TryFromBytes 로 분리.

    /// <summary>review m10 — 사용자 표시용 확장자 (.jpg 등). ToString() 의 "Jpeg" 보다 친숙.
    /// rev 18 review 후속: F# <c>Attachment.extOf</c> SSOT 위임 — 종전 IsPng/IsJpeg/IsGif/IsWebp 4 case
    /// 분기 폐기. F# pattern match exhaustiveness 활용 (신규 case 추가 시 컴파일러 강제).
    /// type/module 동명 (`Attachment`) 회피로 module IL 노출명은 <c>AttachmentModule</c>.</summary>
    private static string ExtOf(ImageFormat fmt) => Ds2.LlmAgent.AttachmentModule.extOf(fmt);
}

/// <summary>
/// chip 표시용 VM. F# <see cref="Attachment"/> 인스턴스 매핑 보유 — <see cref="LlmChatViewModel.SendAsync"/>
/// 가 commit-6 race-free snapshot 시 본 instance 의 <see cref="Source"/> 만 추출해 wire.
///
/// 정책 4: filename + 크기 + 추정 token + ×제거. 썸네일 없음.
/// </summary>
public sealed class AttachmentChipVm
{
    public string FileName { get; }
    public long ByteSize { get; }
    public long EstimatedTokens { get; }
    public Attachment Source { get; }
    public string SizeLabel => FormatSize(ByteSize);
    public string TokensLabel => EstimatedTokens > 0 ? $"≈{EstimatedTokens}t" : "";

    public AttachmentChipVm(string fileName, long byteSize, long estimatedTokens, Attachment source)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ByteSize = byteSize;
        EstimatedTokens = estimatedTokens;
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024.0:0.#}KB";
        return $"{bytes / 1024.0 / 1024.0:0.##}MB";
    }
}
