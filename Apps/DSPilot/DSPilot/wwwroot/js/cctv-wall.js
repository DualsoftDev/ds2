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
                cctvAbnDetect: false,             // 이상탐지 버튼(헤더 토글) — ON 이면 서버 메모리 센서에러를 오버레이에 표시
                cctvSensorByCall: {},             // callId -> 센서에러(마지막 발생) — /api/dashboard/sensor-errors
                cctvSensorList: [],               // 센서에러 전체 목록 — flow 바인딩 오버레이 툴팁(그 flow 의 에러들) 용
                cctvSensorFlowNames: new Set(),   // 센서에러 보유 flowName Set (flow 바인딩 오버레이 강조용)
                cctvSensorCount: 0,               // 미해결 센서에러 총 건수 — 버튼 배지(토글 OFF 여도 표시)
                cctvTileRects: {},                // camId -> letterbox displayRect
                cctvOverlaySize: OVL_SZ_DEFAULT,
                cctvHover: null,                  // { id, camId, kind, color, pos, model }
                cctvSolo: null,                   // 단독 보기 대상 camId (null = 분할). CSS 로만 확대 → 추가 스트림 없음.
                cctvImageMode: false,             // true = 라이브 영상 대신 등록한 대체 이미지 표시(WHEP 미연결). 헤더 토글.
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
                try { this.cctvImageMode = localStorage.getItem('dsp.dash.cctvImageMode') === '1'; } catch (e) {}
                try { this.cctvAbnDetect = localStorage.getItem('dsp.dash.cctvAbnDetect') === '1'; } catch (e) {}
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
                this.cctvStopPolling();
                for (const id of (this.cctvWall || []).slice()) this.cctvStopStream(id);
                this._cctvStarted = {}; this._cctvHealth = {}; this._cctvFrameSeen = {};
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
                    const [list, abnList, sensorList] = await Promise.all([
                        this.apiGet('/api/cctv/overlay-state'),
                        // active-alarms: Flow 카드·알람 배너와 동일 소스(_active). 재가동(Going) 시 자동 해제 동일 적용.
                        // (Sensor* 는 2026-07 부터 메모리 전용 → 이 피드엔 Action*/usertag 만 남음.)
                        this.apiGet('/api/dashboard/active-alarms?limit=20').catch(() => []),
                        // 센서에러(단선/오감지): 서버 메모리 전용, 디바이스당 마지막 발생 1건.
                        // 항상 조회 — 버튼 배지(미해결 건수)는 토글 OFF 여도 보여야 한다. 오버레이 표시만 토글 게이트.
                        this.apiGet('/api/dashboard/sensor-errors?limit=100').catch(() => [])
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
                    const sensors = Array.isArray(sensorList) ? sensorList : [];
                    const byCall = {};
                    for (const e of sensors) if (e.callId) byCall[e.callId] = e;
                    this.cctvSensorByCall = byCall;
                    this.cctvSensorList = sensors;
                    this.cctvSensorFlowNames = new Set(sensors.map(e => e.flowName).filter(Boolean));
                    this.cctvSensorCount = sensors.length;
                    this.cctvRenderTick++;
                    if (this.cctvHover) this.cctvHover = { ...this.cctvHover, model: this.cctvTooltipModel(this.cctvHover.id), color: this.cctvColorOf(this.cctvStateOf(this.cctvHover.id)) };
                } catch (e) { console.error(e); }
            },
            cctvStartPolling() {
                this.cctvStopPolling();
                this._cctvStateTimer = setInterval(() => { if (this._cctvActive) this.cctvLoadStates(); }, 3000);
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
            // 라이브 CCTV ⇄ 대체 이미지 보기 전환(헤더 토글). 이미지 모드면 전 스트림 정지(대역 0), 해제 시 재개.
            cctvSetImageMode(on) {
                on = !!on;
                if (this.cctvImageMode === on) return;
                this.cctvImageMode = on;
                try { localStorage.setItem('dsp.dash.cctvImageMode', on ? '1' : '0'); } catch (e) {}
                if (on) {
                    for (const id of (this.cctvWall || []).slice()) this.cctvStopStream(id);
                } else {
                    this.cctvApplySoloGating();   // 라이브 복귀 — 보이는 스트림 재개
                }
                this.$nextTick(() => this.cctvRecalcRects());
            },
            // 이상탐지 토글(헤더 버튼, 라이브·이미지 모드 공통). ON = 서버 메모리 센서에러(단선/오감지)를
            // 오버레이에 빨강 표시. 조회는 항상(배지용) — 토글은 오버레이 표시 여부만 바꾼다.
            // 상태는 이미지 모드와 동일하게 localStorage 영속.
            cctvToggleAbnDetect() {
                this.cctvAbnDetect = !this.cctvAbnDetect;
                try { localStorage.setItem('dsp.dash.cctvAbnDetect', this.cctvAbnDetect ? '1' : '0'); } catch (e) {}
                this.cctvRenderTick++;       // 이미 로드된 데이터로 즉시 재렌더
                this.cctvLoadStates();       // + 최신화 1회(이후는 3초 폴링)
            },
            // 단독 보기 ⇄ 분할 전환 시 스트림 게이팅. 분할이면 전 타일 즉시 재개, 단독이면 보이는 한 대만
            // 두고 나머지는 SOLO_PAUSE_DELAY_MS 뒤 일시정지(끊김 = MediaMTX sourceOnDemand 가 10초 뒤
            // RTSP 원본까지 닫아 LTE 0). 디바운스로 빠른 토글 churn 방지.
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
                        if (this._cctvStarted[id]) this.cctvStopStream(id);
                    }
                }, SOLO_PAUSE_DELAY_MS);
            },

            // ════════ WHEP 스트림 ════════
            cctvStartStream(id) {
                if (!window.cctvWhep) return;
                if (this.cctvImageMode) return;   // 이미지 보기 모드 — 라이브 WHEP 미연결(대체 이미지만 표시)
                // 주소 없는(대체 이미지 전용) 카메라는 WHEP 미시작 — 상태 'off' 로 남겨 대체 이미지가 표시되게 한다.
                const c = this.cctvCamById(id);
                if (c && c.hasStream === false) return;
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
            // 세 반응형 값을 항상 먼저 읽는다 — 조기 return 하면 x-if 이펙트가 _cctvStarted 새 키 추가만
            // 의존성으로 잡아 (스프레드 병합 컴포넌트에서) 재평가가 누락될 수 있음. _cctvHealth 는 스트림
            // 시작 시 통째로 재할당되므로 이걸 추적하면 'connecting' 전환이 확실히 반영된다.
            cctvTileStatus(id) {
                const st = this._cctvHealth[id];
                const seen = this._cctvFrameSeen[id];
                const started = this._cctvStarted[id];
                if (!started) return 'off';
                if (st === 'failed' || st === 'disconnected' || st === 'closed') return 'failed';
                return seen ? 'live' : 'loading';
            },
            // 장애(연결 실패/무응답/대기)로 대체 이미지가 표시 중일 때 우하단 배지 라벨. 그 외(라이브/수동 이미지보기/주소없는 이미지전용)는 null.
            cctvFallbackBadge(cam) {
                if (this.cctvImageMode || !cam || !cam.fallbackImage || cam.hasStream === false) return null;
                const st = this.cctvTileStatus(cam.id);
                if (st === 'failed') return '연결 실패';
                if (st === 'loading') return '연결 중';
                if (st === 'off') return '대기';
                return null;   // live
            },

            // ════════ per-tile displayRect (object-fit:contain letterbox 보정) ════════
            // intrinsic 크기(iw×ih)를 stage 안에 contain 으로 맞춘 표시 영역. 영상/이미지 공통.
            cctvComputeRect(stage, iw, ih) {
                if (!stage) return null;
                const cw = stage.clientWidth, ch = stage.clientHeight;
                if (cw <= 0 || ch <= 0) return null;
                if (!iw || !ih) return { left: 0, top: 0, width: cw, height: ch };
                const scale = Math.min(cw / iw, ch / ih);
                const dw = iw * scale, dh = ih * scale;
                return { left: (cw - dw) / 2, top: (ch - dh) / 2, width: dw, height: dh };
            },
            cctvRecalcRects() {
                const map = {};
                for (const id of this.cctvWall) {
                    const video = document.getElementById('cctv-wall-' + id);
                    const stage = video ? video.parentElement : null;
                    if (!stage) continue;
                    // 표시 중인 매체 기준 레터박스: 라이브 영상이 있으면 영상, 없으면(이미지 모드/실패/대기) 대체 이미지.
                    // 영상·이미지 모두 object-fit:contain 이라 같은 식으로 정렬된다(오버레이가 둘 다에 맞음).
                    let iw = 0, ih = 0;
                    if (video && video.videoWidth) { iw = video.videoWidth; ih = video.videoHeight; }
                    else {
                        const img = stage.querySelector('img.cctv-tile-fallback');
                        if (img && img.naturalWidth) { iw = img.naturalWidth; ih = img.naturalHeight; }
                    }
                    const r = this.cctvComputeRect(stage, iw, ih);
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
            // 신호 의미색 통일: 주황=동작/진행(OutTag 시작 계열), 파랑=완료(InTag 응답 계열),
            // 회색=대기, 빨강=이상. 초록 제거로 Ready↔동작 혼동 소멸.
            cctvColorOf(state) {
                switch ((state || '').toLowerCase()) {
                    case 'going': return 'var(--dash-mt)';   // 주황 = 진행중(동작)
                    case 'finish': return '#1e88e5';         // 파랑 = 완료(InTag echo)
                    case 'error': return 'var(--red)';
                    case 'ready': return 'var(--dash-wt)';    // 회색 = 대기중
                    default: return 'var(--color-text-disabled)';
                }
            },
            cctvStateKeyOf(state) {
                const k = (state || '').toLowerCase();
                if (k === 'going') return 'going';
                if (k === 'finish') return 'finish';
                if (k === 'error') return 'error';
                if (k === 'ready') return 'ready';
                return 'unknown';
            },
            cctvStateLabelFor(state) {
                switch ((state || '').toLowerCase()) {
                    case 'going': return '진행중';
                    case 'finish': return '완료';
                    case 'error': return '이상';
                    case 'ready': return '대기중';
                    default: return '—';
                }
            },
            cctvLabelText(o) { return o.label || o.callName || o.flowName || '(이름없음)'; },
            // 표기 SSOT = shell.js window.dspFmt.dur (한국식 일/시간/분/초).
            cctvFmtDuration(ms) { return window.dspFmt.dur(ms); },
            cctvFmtPct(v) {
                if (v == null) return '';
                const n = Number(v);
                if (!isFinite(n)) return '';
                const p = n <= 1 ? n * 100 : n;
                return Math.round(p) + '%';
            },

            // ════════ 박스 파생 (정규화→px) ════════
            // 이상탐지 ON 일 때 이 오버레이에 해당하는 센서에러(단선/오감지) 목록.
            // Call 바인딩=callId 정확 매칭(0~1건), Flow 바인딩=그 flow 의 에러 전부(디바이스별 마지막 발생)
            // — 빨간 오버레이 호버 시 "어떤 에러였는지"를 그대로 보여준다.
            cctvSensorErrsOf(o) {
                if (!this.cctvAbnDetect) return [];
                if (o.callId) {
                    const e = this.cctvSensorByCall[o.callId];
                    return e ? [e] : [];
                }
                const fn = o.flowName || '';
                return fn ? this.cctvSensorList.filter(e => (e.flowName || '') === fn) : [];
            },
            cctvBoxFrom(o, r) {
                const left = r.left + o.x * r.width, top = r.top + o.y * r.height;
                const width = o.w * r.width, height = o.h * r.height;
                const st = this.cctvStateOf(o.id);
                const isAbnormal = this.cctvAbnNames.has(o.flowName || '') || this.cctvSensorErrsOf(o).length > 0;
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
                // 이상탐지 ON: 이 오버레이의 센서에러 상세 목록 — Call=그 디바이스 1건, Flow=그 flow 의 에러 전부.
                const sensorErrors = this.cctvSensorErrsOf(o).map(e => ({
                    label: e.label || e.kindName || '',
                    device: e.callName || '',
                    sensorTag: e.sensorTag || '',
                    occurredAt: e.occurredAtLocal || ''
                }));
                return {
                    sensorErrors,
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
