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
    private readonly CctvFallbackImageService _fallback;
    private readonly PlcToCallMapperService _callMapper;
    private readonly DspDbService _dspDb;
    private readonly DsProjectService _project;
    private readonly CctvSnapshotService _snapshot;

    public CctvController(
        AppSettingsService settings,
        CctvMediaMtxService mediaMtx,
        CctvOverlayService overlays,
        CctvFallbackImageService fallback,
        PlcToCallMapperService callMapper,
        DspDbService dspDb,
        DsProjectService project,
        CctvSnapshotService snapshot)
    {
        _settings = settings;
        _mediaMtx = mediaMtx;
        _overlays = overlays;
        _fallback = fallback;
        _callMapper = callMapper;
        _dspDb = dspDb;
        _project = project;
        _snapshot = snapshot;
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
        // RTSP 가 있거나(라이브) 대체 이미지가 있는(주소 없는 정지 표시용) 카메라를 모두 노출 —
        // 대체 이미지 전용 카메라는 HasStream=false 로 내려 클라이언트가 WHEP 를 시작하지 않고 이미지만 띄운다.
        var cameras = cctv.Cameras
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Slug)
                && (!string.IsNullOrWhiteSpace(c.RtspUrl) || !string.IsNullOrWhiteSpace(c.FallbackImage)))
            .Select(c => new CctvCameraDto(c.Name, c.Slug, c.FallbackImage, !string.IsNullOrWhiteSpace(c.RtspUrl)))
            .ToList();
        return new CctvConfigDto(cctv.WebRtcPort, cameras, cameras.Count, MaxConcurrentCameras,
            cctv.IdlePauseEnabled, cctv.IdlePauseMinutes);
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
            cctv.Cameras.Select(c => new CameraDto(c.Name, c.RtspUrl, c.Enabled, c.Slug, c.FallbackImage)).ToList(),
            _mediaMtx.LastSyncOk,
            _mediaMtx.LastSyncMessage,
            cctv.WebRtcAdditionalHosts,
            cctv.IdlePauseEnabled,
            cctv.IdlePauseMinutes);
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
        // 무조작 일시정지(절전 가드): null 이면 미변경(구 클라이언트 호환). 시간은 1분~24시간 클램프.
        if (req.IdlePauseEnabled is not null) m.Cctv.IdlePauseEnabled = req.IdlePauseEnabled.Value;
        if (req.IdlePauseMinutes is not null) m.Cctv.IdlePauseMinutes = Math.Clamp(req.IdlePauseMinutes.Value, 1, 1440);
        // 경로명(slug)은 클라이언트가 보내지 않으므로 기존 저장값을 포지션 기준으로 이어받는다.
        // 순서 변경 없이 이름만 바꿔도 경로가 안정 유지된다(MediaMTX 재등록·오버레이 흔들림 방지).
        var prevSlugs = m.Cctv.Cameras.Select(c => c.Slug).ToList();
        var prevFallbacks = m.Cctv.Cameras.Select(c => c.FallbackImage).ToList();
        m.Cctv.Cameras = (req.Cameras ?? new List<CameraDto>())
            .Select((c, i) => new CctvCamera
            {
                Name = c.Name ?? "",
                Slug = i < prevSlugs.Count ? prevSlugs[i] : "",   // 기존 slug 이어받기; 초과분은 신규 → 빈값
                RtspUrl = c.RtspUrl ?? "",
                Enabled = c.Enabled,
                // 대체 이미지: 클라이언트가 보낸 값 우선(업로드/캡쳐 후 라운드트립; ""=명시적 해제),
                // null(구 클라이언트)이면 포지션 기준 기존값 유지.
                FallbackImage = c.FallbackImage ?? (i < prevFallbacks.Count ? prevFallbacks[i] : "")
            })
            .ToList();
        // 빈 slug(신규 카메라)에만 cam1/cam2/… 부여; 기존 slug 는 그대로.
        CctvMediaMtxService.AssignSlugs(m.Cctv.Cameras);

        // 신규 카메라의 대기(pending) 대체 이미지: 저장 전에는 slug 가 없어 파일로 못 넣고 클라이언트가
        // data URL 로 들고 있다가 이 저장에 함께 실어 보낸다(카메라 추가 직후 이미지 등록 지원, 사용자 요구).
        // slug 가 확정된 지금 파일로 영속하고 FallbackImage 를 서빙 URL 로 치환한다. 실패 시 연결 해제("").
        foreach (var cam in m.Cctv.Cameras)
        {
            if (!string.IsNullOrWhiteSpace(cam.Slug)
                && cam.FallbackImage.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                try { cam.FallbackImage = _fallback.Save(cam.Slug, cam.FallbackImage); }
                catch { cam.FallbackImage = ""; /* 비치명 — 이미지만 누락, 저장은 성공 처리 */ }
            }
        }

        _settings.SaveSettings(m);

        // 더 이상 참조되지 않는 대체 이미지 파일 정리(카메라 삭제/이미지 해제/저장 안 한 캡쳐).
        try { _fallback.Prune(m.Cctv.Cameras); } catch { /* 비치명 — 저장은 성공 처리 */ }

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
            m.Cctv.Cameras.Select(c => new CameraDto(c.Name, c.RtspUrl, c.Enabled, c.Slug, c.FallbackImage)).ToList(),
            _mediaMtx.LastSyncOk,
            _mediaMtx.LastSyncMessage,
            m.Cctv.WebRtcAdditionalHosts,
            m.Cctv.IdlePauseEnabled,
            m.Cctv.IdlePauseMinutes);
    }

    // ──────────────────────────── 대체(폴백) 이미지 ────────────────────────────

    /// <summary>
    /// 카메라 대체 이미지 등록(직접 업로드/CCTV 캡쳐 공용). body.imageDataUrl = data:image/...;base64,... .
    /// 파일만 저장하고 서빙 URL 을 반환 — 카메라-이미지 연결(FallbackImage)의 영속은 설정 저장(POST settings)
    /// 라운드트립으로 처리한다(이중 저장/경합 회피). slug 가 있어야 함(미저장 신규 카메라는 먼저 저장).
    /// antiforgery 미적용 — 평범한 JSON POST.
    /// </summary>
    [HttpPost("fallback")]
    public ActionResult<object> SaveFallback([FromBody] CctvFallbackSaveDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Slug))
            return BadRequest(new { error = "카메라를 먼저 저장한 뒤 이미지를 등록할 수 있습니다." });
        try
        {
            var url = _fallback.Save(req.Slug, req.ImageDataUrl ?? "");
            return new { url };
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (FormatException) { return BadRequest(new { error = "이미지 데이터를 해석할 수 없습니다." }); }
    }

    /// <summary>
    /// 자동 캡쳐 등록: 라이브 스트림 첫 프레임을 대체 이미지로 자동 저장 — 단, 등록된 이미지가 없을 때만(사용자 요구).
    /// 영상벽에서 스트림이 들어오면 클라이언트가 호출한다. 이미 대체 이미지가 있으면 덮어쓰지 않고 skipped 로 응답.
    /// 파일 저장과 카메라의 FallbackImage 연결 영속을 <see cref="AppSettingsService.Update"/> 로 원자화해
    /// (설정 페이지 저장과 경합해도 유실 없음, 중복 자동캡쳐 방지) 설정 모달 왕복 없이 자족적으로 처리한다.
    /// antiforgery 미적용 — 평범한 JSON POST.
    /// </summary>
    [HttpPost("fallback/auto")]
    public ActionResult<object> SaveFallbackAuto([FromBody] CctvFallbackSaveDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Slug))
            return BadRequest(new { error = "slug 가 비어 있습니다." });

        string? savedUrl = null, error = null;
        bool skipped = false;
        try
        {
            _settings.Update(m =>
            {
                // slug 는 GET 응답에만 실려 나가고 저장 시 재부여되므로, 조회 시점에도 동일 규칙으로 확정한다.
                CctvMediaMtxService.AssignSlugs(m.Cctv.Cameras);
                var cam = m.Cctv.Cameras.FirstOrDefault(
                    c => string.Equals(c.Slug, req.Slug, StringComparison.OrdinalIgnoreCase));
                if (cam is null) { error = "카메라를 찾을 수 없습니다."; return; }
                // 사용자 등록/기존 자동캡쳐 이미지가 있으면 보존 — 저장하지 않고 현재 값 반환.
                if (!string.IsNullOrWhiteSpace(cam.FallbackImage)) { savedUrl = cam.FallbackImage; skipped = true; return; }
                savedUrl = _fallback.Save(req.Slug, req.ImageDataUrl ?? "");
                cam.FallbackImage = savedUrl;
            });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (FormatException) { return BadRequest(new { error = "이미지 데이터를 해석할 수 없습니다." }); }

        if (error is not null) return NotFound(new { error });
        return new { url = savedUrl ?? "", skipped };
    }

    /// <summary>카메라 대체 이미지 파일 삭제. 연결 해제(FallbackImage="")의 영속은 설정 저장으로 처리.</summary>
    [HttpPost("fallback/delete")]
    public ActionResult<object> DeleteFallback([FromBody] CctvFallbackDeleteDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Slug))
            return BadRequest(new { error = "slug 가 비어 있습니다." });
        _fallback.Delete(req.Slug);
        return new { removed = true };
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

    // ──────────────────────────── 스냅샷 (프레임 + 오버레이 합성 이미지) ────────────────────────────

    /// <summary>
    /// 카메라 현재 프레임을 오버레이(설비 상태색 포함) 합성 JPEG 로 반환 — API 소비자(외부/DSPilot 공용)용.
    /// 프레임 소스: MediaMTX RTSP 재게시에서 ffmpeg 원샷 그랩(<see cref="CctvSnapshotService"/>),
    /// 실패 시 대체(폴백) 이미지 베이스로 폴백. camera = 표시명 또는 slug(대소문자 무시).
    /// overlay=0 이면 원본 프레임만. width 로 비율 유지 다운스케일(업스케일 안 함).
    /// 오버레이 상태색은 <see cref="GetOverlayState"/> 와 동일 스냅샷 소스라 화면과 일치한다.
    /// </summary>
    [HttpGet("snapshot/{camera}")]
    public async Task<IActionResult> GetSnapshot(string camera, [FromQuery] int overlay = 1,
        [FromQuery] int? width = null, CancellationToken ct = default)
    {
        var cctv = _settings.LoadSettings().Cctv;
        CctvMediaMtxService.AssignSlugs(cctv.Cameras);
        var cam = cctv.Cameras.FirstOrDefault(c => c.Enabled
            && (string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Slug, camera, StringComparison.OrdinalIgnoreCase)));
        if (cam is null)
            return NotFound(new { error = $"카메라를 찾을 수 없습니다: {camera}" });

        var (bytes, fromLive, error) = await _snapshot.GetFrameAsync(cam, ct);
        if (bytes is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = error ?? "프레임을 획득할 수 없습니다." });

        // 라이브/폴백 여부를 헤더로 노출 — 소비자가 stale 이미지를 구분할 수 있게.
        Response.Headers["X-Cctv-Source"] = fromLive ? "live" : "fallback";

        if (overlay == 0)
        {
            if (width is int w0 && w0 > 0)
                bytes = CctvOverlayRenderer.Render(bytes, [], w0);   // 다운스케일만
            return File(bytes, "image/jpeg");
        }

        var items = _overlays.GetByCamera(cam.Name);
        var stateMap = BuildOverlayStateMap(items);
        var drawList = items
            .Select(o => new CctvOverlayRenderer.Item(o, stateMap.TryGetValue(o.Id, out var s) ? s : ""))
            .ToList();
        try
        {
            var composed = CctvOverlayRenderer.Render(bytes, drawList, width is int w1 && w1 > 0 ? w1 : null);
            return File(composed, "image/jpeg");
        }
        catch (ArgumentException ex) // 디코드 불가(손상 폴백 파일 등)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 오버레이 id → 라이브 상태 문자열 맵. <see cref="GetOverlayState"/> 의 상태 해석부만 —
    /// Call 바인딩 우선(callId 조인), 없으면 Flow(FlowName 조인). 미해석은 빈 문자열(미상 색).
    /// </summary>
    private Dictionary<string, string> BuildOverlayStateMap(IReadOnlyList<CctvOverlay> overlays)
    {
        var map = new Dictionary<string, string>();
        if (overlays.Count == 0) return map;
        var snap = _dspDb.Snapshot;
        var callById = snap.Calls.GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.Last());
        var flowByName = snap.Flows.GroupBy(f => f.FlowName).ToDictionary(g => g.Key, g => g.Last());
        foreach (var ovl in overlays)
        {
            if (ovl.CallId is Guid cid && callById.TryGetValue(cid, out var call))
                map[ovl.Id] = call.State ?? "";
            else if (!string.IsNullOrEmpty(ovl.FlowName) && flowByName.TryGetValue(ovl.FlowName, out var flow))
                map[ovl.Id] = flow.State ?? "";
        }
        return map;
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

// IdlePause* = 무조작 일시정지(절전 가드, LTE 종량 회선 보호) — cctv-whep.js configureSaver 가 소비.
public record CctvConfigDto(int WebRtcPort, List<CctvCameraDto> Cameras, int TotalCount, int MaxConcurrent,
    bool IdlePauseEnabled = true, int IdlePauseMinutes = 60);
// FallbackImage = 영상 미표시(실패/주소없음/대기) 시 띄울 정지 이미지 URL(없으면 빈 문자열).
// HasStream = RTSP 주소가 있어 WHEP 라이브 연결을 시도해야 하는지(false = 대체 이미지 전용 카메라).
public record CctvCameraDto(string Name, string Id, string FallbackImage = "", bool HasStream = true);
public record CctvStatusDto(bool Ok, string Message);

// 대체(폴백) 이미지 등록/삭제 요청. ImageDataUrl = data:image/...;base64,... (업로드·캡쳐 공용).
public record CctvFallbackSaveDto(string Slug, string ImageDataUrl);
public record CctvFallbackDeleteDto(string Slug);

/// <summary>
/// 카메라 설정 저장 요청. SettingsController.SaveRequestDto 의 CCTV 부분을 분리한 것.
/// 응답은 <see cref="CctvDto"/> 재사용(MediaMtxApiUrl/WebRtcPort/Cameras + 동기화 상태).
/// </summary>
public record CctvSettingsSaveDto(string? MediaMtxApiUrl, int WebRtcPort, List<CameraDto>? Cameras, string? WebRtcAdditionalHosts = null,
    // 무조작 일시정지(절전 가드): null = 미변경 (구 클라이언트 호환).
    bool? IdlePauseEnabled = null, int? IdlePauseMinutes = null);

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
