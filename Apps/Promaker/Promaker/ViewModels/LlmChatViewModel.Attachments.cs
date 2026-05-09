using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// commit-4 범위: 이미지 + 텍스트/코드 까지. PDF 는 capability 통과해도 Phase 3b 미구현 안내 후 거부 (chip 미생성).
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>turn 당 첨부 개수 cap (정책 6).</summary>
    public const int MaxAttachmentCount = 10;

    /// <summary>텍스트 첨부 단일 파일 size cap (정책 6: 1MB). capability 별 분기 없음.</summary>
    private const long MaxTextBytes = 1024L * 1024L;

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

        var loaded = await Task.Run(() => LoadAcceptedAttachments(accepted)).ConfigureAwait(true);

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
        {
            var prev = string.IsNullOrEmpty(AttachmentNotice) ? "" : AttachmentNotice + " / ";
            AttachmentNotice = $"{prev}첨부 cap {MaxAttachmentCount}개 초과 — 일부 파일 거부";
        }
    }

    /// <summary>
    /// 클립보드 image (CF_PNG/CF_DIB) 진입점 — commit-5 의 AddPastingHandler 가 호출.
    /// 이미 PNG bytes 로 인코딩된 상태로 진입. dim 측정은 bytes stream 으로.
    /// </summary>
    public async Task AddImageBytesAsync(byte[] bytes, string mime, string suggestedName)
    {
        if (bytes == null || bytes.Length == 0) return;

        var caps = _provider?.Capabilities ?? Capabilities.TextOnly;
        if (caps.ImageFormats.IsEmpty)
        {
            AttachmentNotice = $"{suggestedName}: 현재 provider 가 이미지 미지원";
            return;
        }
        if (!caps.ImageFormats.Contains(ImageFormat.Png))
        {
            AttachmentNotice = $"{suggestedName}: 현재 provider 가 PNG 미지원";
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
            var (w, h) = TryReadImageDimFromBytes(bytes);
            var tok = TokenEstimator.anthropicImageTokens(w, h, TokenEstimator.opus47ImageCap);
            var att = Attachment.NewImage(suggestedName, bytes, mime);
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
                notices.Add($"{name}: 현재 provider 가 {img.Item} 미지원");
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
            // commit-4 단계: PDF 는 chip 단계에서 차단. Phase 3b 진입 시 이 분기 제거.
            notices.Add($"{name}: PDF 는 Phase 3b 에서 지원 예정");
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
    /// background thread 진입 — accepted 목록의 bytes 로드 + 이미지 dim + 토큰 추정.
    /// 단일 파일 실패는 swallow + log (다른 파일 chip 추가 계속).
    /// </summary>
    private static List<AttachmentChipVm> LoadAcceptedAttachments(
        List<(string Path, Classification Cls, long Size)> accepted)
    {
        var chips = new List<AttachmentChipVm>(accepted.Count);
        foreach (var (path, cls, size) in accepted)
        {
            var name = Path.GetFileName(path);
            try
            {
                if (cls is Classification.AcceptImage img)
                {
                    var bytes = File.ReadAllBytes(path);
                    var (w, h) = TryReadImageDimFromPath(path);
                    var tok = TokenEstimator.anthropicImageTokens(w, h, TokenEstimator.opus47ImageCap);
                    var mime = MimeOf(img.Item);
                    var att = Attachment.NewImage(name, bytes, mime);
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
            }
            catch (Exception ex)
            {
                // log4net 에 기록 — main partial 의 Log 재사용. 단일 파일 실패는 계속.
                Log.Warn($"첨부 로드 실패: {path}", ex);
            }
        }
        return chips;
    }

    private static (int W, int H) TryReadImageDimFromPath(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadImageDim(fs);
        }
        catch { return (0, 0); }
    }

    private static (int W, int H) TryReadImageDimFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return ReadImageDim(ms);
        }
        catch { return (0, 0); }
    }

    private static (int W, int H) ReadImageDim(Stream stream)
    {
        // metadata-only: BitmapCreateOptions.IgnoreColorProfile + BitmapCacheOption.None.
        var dec = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var f = dec.Frames.FirstOrDefault();
        return f is null ? (0, 0) : (f.PixelWidth, f.PixelHeight);
    }

    private static string MimeOf(ImageFormat fmt)
    {
        // F# DU 는 C# enum 이 아니라 sealed class + static instances → switch 상수 불가.
        // `IsPng` / `IsJpeg` / ... 자동 생성 boolean property 로 분기.
        if (fmt.IsPng) return "image/png";
        if (fmt.IsJpeg) return "image/jpeg";
        if (fmt.IsGif) return "image/gif";
        if (fmt.IsWebp) return "image/webp";
        return "application/octet-stream";
    }
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
