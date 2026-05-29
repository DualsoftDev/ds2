using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 CCTV API.
/// Blazor /cctv 가 쓰던 AppSettingsService(카메라 설정) + CctvMediaMtxService(동기화 상태) 를 얇게 래핑.
/// 영상 자체는 MediaMTX 의 WHEP 엔드포인트(:8889 등)로 브라우저가 직접 연결 — /js/cctv-whep.js 그대로 재사용.
/// MediaMTX 경로명(SanitizeName)을 서버에서 계산해 내려보내므로 클라이언트는 그대로 video id 에 사용한다.
/// </summary>
[ApiController]
[Route("api/cctv")]
public class CctvController : ControllerBase
{
    /// <summary>FHD 모니터 디테일 식별 가능한 최소 분할 = 6대(3×2). Blazor Cctv 와 동일.</summary>
    private const int MaxConcurrentCameras = 6;

    private readonly AppSettingsService _settings;
    private readonly CctvMediaMtxService _mediaMtx;

    public CctvController(AppSettingsService settings, CctvMediaMtxService mediaMtx)
    {
        _settings = settings;
        _mediaMtx = mediaMtx;
    }

    /// <summary>카메라 목록(활성·유효만, 최대 6대) + WebRTC 포트.</summary>
    [HttpGet("config")]
    public ActionResult<CctvConfigDto> GetConfig()
    {
        var cctv = _settings.LoadSettings().Cctv;
        var enabled = cctv.Cameras
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.RtspUrl))
            .ToList();
        var shown = enabled.Take(MaxConcurrentCameras)
            .Select(c => new CctvCameraDto(c.Name, CctvMediaMtxService.SanitizeName(c.Name)))
            .ToList();
        var hidden = Math.Max(0, enabled.Count - shown.Count);
        return new CctvConfigDto(cctv.WebRtcPort, shown, shown.Count, hidden, enabled.Count);
    }

    /// <summary>마지막 MediaMTX 동기화 상태.</summary>
    [HttpGet("status")]
    public ActionResult<CctvStatusDto> GetStatus()
        => new CctvStatusDto(_mediaMtx.LastSyncOk, _mediaMtx.LastSyncMessage);

    /// <summary>카메라 설정을 MediaMTX 에 즉시 재동기화. (antiforgery 미적용 — 평범한 JSON POST)</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<CctvStatusDto>> Resync(CancellationToken ct)
    {
        await _mediaMtx.SyncAsync(ct);
        return new CctvStatusDto(_mediaMtx.LastSyncOk, _mediaMtx.LastSyncMessage);
    }
}

public record CctvConfigDto(int WebRtcPort, List<CctvCameraDto> Cameras, int ShownCount, int HiddenCount, int TotalCount);
public record CctvCameraDto(string Name, string Id);
public record CctvStatusDto(bool Ok, string Message);
