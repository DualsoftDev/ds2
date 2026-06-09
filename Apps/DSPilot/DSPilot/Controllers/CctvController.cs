// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Cctv;
using DSPilot.Models;
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
    private readonly CctvOverlayService _overlays;
    private readonly PlcToCallMapperService _callMapper;
    private readonly DspDbService _dspDb;
    private readonly DsProjectService _project;

    public CctvController(
        AppSettingsService settings,
        CctvMediaMtxService mediaMtx,
        CctvOverlayService overlays,
        PlcToCallMapperService callMapper,
        DspDbService dspDb,
        DsProjectService project)
    {
        _settings = settings;
        _mediaMtx = mediaMtx;
        _overlays = overlays;
        _callMapper = callMapper;
        _dspDb = dspDb;
        _project = project;
    }

    /// <summary>
    /// 등록된(활성·유효) 카메라 전체 목록 + WebRTC 포트 + 동시표시 상한.
    /// 영상벽 컴포저(좌측 레일 = 전체 카메라 드래그 소스 / 우측 그리드 = 동시표시)를 위해 전체를 내려보내고,
    /// 동시 WHEP 스트림 수는 클라이언트가 <see cref="MaxConcurrentCameras"/> 로 제한한다(FHD 디테일 보존).
    /// </summary>
    [HttpGet("config")]
    public ActionResult<CctvConfigDto> GetConfig()
    {
        var cctv = _settings.LoadSettings().Cctv;
        // 경로명(slug)을 전체 목록에서 먼저 결정(중복회피·안정) → 필터. SyncAsync 와 동일 규칙이라 WHEP path 일치.
        CctvMediaMtxService.AssignSlugs(cctv.Cameras);
        var cameras = cctv.Cameras
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Slug) && !string.IsNullOrWhiteSpace(c.RtspUrl))
            .Select(c => new CctvCameraDto(c.Name, c.Slug))
            .ToList();
        return new CctvConfigDto(cctv.WebRtcPort, cameras, cameras.Count, MaxConcurrentCameras);
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

    // ──────────────────────────── 카메라 설정 편집 (RTSP 추가/편집은 이 페이지에서) ────────────────────────────

    /// <summary>
    /// 카메라 설정 편집용 전체 스냅샷 (RtspUrl·Enabled 포함) + MediaMTX 동기화 상태.
    /// <see cref="GetConfig"/> 는 영상벽용 정제 목록(이름+경로)만 주므로 편집은 별도로 내려보낸다.
    /// (RTSP 카메라 추가/편집을 Settings 페이지에서 CCTV 페이지로 이관 — doc/20 사용자 요구.)
    /// </summary>
    [HttpGet("settings")]
    public ActionResult<CctvDto> GetSettings()
    {
        var cctv = _settings.LoadSettings().Cctv;
        // 편집기에 현재 경로명(slug)을 함께 내려보내 왕복(round-trip)시킨다 — 표시명을 바꿔도 경로가 안정 유지된다.
        CctvMediaMtxService.AssignSlugs(cctv.Cameras);
        return new CctvDto(
            cctv.MediaMtxApiUrl,
            cctv.WebRtcPort,
            cctv.Cameras.Select(c => new CameraDto(c.Name, c.RtspUrl, c.Enabled, c.Slug)).ToList(),
            _mediaMtx.LastSyncOk,
            _mediaMtx.LastSyncMessage,
            cctv.WebRtcAdditionalHosts);
    }

    /// <summary>
    /// 카메라 설정 저장 + MediaMTX 즉시 동기화 (Settings 페이지의 CCTV 파트를 이관).
    /// 단일 카메라 개명(rename)만 오버레이 FK 이전 — 추가/삭제/순서변경과 구분, 모호하면 보존만 하고 미적용
    /// (좌표 유실 방지, doc/20 §3·§10.3). antiforgery 미적용 — 평범한 JSON POST.
    /// </summary>
    [HttpPost("settings")]
    public async Task<ActionResult<CctvDto>> SaveSettings([FromBody] CctvSettingsSaveDto req, CancellationToken ct)
    {
        var m = _settings.LoadSettings();

        var oldCameraNames = m.Cctv.Cameras
            .Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        m.Cctv.MediaMtxApiUrl = string.IsNullOrWhiteSpace(req.MediaMtxApiUrl) ? m.Cctv.MediaMtxApiUrl : req.MediaMtxApiUrl;
        if (req.WebRtcPort > 0) m.Cctv.WebRtcPort = req.WebRtcPort;
        // 공인주소: null 이면 미변경, 빈 문자열이면 LAN 전용으로 명시적 해제. (Trim 으로 입력 공백 정리)
        if (req.WebRtcAdditionalHosts is not null) m.Cctv.WebRtcAdditionalHosts = req.WebRtcAdditionalHosts.Trim();
        // 경로명(slug)은 클라이언트가 보내지 않으므로 기존 저장값을 포지션 기준으로 이어받는다.
        // 순서 변경 없이 이름만 바꿔도 경로가 안정 유지된다(MediaMTX 재등록·오버레이 흔들림 방지).
        var prevSlugs = m.Cctv.Cameras.Select(c => c.Slug).ToList();
        m.Cctv.Cameras = (req.Cameras ?? new List<CameraDto>())
            .Select((c, i) => new CctvCamera
            {
                Name = c.Name ?? "",
                Slug = i < prevSlugs.Count ? prevSlugs[i] : "",   // 기존 slug 이어받기; 초과분은 신규 → 빈값
                RtspUrl = c.RtspUrl ?? "",
                Enabled = c.Enabled
            })
            .ToList();
        // 빈 slug(신규 카메라)에만 cam1/cam2/… 부여; 기존 slug 는 그대로.
        CctvMediaMtxService.AssignSlugs(m.Cctv.Cameras);

        _settings.SaveSettings(m);

        // 단일 카메라 개명 감지 → 오버레이 FK 이전(삭제 아님).
        var newCameraNames = m.Cctv.Cameras
            .Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var removed = oldCameraNames.Except(newCameraNames, StringComparer.OrdinalIgnoreCase).ToList();
        var added = newCameraNames.Except(oldCameraNames, StringComparer.OrdinalIgnoreCase).ToList();
        if (removed.Count == 1 && added.Count == 1)
        {
            try { _overlays.RenameCamera(removed[0], added[0]); }
            catch { /* 오버레이 FK 이전 실패는 비치명 — 저장 자체는 성공 처리 */ }
        }

        // 저장한 카메라 목록을 MediaMTX 에 동기화 (실패해도 저장 자체는 성공).
        await _mediaMtx.SyncAsync(ct);

        return new CctvDto(
            m.Cctv.MediaMtxApiUrl,
            m.Cctv.WebRtcPort,
            m.Cctv.Cameras.Select(c => new CameraDto(c.Name, c.RtspUrl, c.Enabled, c.Slug)).ToList(),
            _mediaMtx.LastSyncOk,
            _mediaMtx.LastSyncMessage,
            m.Cctv.WebRtcAdditionalHosts);
    }

    // ──────────────────────────── 설비 오버레이 (P4) ────────────────────────────

    /// <summary>카메라별 오버레이 목록. camera 미지정 시 전체.</summary>
    [HttpGet("overlays")]
    public ActionResult<List<CctvOverlayDto>> GetOverlays([FromQuery] string? camera)
    {
        var list = string.IsNullOrWhiteSpace(camera) ? _overlays.GetAll() : _overlays.GetByCamera(camera);
        return list.Select(ToDto).ToList();
    }

    /// <summary>
    /// 오버레이 upsert (드래그 이동마다 즉시 저장 — BlueprintController 즉시영속 CRUD 선례).
    /// 바인딩은 Flow(정본 flowId) 또는 Call(정본 callId) 둘 다 허용 — 최소 하나는 있어야 한다.
    /// *Name 은 클라 캐시값을 신뢰하지 않고 flowId/callId 로 재해석(rename 반영, 못 찾으면 클라값 폴백).
    /// </summary>
    [HttpPost("overlays")]
    public ActionResult<CctvOverlayDto> UpsertOverlay([FromBody] CctvOverlayUpsertDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            return BadRequest(new { error = "id 가 비어 있습니다." });
        if (string.IsNullOrWhiteSpace(req.CameraName))
            return BadRequest(new { error = "cameraName 이 비어 있습니다." });

        var hasFlow = req.FlowId is Guid f && f != Guid.Empty;
        var hasCall = req.CallId is Guid c && c != Guid.Empty;
        if (!hasFlow && !hasCall)
            return BadRequest(new { error = "flowId 또는 callId 중 하나는 있어야 합니다." });

        // flowId/callId 가 정본 — 이름은 서버에서 재해석(rename 반영). 못 찾으면 클라값 유지.
        string? resolvedFlowName = req.FlowName;
        if (hasFlow)
        {
            var info = FlowInfoById(req.FlowId!.Value);
            if (info is not null)
                resolvedFlowName = info.Value.FlowName;
            else if (!_overlays.GetAll().Any(o => o.Id == req.Id))
                // 신규 생성인데 flow 를 못 찾으면 거부. 기존 오버레이의 이동/라벨 수정(이미 저장된 id)은
                // project reload/미로드 구간에도 비파괴로 허용(클라 캐시 FlowName 유지) — CallName 폴백 철학과 동일(doc/20 §10.1).
                return BadRequest(new { error = "unknown flowId (project 미로드 또는 삭제된 Flow)" });
        }

        string resolvedCallName = req.CallName ?? "";
        if (hasCall)
        {
            var pair = _callMapper.GetAllCallTagPairs().FirstOrDefault(p => p.CallId == req.CallId!.Value);
            if (!string.IsNullOrWhiteSpace(pair.CallName)) resolvedCallName = pair.CallName;
        }

        var saved = _overlays.Upsert(new CctvOverlay
        {
            Id = req.Id,
            CameraName = req.CameraName,
            FlowId = hasFlow ? req.FlowId : null,
            FlowName = hasFlow ? resolvedFlowName : null,
            CallId = hasCall ? req.CallId : null,
            CallName = hasCall ? resolvedCallName : "",
            X = req.X,
            Y = req.Y,
            W = req.W,
            H = req.H,
            Label = req.Label,
            AnchorX = req.AnchorX,
            AnchorY = req.AnchorY,
        });
        return ToDto(saved);
    }

    /// <summary>오버레이 삭제 ({id}).</summary>
    [HttpPost("overlays/delete")]
    public ActionResult<object> DeleteOverlay([FromBody] CctvOverlayDeleteDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            return BadRequest(new { error = "id 가 비어 있습니다." });
        var removed = _overlays.Delete(req.Id);
        return new { removed };
    }

    /// <summary>
    /// 바인딩 후보 Flow 목록 = 활성 시스템들의 Flow 평탄화 (BlueprintController.BuildOrderedFlows 와 동일 소스).
    /// (flowId, flowName, systemName) — 에디터 Flow 팔레트용(대시보드 도면 레이아웃과 동일 단위).
    /// </summary>
    [HttpGet("available-flows")]
    public ActionResult<List<CctvAvailableFlowDto>> GetAvailableFlows()
    {
        if (!_project.IsLoaded) return new List<CctvAvailableFlowDto>();
        return _project.GetActiveSystems()
            .SelectMany(sys => _project.GetFlows(sys.Id)
                .Select(f => new CctvAvailableFlowDto(f.Id, f.Name, sys.Name)))
            .ToList();
    }

    /// <summary>
    /// 바인딩 후보 Call 목록 = PlcToCallMapperService.GetAllCallTagPairs() projection.
    /// (callId, callName, flowName, workName) — 에디터 드롭다운용(Flow 안에서 특정 설비 선택).
    /// </summary>
    [HttpGet("available-calls")]
    public ActionResult<List<CctvAvailableCallDto>> GetAvailableCalls()
    {
        // callId → flowId 맵(프로젝트 구조에서 직접). GetAllCallTagPairs 는 FlowName 만 주므로,
        // 동명 Flow(다른 시스템) 충돌을 피하려고 flowId 를 정확히 부여한다(doc/20 §10.1).
        var callToFlow = BuildCallToFlowMap();
        return _callMapper.GetAllCallTagPairs()
            .Select(p => new CctvAvailableCallDto(
                p.CallId, p.CallName,
                callToFlow.TryGetValue(p.CallId, out var fid) ? fid : (Guid?)null,
                p.FlowName, p.WorkName))
            .OrderBy(c => c.FlowName)
            .ThenBy(c => c.CallName)
            .ToList();
    }

    /// <summary>활성 시스템 구조(시스템→Flow→Work→Call)를 평탄화해 callId → flowId 맵을 만든다.</summary>
    private Dictionary<Guid, Guid> BuildCallToFlowMap()
    {
        var map = new Dictionary<Guid, Guid>();
        if (!_project.IsLoaded) return map;
        foreach (var sys in _project.GetActiveSystems())
            foreach (var f in _project.GetFlows(sys.Id))
                foreach (var w in _project.GetWorks(f.Id))
                    foreach (var call in _project.GetCalls(w.Id))
                        map[call.Id] = f.Id;
        return map;
    }

    /// <summary>
    /// 해당 카메라 오버레이들의 현재 상태 projection (라이브 색 구동, doc/20 §5). 오버레이 id 기준.
    /// 상태 소스: Call 바인딩이 있으면 DspDbService.Snapshot.Calls(per-Call, 더 구체적),
    ///            없으면 Flow 바인딩으로 DspDbService.Snapshot.Flows(per-Flow).
    /// state 는 원본 그대로(예 "Ready"/"Going"/"Finish") — 색 판정은 프론트가 toLowerCase.
    /// avgTimeMs: Call=AverageGoingTime, Flow=AvgCT ?? CT. 한도/error 적색 판정은 보류(§5.3).
    /// </summary>
    [HttpGet("overlay-state")]
    public ActionResult<List<CctvOverlayStateDto>> GetOverlayState([FromQuery] string? camera)
    {
        var overlays = string.IsNullOrWhiteSpace(camera) ? _overlays.GetAll() : _overlays.GetByCamera(camera);
        if (overlays.Count == 0)
            return new List<CctvOverlayStateDto>();

        var snap = _dspDb.Snapshot;
        // Call 은 정본 callId(=AASX Call.Id, CallState.CallId 와 동일)로 조인 — rename/동명 충돌 무관(FlowController 선례).
        // Flow 는 스냅샷에 Guid 키가 없어(FlowState.Id 는 int rowid) FlowName 으로 조인(대시보드와 동일).
        var callById = snap.Calls.GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.Last());
        var flowByName = snap.Flows.GroupBy(f => f.FlowName).ToDictionary(g => g.Key, g => g.Last());

        var result = new List<CctvOverlayStateDto>(overlays.Count);
        foreach (var ovl in overlays)
        {
            string state = "";
            double? avg = null;
            // 툴팁 상세(요구사항 #4) — 색은 state, 그 외 진단 필드는 같은 스냅샷 소스에서 함께 내려보낸다(색/툴팁 불일치 방지).
            // Call 분기와 Flow 분기는 상호배타 — 미해당 필드는 null 유지(프론트 x-show 로 행 생략).
            string? workName = null, device = null, errorText = null, movingStart = null, movingEnd = null;
            int? goingCount = null, currentCt = null;
            double? progressRate = null;
            // Call 이 더 구체적 — 우선. 없으면 Flow.
            if (ovl.CallId is Guid cid && callById.TryGetValue(cid, out var call))
            {
                state = call.State ?? "";
                avg = call.AverageGoingTime;
                workName = string.IsNullOrWhiteSpace(call.WorkName) ? null : call.WorkName;
                device = string.IsNullOrWhiteSpace(call.Device) ? null : call.Device;
                goingCount = call.GoingCount;
                progressRate = call.ProgressRate;
                // ErrorText 는 표시 전용 — Call 을 적색으로 합성하지 않는다(Error 는 Flow 구동, doc/20 §5.3).
                errorText = string.IsNullOrWhiteSpace(call.ErrorText) ? null : call.ErrorText;
            }
            else if (!string.IsNullOrEmpty(ovl.FlowName) && flowByName.TryGetValue(ovl.FlowName, out var flow))
            {
                state = flow.State ?? "";
                avg = flow.AvgCT ?? (flow.CT.HasValue ? flow.CT.Value : (double?)null);
                currentCt = flow.CT;
                movingStart = string.IsNullOrWhiteSpace(flow.MovingStartName) ? null : flow.MovingStartName;
                movingEnd = string.IsNullOrWhiteSpace(flow.MovingEndName) ? null : flow.MovingEndName;
            }
            result.Add(new CctvOverlayStateDto(ovl.Id, state, avg,
                WorkName: workName, Device: device, GoingCount: goingCount,
                ProgressRate: progressRate, ErrorText: errorText,
                CurrentCt: currentCt, MovingStartName: movingStart, MovingEndName: movingEnd));
        }
        return result;
    }

    /// <summary>활성 시스템의 Flow 들에서 flowId 로 (FlowName, SystemName) 를 찾는다. 없으면 null.</summary>
    private (string FlowName, string SystemName)? FlowInfoById(Guid flowId)
    {
        if (!_project.IsLoaded) return null;
        foreach (var sys in _project.GetActiveSystems())
            foreach (var f in _project.GetFlows(sys.Id))
                if (f.Id == flowId)
                    return (f.Name, sys.Name);
        return null;
    }

    private static CctvOverlayDto ToDto(CctvOverlay o) => new(
        o.Id, o.CameraName, o.FlowId, o.FlowName, o.CallId, o.CallName,
        o.X, o.Y, o.W, o.H, o.Label, o.AnchorX, o.AnchorY);
}

public record CctvConfigDto(int WebRtcPort, List<CctvCameraDto> Cameras, int TotalCount, int MaxConcurrent);
public record CctvCameraDto(string Name, string Id);
public record CctvStatusDto(bool Ok, string Message);

/// <summary>
/// 카메라 설정 저장 요청. SettingsController.SaveRequestDto 의 CCTV 부분을 분리한 것.
/// 응답은 <see cref="CctvDto"/> 재사용(MediaMtxApiUrl/WebRtcPort/Cameras + 동기화 상태).
/// </summary>
public record CctvSettingsSaveDto(string? MediaMtxApiUrl, int WebRtcPort, List<CameraDto>? Cameras, string? WebRtcAdditionalHosts = null);

// ── 오버레이 DTO (camelCase 자동: cameraName, flowId, callId, anchorX ...) ──

public record CctvOverlayDto(
    string Id,
    string CameraName,
    Guid? FlowId,
    string? FlowName,
    Guid? CallId,
    string CallName,
    double X,
    double Y,
    double W,
    double H,
    string? Label,
    double? AnchorX,
    double? AnchorY);

public record CctvOverlayUpsertDto(
    string Id,
    string CameraName,
    Guid? FlowId,
    string? FlowName,
    Guid? CallId,
    string? CallName,
    double X,
    double Y,
    double W,
    double H,
    string? Label,
    double? AnchorX,
    double? AnchorY);

public record CctvOverlayDeleteDto(string Id);

public record CctvAvailableFlowDto(Guid FlowId, string FlowName, string SystemName);

public record CctvAvailableCallDto(Guid CallId, string CallName, Guid? FlowId, string FlowName, string WorkName);

/// <summary>
/// 오버레이별 라이브 상태 + 툴팁 상세(요구사항 #4). State 는 색 구동(필수), 나머지는 호버 툴팁 상세(옵션·null 허용).
/// Call 분기 필드(WorkName/Device/GoingCount/ProgressRate/ErrorText)와 Flow 분기 필드(CurrentCt/MovingStartName/MovingEndName)는
/// 상호배타이며 미해당 쪽은 null. systemName/workName-of-flow 등 이미 로드된 목록으로 조인 가능한 값은 프론트가 클라에서 해석(서버 미포함).
/// 기존 3-인자 생성도 유지되도록 추가 필드는 모두 옵션.
/// </summary>
public record CctvOverlayStateDto(
    string Id,
    string State,
    double? AvgTimeMs,
    string? WorkName = null,
    string? Device = null,
    int? GoingCount = null,
    double? ProgressRate = null,
    string? ErrorText = null,
    int? CurrentCt = null,
    string? MovingStartName = null,
    string? MovingEndName = null);
