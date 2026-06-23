// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text.RegularExpressions;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// CCTV 대체(폴백) 이미지 영속 서비스. 싱글톤.
///
/// CCTV 연결 실패·주소 없음·대기(standby) 시 대시보드/영상벽이 라이브 영상 대신 표시할
/// 정지 이미지를 카메라 slug 기준으로 <c>%WebRoot%/uploads/cctv-fallbacks/{slug}.{ext}</c> 에 저장한다.
/// (CctvOverlayService 와 동일하게 plc.db 가 아닌 uploads 에 두어 DB 재구축 시 유실 방지 — doc/20 §1.)
/// 저장 파일은 정적 파일 서버(/uploads)가 그대로 서빙하므로 클라이언트는 반환된 URL 을 그대로 쓴다.
///
/// 카메라-이미지 연결(<see cref="CctvCamera.FallbackImage"/>)의 영속은 설정 저장(POST /api/cctv/settings)
/// 라운드트립으로 처리하고, 이 서비스는 파일 입출력만 담당한다(이중 저장/경합 회피).
/// </summary>
public partial class CctvFallbackImageService
{
    private const long MaxBytes = 8 * 1024 * 1024;   // 8MB — 정지 한 컷이면 충분, 과대 업로드 차단
    private const string UrlPrefix = "/uploads/cctv-fallbacks/";

    private readonly string _dir;
    private readonly ILogger<CctvFallbackImageService> _logger;
    private readonly object _gate = new();

    public CctvFallbackImageService(IWebHostEnvironment env, ILogger<CctvFallbackImageService> logger)
    {
        _logger = logger;
        // WebRootPath: Release 직접 실행 시 null 일 수 있어 ContentRoot 로 폴백 (Program.cs uploads 처리와 동일).
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _dir = Path.Combine(webRoot, "uploads", "cctv-fallbacks");
        Directory.CreateDirectory(_dir);
    }

    // slug = MediaMTX 경로명(ASCII 영숫자/-/_). 파일명에 그대로 쓰므로 경로 탈출 방지용으로 엄격 검증.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SlugRegex();

    /// <summary>
    /// data URL(base64) 을 디코드해 {slug}.{ext} 로 저장하고 서빙 URL(+버전 쿼리)을 반환.
    /// 같은 slug 의 다른 확장자 파일은 먼저 제거해 유령 파일을 남기지 않는다.
    /// </summary>
    public string Save(string slug, string dataUrl)
    {
        if (!SlugRegex().IsMatch(slug ?? ""))
            throw new ArgumentException("유효하지 않은 카메라 식별자입니다.", nameof(slug));
        var (bytes, ext) = DecodeDataUrl(dataUrl);
        if (bytes.Length == 0) throw new ArgumentException("이미지 데이터가 비어 있습니다.");
        if (bytes.Length > MaxBytes) throw new ArgumentException("이미지가 너무 큽니다(최대 8MB).");

        lock (_gate)
        {
            DeleteFilesFor(slug!);
            var path = Path.Combine(_dir, slug + "." + ext);
            File.WriteAllBytes(path, bytes);
            // 캐시 무력화용 버전(파일 수정시각 ticks) — 같은 파일명을 덮어써도 브라우저가 새 이미지를 받게 한다.
            var v = new FileInfo(path).LastWriteTimeUtc.Ticks;
            return $"{UrlPrefix}{slug}.{ext}?v={v}";
        }
    }

    /// <summary>해당 slug 의 대체 이미지 파일을 모두 삭제.</summary>
    public void Delete(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        lock (_gate) DeleteFilesFor(slug);
    }

    /// <summary>
    /// 현재 카메라들이 참조하는 파일만 남기고 나머지(개명/삭제/저장하지 않고 포기한 캡쳐)는 정리.
    /// 설정 저장 직후 호출 — slug 기준이 아니라 실제 참조 파일명 기준이라 안전.
    /// </summary>
    public void Prune(IEnumerable<CctvCamera> cameras)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cameras ?? [])
        {
            var name = FileNameFromUrl(c.FallbackImage);
            if (name is not null) keep.Add(name);
        }
        lock (_gate)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_dir))
                    if (!keep.Contains(Path.GetFileName(f)))
                        TryDelete(f);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CCTV 대체 이미지 prune 실패"); }
        }
    }

    private void DeleteFilesFor(string slug)
    {
        foreach (var ext in new[] { "jpg", "png", "webp" })
            TryDelete(Path.Combine(_dir, slug + "." + ext));
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "CCTV 대체 이미지 삭제 실패: {Path}", path); }
    }

    /// <summary>"/uploads/cctv-fallbacks/cam1.jpg?v=123" → "cam1.jpg". 쿼리 제거 후 파일명만.</summary>
    private static string? FileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var q = url.IndexOf('?');
        if (q >= 0) url = url[..q];
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 ? url[(slash + 1)..] : url;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>data:image/jpeg;base64,XXXX → (bytes, "jpg"). jpeg/png/webp 만 허용.</summary>
    private static (byte[] bytes, string ext) DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return ([], "jpg");
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
            throw new ArgumentException("이미지 데이터 형식(data URL)이 아닙니다.");
        var header = dataUrl[5..comma];          // 예: image/jpeg;base64
        var b64 = dataUrl[(comma + 1)..];
        var mime = header.Split(';')[0].Trim().ToLowerInvariant();
        var ext = mime switch
        {
            "image/jpeg" or "image/jpg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => throw new ArgumentException("지원하지 않는 이미지 형식입니다(jpeg/png/webp).")
        };
        return (Convert.FromBase64String(b64), ext);
    }
}
