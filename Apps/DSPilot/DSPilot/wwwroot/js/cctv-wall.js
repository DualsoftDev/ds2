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

   의존: window.cctvWhep(/js/cctv-whep.js), this.apiGet(대시보드 제공), this.demoBlocked.
   ════════════════════════════════════════════════════════════════════════ */
(function () {
    'use strict';

    const OVL_SZ_DEFAULT = 32;

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
                cctvFullscreen: false,
                cctvRenderTick: 0,
                cctvLoaded: false,
                _cctvActive: false,
                _cctvStarted: {},                 // id -> true (WHEP 시작 여부)
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
                    this.cctvSyncStreams();
                    this.cctvSetupObserver();
                    this.cctvRecalcRects();
                });
                await this.cctvLoadStates();
                this.cctvStartPolling();
                this.cctvStatusPolling();
            },
            cctvStop() {
                this._cctvActive = false;
                this.cctvStopPolling();
                this.cctvStatusStopPolling();
                for (const id of (this.cctvWall || []).slice()) this.cctvStopStream(id);
                this._cctvStarted = {};
                this._cctvHealth = {};
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
                    // 대시보드 영상벽 = 활성 카메라 자동표시(config 순, 최대 N). localStorage 미사용.
                    this.cctvWall = this.cctvCameras.slice(0, this.cctvMaxConcurrent).map(c => c.id);
                    if (this.cctvSolo && !this.cctvWall.includes(this.cctvSolo)) this.cctvSolo = null;
                    this.cctvLoaded = true;
                } catch (e) { if (e.message !== 'demo-blocked') console.error(e); this.cctvLoaded = true; }
            },
            async cctvLoadStatus() {
                try { this.cctvStatus = await this.apiGet('/api/cctv/status'); }
                catch (e) { /* 503 already flagged */ }
            },
            async cctvLoadOverlays() {
                try { this.cctvOverlaysAll = await this.apiGet('/api/cctv/overlays') || []; }
                catch (e) { if (e.message !== 'demo-blocked') console.error(e); this.cctvOverlaysAll = []; }
            },
            async cctvLoadFlows() {
                try { this.cctvFlows = await this.apiGet('/api/cctv/available-flows') || []; }
                catch (e) { if (e.message !== 'demo-blocked') console.error(e); this.cctvFlows = []; }
            },
            async cctvLoadCalls() {
                try { this.cctvCalls = await this.apiGet('/api/cctv/available-calls') || []; }
                catch (e) { if (e.message !== 'demo-blocked') console.error(e); this.cctvCalls = []; }
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
                } catch (e) { if (e.message !== 'demo-blocked') console.error(e); }
            },
            cctvStartPolling() {
                this.cctvStopPolling();
                this._cctvStateTimer = setInterval(() => { if (this._cctvActive && !this.demoBlocked) this.cctvLoadStates(); }, 3000);
            },
            cctvStopPolling() { if (this._cctvStateTimer) { clearInterval(this._cctvStateTimer); this._cctvStateTimer = null; } },
            cctvStatusPolling() {
                this.cctvStatusStopPolling();
                this._cctvStatusTimer = setInterval(() => { if (this._cctvActive && !this.demoBlocked) this.cctvLoadStatus(); }, 15000);
            },
            cctvStatusStopPolling() { if (this._cctvStatusTimer) { clearInterval(this._cctvStatusTimer); this._cctvStatusTimer = null; } },

            // ════════ 카메라/그리드 ════════
            cctvCamById(id) { return this.cctvCameras.find(c => c.id === id) || null; },
            cctvWallCams() {
                this.cctvRenderTick;
                return this.cctvWall.map(id => this.cctvCamById(id)).filter(Boolean);
            },
            cctvGridClass() { return this.cctvSolo ? 'layout-solo' : ('count-' + this.cctvWall.length); },
            // 단독(확대) ⇄ 분할 토글. 같은 타일을 CSS 로만 확대 → 추가 WHEP 스트림 없음. 타일 크기 변화 → 좌표 재계산.
            cctvToggleSolo(id) {
                this.cctvSolo = (this.cctvSolo === id) ? null : id;
                this.cctvClearHover();
                this.$nextTick(() => this.cctvRecalcRects());
            },

            // ════════ WHEP 스트림 ════════
            cctvStartStream(id) {
                if (!window.cctvWhep) return;
                this._cctvHealth = { ...this._cctvHealth, [id]: 'connecting' };
                window.cctvWhep.start('cctv-wall-' + id, this.cctvWebRtcPort, id,
                    (st) => { this._cctvHealth = { ...this._cctvHealth, [id]: st }; });
                this._cctvStarted[id] = true;
            },
            cctvStopStream(id) {
                if (window.cctvWhep) window.cctvWhep.stop('cctv-wall-' + id);
                delete this._cctvStarted[id];
                const h = { ...this._cctvHealth }; delete h[id]; this._cctvHealth = h;
            },
            cctvSyncStreams() { for (const id of this.cctvWall) if (!this._cctvStarted[id]) this.cctvStartStream(id); },

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
