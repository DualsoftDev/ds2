/* ════════════════════════════════════════════════════════════════════════
   cctv-wall.js — 대시보드 임베드용 CCTV 영상벽 "읽기 전용 시청" 엔진.

   목적
     · cctv.html(/cctv) 의 라이브 시청 로직(WHEP 스트림·레터박스 좌표·오버레이
       렌더·상태색·호버 툴팁)을 대시보드(dashboard2.html)에서 재사용.
     · /cctv 페이지는 손대지 않음 → 시청 수식은 cctv.html 의 검증본을 충실히 복사
       (프로젝트의 cycle-gantt.js 독립복사 선례와 동일).
     · 편집(오버레이 배치/카메라 설정)은 전부 제외 — 그건 /cctv 에서.

   사용법
     dashboardApp() 의 반환 객체에 state/methods 를 스프레드:
         return { ...CctvWall.state(), ...CctvWall.methods, ...대시보드상태메서드 };
     모든 식별자는 cctv* 접두사 → 대시보드 상태와 충돌 없음.
     (충돌 방지: cctv.html 의 layout→cctvLayout, availableFlows→cctvFlows 등)

   대시보드 영상벽 구성(결정사항): 활성 카메라 자동표시(config 순, 최대 N대).
     · localStorage·서버 추가상태 없음 — /api/cctv/config 의 Enabled 카메라가 단일소스.
     · 단독/프리셋/드래그 없음(읽기 전용). 그리드는 항상 count-N 자동.

   의존: window.cctvWhep(/js/cctv-whep.js), this.apiGet(대시보드 제공).
   ════════════════════════════════════════════════════════════════════════ */
(function () {
    'use strict';

    const OVL_SZ_DEFAULT = 32;
    // 단독(solo) 보기 시 숨은 타일의 WHEP 스트림을 끊기까지의 지연. 빠른 토글에선 끊지 않아 재연결
    // churn(검은 화면) 을 막고, 한 대만 오래 보면 안 보이는 카메라의 LTE 원본을 절약한다.
    const SOLO_PAUSE_DELAY_MS = 4000;

    // PiP 합성 런타임(canvas/video/timer) — Alpine 반응형 밖(closure) 보관.
    // DOM/Chart 류 객체를 반응형 상태에 넣으면 Proxy 화로 깨지는 전례(histCache 패턴) → 식별자(cctvPipId)만 상태에 둔다.
    let pipRt = null;

    window.CctvWall = {
        // ── Alpine 반응형 상태(평면 데이터만 — 스프레드 안전) ──
        state() {
            return {
                layoutView: 'diagram',            // 'diagram' | 'cctv' (헤더 토글)
                cctvCameras: [],                  // [{ id(slug), name }]
                cctvWebRtcPort: 8889,
                cctvMaxConcurrent: 6,
                cctvTotalCount: 0,
                cctvWall: [],                     // 그리드에 표시 중인 카메라 id 배열(=config 순, cap N)
                cctvStatus: { ok: false, message: '' },
                cctvOverlaysAll: [],
                cctvFlows: [],                    // [{ flowId, flowName, systemName }]
                cctvCalls: [],                    // [{ callId, callName, flowId, flowName, workName }]
                cctvOverlayStates: {},            // overlayId -> { state, avgTimeMs, ... }
                cctvAbnNames: new Set(),          // 최근 이상 flowName Set
                cctvTileRects: {},                // camId -> letterbox displayRect
                cctvOverlaySize: OVL_SZ_DEFAULT,
                cctvHover: null,                  // { id, camId, kind, color, pos, model }
                cctvSolo: null,                   // 단독 보기 대상 camId (null = 분할). CSS 로만 확대 → 추가 스트림 없음.
                cctvPipId: null,                  // PiP(오버레이 합성 작은 창) 송출 중인 camId (null = 없음)
                cctvFullscreen: false,
                cctvRenderTick: 0,
                cctvLoaded: false,
                _cctvActive: false,
                _cctvStarted: {},                 // id -> true (WHEP 시작 여부)
                _cctvFrameSeen: {},               // id -> true (첫 영상 프레임 수신 — 연결 중/연결됨 구분)
                _cctvSoloPauseTimer: null,        // 단독 보기 시 숨은 타일 일시정지 디바운스 타이머
                _cctvHealth: {},                  // id -> RTCPeerConnection.connectionState
                _cctvResizeObs: null,
                _cctvStateTimer: null,
                _cctvStatusTimer: null,
            };
        },

        methods: {
            // ════════ 라이프사이클 (CCTV 뷰 진입/이탈 시 대시보드가 호출) ════════
            async cctvStart() {
                if (this._cctvActive) { this.$nextTick(() => this.cctvRecalcRects()); return; }
                this._cctvActive = true;
                await this.cctvLoadConfig();
                await Promise.all([
                    this.cctvLoadOverlays(),
                    this.cctvLoadFlows(),
                    this.cctvLoadCalls(),
                    this.cctvLoadStatus(),
                ]);
                this.$nextTick(() => {
                    this.cctvApplySoloGating();   // 단독 상태 유지 채로 재진입하면 보이는 한 대만 시작
                    this.cctvSetupObserver();
                    this.cctvRecalcRects();
                });
                await this.cctvLoadStates();
                this.cctvStartPolling();
                this.cctvStatusPolling();
            },
            cctvStop() {
                this._cctvActive = false;
                if (this._cctvSoloPauseTimer) { clearTimeout(this._cctvSoloPauseTimer); this._cctvSoloPauseTimer = null; }
                this.cctvStatusStopPolling();
                // PiP 작은 창이 떠 있으면 그 카메라의 스트림·상태 폴링(오버레이 상태색)은 유지 — 마무리는 cctvPipStop 이.
                if (!pipRt) this.cctvStopPolling();
                for (const id of (this.cctvWall || []).slice()) {
                    if (pipRt && pipRt.camId === id) continue;
                    this.cctvStopStream(id);
                }
                if (!pipRt) { this._cctvStarted = {}; this._cctvHealth = {}; this._cctvFrameSeen = {}; }
                if (this._cctvResizeObs) { try { this._cctvResizeObs.disconnect(); } catch (e) {} this._cctvResizeObs = null; }
                this.cctvClearHover();
            },

            // ════════ 설정/데이터 로드 ════════
            async cctvLoadConfig() {
                try {
                    const c = await this.apiGet('/api/cctv/config');
                    this.cctvCameras = c.cameras || [];
                    this.cctvWebRtcPort = c.webRtcPort || 8889;
                    this.cctvTotalCount = c.totalCount || 0;
                    this.cctvMaxConcurrent = c.maxConcurrent || 6;
                    // 무조작 일시정지(절전 가드) 서버 공유 설정 주입 — /cctv 의 카메라 설정과 동일하게 적용.
                    try { window.cctvWhep?.configureSaver?.({ idleEnabled: c.idlePauseEnabled !== false, idleMinutes: c.idlePauseMinutes }); } catch (e) {}
                    // 대시보드 영상벽 = 활성 카메라 자동표시(config 순, 최대 N). localStorage 미사용.
                    this.cctvWall = this.cctvCameras.slice(0, this.cctvMaxConcurrent).map(c => c.id);
                    if (this.cctvSolo && !this.cctvWall.includes(this.cctvSolo)) this.cctvSolo = null;
                    this.cctvLoaded = true;
                } catch (e) { console.error(e); this.cctvLoaded = true; }
            },
            async cctvLoadStatus() {
                try { this.cctvStatus = await this.apiGet('/api/cctv/status'); }
                catch (e) { /* 상태 조회 실패 시 무시 */ }
            },
            async cctvLoadOverlays() {
                try { this.cctvOverlaysAll = await this.apiGet('/api/cctv/overlays') || []; }
                catch (e) { console.error(e); this.cctvOverlaysAll = []; }
            },
            async cctvLoadFlows() {
                try { this.cctvFlows = await this.apiGet('/api/cctv/available-flows') || []; }
                catch (e) { console.error(e); this.cctvFlows = []; }
            },
            async cctvLoadCalls() {
                try { this.cctvCalls = await this.apiGet('/api/cctv/available-calls') || []; }
                catch (e) { console.error(e); this.cctvCalls = []; }
            },
            async cctvLoadStates() {
                try {
                    const [list, abnList] = await Promise.all([
                        this.apiGet('/api/cctv/overlay-state'),
                        // active-alarms: Flow 카드·알람 배너와 동일 소스(_active). 재가동(Going) 시 자동 해제 동일 적용.
                        this.apiGet('/api/dashboard/active-alarms?limit=20').catch(() => [])
                    ]);
                    const map = {};
                    for (const s of (list || [])) map[s.id] = {
                        state: s.state, avgTimeMs: s.avgTimeMs,
                        workName: s.workName, device: s.device, goingCount: s.goingCount,
                        progressRate: s.progressRate, errorText: s.errorText,
                        currentCt: s.currentCt, movingStartName: s.movingStartName, movingEndName: s.movingEndName
                    };
                    this.cctvOverlayStates = map;
                    this.cctvAbnNames = new Set((Array.isArray(abnList) ? abnList : []).map(a => a.flowName).filter(Boolean));
                    this.cctvRenderTick++;
                    if (this.cctvHover) this.cctvHover = { ...this.cctvHover, model: this.cctvTooltipModel(this.cctvHover.id), color: this.cctvColorOf(this.cctvStateOf(this.cctvHover.id)) };
                } catch (e) { console.error(e); }
            },
            cctvStartPolling() {
                this.cctvStopPolling();
                this._cctvStateTimer = setInterval(() => { if (this._cctvActive || this.cctvPipId) this.cctvLoadStates(); }, 3000);
            },
            cctvStopPolling() { if (this._cctvStateTimer) { clearInterval(this._cctvStateTimer); this._cctvStateTimer = null; } },
            cctvStatusPolling() {
                this.cctvStatusStopPolling();
                this._cctvStatusTimer = setInterval(() => {
                    if (!this._cctvActive) return;
                    this.cctvLoadStatus();
                    this.cctvSyncSaverConfig();
                }, 15000);
            },
            // 무조작 일시정지 설정 재동기화 — /cctv 에서 바꾼 서버 공유 설정을 이미 떠 있는
            // 대시보드 영상벽도 시청 중에 따라가게(15초 상태 폴링에 피기백).
            async cctvSyncSaverConfig() {
                try {
                    const c = await this.apiGet('/api/cctv/config');
                    if (window.cctvWhep && window.cctvWhep.configureSaver)
                        window.cctvWhep.configureSaver({ idleEnabled: c.idlePauseEnabled !== false, idleMinutes: c.idlePauseMinutes });
                } catch (e) { /* 다음 폴링에 재시도 */ }
            },
            cctvStatusStopPolling() { if (this._cctvStatusTimer) { clearInterval(this._cctvStatusTimer); this._cctvStatusTimer = null; } },

            // ════════ 카메라/그리드 ════════
            cctvCamById(id) { return this.cctvCameras.find(c => c.id === id) || null; },
            cctvWallCams() {
                this.cctvRenderTick;
                return this.cctvWall.map(id => this.cctvCamById(id)).filter(Boolean);
            },
            cctvGridClass() { return this.cctvSolo ? 'layout-solo' : ('count-' + this.cctvWall.length); },
            // 단독(확대) ⇄ 분할 토글. 같은 타일을 CSS 로만 확대 → 진입 시 추가 WHEP 스트림 없음. 타일 크기 변화 → 좌표 재계산.
            cctvToggleSolo(id) {
                this.cctvSolo = (this.cctvSolo === id) ? null : id;
                this.cctvClearHover();
                this.cctvApplySoloGating();
                this.$nextTick(() => this.cctvRecalcRects());
            },
            // 단독 보기 ⇄ 분할 전환 시 스트림 게이팅. 분할이면 전 타일 즉시 재개, 단독이면 보이는 한 대만
            // 두고 나머지는 SOLO_PAUSE_DELAY_MS 뒤 일시정지(끊김 = MediaMTX sourceOnDemand 가 10초 뒤
            // RTSP 원본까지 닫아 LTE 0). 디바운스로 빠른 토글 churn 방지. PiP 송출 중 카메라는 보던 중이라 제외.
            cctvApplySoloGating() {
                if (this._cctvSoloPauseTimer) { clearTimeout(this._cctvSoloPauseTimer); this._cctvSoloPauseTimer = null; }
                const active = (this.cctvSolo && this.cctvWall.includes(this.cctvSolo)) ? this.cctvSolo : null;
                if (!active) { this.cctvSyncStreams(); return; }   // 분할 복귀 — 멈췄던 스트림 즉시 재개
                if (!this._cctvStarted[active]) this.cctvStartStream(active);
                this._cctvSoloPauseTimer = setTimeout(() => {
                    this._cctvSoloPauseTimer = null;
                    if (!(this.cctvSolo === active && this.cctvWall.includes(active))) return;   // 그새 상태 바뀜
                    for (const id of this.cctvWall) {
                        if (id === active) continue;
                        if (pipRt && pipRt.camId === id) continue;   // PiP 작은 창으로 보는 중 — 끊지 않음
                        if (this._cctvStarted[id]) this.cctvStopStream(id);
                    }
                }, SOLO_PAUSE_DELAY_MS);
            },

            // ════════ WHEP 스트림 ════════
            cctvStartStream(id) {
                if (!window.cctvWhep) return;
                this._cctvHealth = { ...this._cctvHealth, [id]: 'connecting' };
                this._cctvFrameSeen = { ...this._cctvFrameSeen, [id]: false };
                window.cctvWhep.start('cctv-wall-' + id, this.cctvWebRtcPort, id,
                    (st) => { this._cctvHealth = { ...this._cctvHealth, [id]: st }; });
                this._cctvStarted[id] = true;
            },
            cctvStopStream(id) {
                if (window.cctvWhep) window.cctvWhep.stop('cctv-wall-' + id);
                delete this._cctvStarted[id];
                const h = { ...this._cctvHealth }; delete h[id]; this._cctvHealth = h;
                const f = { ...this._cctvFrameSeen }; delete f[id]; this._cctvFrameSeen = f;
            },
            cctvSyncStreams() { for (const id of this.cctvWall) if (!this._cctvStarted[id]) this.cctvStartStream(id); },
            // 첫 영상 프레임 수신(video 'playing') → 연결 중 → 연결됨. 멱등.
            cctvMarkFrameSeen(id) { if (!this._cctvFrameSeen[id]) this._cctvFrameSeen = { ...this._cctvFrameSeen, [id]: true }; },
            // 타일 연결 상태 오버레이용: 'off'(미시작/단독 일시정지) | 'loading'(연결 중) | 'failed' | 'live'(표시 안 함).
            cctvTileStatus(id) {
                if (!this._cctvStarted[id]) return 'off';
                const st = this._cctvHealth[id];
                if (st === 'failed' || st === 'disconnected' || st === 'closed') return 'failed';
                return this._cctvFrameSeen[id] ? 'live' : 'loading';
            },

            // ════════ PiP (오버레이 합성 작은 창) ════════
            // 브라우저 기본 video PiP 는 영상 픽셀만 떼어가 DOM 오버레이가 빠짐(타일 video 는 disablepictureinpicture 로 차단).
            // → canvas 에 영상 프레임+오버레이(박스/핀/상태색)를 합성한 captureStream 을 PiP 로 띄운다.
            //   Document PiP API 가 더 깔끔하지만 secure context 전용이라 LAN HTTP 환경에선 불가 → canvas 합성이 정답.
            cctvPipSupported() {
                return !!(document.pictureInPictureEnabled && HTMLCanvasElement.prototype.captureStream);
            },
            async cctvTogglePip(camId) {
                if (this.cctvPipId === camId) { await this.cctvPipStop(); return; }
                await this.cctvPipStop();                                  // 다른 카메라 PiP → 교체
                const src = document.getElementById('cctv-wall-' + camId);
                const cam = this.cctvCamById(camId);
                if (!src || !cam || !src.videoWidth) return;               // 스트림 미연결(첫 프레임 전) — 무시
                const canvas = document.createElement('canvas');
                canvas.width = src.videoWidth; canvas.height = src.videoHeight;
                const out = document.createElement('video');               // DOM 미부착 — PiP 진입에 부착 불필요
                out.muted = true; out.playsInline = true;
                out.srcObject = canvas.captureStream();
                const rt = { camId, cam, src, canvas, ctx: canvas.getContext('2d'), out, timer: null };
                pipRt = rt;
                this.cctvPipDraw(rt);
                // rAF 는 탭 백그라운드에서 멈춰 PiP 가 얼어붙음 → setInterval ~15fps.
                // (활성 WebRTC 연결이 있는 페이지는 크롬 백그라운드 타이머 집중제한 면제 대상)
                rt.timer = setInterval(() => { if (pipRt === rt) this.cctvPipDraw(rt); }, 66);
                out.addEventListener('leavepictureinpicture', () => { if (pipRt === rt) this.cctvPipStop(); });
                try {
                    await out.play();
                    await out.requestPictureInPicture();
                    this.cctvPipId = camId;
                    // PiP 는 탭이 숨겨져도 보는 중 — 절전 가드(탭 숨김/무입력 일시정지)에서 면제.
                    if (window.cctvWhep && window.cctvWhep.setKeepAlive) window.cctvWhep.setKeepAlive('cctv-wall-' + camId, true);
                } catch (e) {
                    console.warn('[cctv] PiP 시작 실패:', e && e.message);
                    pipRt = pipRt || rt;
                    await this.cctvPipStop();
                }
            },
            async cctvPipStop() {
                const rt = pipRt;
                pipRt = null;
                this.cctvPipId = null;
                if (!rt) return;
                if (rt.timer) { clearInterval(rt.timer); rt.timer = null; }
                // 절전 면제 해제 — 탭이 숨김/방치 상태면 whep 레이어가 이 스트림도 즉시 보류한다.
                if (window.cctvWhep && window.cctvWhep.setKeepAlive) window.cctvWhep.setKeepAlive('cctv-wall-' + rt.camId, false);
                if (document.pictureInPictureElement === rt.out) {
                    try { await document.exitPictureInPicture(); } catch (e) {}
                }
                try { rt.out.srcObject = null; } catch (e) {}
                // CCTV 뷰 밖(도면 등)에서 PiP 만 보던 중이었다면, cctvStop 이 남겨둔 스트림·폴링을 여기서 정리.
                if (!this._cctvActive) {
                    this.cctvStopPolling();
                    this.cctvStopStream(rt.camId);
                }
            },
            // 합성 1프레임: 영상 → Flow 박스 → Call 핀 → 카메라 이름. 좌표는 화면과 동일한 정규화 모델(cctvBoxFrom)을
            // 캔버스 전체 rect 에 적용(레터박스 없음 = 원본 해상도 그대로).
            cctvPipDraw(rt) {
                const { src, canvas, ctx, cam } = rt;
                const vw = src.videoWidth, vh = src.videoHeight;
                if (!vw || !vh) return;
                if (canvas.width !== vw || canvas.height !== vh) { canvas.width = vw; canvas.height = vh; }
                ctx.drawImage(src, 0, 0, vw, vh);
                const s = Math.max(1, vh / 540);   // 1080p≈2배 — 화면 DOM 오버레이와 비슷한 체감 크기
                const r = { left: 0, top: 0, width: vw, height: vh };
                const n = (cam.name || '').toLowerCase();
                const boxes = this.cctvOverlaysAll
                    .filter(o => (o.cameraName || '').toLowerCase() === n)
                    .map(o => this.cctvBoxFrom(o, r));
                for (const b of boxes) if (b.kind !== 'Call') this.cctvPipDrawFlow(ctx, b, s);
                for (const b of boxes) if (b.kind === 'Call') this.cctvPipDrawPin(ctx, b, s);
                this.cctvPipChip(ctx, cam.name || '', 8 * s, 8 * s, 'rgba(8,13,20,0.78)', s, {});
            },
            // canvas 는 CSS 변수를 못 그림 → 'var(--x)' 를 :root 에서 해석(팔레트는 :root 단일소스 — 차트와 동일 규칙).
            cctvPipColor(c) {
                const m = /^var\((--[^)]+)\)/.exec((c || '').trim());
                if (!m) return c || '#9aa3ad';
                return getComputedStyle(document.documentElement).getPropertyValue(m[1]).trim() || '#9aa3ad';
            },
            cctvPipDrawFlow(ctx, b, s) {
                const c = this.cctvPipColor(b.color);
                const p = b.px;
                ctx.save();
                ctx.beginPath();
                if (ctx.roundRect) ctx.roundRect(p.left, p.top, p.width, p.height, 8 * s); else ctx.rect(p.left, p.top, p.width, p.height);
                ctx.globalAlpha = b.isAbnormal ? 0.30 : 0.12;   // DOM 의 color-mix 12% 틴트 근사(이상 시 강조)
                ctx.fillStyle = c; ctx.fill();
                ctx.globalAlpha = 1;
                ctx.lineWidth = 2 * s; ctx.strokeStyle = c; ctx.stroke();
                ctx.restore();
                this.cctvPipChip(ctx, 'FLOW ' + b.label, p.left, b.tagBelow ? p.top + p.height + 3 * s : p.top - 3 * s, c, s, { bottom: !b.tagBelow });
            },
            cctvPipDrawPin(ctx, b, s) {
                const c = this.cctvPipColor(b.color);
                const R = (this.cctvOverlaySize / 2) * s;        // DOM --cctv-ovl-sz(32px) 과 동일 비율
                const cx = b.px.cx, cy = b.px.cy;
                ctx.save();
                ctx.beginPath();                                  // 꼬리(stem)
                ctx.moveTo(cx - R * 0.44, cy + R * 0.78);
                ctx.lineTo(cx + R * 0.44, cy + R * 0.78);
                ctx.lineTo(cx, cy + R * 1.6);
                ctx.closePath();
                ctx.fillStyle = c; ctx.fill();
                ctx.beginPath();                                  // 머리
                ctx.arc(cx, cy, R, 0, Math.PI * 2);
                ctx.fillStyle = c; ctx.fill();
                ctx.lineWidth = 2.5 * s; ctx.strokeStyle = 'rgba(255,255,255,0.45)'; ctx.stroke();
                ctx.restore();
                this.cctvPipChip(ctx, b.label, cx, cy + R * 1.6 + 4 * s, c, s, { center: true });
            },
            // 라벨 칩. opts: center=x 가 중심, bottom=y 가 칩 하단(기본은 x=좌측·y=상단). 캔버스 밖으로 나가지 않게 클램프.
            cctvPipChip(ctx, text, x, y, bg, s, opts) {
                if (!text) return;
                const o = opts || {};
                const fs = Math.round(11 * s);
                ctx.save();
                ctx.font = '700 ' + fs + 'px Inter, "Noto Sans KR", sans-serif';
                const padX = Math.round(7 * s), h = Math.round(fs * 1.7);
                const tw = Math.min(Math.ceil(ctx.measureText(text).width), Math.round(240 * s));
                const w = tw + padX * 2;
                const left = Math.max(2, Math.min(o.center ? x - w / 2 : x, ctx.canvas.width - w - 2));
                const top = Math.max(2, Math.min(o.bottom ? y - h : y, ctx.canvas.height - h - 2));
                ctx.beginPath();
                if (ctx.roundRect) ctx.roundRect(left, top, w, h, 4 * s); else ctx.rect(left, top, w, h);
                ctx.fillStyle = bg; ctx.fill();
                ctx.beginPath(); ctx.rect(left + padX, top, tw, h); ctx.clip();   // 넘치는 라벨 잘라내기
                ctx.fillStyle = '#fff'; ctx.textBaseline = 'middle';
                ctx.fillText(text, left + padX, top + h / 2 + s);
                ctx.restore();
            },

            // ════════ per-tile displayRect (object-fit:contain letterbox 보정) ════════
            cctvComputeRect(video) {
                if (!video) return null;
                const stage = video.parentElement;
                if (!stage) return null;
                const cw = stage.clientWidth, ch = stage.clientHeight;
                if (cw <= 0 || ch <= 0) return null;
                const vw = video.videoWidth, vh = video.videoHeight;
                if (!vw || !vh) return { left: 0, top: 0, width: cw, height: ch };
                const scale = Math.min(cw / vw, ch / vh);
                const dw = vw * scale, dh = vh * scale;
                return { left: (cw - dw) / 2, top: (ch - dh) / 2, width: dw, height: dh };
            },
            cctvRecalcRects() {
                const map = {};
                for (const id of this.cctvWall) {
                    const r = this.cctvComputeRect(document.getElementById('cctv-wall-' + id));
                    if (r) map[id] = r;
                }
                this.cctvTileRects = map;
                this.cctvRenderTick++;
            },
            cctvSetupObserver() {
                const grid = this.$refs.cctvWallGrid;
                if (!grid) return;
                if (this._cctvResizeObs) { try { this._cctvResizeObs.disconnect(); } catch (e) {} }
                this._cctvResizeObs = new ResizeObserver(() => this.cctvRecalcRects());
                this._cctvResizeObs.observe(grid);
            },

            // ════════ 오버레이 개수/상태 ════════
            cctvOverlayCountFor(cameraName) {
                // 대시보드는 카메라 ≤6 + 오버레이 소수 → 캐시 없이 즉시 카운트(렌더 중 반응형 쓰기 회피).
                const n = (cameraName || '').toLowerCase();
                return this.cctvOverlaysAll.filter(o => (o.cameraName || '').toLowerCase() === n).length;
            },
            cctvStateOf(overlayId) { const s = this.cctvOverlayStates[overlayId]; return s ? (s.state || '') : ''; },
            cctvColorOf(state) {
                switch ((state || '').toLowerCase()) {
                    case 'going': return 'var(--color-warning)';
                    case 'error': return 'var(--red)';
                    case 'ready': case 'finish': return 'var(--green)';
                    default: return 'var(--color-text-disabled)';
                }
            },
            cctvStateKeyOf(state) {
                const k = (state || '').toLowerCase();
                if (k === 'going') return 'going';
                if (k === 'error') return 'error';
                if (k === 'ready' || k === 'finish') return 'ready';
                return 'unknown';
            },
            cctvStateLabelFor(state) {
                switch ((state || '').toLowerCase()) {
                    case 'going': return '진행중';
                    case 'error': return '이상';
                    case 'ready': case 'finish': return '대기중';
                    default: return '—';
                }
            },
            cctvLabelText(o) { return o.label || o.callName || o.flowName || '(이름없음)'; },
            cctvFmtDuration(ms) {
                if (ms == null) return '—';
                const n = Number(ms);
                if (!isFinite(n)) return '—';
                if (n >= 1000) return (n / 1000).toFixed(1) + 's';
                return Math.round(n) + 'ms';
            },
            cctvFmtPct(v) {
                if (v == null) return '';
                const n = Number(v);
                if (!isFinite(n)) return '';
                const p = n <= 1 ? n * 100 : n;
                return Math.round(p) + '%';
            },

            // ════════ 박스 파생 (정규화→px) ════════
            cctvBoxFrom(o, r) {
                const left = r.left + o.x * r.width, top = r.top + o.y * r.height;
                const width = o.w * r.width, height = o.h * r.height;
                const st = this.cctvStateOf(o.id);
                const isAbnormal = this.cctvAbnNames.has(o.flowName || '');
                return {
                    id: o.id, label: this.cctvLabelText(o), kind: o.callId ? 'Call' : 'Flow',
                    color: isAbnormal ? 'var(--red)' : this.cctvColorOf(st),
                    stateKey: this.cctvStateKeyOf(st), tagBelow: top < 24, tagInsetLeft: left < 6,
                    isAbnormal,
                    px: { left, top, width, height, cx: left + width / 2, cy: top + height / 2 }
                };
            },
            cctvBoxesForCam(cam) {
                const _ = this.cctvRenderTick;
                const r = this.cctvTileRects[cam.id];
                if (!r) return [];
                const n = (cam.name || '').toLowerCase();
                return this.cctvOverlaysAll.filter(o => (o.cameraName || '').toLowerCase() === n).map(o => this.cctvBoxFrom(o, r));
            },

            // ════════ 호버 툴팁 ════════
            cctvTooltipModel(id) {
                const o = this.cctvOverlaysAll.find(x => x.id === id) || {};
                const s = this.cctvOverlayStates[id] || {};
                let systemName = '';
                if (o.flowId) { const f = this.cctvFlows.find(x => x.flowId === o.flowId); if (f) systemName = f.systemName || ''; }
                let workName = s.workName || '';
                if (!workName && o.callId) { const c = this.cctvCalls.find(x => x.callId === o.callId); if (c) workName = c.workName || ''; }
                return {
                    title: this.cctvLabelText(o),
                    flowName: o.flowName || '',
                    systemName,
                    callName: o.callName || '',
                    workName,
                    stateLabel: this.cctvStateLabelFor(s.state),
                    avgMs: (s.avgTimeMs == null ? null : s.avgTimeMs),
                    goingCount: (s.goingCount == null ? null : s.goingCount),
                    progressRate: (s.progressRate == null ? null : s.progressRate),
                    device: s.device || '',
                    currentCt: (s.currentCt == null ? null : s.currentCt),
                    movingStartName: s.movingStartName || '',
                    movingEndName: s.movingEndName || '',
                    errorText: s.errorText || ''
                };
            },
            cctvSetHover(b, camId, e) {
                let layer = e.currentTarget;
                while (layer && !layer.classList.contains('cctv-ovl-layer')) layer = layer.parentElement;
                const rect = layer ? layer.getBoundingClientRect() : { left: 0, top: 0, width: 0, height: 0 };
                const sw = rect.width, sh = rect.height;
                const isPin = b.kind === 'Call';
                const refY = isPin ? b.px.cy : (b.px.top + b.px.height);
                const refX = isPin ? b.px.cx : b.px.left;
                const pad = isPin ? Math.round(this.cctvOverlaySize * 0.82 + 8) : 8;
                const below = refY < sh * 0.62;
                const vLeft = Math.max(4, Math.min(rect.left + refX - 90, rect.left + sw - 214));
                const pos = below
                    ? { left: vLeft, top: rect.top + refY + pad }
                    : { left: vLeft, bottom: window.innerHeight - (rect.top + refY) + pad };
                this.cctvHover = { id: b.id, camId, kind: b.kind, color: b.color, pos, model: this.cctvTooltipModel(b.id) };
            },
            cctvClearHover() { this.cctvHover = null; },
            cctvTipStyle(cam) {
                const h = this.cctvHover;
                if (!h || h.camId !== cam.id) return 'display:none;';
                const p = h.pos;
                const vpos = (p.top != null) ? ('top:' + p.top + 'px;') : ('bottom:' + p.bottom + 'px;');
                return `left:${p.left}px; ${vpos} --ovl-color:${h.color};`;
            },
        },
    };
})();
