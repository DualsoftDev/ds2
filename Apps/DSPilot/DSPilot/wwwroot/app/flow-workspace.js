        function flowApp() {
            // ── BuildSvg 레이아웃 상수 (cycle-time-analysis 와 동일) ──
            const TOP_MARGIN = 50, LANE_HEIGHT = 44, RIBBON_H = 48, LEFT_PAD = 12, RIGHT_PAD = 40, MIN_PLOT_WIDTH = 640, MAX_ZOOM = 24;   // 렌더 상수 본체는 cycle-gantt.js — 여기선 드래그/툴팁 좌표·템플릿 높이만 사용
            // 사이클 분기 팔레트 — 미리보기 스트립/통계 칩 공용(미분류 = 회색 #9e9e9e 고정).
            const BR_COLORS = ['#2e7d32', '#7b1fa2', '#0277bd', '#ef6c00', '#c2185b', '#5d4037', '#00695c', '#455a64'];
            // 모바일(≤480px) 에서는 최소 플롯 폭을 줄여 좁은 화면에 맞춤(불필요한 가로 overflow 방지).
            const minPlotW = () => (typeof window !== 'undefined' && window.innerWidth < 480) ? 280 : MIN_PLOT_WIDTH;
            const histCache = {};   // flowName → rows: 전환 시 즉시 표시(서버 왕복 대기 없이)
            // ── Chart.js 인스턴스는 Alpine 반응형 밖(클로저)에 보관 ──
            // 컴포넌트 프로퍼티(this._x)로 두면 Alpine 이 차트의 내부 config/_resolverCache/_fallback 까지
            // 깊은 Proxy 로 감싸고, 재사용 update() 시 리졸버가 Proxy 순환 스코프를 타며 폭주한다
            // (stack overflow / 레이아웃 box undefined / 옵션 resolver 의 `.includes` of undefined).
            // 구버전이 매번 destroy()+new Chart() 라 update() 를 안 해서 안 터졌을 뿐 → 클로저 보관으로 근본 차단.
            let _charts = { trend: null, count: null };   // 추이 탭 (trend/count)
            let _cycleChart = null;   // 사이클 분석 탭
            let _histChart = null;    // 최근 히스토리 탭
            // 간트 슬라이스(2026-09-06 2간트) — 상단=flow 항상, 하단=분기(있을 때, 탭으로 활성 분기 1개).
            // lane 배열·스팬을 통째로 들고 있어 Alpine 깊은 Proxy 를 피하려 클로저에 둔다(템플릿용 요약은 flowAct/brAct).
            let _flowAct = null, _brAct = null;

            // x축(category) 눈금 라벨: 첫·마지막은 항상 표시하고 나머지는 균등 간격으로 남긴다.
            // Chart.js 기본 autoSkip 은 균등 간격만 유지하고 끝 눈금 보존을 보장하지 않아
            // 마지막 버킷(가장 최근 날짜/시각) 라벨이 잘려 안 보이던 문제를 해결한다.
            function edgeTickCallback(value, index, ticks) {
                const n = ticks.length;
                if (n <= 1 || index === 0 || index === n - 1) return this.getLabelForValue(value);
                const step = Math.max(1, Math.ceil(n / 12));
                return index % step === 0 ? this.getLabelForValue(value) : '';
            }

            return {
                TOP_MARGIN, LANE_HEIGHT, RIBBON_H,

                // ── Flow 워크스페이스 공통 ──
                flowName: null,
                flow: null,
                loading: true, error: null, dark: false,
                rt: { connected: false },

                // ── 사이클 분기(branch) 편집기 (2026-08-27) ──
                branches: [],            // [{name, startCallName, endCallName, excludedCallNames[]}] — 편집 중 상태
                branchesSaved: '[]',     // 저장 스냅샷(JSON) — dirty 판정
                branchSavedCount: 0,     // 저장된 분기 수(0 = 분기 미사용)
                branchBusy: false,
                // ── 2간트 구성(2026-09-06) — 상단 flow 간트는 항상, 하단 분기 간트는 분기가 있을 때만. 분기가 여럿이면
                //    하단 간트를 탭(branchTab)으로 전환한다(최대 2개 간트). 각 간트는 SVG + 사이드바 행 + 요약을 따로 갖는다.
                branchTab: 0,            // 하단 간트에 그릴 활성 분기 index
                flowSvg: '', brSvg: '',  // 각 간트 SVG (x-html)
                flowRows: [], brRows: [],// 각 간트 사이드바 행(CycleGantt.laneRows)
                // 템플릿용 요약(배지·버튼 활성·헤더 통계). 상세 슬라이스는 클로저 _flowAct/_brAct.
                flowAct: { headCallId: null, tailCallId: null, editable: true, unionMode: false, hasRibbon: false,
                           avgCycleMs: null, avgActiveMs: null, tailCompletionSource: null, visibleLanes: 0, totalLanes: 0 },
                brAct: { bi: -1, headCallId: null, tailCallId: null, hasRibbon: false,
                         avgCycleMs: null, avgActiveMs: null, tailCompletionSource: null, excluded: [], visibleLanes: 0, totalLanes: 0, hiddenLanes: 0 },
                // 분기 간트 '제외 call 접기'(2026-09-07) — ON 이면 제외 call 을 얇은 띠 대신 행 자체를 숨긴다(사이드바+SVG).
                //   표시만 줄이며 분기 판정·리본은 불변. localStorage 보존(브라우저별). 기본 OFF = 종전 접힌 띠 표시.
                hideExcl: false,
                // CALL 정렬 모드 — 'signal'(시작 맨 위·끝 맨 아래·사이는 첫 신호 시각 순, Work 헤더 없음)
                //                 | 'work'(Work 그룹 고정 순서 = 모델 순, 시작/끝을 바꿔도 행이 안 움직임). localStorage 보존.
                sortMode: 'signal',
                laneFilter: '',          // 간트 내부 검색(사이드바 행 필터) — call/Work/태그/I/O 쌍 이름 부분 일치, 공백 AND
                // 복수 선택 모드(분기 탭) — 체크한 call 들을 '선택 제외/해제' 로 일괄 적용. selCalls = callName → true
                selMode: false, selCalls: {}, selMsg: '', _selAnchor: null,
                branchMsg: '',
                branchError: '',
                tab: 'trend',   // 'trend' | 'cycle' | 'history' — 콘텐츠 탭 전환
                // 페이지 뷰 모드 — 'trend'(추이 분석 전용) | 'cycle'(사이클 분석 전용) | 'both'(구 flow.html: 탭으로 둘 다).
                // flow-trend.html / flow-cycle.html 이 window.DSP_FLOW_VIEW 로 지정. init() 에서 확정 + 불필요한 로드를 건너뛴다.
                view: 'both',

                // ── 기간별 추이 ──
                period: 'today',
                trendLoading: false,
                trend: { cycleCount: 0, idleCount: 0, avgCT: null, avgMT: null, avgWT: null, minCT: null, maxCT: null, utilization: 0, totalMt: 0, totalWt: 0 },
                buckets: [],
                granularity: 'hour',
                periodStart: null, periodEnd: null,
                _trendSeq: 0,
                _drawRetry: 0,
                trendClipped: false,   // 추이 차트 Y축이 이상치 때문에 로버스트 상한으로 축약됐는지
                // 날짜 직접 지정(기간) — 프리셋(오늘/7·30·60일) 외에 시작·종료를 직접 골라 조회.
                trendRangeOpen: false, customStart: '', customEnd: '', _trendRangeTimer: null,
                trendExporting: false,   // 추이 Excel 내보내기 진행
                // 전체 추이 모드 — /flow-trend 에 ?name= 없이 진입하면 라인 전체(모든 Flow) 사이클을 합산해 추이를 본다.
                //   nav 트리(/api/nav)로 Flow 이름을 모아 각 Flow 히스토리를 병렬 조회 후 병합. 추이 페이지 전용.
                allMode: false,
                allFlowNames: [],

                // ── 사이클 분석 (구 cycle-time-analysis, 이 Flow 스코프) ──
                selectedFlow: '',
                startTime: '', endTime: '',
                isLoading: false, overlayBusy: false, errorMessage: null,
                recomputeBusy: false, recomputeMsg: '', recomputeError: false,
                callLanes: [],
                cycleBoundaries: [], tailEdges: [],
                chartStart: null, chartEnd: null,
                chartStartIso: '', chartEndIso: '',
                cycleBoundariesIso: [], tailEdgesIso: [],
                plotWidth: 1200, baseWidth: 1200, zoom: 1, viewMode: 'bar',
                headCallId: null, tailCallId: null,
                projectHeadId: null, projectTailId: null,
                userOverrodeHeadTail: false,
                // Tail 1차 제안(2026-08-24) — Head 를 찍으면 서버가 Tail 후보를 골라 채운다.
                //   자동 확정이 아니라 입력 보조: 사용자가 Tail 을 직접 찍으면 즉시 해제되고 다시 덮지 않는다.
                //   목적 = 격사이클 Tail 오지정 방지(실측 xgk103: head 978 vs tail 440 → 가동시간 2배 계상).
                tailSuggested: false, tailSuggestReason: '',
                isOverride: false,
                exporting: false,
                avgCycleMs: null, avgActiveMs: null,
                tailCompletionSource: null,
                cycleView: 'chart',   // 사이클 목록: 'table' | 'chart' (기본=차트)
                cyclePreset: null,    // 활성 사이클-기준 프리셋(최근 N 사이클) — 시간 프리셋/수동 변경 시 해제
                timePreset: null,     // 활성 시간 프리셋('m1'|'m5'|'m30'|'h1'|'h24') — 사이클/수동 변경 시 해제
                rangePopupOpen: false, // 시작·종료 직접 지정 팝업 표시
                dataLatestAt: null,   // 프리셋 앵커 = DB 최신 로그 시각(벽시계 now 아님) — effectiveLatest() 가 채움
                dataAnchorHint: '',   // 그 앵커의 지연 안내 문구(1분 미만이면 빈 문자열 = 표시 안 함)
                callLanesRaw: [],
                unmeasuredRegions: [],   // [{startMs,endMs,cause}] — 서버 미계측(수신 공백) 구간, 간트 회색 오버레이
                expandedCalls: {},   // callId → bool : Call lane 행 확장(소속 ApiCall + 실측 duration 표시) 상태
                applyDurBusy: false, applyDurMsg: '',   // 실측 → AASX duration 적용 진행/피드백
                // ── 디바이스 Duration/Min/Max 직접 편집 다이얼로그 (초 단위 입력, 빈칸=null) ──
                durEditOpen: false,
                durEditCtx: null,               // { workId, name, m, curDurMs, curMinMs, curMaxMs }
                durEditForm: { dur: '', min: '', max: '' },   // 초 단위 문자열, '' = null(미설정)
                showMaxGap: false,
                topGaps: [],
                selectedGapIndex: 0,
                selectedRange: null,
                // 이동/확대는 툴바 슬라이더 전담(간트 휠 줌은 제거). 간트 가로 드래그는 "구간 선택" 으로 유지된다.
                panPct: 0,          // 이동 슬라이더 값 0~1000 (= 가로 스크롤 비율)
                canPan: false,      // 스크롤 여지가 있을 때만 이동 슬라이더 활성
                _geo: null, _drag: null, _timeReloadTimer: null,

                // ── 최근 히스토리 (구 대시보드 하단) ──
                flowHistory: [],
                histAvgCt: 0, histStdCt: 0,
                _histShownFor: null,
                histView: 'chart',
                histLimit: 200,
                rangeByFlow: {},
                rangeModalOpen: false,
                // 미완료(완료신호 없는) 사이클 제외 — 로컬 표시 토글(기본 ON). CT 범위 이상치와는 별개 개념.
                excludeIncomplete: true,
                rangeForm: { min: '', max: '', minUnit: 's', maxUnit: 's' },
                _unitSec: { s: 1, m: 60, h: 3600 },

                _conn: null, _dt: null, _pollTimer: null,

                async init() {
                    this.dark = localStorage.getItem('dspilot-theme') === 'dark';
                    window.addEventListener('storage', (e) => { if (e.key === 'dspilot-theme') this.dark = e.newValue === 'dark'; });
                    // 미완료 제외 토글 복원(로컬 UI 선호 — 기본 ON)
                    this.excludeIncomplete = localStorage.getItem('dspilot-flow-exclude-incomplete') !== '0';
                    // 간트 CALL 정렬 모드 복원(기본 = 시작·끝 정렬)
                    try { this.sortMode = localStorage.getItem('dspilot-gantt-sort') === 'work' ? 'work' : 'signal'; } catch (_) { }
                    // 분기 간트 제외 call 접기 복원(기본 OFF)
                    try { this.hideExcl = localStorage.getItem('dspilot-gantt-hide-excl') === '1'; } catch (_) { }
                    this.flowName = new URLSearchParams(location.search).get('name');
                    // 뷰 모드 확정 — 전용 페이지(window.DSP_FLOW_VIEW) ▸ ?view= ▸ 기본 'both'(구 flow.html).
                    this.view = window.DSP_FLOW_VIEW || new URLSearchParams(location.search).get('view') || 'both';
                    if (this.view === 'trend') this.tab = 'trend';
                    else if (this.view === 'cycle') this.tab = 'cycle';
                    // 추이 페이지에 ?name= 없이 진입 → 전체 추이(라인 전체 합산) 모드.
                    this.allMode = (this.view === 'trend' && !this.flowName);
                    // 더티 가드 등록 — 가동시간 분석(cycle)에서 Head/Tail 미저장 이탈 방지
                    if (this.view === 'cycle') {
                        window.dspDirtyRegister(() => this.userOverrodeHeadTail || this.branchesDirty);
                        // 앵커 지연 문구는 시간이 지나면 커진다 — 30초마다 재계산(숨긴 탭에서는 정지).
                        setInterval(() => { if (!document.hidden) this.refreshAnchorHint(); }, 30000);
                    }
                    this.computePeriod('today');

                    // 컨테이너 폭 변화 시 간트 폭맞춤(줌 대체) + Chart.js 차트 강제 리사이즈
                    // Chart.js responsive:true 의 ResizeObserver 가 orientationchange 후 옛 폭을 잡아
                    // 캔버스가 컨테이너보다 넓게 고정되는 문제를 chart.resize() 로 보정한다.
                    let _rt;
                    const _resizeCharts = () => {
                        if (_charts.trend) try { _charts.trend.resize(); } catch (e) {}
                        if (_charts.count) try { _charts.count.resize(); } catch (e) {}
                    };
                    window.addEventListener('resize', () => {
                        clearTimeout(_rt);
                        _rt = setTimeout(() => {
                            if (this._drag) return;
                            if (this.callLanes.length) { this.measurePlotWidth(); this.render(); this.syncPanSoon(); }
                            _resizeCharts();
                        }, 150);
                    });
                    // orientationchange 는 resize 보다 먼저 발화하고 뷰포트 갱신은 100-300ms 뒤에 확정.
                    // resize 핸들러가 뒤따르지만 혹시 놓칠 경우를 대비해 300ms 후 한 번 더 강제 리사이즈.
                    window.addEventListener('orientationchange', () => { setTimeout(_resizeCharts, 300); });

                    if (this.allMode) {
                        // 전체 추이 — 특정 Flow 로드 없이 nav 트리에서 Flow 이름을 모아 히스토리를 합산.
                        await this.loadAllFlowNames();
                        await this.reloadTrend();
                    } else {
                        await this.loadFlow();
                        if (this.flow) {
                            this.selectedFlow = this.flow.flowName;
                            await this.reloadTrend();
                            await this.loadExclusions();
                            if (this.view === 'cycle') await this.loadBranches();
                            this.syncHistory();
                            // 사이클 분석 최초 범위 — URL 기간 파라미터(?period/from/to, 같은 페이지 나브 이동 시
                            // shell 이 실어 보냄) 복원, 없으면 기본 최근 5분. 추이 전용 페이지에서는 불필요하므로 건너뜀.
                            if (this.view !== 'trend') {
                                await this.applyRangeFromUrl();
                            }
                        }
                    }
                    this.connectSignalR();
                    if (!this.allMode) this._pollTimer = setInterval(() => this.loadFlow(true), 10000);
                },

                destroy() {
                    clearInterval(this._pollTimer);
                    clearTimeout(this._dt);
                    clearTimeout(this._timeReloadTimer);
                    clearTimeout(this._trendRangeTimer);
                    this._conn?.stop();
                    Object.values(_charts).forEach(c => { if (c) c.destroy(); });
                    if (_histChart) _histChart.destroy();
                },

                toggleTheme() { this.dark = !this.dark; localStorage.setItem('dspilot-theme', this.dark ? 'dark' : 'light'); },

                async apiGet(url) {
                    const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
                    if (res.status === 404) throw new Error('not-found');
                    if (!res.ok) throw new Error('HTTP ' + res.status);
                    return await res.json();
                },
                async apiPost(url, body) {
                    const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' }, body: JSON.stringify(body) });
                    if (!res.ok) throw new Error('HTTP ' + res.status);
                    return await res.json();
                },

                async loadFlow(silent) {
                    if (!this.flowName) { this.loading = false; this.flow = null; return; }
                    if (!silent) this.loading = true;
                    try {
                        const data = await this.apiGet('/api/flow/' + encodeURIComponent(this.flowName));
                        this.flow = data;
                        this.error = null;
                        if (!this.selectedFlow) this.selectedFlow = data.flowName;
                    } catch (e) {
                        if (e.message === 'not-found') this.error = "Flow '" + this.flowName + "' 을(를) 찾을 수 없습니다.";
                        else this.error = 'Flow 데이터를 불러오지 못했습니다: ' + e.message;
                    } finally { this.loading = false; }
                },

                // 전체 추이 모드: nav 트리에서 모든 시스템의 Flow 이름을 모은다(설비 필터 없이 라인 전체).
                async loadAllFlowNames() {
                    this.loading = true;
                    try {
                        const nav = await this.apiGet('/api/nav');
                        const names = [];
                        (nav && nav.systems || []).forEach(s => (s.flows || []).forEach(n => { if (n && names.indexOf(n) === -1) names.push(n); }));
                        this.allFlowNames = names;
                        this.error = null;
                    } catch (e) {
                        this.allFlowNames = [];
                        this.error = 'Flow 목록을 불러오지 못했습니다: ' + e.message;
                    } finally { this.loading = false; }
                },

                async refreshAll() {
                    if (this.allMode) { await this.reloadTrend(); return; }
                    await this.loadFlow(); if (this.flow) { await this.reloadTrend(); this.syncHistory(); }
                },

                // 탭 전환 — 숨겨진 탭(display:none)에서 0 크기로 렌더된 차트/간트를 보일 때 다시 맞춘다.
                setTab(t) {
                    if (this.tab === t) return;
                    this.tab = t;
                    this.$nextTick(() => {
                        if (t === 'trend') { if (this.trend.cycleCount > 0) this.drawCharts(); }
                        else if (t === 'cycle') { if (this.callLanes.length) { this.measurePlotWidth(); this.render(); } if (this.cycleView === 'chart') this.renderCycleChart(); }
                        else if (t === 'history') { if (this.histView === 'chart') this.renderHistChart(); }
                    });
                },

                kpi(field) { return (this.flow && this.flow.kpi) ? this.flow.kpi[field] : null; },

                get updateTime() {
                    if (!this.flow || !this.flow.timestamp) return '-';
                    const d = new Date(this.flow.timestamp); if (isNaN(d)) return '-';
                    const p = (x) => String(x).padStart(2, '0');
                    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
                },

                stateClass(s) {
                    switch ((s || '').toLowerCase()) {
                        case 'going': return 'warn';
                        case 'finish': return 'info';
                        case 'error': case 'abort': return 'bad';
                        case 'ready': return 'ok';
                        default: return 'info';
                    }
                },

                // ── 실시간 (디바운스 refetch) ──
                connectSignalR() {
                    if (!window.signalR) return;
                    const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/monitoring').withAutomaticReconnect([0, 0, 1000, 3000, 5000, 10000]).build();
                    const trigger = () => { clearTimeout(this._dt); this._dt = setTimeout(() => { this.loadFlow(true); this.syncHistory(); }, 250); };
                    conn.on('CallStateChangedBatch', trigger);
                    conn.on('CallStateChanged', trigger);
                    conn.on('DatabaseRebuilt', () => { this.refreshAll(); });
                    conn.on('FlowHistoryCleared', () => { this.refreshAll(); });
                    conn.on('ExclusionsChanged', () => { this.loadExclusions(); });   // 다른 화면이 제외 범위 변경 → 동기화
                    conn.onreconnected(() => { this.rt.connected = true; this.loadFlow(true); this.syncHistory(); });
                    conn.onreconnecting(() => { this.rt.connected = false; });
                    conn.onclose(() => { this.rt.connected = false; });
                    conn.start().then(() => { this.rt.connected = true; }).catch(() => { this.rt.connected = false; });
                    this._conn = conn;
                },

                // ════════════════════════════════════════════════════════════════
                //  기간별 추이 (클라 집계 — 대시보드 히스토리 재사용)
                // ════════════════════════════════════════════════════════════════
                computePeriod(preset) {
                    const now = new Date();
                    const startOfDay = new Date(now.getFullYear(), now.getMonth(), now.getDate());
                    this.period = preset;
                    if (preset === 'today') { this.periodStart = startOfDay; this.granularity = 'hour'; }
                    else if (preset === '7d') { this.periodStart = new Date(startOfDay.getTime() - 6 * 864e5); this.granularity = 'day'; }
                    else if (preset === '30d') { this.periodStart = new Date(startOfDay.getTime() - 29 * 864e5); this.granularity = 'day'; }
                    else if (preset === '60d') { this.periodStart = new Date(startOfDay.getTime() - 59 * 864e5); this.granularity = 'day'; }
                    else { this.periodStart = startOfDay; this.granularity = 'hour'; this.period = 'today'; }
                    this.periodEnd = now;
                },
                async setPeriod(preset) { this.trendRangeOpen = false; this.computePeriod(preset); await (window.dspLoading ? window.dspLoading.wrap(() => this.reloadTrend(), '기간 데이터 불러오는 중…') : this.reloadTrend()); },

                // ── 날짜 직접 지정(기간) ──
                openTrendRange() {
                    // 현재 기간을 입력칸 기본값으로 채우고 팝업 토글.
                    if (!this.customStart && this.periodStart) this.customStart = this.dateToInput(this.periodStart);
                    if (!this.customEnd && this.periodEnd) this.customEnd = this.dateToInput(this.periodEnd);
                    this.trendRangeOpen = !this.trendRangeOpen;
                },
                onTrendRangeChanged() {
                    if (!this.customStart || !this.customEnd) return;
                    let s = this.inputToDate(this.customStart), e = this.inputToDate(this.customEnd);
                    if (isNaN(s) || isNaN(e) || e.getTime() <= s.getTime()) return;
                    // 커스텀 기간 상한(2개월) — 초과 시 종료 기준으로 시작을 당기고 토스트 안내(shell.js SSOT).
                    if (window.dspClampRange) {
                        const r = window.dspClampRange(s, e, 'end');
                        if (r.clamped) {
                            s = r.start; e = r.end;
                            this.customStart = this.dateToInput(s);
                            this.customEnd = this.dateToInput(e);
                            if (window.dspToast) window.dspToast(window.dspRangeClampMsg, 'warning');
                        }
                    }
                    clearTimeout(this._trendRangeTimer);
                    this._trendRangeTimer = setTimeout(async () => {
                        this.period = 'custom';
                        this.periodStart = s; this.periodEnd = e;
                        // 버킷 단위 = 범위 길이에 맞춤: ≤2일→1시간, ≤92일→1일, 그 이상→1주.
                        const days = (e.getTime() - s.getTime()) / 864e5;
                        this.granularity = days <= 2 ? 'hour' : (days <= 92 ? 'day' : 'week');
                        await (window.dspLoading ? window.dspLoading.wrap(() => this.reloadTrend(), '기간 데이터 불러오는 중…') : this.reloadTrend());
                    }, 350);
                },

                // ── 내보내기 (기간별 추이) ─────────────────────────────────────────────
                trendName() { return this.allMode ? '전체추이' : (this.flow ? this.flow.flowName : (this.flowName || 'Flow')); },
                _stamp() { const t = new Date(); const p = (x) => String(x).padStart(2, '0'); return `${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}`; },
                _downloadBlob(filename, blob) {
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement('a'); a.href = url; a.download = filename;
                    document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url);
                },

                // Excel = 차트(화면 캔버스 캡처) + 데이터. 서버(TrendExcelExporter)가 렌더.
                async exportTrendExcel() {
                    if (!this.buckets.length || this.trendExporting) return;
                    this.trendExporting = true; this.error = null;
                    try {
                        const root = this.$root || document;
                        const grab = (ref, label) => {
                            const cv = root.querySelector(`canvas[x-ref="${ref}"]`);
                            if (!cv) return null;
                            try {
                                const rc = cv.getBoundingClientRect();
                                return { name: label, dataUrl: cv.toDataURL('image/png'), width: Math.round(rc.width), height: Math.round(rc.height) };
                            } catch (e) { return null; }
                        };
                        const images = [
                            grab('trendChart', '기간별 가동시간 (동작·대기)'),
                            grab('countChart', '가동횟수'),
                        ].filter(Boolean);
                        const model = {
                            title: this.trendName(),
                            systemName: (this.flow && this.flow.systemName) ? this.flow.systemName : (this.allMode ? '라인 전체' : null),
                            periodStart: this.periodStart ? this.dateToInput(this.periodStart) : '',
                            periodEnd: this.periodEnd ? this.dateToInput(this.periodEnd) : '',
                            granularity: this.granularity,
                            stats: {
                                cycleCount: this.trend.cycleCount, idleCount: this.trend.idleCount,
                                avgCT: this.trend.avgCT, avgMT: this.trend.avgMT, avgWT: this.trend.avgWT,
                                minCT: this.trend.minCT, maxCT: this.trend.maxCT,
                                utilization: this.trend.utilization, totalMt: this.trend.totalMt, totalWt: this.trend.totalWt
                            },
                            buckets: this.buckets.map(b => ({ ts: this.dateToInput(new Date(b.ts)), count: b.count, idle: b.idle, avgCT: b.avgCT, avgMT: b.avgMT, avgWT: b.avgWT })),
                            images
                        };
                        const res = await fetch('/api/flow-trend/export-excel', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(model)
                        });
                        if (!res.ok) throw new Error('HTTP ' + res.status);
                        let fn = `Trend_${this.trendName()}_${this._stamp()}.xlsx`;
                        const cd = res.headers.get('Content-Disposition');
                        if (cd) {
                            const star = cd.match(/filename\*=(?:UTF-8'')?([^;]+)/i);
                            const plain = cd.match(/filename="?([^";]+)"?/i);
                            if (star) { try { fn = decodeURIComponent(star[1].trim()); } catch (_) {} }
                            else if (plain) { fn = plain[1].trim(); }
                        }
                        this._downloadBlob(fn, await res.blob());
                    } catch (e) {
                        this.error = 'Excel 내보내기 실패: ' + e.message;
                    } finally { this.trendExporting = false; }
                },

                get trendSubtitle() {
                    const fmt = (d) => { if (!d) return '-'; const p = (x) => String(x).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`; };
                    const g = this.granularity === 'hour' ? '1시간' : this.granularity === 'day' ? '1일' : this.granularity === 'week' ? '1주' : this.granularity;
                    return `현재 기간: ${fmt(this.periodStart)} ~ ${fmt(this.periodEnd)} (버킷: ${g}, 가동 ${this.trend.cycleCount.toLocaleString()}회)`;
                },

                async reloadTrend() {
                    if (this.view === 'cycle') return;   // 사이클 전용 페이지 — 추이 로드 건너뜀
                    if (!this.flow && !this.allMode) return;
                    const seq = ++this._trendSeq;
                    this.trendLoading = true;
                    try {
                        let hist;
                        if (this.allMode) {
                            // 라인 전체(모든 Flow) 히스토리를 병렬 조회 후 하나의 사이클 목록으로 병합.
                            const lists = await Promise.all(this.allFlowNames.map(n =>
                                this.apiGet('/api/dashboard/flows/' + encodeURIComponent(n) + '/history?limit=2000').catch(() => [])));
                            hist = lists.flat();
                        } else {
                            hist = await this.apiGet('/api/dashboard/flows/' + encodeURIComponent(this.flow.flowName) + '/history?limit=2000');
                        }
                        if (seq !== this._trendSeq) return;
                        const startMs = this.periodStart.getTime(), endMs = this.periodEnd.getTime();
                        const rows = (hist || []).filter(h => { const t = new Date(h.recordedAt).getTime(); return t >= startMs && t <= endMs; });
                        this.buildStats(rows);
                        this.buckets = this.buildBuckets(rows);
                        this.$nextTick(() => this.drawCharts());
                    } catch (e) {
                        if (seq === this._trendSeq) { this.resetStats(); this.buckets = []; }
                    } finally { if (seq === this._trendSeq) this.trendLoading = false; }
                },

                resetStats() { this.trend = { cycleCount: 0, idleCount: 0, avgCT: null, avgMT: null, avgWT: null, minCT: null, maxCT: null, utilization: 0, totalMt: 0, totalWt: 0 }; },

                buildStats(rows) {
                    this.resetStats();
                    if (rows.length === 0) return;
                    const ct = rows.filter(r => r.ct != null).map(r => r.ct);
                    const mt = rows.filter(r => r.mt != null).map(r => r.mt);
                    const wt = rows.filter(r => r.wt != null).map(r => r.wt);
                    const avg = (a) => a.reduce((s, v) => s + v, 0) / a.length;
                    const sum = (a) => a.reduce((s, v) => s + v, 0);
                    this.trend.cycleCount = rows.length;
                    this.trend.idleCount = rows.filter(r => r.isIdle).length;
                    if (ct.length) { this.trend.avgCT = avg(ct); this.trend.minCT = Math.min(...ct); this.trend.maxCT = Math.max(...ct); }
                    if (mt.length) this.trend.avgMT = avg(mt);
                    if (wt.length) this.trend.avgWT = avg(wt);
                    this.trend.totalMt = sum(mt); this.trend.totalWt = sum(wt);
                    const denom = this.trend.totalMt + this.trend.totalWt;
                    this.trend.utilization = denom > 0 ? this.trend.totalMt / denom : 0;
                },

                truncBucket(d) {
                    if (this.granularity === 'hour') return new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours());
                    if (this.granularity === 'week') { const wd = new Date(d.getFullYear(), d.getMonth(), d.getDate()); wd.setDate(wd.getDate() - wd.getDay()); return wd; }
                    return new Date(d.getFullYear(), d.getMonth(), d.getDate()); // day
                },
                nextBucket(d) {
                    const n = new Date(d);
                    if (this.granularity === 'hour') n.setHours(n.getHours() + 1);
                    else if (this.granularity === 'week') n.setDate(n.getDate() + 7);
                    else if (this.granularity === 'month') n.setMonth(n.getMonth() + 1);
                    else n.setDate(n.getDate() + 1); // day
                    return n;
                },
                buildBuckets(rows) {
                    const map = new Map();
                    for (const r of rows) {
                        const key = this.truncBucket(new Date(r.recordedAt)).getTime();
                        let a = map.get(key);
                        if (!a) { a = { key, count: 0, idle: 0, sCt: 0, nCt: 0, sMt: 0, nMt: 0, sWt: 0, nWt: 0 }; map.set(key, a); }
                        a.count++; if (r.isIdle) a.idle++;
                        if (r.ct != null) { a.sCt += r.ct; a.nCt++; }
                        if (r.mt != null) { a.sMt += r.mt; a.nMt++; }
                        if (r.wt != null) { a.sWt += r.wt; a.nWt++; }
                    }
                    const emit = (a, key) => ({
                        ts: key,
                        avgCT: a && a.nCt > 0 ? a.sCt / a.nCt : 0,
                        avgMT: a && a.nMt > 0 ? a.sMt / a.nMt : 0,
                        avgWT: a && a.nWt > 0 ? a.sWt / a.nWt : 0,
                        count: a ? a.count : 0, idle: a ? a.idle : 0,
                    });
                    // 기간 내 모든 단위시간 버킷을 빠짐없이 생성(데이터 없으면 0). — 시계열 연속성 규약
                    const out = [];
                    if (this.periodStart && this.periodEnd) {
                        let d = this.truncBucket(this.periodStart);
                        const endMs = this.periodEnd.getTime();
                        let guard = 0;
                        while (d.getTime() <= endMs && guard++ < 10000) {
                            const key = d.getTime();
                            out.push(emit(map.get(key), key));
                            map.delete(key);
                            d = this.nextBucket(d);
                        }
                    }
                    // 경계 절삭 오차 등으로 범위 밖에 남은 버킷도 유실 없이 편입.
                    for (const a of [...map.values()]) out.push(emit(a, a.key));
                    return out.sort((x, y) => x.ts - y.ts);
                },

                drawCharts() {
                    if (!window.Chart) return;
                    Object.values(_charts).forEach(c => { if (c) c.destroy(); });
                    _charts = { trend: null, count: null };
                    if (this.trend.cycleCount === 0) { this._drawRetry = 0; return; }
                    // 캔버스를 $refs 가 아닌 DOM 에서 직접 찾는다.
                    // 첫 접속 시 중첩 x-if(flow→추이) 가 mount 될 때 Alpine 이 $refs.trendChart 를
                    // 간헐적으로 등록하지 못해(캔버스는 DOM 에 있는데 $refs 는 비어 있음) 차트가 안 그려졌다.
                    const root = this.$root || document;
                    const trendCv = root.querySelector('canvas[x-ref="trendChart"]');
                    const countCv = root.querySelector('canvas[x-ref="countChart"]');
                    // 아직 mount 전이면(템플릿 미렌더) 다음 프레임 재시도.
                    if (!trendCv) {
                        if (this._drawRetry < 60) { this._drawRetry++; requestAnimationFrame(() => this.drawCharts()); }
                        return;
                    }
                    this._drawRetry = 0;

                    const labelFor = (ts) => {
                        const d = new Date(ts); const p = (x) => String(x).padStart(2, '0');
                        if (this.granularity === 'hour') return `${p(d.getHours())}:00`;
                        return `${p(d.getMonth() + 1)}-${p(d.getDate())}`;
                    };
                    const labels = this.buckets.map(b => labelFor(b.ts));
                    const toSec = (ms) => Math.round(ms / 1000 * 10) / 10;
                    const css = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim() || '#888';
                    const cCt = css('--color-primary') || '#0E7CCB';
                    const grid = css('--color-lines') || 'rgba(14,27,42,0.10)';
                    const tickColor = css('--color-text-secondary') || '#5A6B7E';

                    // hex(#RGB/#RRGGBB) → rgba 문자열 (차트 영역 채움용)
                    const hexA = (hex, a) => {
                        let h = (hex || '').replace('#', '').trim();
                        if (h.length === 3) h = h.split('').map(c => c + c).join('');
                        if (h.length !== 6) return `rgba(14,124,203,${a})`;
                        const n = parseInt(h, 16);
                        return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${a})`;
                    };
                    // 라벨용 짧은 시간 포맷 (막대 위 이상치 표시)
                    const fmtShort = (sec) => { const ms = sec * 1000; if (ms >= 3600000) return Math.round(ms / 3600000) + '시간'; if (ms >= 60000) return Math.round(ms / 60000) + '분'; return Math.round(ms / 1000) + '초'; };

                    if (trendCv) {
                        // 최근 히스토리와 동일한 차트 스타일: 버킷별 MT/WT 스택 막대 + 평균 CT 기준선
                        const cssRaw = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
                        const cMtBar = cssRaw('--dash-mt') || cssRaw('--color-primary') || '#12A594';
                        const cWtBar = cssRaw('--dash-wt') || '#AEB9C6';
                        const cRed = cssRaw('--red') || cssRaw('--color-error') || '#D8392B';
                        const mtData = this.buckets.map(b => toSec(b.avgMT));
                        const wtData = this.buckets.map(b => toSec(b.avgWT));
                        const avg = this.trend.avgCT > 0 ? toSec(this.trend.avgCT) : null;

                        // ── 이상치 대응 Y축 상한(로버스트) ──
                        // 가동이 멈춘 한 구간의 대기시간이 유난히 크면 축이 늘어나 나머지 막대가 납작해져
                        // "허전"하게 보인다. 뚜렷한 이상치(최댓값 > p90×2)일 때만 축 상한을 p90 기반으로 고정하고,
                        // 잘린 막대는 ▲+실제값 라벨과 툴팁으로 정직하게 노출한다.
                        const totalsSec = this.buckets.map(b => toSec(b.avgMT) + toSec(b.avgWT)).filter(v => v > 0).sort((a, b) => a - b);
                        let yMax = null;
                        if (totalsSec.length >= 4) {
                            const at = (q) => totalsSec[Math.min(totalsSec.length - 1, Math.floor(q * totalsSec.length))];
                            const p90 = at(0.90), median = at(0.5), maxT = totalsSec[totalsSec.length - 1];
                            if (p90 > 0 && maxT > p90 * 2) {
                                yMax = Math.ceil(Math.max(p90 * 1.25, median * 1.5, (avg || 0) * 1.2));
                            }
                        }
                        this.trendClipped = yMax != null;

                        const datasets = [
                            { label: '동작시간', data: mtData, backgroundColor: cMtBar, stack: 'ct', borderWidth: 0, borderRadius: 3, maxBarThickness: 40 },
                            { label: '대기시간', data: wtData, backgroundColor: cWtBar, stack: 'ct', borderWidth: 0, borderRadius: 3, maxBarThickness: 40 },
                        ];
                        if (avg != null) {
                            datasets.push({ type: 'line', label: '평균 가동시간', data: labels.map(() => avg),
                                borderColor: cRed, borderWidth: 1.5, borderDash: [5, 4], pointRadius: 0, fill: false });
                        }
                        const self = this;
                        const fmtMs = (s) => self.fmt(s * 1000);
                        // 잘린 막대(합계 > yMax) 위에 ▲+실제값을 그려 데이터 은폐를 방지
                        const overflowPlugin = {
                            id: 'fwTrendOverflow',
                            afterDatasetsDraw(chart) {
                                if (yMax == null) return;
                                const ctx = chart.ctx, area = chart.chartArea;
                                const meta = chart.getDatasetMeta(0);
                                if (!meta || !meta.data) return;
                                ctx.save();
                                ctx.font = '700 10px ' + (getComputedStyle(document.body).fontFamily || 'sans-serif');
                                ctx.fillStyle = cRed;
                                ctx.textAlign = 'center';
                                for (let i = 0; i < meta.data.length; i++) {
                                    const total = (mtData[i] || 0) + (wtData[i] || 0);
                                    if (total > yMax + 0.01 && meta.data[i]) {
                                        ctx.fillText('▲ ' + fmtShort(total), meta.data[i].x, area.top + 11);
                                    }
                                }
                                ctx.restore();
                            }
                        };
                        _charts.trend = new Chart(trendCv, {
                            type: 'bar',
                            data: { labels, datasets },
                            plugins: [overflowPlugin],
                            options: {
                                responsive: true, maintainAspectRatio: false, animation: false,
                                interaction: { mode: 'index', intersect: false },
                                layout: { padding: { top: yMax != null ? 16 : 4 } },
                                plugins: {
                                    legend: { position: 'top', labels: { color: tickColor, boxWidth: 12, usePointStyle: true, pointStyle: 'rectRounded', font: { size: 11 } } },
                                    tooltip: {
                                        filter: (it) => it.dataset.type !== 'line',
                                        callbacks: {
                                            title: (items) => items[0].chart.$ctx.labels[items[0].dataIndex] || '',
                                            label: (c) => {
                                                const x = c.chart.$ctx, idx = c.dataIndex;
                                                const ct = (x.mt[idx] ?? 0) + (x.wt[idx] ?? 0);
                                                const v = c.parsed.y || 0;
                                                const pct = ct > 0 ? Math.round(v / ct * 100) : 0;
                                                return `${c.dataset.label}: ${fmtMs(v)} (${pct}%)`;
                                            },
                                            afterBody: (items) => {
                                                const x = items[0].chart.$ctx, idx = items[0].dataIndex;
                                                const ctVal = (x.mt[idx] ?? 0) + (x.wt[idx] ?? 0);
                                                const lines = ['가동시간 (전체): ' + fmtMs(ctVal)];
                                                if (x.avg != null) lines.push('평균 가동시간: ' + fmtMs(x.avg));
                                                return lines;
                                            },
                                        }
                                    },
                                },
                                scales: {
                                    x: { stacked: true, grid: { display: false }, ticks: { color: tickColor, font: { size: 10 }, maxRotation: 0, autoSkip: false, callback: edgeTickCallback } },
                                    y: { stacked: true, beginAtZero: true, min: 0, max: yMax != null ? yMax : undefined, grid: { color: grid }, ticks: { color: tickColor, font: { size: 10 }, callback: (v) => fmtMs(v) }, title: { display: true, text: '시간', color: tickColor } },
                                },
                            }
                        });
                        _charts.trend.$ctx = { labels, mt: mtData, wt: wtData, avg };
                    }
                    if (countCv) {
                        const cctx = countCv.getContext('2d');
                        const grad = cctx.createLinearGradient(0, 0, 0, countCv.clientHeight || 260);
                        grad.addColorStop(0, hexA(cCt, 0.30));
                        grad.addColorStop(1, hexA(cCt, 0.02));
                        _charts.count = new Chart(countCv, {
                            type: 'line',
                            data: { labels, datasets: [{ label: '가동횟수', data: this.buckets.map(b => b.count), borderColor: cCt, backgroundColor: grad, borderWidth: 2.5, tension: 0.35, pointRadius: 0, pointHoverRadius: 5, pointHoverBackgroundColor: cCt, pointHoverBorderColor: '#fff', pointHoverBorderWidth: 2, fill: true, spanGaps: true }] },
                            options: { responsive: true, maintainAspectRatio: false, interaction: { mode: 'index', intersect: false }, plugins: { legend: { display: false }, tooltip: { callbacks: { title: (items) => items[0].label || '', label: (c) => '가동 ' + (c.parsed.y ?? 0).toLocaleString() + '회' } } }, scales: { x: { grid: { display: false }, ticks: { color: tickColor, font: { size: 10 }, maxRotation: 0, autoSkip: false, callback: edgeTickCallback } }, y: { beginAtZero: true, grid: { color: grid }, ticks: { color: tickColor, precision: 0 } } } }
                        });
                    }
                },

                fmtDur(ms) {
                    if (ms == null || ms <= 0) return '—';
                    if (ms < 1000) return Math.round(ms) + 'ms';
                    if (ms < 60000) return (ms / 1000).toFixed(1) + '초';
                    if (ms < 3600000) return Math.floor(ms / 60000) + '분 ' + Math.floor((ms % 60000) / 1000) + '초';
                    return Math.floor(ms / 3600000) + '시간 ' + Math.floor((ms % 3600000) / 60000) + '분 ' + Math.floor((ms % 60000) / 1000) + '초';
                },

                // ════════════════════════════════════════════════════════════════
                //  사이클 분석 (구 cycle-time-analysis @code — 이 Flow 스코프)
                // ════════════════════════════════════════════════════════════════
                measurePlotWidth() {
                    const el = this.chartAreaEl();
                    const avail = el ? el.clientWidth : 1100;
                    const minW = minPlotW();
                    this.baseWidth = Math.max(minW, Math.round(avail - LEFT_PAD - RIGHT_PAD - 4));
                    this.plotWidth = Math.max(minW, Math.round(this.baseWidth * this.zoom));
                },
                setView(mode) {
                    if (this.viewMode === mode) return;
                    this.viewMode = mode;
                    this.render();
                },
                // ── 간트 이동/확대 슬라이더 ────────────────────────────────────────────
                // 확대 슬라이더는 로그 스케일(0=100%, 1000=MAX_ZOOM) — 선형이면 100~200% 구간이
                // 슬라이더 왼쪽 끝 몇 px 에 뭉쳐 실용성이 없다.
                get zoomPct() {
                    const z = Math.min(MAX_ZOOM, Math.max(1, this.zoom));
                    return Math.round(Math.log(z) / Math.log(MAX_ZOOM) * 1000);
                },
                onZoomSlider(value) {
                    const el = this.chartAreaEl();
                    if (!el || !this.callLanes.length) return;
                    const t = Math.min(1, Math.max(0, Number(value) / 1000));
                    // 앵커 = 현재 보이는 구간의 가운데 → 확대해도 보던 시각이 화면에 남는다.
                    this.applyZoom(Math.exp(t * Math.log(MAX_ZOOM)), el.clientWidth / 2);
                },
                // svgMarkup 반영(다음 틱) 후에도 SVG 폭은 그 프레임의 레이아웃이 끝나야 확정된다.
                // 틱만으로 재면 scrollWidth 가 아직 옛 값이라 이동 슬라이더 활성 여부가 어긋난다 → 프레임 뒤에 잰다.
                syncPanSoon() { this.$nextTick(() => requestAnimationFrame(() => this.syncPan())); },
                // 폭 확정 시점은 틱/프레임으로 못 박는다 — 사이드바(CALL 목록)가 뒤늦게 넓어지면 차트 영역이
                // 그만큼 줄어 스크롤 여지가 새로 생긴다. 크기 변화를 직접 관찰해 슬라이더 상태를 맞춘다.
                observePan(el) {
                    if (!el || !window.ResizeObserver) return;
                    // 관찰 대상 el 을 그대로 넘긴다 — 첫 mount 시점엔 $refs.chartArea 가 아직 미등록일 수
                    // 있어(중첩 x-if 레이스) syncPan 이 빈손으로 돌아가고 슬라이더가 비활성으로 굳는다.
                    const ro = new ResizeObserver(() => this.syncPan(el));
                    ro.observe(el);
                    if (el.firstElementChild) ro.observe(el.firstElementChild);   // .ct-gantt-wrapper (SVG 폭)
                },
                // 간트 스크롤 컨테이너. 상단 flow 간트가 이동/확대 슬라이더의 기준(둘은 폭·줌 공유).
                chartAreaEl() { return this.$refs.flowChart || document.querySelector('.ct-gantt-hscroll'); },
                // 현재 존재하는 모든 간트 스크롤 컨테이너(상단 flow + 하단 분기) — 이동/확대는 둘을 함께 맞춘다.
                chartAreaEls() { return Array.from(document.querySelectorAll('.ct-gantt-hscroll')); },
                // 현재 스크롤 위치 → 이동 슬라이더 값(+ 활성 여부). 스크롤 이벤트/줌/로드 후 호출. 기준 = flow 간트.
                syncPan(el) {
                    el = el || this.chartAreaEl();
                    if (!el) { this.panPct = 0; this.canPan = false; return; }
                    const max = el.scrollWidth - el.clientWidth;
                    this.canPan = max > 1;
                    this.panPct = max > 1 ? Math.round(el.scrollLeft / max * 1000) : 0;
                },
                onPanSlider(value) {
                    const t = Math.min(1, Math.max(0, Number(value) / 1000));
                    this.panPct = Math.round(t * 1000);
                    this.chartAreaEls().forEach(el => { const max = el.scrollWidth - el.clientWidth; el.scrollLeft = max > 0 ? t * max : 0; });
                },
                // 한 간트를 스크롤하면 다른 간트도 같은 위치로(두 간트 가로축 잠금) + 슬라이더 동기. 피드백 루프는 플래그로 차단.
                syncScroll(el) {
                    if (this._scrolling) { this.syncPan(el); return; }
                    this._scrolling = true;
                    const left = el.scrollLeft;
                    this.chartAreaEls().forEach(c => { if (c !== el && Math.abs(c.scrollLeft - left) > 0.5) c.scrollLeft = left; });
                    this.syncPan(el);
                    requestAnimationFrame(() => { this._scrolling = false; });
                },
                resetZoom() {
                    this.zoom = 1;
                    this.measurePlotWidth();
                    this.render();
                    this.$nextTick(() => {
                        this.chartAreaEls().forEach(el => { el.scrollLeft = 0; });
                        requestAnimationFrame(() => this.syncPan());
                    });
                },
                // el = 앵커(Ctrl+휠이 일어난 간트) — 줌 자체는 두 간트 공유, 스크롤도 함께 같은 위치로 맞춘다.
                applyZoom(targetZoom, anchorX, el) {
                    el = el || this.chartAreaEl();
                    if (!el) return;
                    const newZoom = Math.min(MAX_ZOOM, Math.max(1, targetZoom));
                    if (Math.abs(newZoom - this.zoom) < 1e-6) return;
                    const plotAreaX = Math.max(0, anchorX + el.scrollLeft - LEFT_PAD);
                    const frac = this.plotWidth > 0 ? Math.min(1, plotAreaX / this.plotWidth) : 0;
                    this.zoom = newZoom;
                    this.plotWidth = Math.max(minPlotW(), Math.round(this.baseWidth * this.zoom));
                    this.render();
                    this.$nextTick(() => {
                        const left = frac * this.plotWidth + LEFT_PAD - anchorX;
                        this.chartAreaEls().forEach(c => { c.scrollLeft = left; });
                        requestAnimationFrame(() => this.syncPan());
                    });
                },
                // Ctrl+휠 = 간트 확대/축소(커서 아래 시각 고정) — 브라우저 페이지 확대를 preventDefault 로
                // 차단해 간트 위에서는 차트 줌으로 고정한다. 일반 휠(Ctrl 없이)은 페이지 스크롤 유지.
                // 분기별 간트 카드에서도 동일 핸들러 — 줌(plotWidth)은 상단과 공유되므로 함께 확대된다.
                onGanttWheel(e) {
                    if (!e.ctrlKey || !this.callLanes.length) return;
                    e.preventDefault();
                    const el = e.currentTarget;
                    // deltaMode 1 = 줄 단위(Firefox 휠) → px 근사 환산.
                    const dy = e.deltaMode === 1 ? e.deltaY * 24 : e.deltaY;
                    const factor = Math.exp(-dy * 0.0022);   // 휠 1노치(±100px) ≈ ±25%
                    const anchorX = e.clientX - el.getBoundingClientRect().left;
                    this.applyZoom(this.zoom * factor, anchorX, el);
                },

                // 시각 helper
                toInputValue(iso) { return iso ? iso.slice(0, 19) : ''; },
                dateToInput(d) {
                    const p = (x) => String(x).padStart(2, '0');
                    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
                },
                inputToDate(v) {
                    if (!v) return new Date();
                    const m = v.match(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?/);
                    if (!m) return new Date(v);
                    return new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], m[6] ? +m[6] : 0);
                },

                onTimeChanged() {
                    if (!this.selectedFlow) return;
                    this.cyclePreset = null;   // 수동 시간 변경 → 사이클-기준 프리셋 해제
                    this.timePreset = null;    // 수동 시간 변경 → 시간 프리셋 해제
                    this.clampTimeRange();     // 커스텀 기간 상한(2개월)
                    clearTimeout(this._timeReloadTimer);
                    this._timeReloadTimer = setTimeout(() => {
                        if (this.inputToDate(this.endTime) <= this.inputToDate(this.startTime)) return;
                        this.errorMessage = null;
                        this.load();
                    }, 300);
                },
                // 시간창(start~end) 상한 — 종료 기준으로 시작을 당기고 토스트 안내(shell.js SSOT).
                clampTimeRange() {
                    if (!window.dspClampRange || !this.startTime || !this.endTime) return;
                    const r = window.dspClampRange(this.inputToDate(this.startTime), this.inputToDate(this.endTime), 'end');
                    if (!r.clamped) return;
                    this.startTime = this.dateToInput(r.start);
                    this.endTime = this.dateToInput(r.end);
                    if (window.dspToast) window.dspToast(window.dspRangeClampMsg, 'warning');
                },

                async setRecentMinutes(minutes) {
                    this.cyclePreset = null;
                    this.timePreset = 'm' + minutes;
                    const end = await this.effectiveLatest();
                    this.endTime = this.dateToInput(end);
                    this.startTime = this.dateToInput(new Date(end.getTime() - minutes * 60000));
                    if (this.selectedFlow) await this.load();
                },
                async setRecentHours(hours) {
                    this.cyclePreset = null;
                    this.timePreset = 'h' + hours;
                    const end = await this.effectiveLatest();
                    this.endTime = this.dateToInput(end);
                    this.startTime = this.dateToInput(new Date(end.getTime() - hours * 3600000));
                    if (this.selectedFlow) await this.load();
                },
                // 프리셋("최근 N분") 의 끝점 = 벽시계 now 가 아니라 *DB 최신 로그 시각*이다(신호 없는 창에
                // 앵커하면 빈 화면이 되는 것을 피하는 기존 설계). 그 사실을 화면에 안 알려주면, 신호가 끊긴
                // 뒤에도 간트가 꽉 차 보여 "실시간인데 헤더는 데이터 대기"로 오해된다 → dataAnchorHint 로 노출.
                async effectiveLatest() {
                    try {
                        const t = await this.apiGet('/api/call-test/latest-time');
                        const d = this.inputToDate(this.toInputValue(t.end));
                        this.dataLatestAt = d;
                        this.refreshAnchorHint();
                        return d;
                    } catch (e) { this.dataLatestAt = null; this.dataAnchorHint = ''; return new Date(); }
                },
                // "기준: 신호 마지막 10:19:43 (6분 전)" — 지연 1분 미만이면 표시 생략(실시간과 다름없음).
                refreshAnchorHint() {
                    const d = this.dataLatestAt;
                    if (!d) { this.dataAnchorHint = ''; return; }
                    const lagSec = Math.floor((Date.now() - d.getTime()) / 1000);
                    const hhmmss = d.toTimeString().slice(0, 8);
                    if (lagSec < 60) { this.dataAnchorHint = ''; return; }
                    const lag = lagSec < 3600
                        ? Math.floor(lagSec / 60) + '분 전'
                        : Math.floor(lagSec / 3600) + '시간 ' + Math.floor((lagSec % 3600) / 60) + '분 전';
                    this.dataAnchorHint = '기준: 신호 마지막 ' + hhmmss + ' (' + lag + ')';
                },
                // 사이클-기준 프리셋 — 최근 N 사이클을 포함하는 시간창을 히스토리(recordedAt=완료시각)로 역산해 로드.
                // rows[0]=최신·완료(비가동) 사이클. 원하는 N개=rows[0..N-1]; 그 직전 완료(rows[N])를 창 시작으로 잡아
                // 가장 오래된 대상 사이클의 시작 경계(Head OutTag↑)까지 포함시킨다.
                async setRecentCycles(n) {
                    const name = this.histFlowName;
                    if (!name || !this.selectedFlow) return;
                    let rows = histCache[name];
                    if (!Array.isArray(rows) || rows.length < n + 1) {
                        try {
                            rows = await this.apiGet('/api/dashboard/flows/' + encodeURIComponent(name) + '/history?limit=' + Math.max(n + 1, 50));
                            histCache[name] = rows;
                        } catch (e) {
                            this.errorMessage = '가동 히스토리 조회 실패: ' + e.message;
                            return;
                        }
                    }
                    rows = Array.isArray(rows) ? rows : [];
                    if (rows.length === 0) { this.errorMessage = '기록된 가동이 없어 가동 기준 범위를 만들 수 없습니다.'; return; }

                    const end = await this.effectiveLatest();
                    let startDate;
                    if (rows.length > n) {
                        startDate = new Date(rows[n].recordedAt);
                    } else {
                        // 보유 사이클이 N 미만 → 전부 포함. 가장 오래된 사이클 시작을 CT 만큼 앞당겨 추정.
                        const oldest = rows[rows.length - 1];
                        const ctMs = (oldest && oldest.ct) ? oldest.ct : 5000;
                        startDate = new Date(new Date(oldest.recordedAt).getTime() - ctMs - 2000);
                    }
                    this.cyclePreset = n;
                    this.timePreset = null;
                    this.endTime = this.dateToInput(end);
                    this.startTime = this.dateToInput(startDate);
                    this.errorMessage = null;
                    await this.load();
                },

                // ── 분석 기간 URL 동기화 (?period=프리셋 | ?from/?to=직접 범위) ──
                // shell 나브의 같은 페이지 전체/FLOW 이동(withPeriodCarry)이 이 파라미터를 실어 가
                // 가동시간 분석 기간이 유지된다(새로고침 유지 포함). 프리셋은 이름(m30/h1/c10)으로 실어
                // 대상에서 최신 데이터 기준 재계산, 직접 범위(수동 입력·드래그)는 from/to 그대로.
                // 기본(최근 5분, m5)은 파라미터 생략.
                syncRangeUrl() {
                    const qp = new URLSearchParams(location.search);
                    qp.delete('period'); qp.delete('from'); qp.delete('to');
                    if (this.timePreset) { if (this.timePreset !== 'm5') qp.set('period', this.timePreset); }
                    else if (this.cyclePreset) qp.set('period', 'c' + this.cyclePreset);
                    else if (this.startTime && this.endTime) { qp.set('from', this.startTime); qp.set('to', this.endTime); }
                    const qs = qp.toString();
                    history.replaceState(null, '', location.pathname + (qs ? '?' + qs : '') + location.hash);
                },
                async applyRangeFromUrl() {
                    const qp = new URLSearchParams(location.search);
                    const per = qp.get('period') || '';
                    let m;
                    if ((m = per.match(/^m(\d+)$/))) return await this.setRecentMinutes(+m[1]);
                    if ((m = per.match(/^h(\d+)$/))) return await this.setRecentHours(+m[1]);
                    if ((m = per.match(/^c(\d+)$/))) return await this.setRecentCycles(+m[1]);
                    const from = qp.get('from'), to = qp.get('to');
                    if (from && to && this.inputToDate(to) > this.inputToDate(from)) {
                        this.startTime = from; this.endTime = to;
                        this.clampTimeRange(); // 북마크/URL 로 상한(2개월) 우회 방지
                        this.timePreset = null; this.cyclePreset = null;
                        return await this.load();
                    }
                    return await this.setRecentMinutes(5);
                },

                // H/T 토글 — Head/Tail 은 무조건 존재(이동만, 해제 없음). 같은 Call 에 둘 다 허용
                // (단일 신호 Call 1개를 자기 OutTag↑→완료(InTag↑/OutTag↓)로 MT 분해 — head==tail).
                async toggleHead(callId) {
                    if (this.branches.length) return;   // 분기 사용 중 = flow 경계 편집 잠김(분기 탭에서 정의)
                    if (this.headCallId === callId) return;
                    this.headCallId = callId;
                    this.userOverrodeHeadTail = true;
                    // Tail 이 비었거나 이전 제안값이면 새 Head 기준으로 다시 제안한다.
                    //   사용자가 직접 찍은 Tail(tailSuggested=false)은 건드리지 않는다.
                    if (!this.tailCallId || this.tailSuggested) await this.applyTailSuggestion();
                    await this.resolveOverlays();
                },
                async toggleTail(callId) {
                    if (this.branches.length) return;
                    if (this.tailCallId === callId) return;
                    this.tailCallId = callId;
                    this.userOverrodeHeadTail = true;
                    this.tailSuggested = false;          // 사용자 선택 우선 — 이후 Head 변경에도 유지
                    this.tailSuggestReason = '';
                    await this.resolveOverlays();
                },
                // 서버 제안(GET /api/flow/{name}/suggest-tail) → 레인에서 같은 이름을 찾아 Tail 로 지정.
                //   제안이 없거나 레인에 없으면 조용히 아무것도 하지 않는다(사용자 흐름을 막지 않음).
                async applyTailSuggestion() {
                    const head = this.headName;
                    if (!this.selectedFlow || !head) return;
                    try {
                        const r = await this.apiGet('/api/flow/' + encodeURIComponent(this.selectedFlow)
                            + '/suggest-tail?head=' + encodeURIComponent(head));
                        if (!r || !r.tailCallName) { this.tailSuggested = false; this.tailSuggestReason = ''; return; }
                        const lane = this.callLanes.find(x => x.callName === r.tailCallName);
                        if (!lane) { this.tailSuggested = false; this.tailSuggestReason = ''; return; }
                        this.tailCallId = lane.callId;
                        this.tailSuggested = true;
                        this.tailSuggestReason = r.reason || '';
                    } catch { this.tailSuggested = false; this.tailSuggestReason = ''; }
                },
                async applyHeadTail() {
                    if (!this.selectedFlow) return;
                    const headName = this.headName, tailName = this.tailName;
                    if (!headName || !tailName) { this.errorMessage = '적용하려면 Head 와 Tail 을 모두 지정하세요.'; return; }
                    await this.saveBoundaryAndRecompute(headName, tailName, '적용(저장) 실패');
                },
                async saveBoundaryAndRecompute(headName, tailName, failLabel) {
                    this.overlayBusy = true; this.errorMessage = null;
                    this.recomputeError = false;
                    this.recomputeMsg = '전체 이력 재계산 준비…';
                    try {
                        await this.apiPost('/api/flow/' + encodeURIComponent(this.selectedFlow) + '/cycle-override',
                            { startCallName: headName, endCallName: tailName });
                        this.userOverrodeHeadTail = false;
                        this.tailSuggested = false; this.tailSuggestReason = '';   // 저장 완료 = 확정값
                        await this.pollRecomputeStatus();
                        await this.load();
                        // 사이클 경계 변경 → 라이브 KPI/추이/히스토리도 새 기준 반영
                        await this.loadFlow(true);
                        await this.reloadTrend();
                        this.syncHistory();
                    } catch (e) {
                        this.errorMessage = failLabel + ': ' + e.message;
                        this.recomputeMsg = '';
                    } finally { this.overlayBusy = false; }
                },
                // ═══ 사이클 분기(branch) — 분기별 Head/Tail + 제외 call. 저장 = 서버 전체 이력 재분류,
                //     설비효율(OEE)은 "부모_분기" 단위로 표시. 미리보기 = 현재 조회 창 신호로 분류한 근사이며
                //     별도 화면이 아니라 <b>본 간트</b>(리본 상단 분기 색 바 + lane 제외 토글)에 그려진다. ═══
                get branchesDirty() { return JSON.stringify(this.branches) !== this.branchesSaved; },
                brColor(i) { return BR_COLORS[i % BR_COLORS.length]; },
                // 편집 변경 → 활성 탭 간트 즉시 재빌드(수동 — svgMarkup/ganttRows 는 반응형이 아님).
                _brRefresh() {
                    if (this.callLanes.length) this.render();
                },
                brSetHead(b, callName) {
                    b.startCallName = callName;
                    b.excludedCallNames = b.excludedCallNames.filter(c => c !== callName);
                    this._brRefresh();
                },
                brSetTail(b, callName) {
                    b.endCallName = callName;
                    b.excludedCallNames = b.excludedCallNames.filter(c => c !== callName);
                    this._brRefresh();
                },
                brToggleExclByName(b, callName) {
                    if (callName === b.startCallName || callName === b.endCallName) return;
                    this.brToggleExcl(b, callName);
                },
                brName(bi) { const b = this.branches[bi]; return b ? (b.name || ('분기' + (bi + 1))) : ''; },
                // 하단 간트의 활성 분기/통계 — null 안전(분기 삭제 틱에 템플릿 자식이 먼저 재평가되는 Alpine 함정 회피)
                get curBranch() { return this.branches[this.branchTab] || null; },
                get curStat() {
                    const st = this.branchPreview.stats[this.branchTab];
                    return st || { count: 0, pct: 0, dup: 0 };
                },
                brRename(name) { const b = this.curBranch; if (!b) return; b.name = name; this._brRefresh(); },

                // ═══ 복수 선택 → 일괄 제외/해제 (2026-09-06) — 분기 탭 전용 ═══
                // 행마다 '제외' 를 하나씩 누르는 대신, 선택 모드에서 체크(행 클릭 · Work 헤더 = 그 Work 전체 · 표시된 행 전체 ·
                // 반전 · Shift+클릭 범위)한 뒤 '선택 제외 / 제외 해제' 로 한 번에 적용한다. 시작/끝 call 은 적용 시 자동 제외.
                toggleSelMode() {
                    this.selMode = !this.selMode;
                    if (!this.selMode) this.selClear();
                    this.selMsg = '';
                },
                selIsOn(name) { return !!this.selCalls[name]; },
                get selCount() { return Object.keys(this.selCalls).length; },
                _selSet(names, on) {
                    const next = { ...this.selCalls };
                    names.forEach(n => { if (on) next[n] = true; else delete next[n]; });
                    this.selCalls = next;
                },
                // 행 클릭/체크 — Shift 로 마지막 클릭 행과의 범위(표시 순서 기준) 일괄 토글
                selToggleRow(row, ev) {
                    const name = row.lane.callName;
                    const callRows = this.brRows.filter(r => r.kind === 'call');
                    const idx = callRows.findIndex(r => r.lane.callName === name);
                    if (ev && ev.shiftKey && this._selAnchor != null) {
                        const a = callRows.findIndex(r => r.lane.callName === this._selAnchor);
                        if (a !== -1 && idx !== -1) {
                            const [lo, hi] = a < idx ? [a, idx] : [idx, a];
                            const on = !this.selIsOn(name);
                            this._selSet(callRows.slice(lo, hi + 1).map(r => r.lane.callName), on);
                            this._selAnchor = name;
                            return;
                        }
                    }
                    this._selSet([name], !this.selIsOn(name));
                    this._selAnchor = name;
                },
                _workCalls(workName) {
                    return this.callLanesRaw.filter(l => (l.workName || '(Work 없음)') === workName).map(l => l.callName);
                },
                // Work 헤더 체크 상태 — 'all' | 'some' | 'none'
                selWorkState(workName) {
                    const calls = this._workCalls(workName);
                    if (!calls.length) return 'none';
                    const n = calls.filter(c => this.selIsOn(c)).length;
                    return n === 0 ? 'none' : (n === calls.length ? 'all' : 'some');
                },
                selToggleWork(workName) {
                    const calls = this._workCalls(workName);
                    this._selSet(calls, this.selWorkState(workName) !== 'all');
                },
                selAll() { this._selSet(this.callLanesRaw.map(l => l.callName), true); },
                // 표시된 행(검색 필터 통과분)만 선택 — 검색으로 좁힌 뒤 한 번에 제외하는 흐름
                selVisible() { this._selSet(this.brRows.filter(r => r.kind === 'call').map(r => r.lane.callName), true); },
                selInvert() {
                    const next = {};
                    this.callLanesRaw.forEach(l => { if (!this.selIsOn(l.callName)) next[l.callName] = true; });
                    this.selCalls = next;
                },
                selClear() { this.selCalls = {}; this._selAnchor = null; },
                // 선택된 call 을 이 분기의 제외 목록에 일괄 반영(on=true 제외 / false 해제). 시작/끝 call 은 건너뛰고 알린다.
                applySelExcl(on) {
                    const b = this.curBranch;
                    if (!b || !this.selCount) return;
                    const names = Object.keys(this.selCalls);
                    const bound = names.filter(n => n === b.startCallName || n === b.endCallName);
                    const targets = names.filter(n => bound.indexOf(n) === -1);
                    const set = new Set(b.excludedCallNames || []);
                    let changed = 0;
                    targets.forEach(n => {
                        if (on && !set.has(n)) { set.add(n); changed++; }
                        else if (!on && set.has(n)) { set.delete(n); changed++; }
                    });
                    b.excludedCallNames = Array.from(set);
                    this.selMsg = (on ? '제외 ' : '제외 해제 ') + changed + '개 적용'
                        + (bound.length ? ' · 시작/끝 call ' + bound.length + '개는 건너뜀' : '');
                    this.selClear();
                    this._brRefresh();
                    setTimeout(() => { this.selMsg = ''; }, 5000);
                },

                // ═══ 2간트 렌더 · 슬라이스 (2026-09-06) ═══════════════════════════════════════════════
                // 상단 flow 간트는 항상, 하단 분기 간트는 분기가 있을 때만(활성 분기 = branchTab). 두 간트가 "같은 슬라이스
                // 규약"으로 CycleGantt 에 위임돼 막대/InOut·정렬·검색·확대/이동·드래그 구간통계가 양쪽 동일 적용.
                setBranchTab(i) {
                    if (!this.branches[i]) i = Math.max(0, this.branches.length - 1);
                    this.branchTab = i;
                    this.selClear(); this.selMsg = '';       // 선택은 분기 단위 — 탭 바꾸면 비움
                    if (this.selectedRange && this.selectedRange.gantt === 'branch') this.selectedRange = null;
                    this.render();
                    this.syncPanSoon();
                },
                // 표시 순서 — 'signal': 시작 맨 위·끝 맨 아래·사이는 첫 신호 시각(Work 헤더 없음) / 'work': Work 그룹 고정(모델 순).
                _orderedLanes(headId, tailId) {
                    const raw = this.callLanesRaw || [];
                    if (this.sortMode === 'work') {
                        const groups = new Map();
                        raw.forEach(l => { const k = l.workName || ''; if (!groups.has(k)) groups.set(k, []); groups.get(k).push(l); });
                        const li = (l) => (typeof l.laneIndex === 'number' ? l.laneIndex : 0);
                        const out = [];
                        groups.forEach(arr => { arr.slice().sort((a, b) => li(a) - li(b)).forEach(l => out.push(l)); });
                        return out;
                    }
                    return window.CycleGantt.sortLanes(raw, headId, tailId);
                },
                _sliceBase() {
                    return {
                        chartStart: this.chartStart, chartEnd: this.chartEnd, plotWidth: this.plotWidth,
                        viewMode: this.viewMode, expandedCalls: this.expandedCalls,
                        topGaps: this.topGaps, showMaxGap: this.showMaxGap, selectedGapIndex: this.selectedGapIndex,
                        unmeasuredRegions: this.unmeasuredRegions || [],
                        laneFilter: this.laneFilter, noWorkRows: this.sortMode !== 'work',
                    };
                },
                // 상단 flow 간트 슬라이스 — 분기 없으면 종전(편집 가능), 분기 있으면 잠금 + 분기별 CT 합산 리본.
                flowSlice() {
                    const base = this._sliceBase();
                    base.selectedRange = (this.selectedRange && this.selectedRange.gantt === 'flow') ? this.selectedRange : null;
                    const s = Object.assign(base, {
                        kind: 'flow', bi: -1, branch: null,
                        callLanes: this._orderedLanes(this.headCallId, this.tailCallId),
                        headCallId: this.headCallId, tailCallId: this.tailCallId,
                        cycleBoundaries: this.cycleBoundaries, tailEdges: this.tailEdges,
                        editable: this.branches.length === 0,
                        avgCycleMs: this.avgCycleMs, avgActiveMs: this.avgActiveMs,
                        tailCompletionSource: this.tailCompletionSource, unionMode: false,
                    });
                    if (this.branches.length) {
                        const pv = this.branchPreview;
                        s.unionMode = true; s.avgCycleMs = null; s.avgActiveMs = null;
                        s.cycleSpans = pv.spans.map((sp, i) => ({
                            start: sp.sMs, end: sp.eMs, number: i + 1, isOpen: sp.isOpen, tailIn: sp.tailIn,
                            union: { win: sp.win, dup: sp.dup, color: sp.color, label: sp.label, title: sp.title },
                        }));
                    }
                    return s;
                },
                // 하단 분기 간트 슬라이스 — 활성 분기(bi) 관점. 리본 = 이 분기로 판별된 스팬만, 상단 색 바 = 자기 관점 분류.
                branchSlice(bi) {
                    const b = this.branches[bi];
                    if (!b) return null;
                    const base = this._sliceBase();
                    base.selectedRange = (this.selectedRange && this.selectedRange.gantt === 'branch') ? this.selectedRange : null;
                    const pv = this.branchPreview;
                    const laneBy = {};
                    (this.callLanesRaw || []).forEach(l => { laneBy[l.callName] = l; });
                    const head = laneBy[b.startCallName] || null;
                    const tail = laneBy[b.endCallName] || null;
                    const spans = [];
                    let ctSum = 0, ctN = 0, atSum = 0, atN = 0;
                    pv.spans.forEach(sp => {
                        if (sp.passing.indexOf(bi) === -1) return;   // 이 분기로 판별된 스팬만(중복 포함) — 사이 구간은 비어 보인다
                        const tailIn = sp.tailInBy[bi] === undefined ? null : sp.tailInBy[bi];
                        spans.push({ start: sp.sMs, end: sp.eMs, number: spans.length + 1, isOpen: sp.isOpen, tailIn });
                        if (!sp.isOpen) { ctSum += sp.eMs - sp.sMs; ctN++; }
                        if (tailIn !== null) { atSum += tailIn - sp.sMs; atN++; }
                    });
                    const collapsed = {};
                    (b.excludedCallNames || []).forEach(n => { collapsed[n] = true; });
                    const ct = (sp) => this.formatMs(sp.eMs - sp.sMs);
                    const bar = pv.spans.map(sp => {
                        const me = sp.byBranch[bi];
                        let color, title;
                        if (me && me.pass) {
                            color = sp.dup ? '#e53935' : this.brColor(bi);
                            title = (sp.dup ? 'CT 중복 — ' + sp.passing.map(x => this.brName(x)).join(', ') + ' 모두 정상 판별 · ' : this.brName(bi) + ' · ') + ct(sp);
                        } else if (me) {
                            color = '#d7dde3'; title = '이 분기 아님 — 제외 call \'' + me.reason + '\' 발화 · ' + ct(sp);
                        } else {
                            color = '#d7dde3'; title = '이 분기 시작 아님 — ' + (sp.win === -1 ? '미분류' : this.brName(sp.win)) + ' · ' + ct(sp);
                        }
                        return { sMs: sp.sMs, eMs: sp.eMs, color, title };
                    });
                    return Object.assign(base, {
                        kind: 'branch', bi, branch: b,
                        callLanes: this._orderedLanes(head ? head.callId : null, tail ? tail.callId : null),
                        headCallId: head ? head.callId : null, tailCallId: tail ? tail.callId : null,
                        cycleBoundaries: [], tailEdges: [], cycleSpans: spans,
                        collapsedCallNames: collapsed, editable: true, unionMode: false,
                        // 제외 call 접기 ON — 같은 목록을 hiddenCallNames 로도 넘겨 CycleGantt.visibleLanes 가 행을 통째 뺀다.
                        hiddenCallNames: this.hideExcl ? collapsed : null,
                        avgCycleMs: ctN ? ctSum / ctN : null, avgActiveMs: atN ? atSum / atN : null,
                        tailCompletionSource: tail ? ((tail.inIntervals || []).length ? 'InTag' : ((tail.outIntervals || []).length ? 'OutTag' : null)) : null,
                        branchPreview: { spans: bar },
                    });
                },
                _summaryOf(s, rows) {
                    return {
                        bi: s.bi, headCallId: s.headCallId, tailCallId: s.tailCallId,
                        editable: s.editable, unionMode: s.unionMode, hasRibbon: window.CycleGantt.hasRibbon(s),
                        avgCycleMs: s.avgCycleMs, avgActiveMs: s.avgActiveMs, tailCompletionSource: s.tailCompletionSource,
                        excluded: s.kind === 'branch' ? (s.branch.excludedCallNames || []).slice() : [],
                        visibleLanes: rows.filter(r => r.kind === 'call').length, totalLanes: s.callLanes.length,
                        // 접기로 숨겨진 행 수 — 모델에 실제 존재하는 제외 call 만 센다(모델 교체로 사라진 이름은 제외).
                        hiddenLanes: s.hiddenCallNames ? s.callLanes.filter(l => s.hiddenCallNames[l.callName]).length : 0,
                    };
                },
                // 제외 call 접기 토글(분기 간트) — 행 숨김/복원. 숨긴 상태에서 제외 버튼을 누르면 그 행은 바로 사라진다.
                toggleHideExcl() {
                    this.hideExcl = !this.hideExcl;
                    try { localStorage.setItem('dspilot-gantt-hide-excl', this.hideExcl ? '1' : '0'); } catch (_) { }
                    this.render();
                },
                // 두 간트를 한 번에 갱신(SVG + 사이드바 행 + 요약 + geo). buildSvg() 대체 — 모든 재빌드 지점이 이걸 부른다.
                render() {
                    const CG = window.CycleGantt;
                    if (!CG || !this.callLanesRaw.length || !this.chartStart || !this.chartEnd) {
                        _flowAct = _brAct = null; this.flowSvg = this.brSvg = ''; this.flowRows = []; this.brRows = [];
                        return;
                    }
                    if (this.branchTab >= this.branches.length) this.branchTab = Math.max(0, this.branches.length - 1);
                    const fs = this.flowSlice();
                    this.flowSvg = CG.buildSvg(fs); this.flowRows = CG.laneRows(fs);
                    _flowAct = fs; this._flowGeo = fs._geo; this.flowAct = this._summaryOf(fs, this.flowRows);
                    if (this.branches.length) {
                        const bs = this.branchSlice(this.branchTab);
                        this.brSvg = CG.buildSvg(bs); this.brRows = CG.laneRows(bs);
                        _brAct = bs; this._brGeo = bs._geo; this.brAct = this._summaryOf(bs, this.brRows);
                    } else {
                        _brAct = null; this.brSvg = ''; this.brRows = []; this._brGeo = null;
                    }
                },
                // 정렬 모드 / 검색 필터 (두 간트 공유)
                setSortMode(m) {
                    if (this.sortMode === m) return;
                    this.sortMode = m;
                    try { localStorage.setItem('dspilot-gantt-sort', m); } catch (_) { }
                    this.render();
                },
                onLaneFilter() { this.render(); },
                clearLaneFilter() { this.laneFilter = ''; this.render(); },
                // 분기 간트 lane 버튼(하단) — 항상 활성 분기 대상. 상단 flow 는 템플릿에서 toggleHead/Tail 직접 호출.
                isBoundRow(row) { const b = this.curBranch; return !!b && (row.lane.callName === b.startCallName || row.lane.callName === b.endCallName); },
                isExcludedRow(row) { return this.brAct.excluded.indexOf(row.lane.callName) !== -1; },
                // rowClass 는 s.headCallId/tailCallId + row.collapsed 만 읽으므로 반응형 요약(flowAct/brAct)을 그대로 넘긴다.
                flowRowClass(row) { return window.CycleGantt.rowClass(this.flowAct, row); },
                brRowClass(row) {
                    const c = window.CycleGantt.rowClass(this.brAct, row);
                    return (this.selMode && row.kind === 'call' && this.selIsOn(row.lane.callName)) ? c + ' is-sel' : c;
                },
                async loadBranches() {
                    if (!this.flowName) return;
                    try {
                        const r = await this.apiGet('/api/flow/' + encodeURIComponent(this.flowName) + '/branches');
                        this.branches = (r.branches || []).map(b => ({
                            name: b.name || '', startCallName: b.startCallName || '', endCallName: b.endCallName || '',
                            excludedCallNames: (b.excludedCallNames || []).slice(),
                        }));
                        this.branchesSaved = JSON.stringify(this.branches);
                        this.branchSavedCount = this.branches.length;
                        // 분기가 있으면 flow 전체 탭 = 분기별 CT 합산 뷰(편집 잠김) — 탭 자체는 'flow' 유지.
                        this._brRefresh();
                    } catch (_) { /* 분기 API 실패 — 편집기만 비활성(가동 분석 자체는 무관) */ }
                },
                brAdd() {
                    if (this.branches.length >= 8) return;
                    // 새 분기의 시작/끝 기본값 = flow 유효 경계 — 분기 간트에서 클릭으로 바꾼다.
                    this.branches.push({
                        name: '분기' + (this.branches.length + 1),
                        startCallName: this.callNameOf(this.headCallId) || '',
                        endCallName: this.callNameOf(this.tailCallId) || '',
                        excludedCallNames: [],
                    });
                    this.setBranchTab(this.branches.length - 1);   // 새 분기를 하단 간트 활성 탭으로
                },
                brRemove(i) {
                    if (!window.confirm('분기 "' + (this.branches[i].name || ('분기' + (i + 1))) + '" 을(를) 삭제합니다. (저장 전까지는 서버에 반영되지 않습니다)')) return;
                    this.branches.splice(i, 1);
                    this.selClear(); this.selMsg = '';
                    // 활성 탭 보정 — 지운 탭이거나 그 뒤면 한 칸 앞으로(하한 0). 분기 0개면 하단 간트 자체가 사라진다.
                    if (this.branchTab >= i && this.branchTab > 0) this.branchTab -= 1;
                    if (this.branchTab >= this.branches.length) this.branchTab = Math.max(0, this.branches.length - 1);
                    this._brRefresh();
                },
                brMove(i, d) {
                    const j = i + d;
                    if (j < 0 || j >= this.branches.length) return;
                    const t = this.branches.splice(i, 1)[0];
                    this.branches.splice(j, 0, t);
                    if (this.branchTab === i) this.branchTab = j;
                    else if (this.branchTab === j) this.branchTab = i;
                    this._brRefresh();
                },
                brToggleExcl(b, name) {
                    const i = b.excludedCallNames.indexOf(name);
                    if (i === -1) b.excludedCallNames.push(name); else b.excludedCallNames.splice(i, 1);
                    this._brRefresh();
                },
                // "이 Work 통째 제외" — 분기 간트의 Work 헤더 행에서 클릭(분기가 Work 단위로 갈리는 현장 구조).
                //   자기 시작/끝 call 은 자동 제외 대상에서 뺀다(자기 사이클 반증 방지 — 서버 방어와 동일).
                brToggleWorkByName(b, workName) {
                    const calls = this.callLanes
                        .filter(l => (l.workName || '(Work 없음)') === workName)
                        .map(l => l.callName);
                    if (!calls.length) return;
                    const allOn = calls.every(c =>
                        c === b.startCallName || c === b.endCallName || b.excludedCallNames.indexOf(c) !== -1);
                    if (allOn) {
                        b.excludedCallNames = b.excludedCallNames.filter(c => calls.indexOf(c) === -1);
                    } else {
                        calls.forEach(c => {
                            if (c === b.startCallName || c === b.endCallName) return;
                            if (b.excludedCallNames.indexOf(c) === -1) b.excludedCallNames.push(c);
                        });
                    }
                    this._brRefresh();
                },
                // 라이브 미리보기 — 서버 재도출과 같은 규칙의 근사: 분기 head OutTag↑ 병합 스트림으로 스팬을
                // 만들고, 스팬 안 제외 call 발화 = 그 분기 기각, 복수 통과 = 정의 순서 첫 매칭, 전멸 = 미분류.
                // 스팬별로 "통과한 분기 전부(passing)" 를 남긴다(2026-09-06) — 서버는 우선순위 첫 통과(win)만 쓰지만, 화면은
                // 둘 이상 통과 = CT 중복(dup) 을 빨간 해치로 드러내 사용자가 제외 call 로 갈라주게 한다. 통과 0 = 정상 CT 없음.
                get branchPreview() {
                    const empty = { spans: [], stats: [], un: 0, unPct: 0, dup: 0, total: 0 };
                    if (!this.branches.length || !this.callLanes.length || !this.chartStart) return empty;
                    const cs = this.chartStart.getTime();
                    const ce = this.chartEnd ? this.chartEnd.getTime() : cs;
                    if (ce <= cs) return empty;
                    const laneByName = {};
                    this.callLanes.forEach(l => { laneByName[l.callName] = l; });
                    const risesOf = (name) => {
                        const l = laneByName[name];
                        return l ? (l.outIntervals || []).map(iv => new Date(iv.start).getTime()).sort((a, b) => a - b) : [];
                    };
                    // 완료 마커 = 끝 call InTag↑, 없으면 OutTag↓ (서버 CycleCompletionResolver 규칙의 클라이언트 근사)
                    const tailsOf = (name) => {
                        const l = laneByName[name];
                        if (!l) return [];
                        const ins = (l.inIntervals || []).map(iv => new Date(iv.start).getTime());
                        return (ins.length ? ins : (l.outIntervals || []).map(iv => new Date(iv.end).getTime())).sort((a, b) => a - b);
                    };
                    const startMap = new Map();   // startMs → [분기 index...] (정의 순서)
                    this.branches.forEach((b, bi) => {
                        risesOf(b.startCallName).forEach(t => {
                            if (!startMap.has(t)) startMap.set(t, []);
                            const arr = startMap.get(t);
                            if (arr.indexOf(bi) === -1) arr.push(bi);
                        });
                    });
                    const starts = Array.from(startMap.keys()).sort((a, b) => a - b);
                    if (!starts.length) return empty;
                    // 분기별 제외 call 의 OutTag↑ 목록(call 이름 보존 — 기각 사유 툴팁용)
                    const exclOf = this.branches.map(b => (b.excludedCallNames || [])
                        .filter(n => n !== b.startCallName && n !== b.endCallName)
                        .map(n => ({ name: n, edges: risesOf(n) })));
                    const tailsBy = this.branches.map(b => tailsOf(b.endCallName));
                    const lowerBound = (arr, v) => { let lo = 0, hi = arr.length; while (lo < hi) { const m = (lo + hi) >> 1; if (arr[m] < v) lo = m + 1; else hi = m; } return lo; };
                    const hasIn = (arr, s, e) => { const lo = lowerBound(arr, s); return lo < arr.length && arr[lo] < e; };
                    const firstAfter = (arr, s, e) => { const lo = lowerBound(arr, s + 1); return (lo < arr.length && arr[lo] < e) ? arr[lo] : null; };
                    const spans = [];
                    const counts = this.branches.map(() => 0);
                    const dupCounts = this.branches.map(() => 0);
                    let un = 0, dupTotal = 0;
                    for (let i = 0; i < starts.length; i++) {
                        const s = starts[i], e = i + 1 < starts.length ? starts[i + 1] : ce;
                        if (e <= s) continue;
                        const cands = startMap.get(s).slice().sort((a, b) => a - b);
                        const byBranch = {};   // bi → { pass, reason(기각시킨 제외 call) }
                        const passing = [];
                        for (const bi of cands) {
                            let fired = null;
                            for (const ex of exclOf[bi]) { if (hasIn(ex.edges, s, e)) { fired = ex.name; break; } }
                            byBranch[bi] = { pass: !fired, reason: fired };
                            if (!fired) passing.push(bi);
                        }
                        const win = passing.length ? passing[0] : -1;   // 서버 규칙 = 정의 순서 첫 통과
                        const dup = passing.length > 1;
                        if (win === -1) un++; else counts[win]++;
                        if (dup) { dupTotal++; passing.forEach(bi => { dupCounts[bi]++; }); }
                        const tailInBy = {};
                        passing.forEach(bi => { tailInBy[bi] = firstAfter(tailsBy[bi], s, e); });
                        const isOpen = i === starts.length - 1;   // 마지막 스팬 끝 = 조회 창 끝(진행중)
                        const ctTxt = this.formatMs(e - s);
                        let label, title, color;
                        if (win === -1) {
                            color = '#9e9e9e'; label = '정상 CT 없음';
                            title = '정상 CT 없음(미분류) — ' + cands.map(bi => this.brName(bi) + (byBranch[bi].reason ? ': 제외 \'' + byBranch[bi].reason + '\' 발화' : '')).join(' · ') + ' · 가동시간 ' + ctTxt;
                        } else if (dup) {
                            color = this.brColor(win); label = 'CT 중복 ' + passing.map(bi => this.brName(bi)).join('+');
                            title = 'CT 중복 — ' + passing.map(bi => this.brName(bi)).join(', ') + ' 모두 정상 판별(우선순위 승자 ' + this.brName(win) + '). 제외 call 을 지정해 갈라주세요 · 가동시간 ' + ctTxt;
                        } else {
                            color = this.brColor(win); label = this.brName(win);
                            title = this.brName(win) + ' · 가동시간 ' + ctTxt;
                        }
                        spans.push({
                            // ms 좌표 — 간트 SVG(합산 리본/분기 색 바)가 같은 xScale 로 그린다.
                            sMs: s, eMs: e, isOpen, win, dup, passing, byBranch,
                            tailIn: win === -1 ? null : tailInBy[win], tailInBy,
                            color, label, title,
                        });
                    }
                    const total = starts.length;
                    return {
                        spans,
                        stats: this.branches.map((b, bi) => ({
                            name: this.brName(bi), color: this.brColor(bi),
                            count: counts[bi], pct: Math.round(counts[bi] / total * 100), dup: dupCounts[bi],
                        })),
                        un, unPct: Math.round(un / total * 100), dup: dupTotal, total,
                    };
                },
                async saveBranches(disable) {
                    if (!this.flowName || this.branchBusy) return;
                    if (disable) {
                        if (!window.confirm('분기를 해제하고 단일 Head/Tail 분석으로 되돌립니다.\n과거 이력의 분기 라벨이 제거되도록 전체 재계산이 실행됩니다. 계속할까요?')) return;
                    } else {
                        if (!this.branches.length) return;
                        if (!window.confirm('분기 정의를 저장합니다.\n이 Flow 의 과거 이력 전체가 새 분기 기준으로 재분류됩니다(백그라운드).\n설비효율 현황에는 "' + this.flowName + '_분기이름" 단위로 표시됩니다. 계속할까요?')) return;
                    }
                    const list = disable ? [] : this.branches.map(b => ({
                        name: (b.name || '').trim(),
                        startCallName: b.startCallName,
                        endCallName: b.endCallName,
                        excludedCallNames: b.excludedCallNames,
                    }));
                    this.branchBusy = true; this.branchError = '';
                    this.branchMsg = disable ? '분기 해제 중…' : '분기 저장 중…';
                    try {
                        const res = await fetch('/api/flow/' + encodeURIComponent(this.flowName) + '/branches', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
                            body: JSON.stringify({ branches: list }),
                        });
                        if (!res.ok) {
                            let msg = 'HTTP ' + res.status;
                            try { const j = await res.json(); if (j && j.message) msg = j.message; } catch (_) { }
                            throw new Error(msg);
                        }
                        const r = await res.json();
                        this.branches = (r.branches || []).map(b => ({
                            name: b.name || '', startCallName: b.startCallName || '', endCallName: b.endCallName || '',
                            excludedCallNames: (b.excludedCallNames || []).slice(),
                        }));
                        this.branchesSaved = JSON.stringify(this.branches);
                        this.branchSavedCount = this.branches.length;
                        this._brRefresh();
                        this.branchMsg = (disable ? '분기 해제됨' : '분기 저장됨') + ' — 이력 재계산 중…';
                        await this.pollRecomputeStatus();
                        this.branchMsg = disable ? '분기 해제 완료' : '분기 저장 완료 — 사이드바는 새로고침 후 분기 단위로 표시됩니다';
                        setTimeout(() => { this.branchMsg = ''; }, 8000);
                    } catch (e) {
                        this.branchError = (disable ? '해제' : '저장') + ' 실패: ' + e.message;
                        this.branchMsg = '';
                    } finally { this.branchBusy = false; }
                },

                async pollRecomputeStatus() {
                    this.recomputeBusy = true; this.recomputeError = false;
                    const flow = this.selectedFlow;
                    let sawMineRunning = false, settled = false;
                    try {
                        for (let i = 0; i < 600; i++) {
                            let s = null;
                            try { const r = await fetch('/api/flow/recompute-status'); if (r.ok) s = await r.json(); } catch (_) {}
                            if (s && s.flow === flow) {
                                if (s.running) {
                                    sawMineRunning = true;
                                    this.recomputeMsg = '전체 이력 재계산 중… (' + (s.phase || '') + (s.cyclesFound ? ', ' + s.cyclesFound + '회' : '') + ')';
                                } else if (s.done && (sawMineRunning || i === 0)) {
                                    if (s.phase === 'error') { this.recomputeError = true; this.recomputeMsg = '전체 이력 재계산 실패: ' + (s.error || ''); }
                                    else { this.recomputeMsg = '전체 이력 재계산 완료 (' + (s.inserted || 0) + '건 재기록)'; }
                                    settled = true; break;
                                }
                            } else if (s && s.running) {
                                this.recomputeMsg = '다른 작업 진행 중 — 대기…';
                            }
                            await new Promise(r => setTimeout(r, 500));
                        }
                    } finally { this.recomputeBusy = false; }
                    if (!settled && !this.recomputeError) this.recomputeMsg = '재계산 상태를 확인할 수 없습니다 (백그라운드에서 계속될 수 있음)';
                    setTimeout(() => { if (!this.recomputeBusy && !this.recomputeError) this.recomputeMsg = ''; }, 6000);
                },
                async restoreAasxDefault() {
                    if (!this.selectedFlow) return;
                    const headName = this.callNameOf(this.projectHeadId);
                    const tailName = this.callNameOf(this.projectTailId);
                    if (!headName || !tailName) { this.errorMessage = '이 Flow 에는 AASX 기본 Head/Tail 정의가 없어 복원할 수 없습니다.'; return; }
                    await this.saveBoundaryAndRecompute(headName, tailName, 'AASX 기본값 복원 실패');
                },
                callNameOf(id) { const l = id ? this.callLanes.find(x => x.callId === id) : null; return l ? l.callName : null; },
                buildExportModel() {
                    const csMs = this.chartStart ? this.chartStart.getTime() : 0;
                    return {
                        flowName: this.selectedFlow,
                        chartStart: this.chartStartIso, chartEnd: this.chartEndIso,
                        viewMode: this.viewMode,
                        headCallId: this.headCallId, tailCallId: this.tailCallId,
                        headName: this.headName, tailName: this.tailName,
                        avgCycleMs: this.avgCycleMs, avgActiveMs: this.avgActiveMs,
                        lanes: this.callLanes.map(l => ({
                            callId: l.callId, callName: l.callName, workName: l.workName, laneIndex: l.laneIndex,
                            inTag: l.inTag, outTag: l.outTag,
                            intervals: l.intervals, outIntervals: l.outIntervals, inIntervals: l.inIntervals
                        })),
                        cycleBoundaries: this.cycleBoundariesIso, tailEdges: this.tailEdgesIso,
                        showMaxGap: this.showMaxGap, selectedGapIndex: this.selectedGapIndex,
                        topGaps: this.topGaps.map(g => ({
                            callId: g.callId, durMs: g.durMs,
                            startOffMs: g.startMs - csMs, endOffMs: g.endMs - csMs
                        }))
                    };
                },
                async exportExcel() {
                    if (!this.selectedFlow || this.exporting) return;
                    this.exporting = true; this.errorMessage = null;
                    try {
                        const res = await fetch('/api/cycle-analysis/export-excel', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(this.buildExportModel())
                        });
                        if (!res.ok) throw new Error('HTTP ' + res.status);
                        let fn = this.excelFileName();
                        const cd = res.headers.get('Content-Disposition');
                        if (cd) {
                            const star = cd.match(/filename\*=(?:UTF-8'')?([^;]+)/i);
                            const plain = cd.match(/filename="?([^";]+)"?/i);
                            if (star) { try { fn = decodeURIComponent(star[1].trim()); } catch (_) {} }
                            else if (plain) { fn = plain[1].trim(); }
                        }
                        const blob = await res.blob();
                        const url = URL.createObjectURL(blob);
                        const a = document.createElement('a'); a.href = url; a.download = fn;
                        document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url);
                    } catch (e) {
                        this.errorMessage = 'Excel 내보내기 실패: ' + e.message;
                    } finally { this.exporting = false; }
                },
                excelFileName() {
                    const t = new Date(); const p = (x) => String(x).padStart(2, '0');
                    return `CycleTime_${this.selectedFlow}_${t.getFullYear()}${p(t.getMonth()+1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}.xlsx`;
                },
                // 이 Flow 를 서버 값으로 되돌리기 — '전체' 편집 페이지의 카드 새로고침(loadSlice→applyLoadResult
                // 이 기준선 리셋)과 같은 의미. 단일 페이지에는 Head/Tail 스테이징(userOverrodeHeadTail,
                // dspDirtyRegister 로 미저장 dirty 등록)을 취소할 수단이 F5 밖에 없었다.
                // headCallId/tailCallId 를 비우고 headSpecified=false 로 재조회 → 서버가 저장값
                // (FlowCycleOverride) ▸ AASX 기본 순으로 유효 Head/Tail 을 다시 해석해 내려준다.
                // 단순 재조회(프리셋 재계산)는 프리셋 버튼 재클릭·F5 와 중복이라 일부러 넣지 않는다.
                async revertStaging() {
                    if (!this.selectedFlow || this.isLoading) return;
                    this.headCallId = null; this.tailCallId = null;
                    this.userOverrodeHeadTail = false;
                    this.errorMessage = null;
                    await this.load();
                },
                async load() {
                    if (this.view === 'trend') return;   // 추이 전용 페이지 — 사이클(간트) 로드 건너뜀
                    if (!this.selectedFlow) return;
                    const start = this.inputToDate(this.startTime);
                    const end = this.inputToDate(this.endTime);
                    if (end <= start) { this.errorMessage = '종료 시각은 시작 시각보다 커야 합니다.'; return; }
                    this.syncRangeUrl(); // 모든 범위 변경(프리셋/수동/드래그)이 여기로 수렴 — 확정된 기간만 URL 반영

                    this.isLoading = true; this.errorMessage = null;
                    if (window.dspLoading) window.dspLoading.begin('가동시간 분석 데이터 불러오는 중…');
                    try {
                        const body = {
                            flowName: this.selectedFlow,
                            start: this.startTime, end: this.endTime,
                            headCallId: this.headCallId, tailCallId: this.tailCallId,
                            headSpecified: this.userOverrodeHeadTail, tailSpecified: this.userOverrodeHeadTail
                        };
                        const d = await this.apiPost('/api/call-test/load', body);
                        this.applyLoadResult(d);
                    } catch (e) {
                        this.errorMessage = '데이터 로딩 실패: ' + e.message;
                    } finally {
                        this.isLoading = false;
                        if (window.dspLoading) window.dspLoading.end();
                    }
                },
                applyLoadResult(d) {
                    this.callLanesRaw = d.lanes || [];
                    this.chartStart = new Date(d.chartStart);
                    this.chartEnd = new Date(d.chartEnd);
                    this.chartStartIso = d.chartStart; this.chartEndIso = d.chartEnd;
                    this.headCallId = d.headCallId || null;
                    this.tailCallId = d.tailCallId || null;
                    this.projectHeadId = d.projectHeadCallId || null;
                    this.projectTailId = d.projectTailCallId || null;
                    this.cycleBoundaries = (d.cycleBoundaries || []).map(s => new Date(s));
                    this.tailEdges = (d.tailEdges || []).map(s => new Date(s));
                    this.cycleBoundariesIso = d.cycleBoundaries || []; this.tailEdgesIso = d.tailEdges || [];
                    this.tailCompletionSource = d.tailCompletionSource ?? null;
                    this.unmeasuredRegions = (d.unmeasuredRegions || []).map(r => ({
                        startMs: new Date(r.start).getTime(), endMs: new Date(r.end).getTime(), cause: r.cause || 'unknown'
                    }));
                    this.avgCycleMs = d.avgCycleMs ?? null;
                    this.avgActiveMs = d.avgActiveMs ?? null;
                    this.isOverride = !!d.isOverride;
                    this.selectedRange = null;
                    this.applySort();
                    this.recomputeTopGaps();
                    this.render();
                    this.$nextTick(() => {
                        this.measurePlotWidth(); this.render();
                        // svgMarkup 은 다음 틱에 DOM 에 붙는다 → 스크롤 폭이 확정된 뒤 이동 슬라이더 동기화.
                        this.syncPanSoon();
                        if (this.tab === 'cycle' && this.cycleView === 'chart') this.renderCycleChart();
                    });
                },

                // 정렬 = Head 맨 위 · Tail 맨 아래 고정. 그 사이는 첫 신호(InTag/OutTag) 시각 순
                // 으로 배열해 신호 흐름(head→tail)이 위→아래로 흐르게 한다. 정렬 선택기 제거(2026-07-01).
                applySort() {
                    const lanes = (this.callLanesRaw || []).slice();
                    const firstStart = (l) => {
                        let m = Infinity;
                        for (const iv of (l.intervals || [])) { const s = new Date(iv.start).getTime(); if (s < m) m = s; }
                        return m;
                    };
                    const li = (l) => (typeof l.laneIndex === 'number' ? l.laneIndex : 0);
                    lanes.sort((a, b) => (firstStart(a) - firstStart(b)) || (li(a) - li(b)));
                    const head = [], mid = [], tail = [];
                    for (const l of lanes) {
                        if (this.headCallId && l.callId === this.headCallId) head.push(l);
                        else if (this.tailCallId && l.callId === this.tailCallId) tail.push(l);
                        else mid.push(l);
                    }
                    this.callLanes = [...head, ...mid, ...tail];
                },

                computeAllGaps() {
                    const gaps = [];
                    for (const lane of this.callLanes) {
                        const ivs = (lane.intervals || []).map(iv => ({ s: new Date(iv.start).getTime(), e: new Date(iv.end).getTime() })).sort((a, b) => a.s - b.s);
                        for (let i = 0; i < ivs.length - 1; i++) {
                            const gs = ivs[i].e, ge = ivs[i + 1].s;
                            if (ge > gs) gaps.push({ callId: lane.callId, callName: lane.callName, startMs: gs, endMs: ge, durMs: ge - gs });
                        }
                    }
                    return gaps;
                },
                recomputeTopGaps() {
                    this.topGaps = this.computeAllGaps().sort((a, b) => b.durMs - a.durMs).slice(0, 5);
                    if (this.selectedGapIndex >= this.topGaps.length) this.selectedGapIndex = 0;
                },
                activeGap() {
                    if (!this.showMaxGap || !this.topGaps.length) return null;
                    const i = (this.selectedGapIndex >= 0 && this.selectedGapIndex < this.topGaps.length) ? this.selectedGapIndex : 0;
                    return this.topGaps[i];
                },
                onGapPicked() {
                    this.render();
                    const g = this.activeGap();
                    if (g) this.$nextTick(() => this.scrollToGap(g));
                },
                focusMaxGap() {
                    if (!this.topGaps.length) return;
                    this.showMaxGap = true; this.selectedGapIndex = 0;
                    this.render();
                    this.$nextTick(() => this.scrollToGap(this.topGaps[0]));
                },
                scrollToGap(gap) {
                    const area = this.chartAreaEl();
                    if (!area || !this._flowGeo || !gap) return;
                    const { cs, xScale } = this._flowGeo;
                    const midX = LEFT_PAD + ((gap.startMs + gap.endMs) / 2 - cs) * xScale;
                    const target = Math.max(0, midX - area.clientWidth / 2);
                    try { area.scrollTo({ left: target, behavior: 'smooth' }); }
                    catch (_) { area.scrollLeft = target; }
                },

                get cycleList() {
                    if (!this.chartStart || this.cycleBoundaries.length === 0) return [];
                    const ce = this.chartEnd ? this.chartEnd.getTime() : 0;
                    const bnd = this.cycleBoundaries.map(d => d.getTime());
                    const tails = this.tailEdges.map(d => d.getTime()).slice().sort((a, b) => a - b);
                    const spans = [];
                    for (let i = 0; i < bnd.length - 1; i++) spans.push({ start: bnd[i], end: bnd[i + 1], number: i + 1, isOpen: false });
                    if (bnd.length > 0 && ce && bnd[bnd.length - 1] < ce) spans.push({ start: bnd[bnd.length - 1], end: ce, number: bnd.length, isOpen: true });
                    let tIdx = 0;
                    return spans.map(span => {
                        while (tIdx < tails.length && tails[tIdx] <= span.start) tIdx++;
                        let tailIn = null;
                        if (tIdx < tails.length && tails[tIdx] < span.end) tailIn = tails[tIdx];
                        const ctMs = span.end - span.start;
                        const atMs = tailIn !== null ? tailIn - span.start : null;   // MT = 완료 − 시작
                        const wtMs = atMs !== null ? Math.max(0, ctMs - atMs) : null; // WT = CT − MT
                        const ratio = (atMs !== null && ctMs > 0) ? +(atMs / ctMs * 100).toFixed(1) : null;
                        return { number: span.number, isOpen: span.isOpen, startMs: span.start, ctMs, atMs, wtMs, ratio };
                    });
                },
                // 이상치 제외(min/max CT) 를 사이클 목록/차트에도 적용 — 히스토리 visibleHistory 와 동일 규칙.
                // 진행중(isOpen) 사이클은 CT 미확정이므로 항상 포함.
                get visibleCycleList() {
                    const r = this._exRangeMs;
                    return this.cycleList.filter(c => {
                        if (c.isOpen) return true;                                   // 진행중은 항상 표시
                        if (this.excludeIncomplete && this.isIncompleteCycle(c)) return false;  // 미완료 제외
                        if (r) {
                            const ct = c.ctMs ?? 0;
                            if (r.min != null && ct < r.min) return false;
                            if (r.max != null && ct > r.max) return false;
                        }
                        return true;
                    });
                },
                // 미완료 = 완료신호(Tail InTag↑) 없는 사이클(atMs=null). Tail 미지정 Flow(=원래 분해 불가)는 제외 대상 아님.
                isIncompleteCycle(c) { return !c.isOpen && c.atMs === null && this.tailCallId !== null; },
                // 현재 탭 기준 미완료 사이클 수 (체크박스 배지용)
                get incompleteCount() {
                    return this.tab === 'cycle'
                        ? this.cycleList.filter(c => this.isIncompleteCycle(c)).length
                        : this.flowHistory.filter(h => h.mt == null).length;
                },
                get excludedCycleCount() {
                    const r = this._exRangeMs;
                    if (!r) return 0;
                    const min = r.min, max = r.max;
                    let n = 0;
                    for (const c of this.cycleList) {
                        if (c.isOpen) continue;
                        const ct = c.ctMs ?? 0;
                        if ((min != null && ct < min) || (max != null && ct > max)) n++;
                    }
                    return n;
                },
                ratioCls(ratio) {
                    if (ratio === null) return '';
                    if (ratio >= 80) return 'ct-ratio-good';
                    if (ratio >= 50) return 'ct-ratio-mid';
                    return 'ct-ratio-low';
                },

                // 마우스 드래그 진입 — 좌클릭만. gantt='flow'|'branch' = 드래그가 일어난 간트(위/아래).
                onDragStart(e, gantt) {
                    if (e.button !== 0) return;
                    this._beginDrag(e, e.clientX, e.clientY, 'mouse', gantt || 'flow', e.currentTarget);
                },
                // 터치 드래그 진입 — 손가락 한 개(핀치는 무시). gantt 는 x-init 리스너에서 넘긴다.
                onDragStartTouch(e, gantt) {
                    if (!e.touches || e.touches.length !== 1) return;
                    const t = e.touches[0];
                    this._beginDrag(e, t.clientX, t.clientY, 'touch', gantt || 'flow', e.currentTarget);
                },
                // 마우스/터치 공통 드래그 선택 코어. mode='mouse'|'touch', gantt='flow'|'branch', area=그 간트의 hscroll.
                _beginDrag(e, clientX, clientY, mode, gantt, area) {
                    const slice = gantt === 'branch' ? _brAct : _flowAct;
                    const geo = gantt === 'branch' ? this._brGeo : this._flowGeo;
                    if (!slice || !geo) return;
                    area = area || (gantt === 'branch' ? document.querySelector('.fg-branch-gantt .ct-gantt-hscroll') : this.chartAreaEl());
                    const svg = area && area.querySelector('svg');
                    if (!svg) return;
                    const lanes = slice.callLanes || [];
                    const rectOf = () => svg.getBoundingClientRect();
                    const clampX = (x) => Math.max(LEFT_PAD, Math.min(LEFT_PAD + this.plotWidth, x));
                    const laneTop = geo.laneTop;
                    const laneAreaH = geo.laneAreaH;
                    const laneBottom = laneTop + laneAreaH;
                    const r0 = rectOf();
                    const x0 = clientX - r0.left, y0 = clientY - r0.top;
                    if (x0 < LEFT_PAD || x0 > LEFT_PAD + this.plotWidth || y0 < laneTop - 8 || y0 > laneBottom + 8) return;
                    e.preventDefault();
                    const { cs, xScale } = geo;
                    const edgePx = [];
                    for (const lane of lanes) {
                        for (const arr of [lane.intervals, lane.outIntervals, lane.inIntervals]) {
                            for (const iv of (arr || [])) {
                                edgePx.push(LEFT_PAD + (new Date(iv.start).getTime() - cs) * xScale);
                                edgePx.push(LEFT_PAD + (new Date(iv.end).getTime() - cs) * xScale);
                            }
                        }
                    }
                    // 스냅 후보에 이 간트의 사이클 경계(분기 간트면 그 분기 스팬의 시작/끝)도 포함
                    for (const sp of window.CycleGantt.cycleSpansOf(slice)) { edgePx.push(LEFT_PAD + (sp.start - cs) * xScale); edgePx.push(LEFT_PAD + (sp.end - cs) * xScale); }
                    const SNAP_PX = 10;
                    const snapCurX = (x) => {
                        let best = x, bestDist = SNAP_PX;
                        for (const ex of edgePx) { const d = Math.abs(ex - x); if (d < bestDist) { bestDist = d; best = ex; } }
                        return best;
                    };
                    const sx = clampX(snapCurX(x0));
                    const startMs = cs + (sx - LEFT_PAD) / xScale;
                    const ns = 'http://www.w3.org/2000/svg';
                    const live = document.createElementNS(ns, 'rect');
                    live.setAttribute('x', this.f(sx)); live.setAttribute('width', '0');
                    live.setAttribute('y', laneTop); live.setAttribute('height', laneAreaH);
                    live.setAttribute('fill', 'rgba(126,87,194,0.16)');
                    live.setAttribute('stroke', '#7e57c2'); live.setAttribute('stroke-dasharray', '4 3');
                    live.setAttribute('pointer-events', 'none');
                    svg.appendChild(live);
                    let moved = false;
                    // 이벤트(마우스/터치)에서 현재 X 좌표 추출 — touchmove 는 changedTouches/touches 사용.
                    const eventX = (ev) => {
                        if (ev.touches && ev.touches.length) return ev.touches[0].clientX;
                        if (ev.changedTouches && ev.changedTouches.length) return ev.changedTouches[0].clientX;
                        return ev.clientX;
                    };
                    const eventY = (ev) => {
                        if (ev.touches && ev.touches.length) return ev.touches[0].clientY;
                        if (ev.changedTouches && ev.changedTouches.length) return ev.changedTouches[0].clientY;
                        return ev.clientY;
                    };
                    const onMove = (ev) => {
                        if (mode === 'touch') ev.preventDefault();   // 드래그 중 페이지 스크롤 방지
                        const cur = clampX(snapCurX(eventX(ev) - rectOf().left));
                        const x = Math.min(sx, cur), w = Math.abs(cur - sx);
                        live.setAttribute('x', this.f(x)); live.setAttribute('width', this.f(w));
                        if (w > 3) moved = true;
                    };
                    const onUp = (ev) => {
                        if (mode === 'touch') {
                            document.removeEventListener('touchmove', onMove, { capture: true });
                            document.removeEventListener('touchend', onUp, true);
                            document.removeEventListener('touchcancel', onUp, true);
                        } else {
                            document.removeEventListener('mousemove', onMove, true);
                            document.removeEventListener('mouseup', onUp, true);
                        }
                        if (live.parentNode) live.parentNode.removeChild(live);
                        this._drag = null;
                        if (!moved) { if (this.selectedRange) this.clearRangeSelection(); return; }
                        const cur = clampX(snapCurX(eventX(ev) - rectOf().left));
                        const endMs = cs + (cur - LEFT_PAD) / xScale;
                        // 툴팁 세로 위치 = 드래그를 끝낸 포인터 아래, 현재 보이는 영역 안(레인 상단 고정이면 세로 스크롤 시 화면 밖).
                        const upY = eventY(ev);
                        const tipTopOf = (h) => (window.CycleGantt ? window.CycleGantt.selTipTop(area, upY, h) : laneTop + 6);
                        this.selectedRange = { gantt, startMs: Math.min(startMs, endMs), endMs: Math.max(startMs, endMs), tipTop: tipTopOf(0) };
                        this.render();
                        // 렌더 후 실제 툴팁 높이로 한 번 보정(추정치 190px 와 다를 때 하단 잘림 방지)
                        this.$nextTick(() => {
                            const tip = area.querySelector('.ct-sel-tip');
                            if (tip && this.selectedRange) this.selectedRange.tipTop = tipTopOf(tip.offsetHeight);
                        });
                    };
                    this._drag = { onMove, onUp };
                    if (mode === 'touch') {
                        document.addEventListener('touchmove', onMove, { capture: true, passive: false });
                        document.addEventListener('touchend', onUp, true);
                        document.addEventListener('touchcancel', onUp, true);
                    } else {
                        document.addEventListener('mousemove', onMove, true);
                        document.addEventListener('mouseup', onUp, true);
                    }
                },
                clearRangeSelection() { this.selectedRange = null; this.render(); },

                // 드래그 선택 구간을 분석 시작·종료 시간으로 설정하고 다시 로드(사이클 기간 = 이 구간).
                async applyRangeAsPeriod() {
                    const r = this.selectedRange;
                    if (!r) return;
                    const s = new Date(r.startMs), e = new Date(r.endMs);
                    if (e <= s) return;
                    this.cyclePreset = null;          // 수동 범위 → 사이클-기준 프리셋 해제
                    this.timePreset = null;
                    this.startTime = this.dateToInput(s);
                    this.endTime = this.dateToInput(e);
                    this.clampTimeRange();            // 커스텀 기간 상한(2개월)
                    this.errorMessage = null;
                    this.selectedRange = null;        // 다이얼로그 닫기 (load() 의 applyLoadResult 에서도 재초기화됨)
                    await this.load();
                },

                get selRangeLenMs() { return this.selectedRange ? Math.max(0, this.selectedRange.endMs - this.selectedRange.startMs) : 0; },
                // 구간 통계는 선택이 일어난 간트 기준 — 상단 flow(분기 있으면 합산 스팬) / 하단 분기(그 분기로 분류된 사이클).
                _selSlice() { return (this.selectedRange && this.selectedRange.gantt === 'branch') ? _brAct : _flowAct; },
                _selGeo() { return (this.selectedRange && this.selectedRange.gantt === 'branch') ? this._brGeo : this._flowGeo; },
                _selSummary() { return (this.selectedRange && this.selectedRange.gantt === 'branch') ? this.brAct : this.flowAct; },
                actCycleRows() { const s = this._selSlice(); return (s && window.CycleGantt) ? window.CycleGantt.cycleRows(s) : []; },
                // 이 구간에 걸치는(겹치는) 사이클 수. Tail 미지정(경계 없음) Flow 는 산출 불가 → selHasCycles=false.
                get selHasCycles() { return this._selSummary().hasRibbon; },
                get selCycleCount() {
                    const r = this.selectedRange; if (!r) return 0;
                    let n = 0;
                    for (const c of this.actCycleRows()) {
                        const s = c.startMs, e = c.startMs + (c.ctMs || 0);
                        if (Math.min(e, r.endMs) > Math.max(s, r.startMs)) n++;
                    }
                    return n;
                },
                // 동작/대기 = 이 구간에 걸친 사이클들의 동작시간(MT=Head→Tail) 합 / 대기시간(WT=CT−MT) 합.
                // 상단 리본·OEE 와 동일 정의. 진행중(isOpen)·미완료(atMs=null) 사이클은 CT 미확정이라 제외.
                get _selCycleAgg() {
                    const r = this.selectedRange;
                    const out = { mt: 0, wt: 0 };
                    if (!r) return out;
                    for (const c of this.actCycleRows()) {
                        if (c.isOpen || c.atMs == null) continue;
                        const s = c.startMs, e = c.startMs + (c.ctMs || 0);
                        if (Math.min(e, r.endMs) > Math.max(s, r.startMs)) { out.mt += c.atMs; out.wt += (c.wtMs || 0); }
                    }
                    return out;
                },
                get selActiveMs() { return this._selCycleAgg.mt; },
                get selWaitMs() { return this._selCycleAgg.wt; },

                // 선택 구간 플로팅 툴팁의 앵커 좌표(스크롤 컨테이너 콘텐츠=SVG 픽셀 좌표계).
                // 가로: 선택 구간의 중앙(양 끝 근처면 툴팁이 잘리지 않게 클램프).
                // 세로: 드래그 종료 포인터 기준·보이는 영역 안(CycleGantt.selTipTop, 드래그 시 selectedRange.tipTop 에 확정). 폴백=레인 상단.
                get selTipCenterPx() {
                    const r = this.selectedRange; const geo = this._selGeo(); if (!r || !geo) return 0;
                    const { cs, xScale } = geo;
                    const raw = LEFT_PAD + ((r.startMs + r.endMs) / 2 - cs) * xScale;
                    const chartW = LEFT_PAD + this.plotWidth + RIGHT_PAD;
                    return Math.round(Math.max(170, Math.min(chartW - 170, raw)));
                },
                get selTipTopPx() {
                    const r = this.selectedRange; const geo = this._selGeo();
                    return (r && typeof r.tipTop === 'number') ? r.tipTop : ((geo ? geo.laneTop : TOP_MARGIN) + 6);
                },

                async resolveOverlays() {
                    if (!this.selectedFlow) return;
                    this.overlayBusy = true;
                    // Head/Tail 토글은 사용자의 명시적 조작 → 공용 상단 인디케이터로 진행 표시
                    // (카드 헤더 'SYNC · 갱신중' 배지는 중복이라 제거됨).
                    if (window.dspLoading) window.dspLoading.begin('가동 경계 다시 계산 중…');
                    try {
                        const headLane = this.headCallId ? this.callLanes.find(l => l.callId === this.headCallId) : null;
                        const tailLane = this.tailCallId ? this.callLanes.find(l => l.callId === this.tailCallId) : null;
                        const body = {
                            flowName: this.selectedFlow,
                            start: this.startTime, end: this.endTime,
                            headCallId: this.headCallId, tailCallId: this.tailCallId,
                            headStartTag: headLane ? headLane.outTag : null,
                            tailFinishTag: tailLane ? tailLane.inTag : null,
                            tailOutTag: tailLane ? tailLane.outTag : null
                        };
                        const d = await this.apiPost('/api/call-test/resolve-overlays', body);
                        this.cycleBoundaries = (d.cycleBoundaries || []).map(s => new Date(s));
                        this.tailEdges = (d.tailEdges || []).map(s => new Date(s));
                        this.cycleBoundariesIso = d.cycleBoundaries || []; this.tailEdgesIso = d.tailEdges || [];
                        this.avgCycleMs = d.avgCycleMs ?? null;
                        this.avgActiveMs = d.avgActiveMs ?? null;
                        this.tailCompletionSource = d.tailCompletionSource ?? null;
                        this.applySort();   // head/tail 변경 → Head↑/Tail↓ 순서 즉시 반영
                        this.render();
                        if (this.cycleView === 'chart') this.$nextTick(() => this.renderCycleChart());
                    } catch (e) {
                        this.errorMessage = '오버레이 갱신 실패: ' + e.message;
                    } finally {
                        this.overlayBusy = false;
                        if (window.dspLoading) window.dspLoading.end();
                    }
                },

                get headName() { const l = this.headCallId ? this.callLanes.find(x => x.callId === this.headCallId) : null; return l ? l.callName : null; },
                get tailName() { const l = this.tailCallId ? this.callLanes.find(x => x.callId === this.tailCallId) : null; return l ? l.callName : null; },

                // 활성 탭 레인 영역 상단(SVG y) — 드래그 툴팁 폴백 위치. _geo 는 buildSvg 가 세팅.
                get laneTopY() { return this._flowGeo ? this._flowGeo.laneTop : TOP_MARGIN; },

                // ── Call lane 확장(ApiCall 서브행) ──────────────────────────────
                hasApiCalls(lane) { return !!(lane && lane.apiCalls && lane.apiCalls.length); },
                toggleExpand(callId) {
                    // 새 객체로 재할당 → Alpine 이 키 추가/변경을 확실히 추적.
                    this.expandedCalls = { ...this.expandedCalls, [callId]: !this.expandedCalls[callId] };
                    this.render();   // SVG 는 x-html 이라 수동 재빌드
                },
                // 사이드바 행 레이아웃은 cycle-gantt.js laneLayout 단일 소스. 행 클래스는 flowRowClass/brRowClass(위)로 분리.

                // OutTag↑(명령) → 다음 InTag↑(응답) 까지를 한 동작 duration 으로 페어링한 ms 배열.
                // 진영 B(PLC 기준): OutTag=출력(명령)=동작 시작, InTag=입력(응답)=동작 완료.
                apiSpansOf(outIntervals, inIntervals) {
                    const outs = (outIntervals || []).map(iv => new Date(iv.start).getTime()).sort((a, b) => a - b);
                    const ins = (inIntervals || []).map(iv => new Date(iv.start).getTime()).sort((a, b) => a - b);
                    if (!outs.length || !ins.length) return [];
                    const spans = [];
                    let j = 0;
                    for (let i = 0; i < outs.length; i++) {
                        const o = outs[i];
                        const nextO = (i + 1 < outs.length) ? outs[i + 1] : Infinity;
                        while (j < ins.length && ins[j] < o) j++;       // 이전 명령에 묶이지 못한 응답 건너뜀
                        if (j < ins.length && ins[j] < nextO) { spans.push(ins[j] - o); j++; }
                        // 응답 없는 명령(미완료)은 스킵
                    }
                    return spans;
                },
                apiSpans(lane) { return this.apiSpansOf(lane.outIntervals, lane.inIntervals); },
                // 쌍(ApiCall)별 실측(2026-09-02) — 서버가 내려준 이 쌍 자신의 인터벌 우선, 필드 자체가 없을 때만
                // lane 폴백(구버전 응답). 빈 배열은 "이 쌍 실측 0건"이라는 정직한 값이므로 폴백하지 않는다.
                apiMeasuredOf(lane, ac) {
                    const outs = (ac && ac.outIntervals) || lane.outIntervals;
                    const ins = (ac && ac.inIntervals) || lane.inIntervals;
                    const spans = this.apiSpansOf(outs, ins);
                    if (!spans.length) return { count: 0, min: null, max: null, mean: null };
                    let mn = Infinity, mx = -Infinity, sum = 0;
                    for (const s of spans) { if (s < mn) mn = s; if (s > mx) mx = s; sum += s; }
                    return { count: spans.length, min: mn, max: mx, mean: sum / spans.length };
                },
                // 현재 AASX 값(ms) 포매팅 — 미설정(null) 은 '—'.
                fmtAasx(ms) { return (ms === null || ms === undefined) ? '—' : this.formatMs(ms); },

                // ── 실측 → AASX 적용 (평균→Duration, min→MinDuration, max→MaxDuration) ──
                // 대상 = ApiCall 의 device Work(targetWorkId). 실측 없음/대상 미해석이면 적용 불가.
                // 실측은 쌍(ApiCall)별 — 복수 쌍 Call 에서 다른 쌍의 span 이 이 Work 에 섞이지 않는다.
                buildDurationChange(lane, ac) {
                    const m = this.apiMeasuredOf(lane, ac);
                    if (m.count === 0 || !ac || !ac.targetWorkId) return null;
                    return { workId: ac.targetWorkId, durationMs: Math.round(m.mean), minMs: Math.round(m.min), maxMs: Math.round(m.max) };
                },
                canApplyApi(lane, ac) { return !!this.buildDurationChange(lane, ac); },
                collectAllDurationChanges() {
                    const out = [];
                    for (const lane of this.callLanes)
                        for (const ac of (lane.apiCalls || [])) {
                            const ch = this.buildDurationChange(lane, ac);
                            if (ch) out.push(ch);
                        }
                    return out;   // 같은 Work 중복은 백엔드가 distinct 처리
                },
                get hasApplicableDurations() { return this.collectAllDurationChanges().length > 0; },
                async applyApiCallDuration(row) {
                    const ch = this.buildDurationChange(row.lane, row.ac);
                    if (!ch) return;
                    await this._applyDurations([ch], `'${row.ac.name}' 실측 duration 을 AASX 에 적용`);
                },

                // ── 디바이스 Duration/Min/Max 직접 편집 다이얼로그 ─────────────────
                // 대상 Work(targetWorkId) 를 해석할 수 있는 ApiCall 만 편집 가능.
                canEditApi(ac) { return !!(ac && ac.targetWorkId); },
                // ms ↔ 초 변환 (입력/표시는 초, 저장은 ms). 빈칸/null 은 서로 '' ↔ null.
                _msToSecStr(ms) {
                    if (ms === null || ms === undefined) return '';
                    return String(Math.round(ms / 10) / 100);   // 소수 2자리(초)
                },
                _secStrToMs(s) {
                    if (s === null || s === undefined) return null;
                    const t = String(s).trim();
                    if (t === '') return null;
                    const v = Number(t);
                    if (!isFinite(v) || v < 0) return null;
                    return Math.round(v * 1000);
                },
                openDurEdit(row) {
                    const ac = row.ac;
                    if (!this.canEditApi(ac)) return;
                    this.durEditCtx = {
                        workId: ac.targetWorkId,
                        name: ac.name,
                        m: row.m || { count: 0, min: null, max: null, mean: null },
                        curDurMs: ac.currentDurationMs ?? null,
                        curMinMs: ac.currentMinMs ?? null,
                        curMaxMs: ac.currentMaxMs ?? null,
                    };
                    this.durEditForm = {
                        dur: this._msToSecStr(this.durEditCtx.curDurMs),
                        min: this._msToSecStr(this.durEditCtx.curMinMs),
                        max: this._msToSecStr(this.durEditCtx.curMaxMs),
                    };
                    this.durEditOpen = true;
                },
                closeDurEdit() { this.durEditOpen = false; this.durEditCtx = null; },
                // '실측 적용' — 이 구간 실측 mean/min/max 를 폼에 자동 채움(초).
                fillDurEditFromMeasured() {
                    const m = this.durEditCtx && this.durEditCtx.m;
                    if (!m || m.count === 0) return;
                    this.durEditForm = {
                        dur: this._msToSecStr(Math.round(m.mean)),
                        min: this._msToSecStr(Math.round(m.min)),
                        max: this._msToSecStr(Math.round(m.max)),
                    };
                },
                get durEditHasMeasured() { return !!(this.durEditCtx && this.durEditCtx.m && this.durEditCtx.m.count > 0); },
                async saveDurEdit() {
                    if (!this.durEditCtx) return;
                    const dur = this._secStrToMs(this.durEditForm.dur);
                    let min = this._secStrToMs(this.durEditForm.min);
                    let max = this._secStrToMs(this.durEditForm.max);
                    if (min !== null && max !== null && min > max) { const t = min; min = max; max = t; }
                    const change = { workId: this.durEditCtx.workId, durationMs: dur, minMs: min, maxMs: max };
                    const ok = await this._applyDurations(
                        [change],
                        `'${this.durEditCtx.name}' 의 Duration/Min/Max 를 직접 입력값으로 AASX 에 적용`,
                        { min, max });
                    if (ok) this.closeDurEdit();
                },
                async applyAllDurations() {
                    const changes = this.collectAllDurationChanges();
                    if (!changes.length) return;
                    await this._applyDurations(changes, `실측 duration ${changes.length}건을 AASX 에 일괄 적용`);
                },
                // returns true on success (커밋됨), false on cancel/error. min/max=null 은 해당 임계 해제(clear).
                async _applyDurations(changes, label, cleared) {
                    if (!changes.length) return false;
                    let note = '';
                    if (cleared) {
                        const c = [];
                        if (cleared.min === null) c.push('min');
                        if (cleared.max === null) c.push('max');
                        if (c.length) note = `\n\n비워둔 ${c.join('·')} 값은 해제(미설정)됩니다.`;
                    }
                    if (!window.confirm(`${label}합니다.${note}\n\n공유 project.aasx 의 Device Work(Duration/Min/Max)를 덮어씁니다 — Promaker 와 공유되는 파일입니다. 계속할까요?`)) return false;
                    this.applyDurBusy = true; this.applyDurMsg = 'AASX 적용 중…'; this.errorMessage = null;
                    try {
                        const r = await this.apiPost('/api/call-test/apply-durations', { changes });
                        this.applyDurMsg = `AASX 적용 완료 (${r.applied}건)`;
                        await this.load();   // '현재 AASX' 값 재조회
                        setTimeout(() => { this.applyDurMsg = ''; }, 5000);
                        return true;
                    } catch (e) {
                        this.errorMessage = '실측 적용 실패: ' + e.message;
                        this.applyDurMsg = '';
                        return false;
                    } finally { this.applyDurBusy = false; }
                },

                // 렌더는 render()(위) 단일 진입 — 상단 flow + 하단 분기 간트를 한 번에 갱신한다.

                formatMs(ms) {
                    if (ms <= 0) return '0초';
                    if (ms < 1000) return Math.round(ms) + 'ms';
                    const totalSec = ms / 1000.0;
                    const h = Math.floor(totalSec / 3600);
                    const m = Math.floor((totalSec % 3600) / 60);
                    const s = totalSec % 60;
                    const parts = [];
                    if (h) parts.push(h + '시간');
                    if (m) parts.push(m + '분');
                    if (h || m) { const rs = Math.round(s); if (rs > 0) parts.push(rs + '초'); }
                    else parts.push(s.toFixed(2) + '초');
                    return parts.join(' ');
                },
                f(v) { return String(Math.round(v * 100) / 100); },
                hms(d) {
                    const p = (x) => String(x).padStart(2, '0');
                    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
                },
                hms2(d) {
                    const p = (x) => String(x).padStart(2, '0');
                    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
                },
                // 한글 시·분·초 (선택 구간 제목용)
                hms2k(d) {
                    return `${d.getHours()}시 ${d.getMinutes()}분 ${d.getSeconds()}초`;
                },
                esc(s) {
                    if (s == null || s === '') return '';
                    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
                },

                // ════════════════════════════════════════════════════════════════
                //  최근 히스토리 (구 대시보드 하단 — 이 Flow 스코프, 서버 공유 이상치 필터)
                // ════════════════════════════════════════════════════════════════
                // 히스토리 대상 Flow = 이 페이지의 Flow (자동 전환/병목 폴백 없음)
                get histFlowName() { return this.flow ? this.flow.flowName : (this.flowName || null); },
                get _exRange() {
                    const r = this.rangeByFlow[this.histFlowName];
                    return (r && (r.min != null || r.max != null)) ? r : null;
                },
                get _exRangeMs() {
                    const r = this._exRange;
                    if (!r) return null;
                    return { min: r.min != null ? r.min * 1000 : null, max: r.max != null ? r.max * 1000 : null };
                },
                get rangeLabel() {
                    const r = this._exRangeMs;
                    if (!r) return '';
                    const lo = r.min != null ? this.fmt(r.min) : null;
                    const hi = r.max != null ? this.fmt(r.max) : null;
                    if (lo != null && hi != null) return lo + ' ~ ' + hi;
                    if (lo != null) return '≥ ' + lo;
                    return '≤ ' + hi;
                },
                get histCtMinMs() { const c = this.flowHistory.map(h => h.ct ?? 0).filter(v => v > 0); return c.length ? Math.min(...c) : 0; },
                get histCtMaxMs() { const c = this.flowHistory.map(h => h.ct ?? 0).filter(v => v > 0); return c.length ? Math.max(...c) : 0; },
                get visibleHistory() {
                    const r = this._exRangeMs;
                    return this.flowHistory.filter(h => {
                        if (this.excludeIncomplete && h.mt == null) return false;   // 미완료(완료신호 없음) 제외
                        if (r) {
                            const ct = h.ct ?? 0;
                            if (r.min != null && ct < r.min) return false;
                            if (r.max != null && ct > r.max) return false;
                        }
                        return true;
                    });
                },
                get excludedCount() {
                    const r = this._exRangeMs;
                    if (!r) return 0;
                    const min = r.min, max = r.max;
                    let n = 0;
                    for (const h of this.flowHistory) {
                        const ct = h.ct ?? 0;
                        if ((min != null && ct < min) || (max != null && ct > max)) n++;
                    }
                    return n;
                },

                syncHistory() {
                    if (this.view === 'trend') return;   // 추이 전용 페이지 — 히스토리(이상치 모달 참고용) 불필요
                    const target = this.histFlowName;
                    if (!target) { this._histShownFor = null; this.setHistory([]); return; }
                    if (target !== this._histShownFor) {
                        this._histShownFor = target;
                        if (histCache[target]) this.setHistory(histCache[target]);
                    }
                    this.loadHistory(target);
                },
                async loadHistory(name) {
                    try {
                        const rows = await this.apiGet('/api/dashboard/flows/' + encodeURIComponent(name) + '/history?limit=' + this.histLimit);
                        histCache[name] = rows;
                        if (this.histFlowName === name) this.setHistory(rows);
                    } catch (e) {
                        console.error('history failed', e);
                        if (this.histFlowName === name) this.setHistory([]);
                    }
                },

                setHistory(rows) {
                    rows = Array.isArray(rows) ? rows : [];
                    rows.forEach((h, i) => {
                        h._key = (h.recordedAt != null && h.recordedAt !== '')
                            ? ('r' + h.recordedAt)
                            : ('n' + (h.cycleNo ?? '?') + '|' + (h.mt ?? 0) + '|' + (h.ct ?? 0) + '|' + i);
                        h._time = this.histTime(h.recordedAt);
                    });
                    this.flowHistory = rows;
                    this.recomputeHist();
                },
                recomputeHist() {
                    const cts = [];
                    for (const h of this.visibleHistory) { const ct = h.ct ?? 0; if (ct > 0) cts.push(ct); }
                    let avg = 0, std = 0;
                    if (cts.length) {
                        avg = cts.reduce((a, b) => a + b, 0) / cts.length;
                        if (cts.length >= 2) std = Math.sqrt(cts.reduce((a, b) => a + (b - avg) * (b - avg), 0) / cts.length);
                    }
                    this.histAvgCt = avg;
                    this.histStdCt = std;
                    for (const h of this.flowHistory) {
                        const ct = h.ct ?? 0, wt = h.wt ?? 0;
                        h._status = (ct > 0 && std > 0)
                            ? (ct > avg + std ? { cls: 'bad', label: '초과' }
                               : ct > avg + std * 0.5 ? { cls: 'warn', label: '주의' }
                               : { cls: 'ok', label: '정상' })
                            : null;
                        h._wt = ct > 0 ? (wt * 100 / ct).toFixed(1) + '%' : '--';
                    }
                    // 활성 탭이 history 일 때만 렌더 — 숨겨진(off-tab) 차트를 SignalR 틱마다 재생성하던 churn 차단.
                    // (setTab('history') 가 탭 진입 시 렌더하므로 off-tab 갱신은 통계만 하고 시각 렌더는 생략)
                    if (this.tab === 'history' && this.histView === 'chart') this.$nextTick(() => this.renderHistChart());
                },
                setHistView(v) {
                    this.histView = v;
                    if (v === 'chart') this.$nextTick(() => this.renderHistChart());
                },
                setCycleView(v) {
                    this.cycleView = v;
                    if (v === 'chart') this.$nextTick(() => this.renderCycleChart());
                },
                // 사이클 목록 차트 — 히스토리 차트와 동일 형태/용어(MT/WT 스택 + 평균선).
                // MT(가동)=atMs(완료−시작), WT(대기)=CT−MT. Tail 미지정(atMs=null) 사이클은 CT 단일 막대.
                renderCycleChart() {
                    // $refs 가 중첩 x-if mount 시 간헐적으로 비는 문제 → DOM 에서 직접 조회
                    const cv = (this.$root || document).querySelector('canvas[x-ref="cycleChart"]') || this.$refs.cycleChart;
                    if (!cv || !window.Chart) return;
                    const css = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
                    const rows = this.visibleCycleList;
                    if (!rows.length) { if (_cycleChart) { _cycleChart.destroy(); _cycleChart = null; } return; }
                    const labels = rows.map(c => '#' + c.number + (c.isOpen ? ' ↻' : ''));
                    const times = rows.map(c => this.hms(new Date(c.startMs)));
                    const ratios = rows.map(c => c.ratio);
                    const toS = (ms) => Math.round((ms ?? 0) / 100) / 10;
                    const hasTail = rows.some(c => c.atMs !== null);
                    const at = rows.map(c => c.atMs !== null ? toS(c.atMs) : null);
                    const idle = rows.map(c => c.atMs !== null ? toS(Math.max(0, c.ctMs - c.atMs)) : null);
                    const ctOnly = rows.map(c => c.atMs === null ? toS(c.ctMs) : null);
                    // Tail 지정 Flow 에서 완료신호 없는 사이클 = 미완료(회색). Tail 미지정 Flow 면 CT 단일막대가 정상.
                    const incompleteFallback = this.tailCallId !== null;
                    // 평균 CT = 현재 표시 중인 '완료' 사이클 평균. 미완료(atMs=null)는 CT 가 부정확하므로 보이더라도 평균에서 제외.
                    // (Tail 미지정 Flow 는 분해 자체가 없으므로 CT 단일막대를 평균에 포함)
                    const completed = rows.filter(c => !c.isOpen && c.ctMs > 0 && (!incompleteFallback || c.atMs !== null));
                    const avgMs = completed.length ? completed.reduce((s, c) => s + c.ctMs, 0) / completed.length : (this.avgCycleMs || 0);
                    const avg = avgMs > 0 ? toS(avgMs) : null;
                    const cAt = css('--dash-mt') || css('--color-primary') || '#12A594';
                    const cIdle = css('--dash-wt') || '#AEB9C6';
                    const cIncomplete = '#5b6675';   // 어두운 회색 — 미완료(완료신호 없음)
                    const cRed = css('--red') || css('--color-error') || '#D8392B';
                    const grid = css('--color-lines') || 'rgba(14,27,42,0.10)';
                    const txt = css('--color-text-secondary') || '#51637A';
                    const datasets = [];
                    if (hasTail) {
                        datasets.push({ label: '동작시간', data: at, backgroundColor: cAt, stack: 'ct', borderWidth: 0 });
                        datasets.push({ label: '대기시간', data: idle, backgroundColor: cIdle, stack: 'ct', borderWidth: 0 });
                    }
                    // 완료신호 없는 사이클: Tail 지정 Flow 면 '미완료'(회색), Tail 미지정 Flow 면 'CT (사이클)' 단일막대
                    if (ctOnly.some(v => v !== null)) {
                        datasets.push({ label: incompleteFallback ? '미완료 (완료신호 없음)' : '가동시간',
                            data: ctOnly, backgroundColor: incompleteFallback ? cIncomplete : cAt, stack: 'ct', borderWidth: 0 });
                    }
                    if (avg != null) {
                        datasets.push({ type: 'line', label: '평균 가동시간', data: labels.map(() => avg),
                            borderColor: cRed, borderWidth: 1.5, borderDash: [5, 4], pointRadius: 0, fill: false });
                    }
                    // 차트 1개만 유지하고 update('none') 로 갱신 — destroy()+new Chart() canvas/GPU churn 제거.
                    // 가변 데이터셋 수(MT/WT vs CT단일 + 평균선)는 datasets 재할당으로 reconcile. 툴팁은 chart.$ctx 로 최신값.
                    const dctx = { labels, times, at, idle, ratios, avg };
                    const themeSig = cAt + '|' + cIdle + '|' + cRed + '|' + grid + '|' + txt + '|' + (incompleteFallback ? 1 : 0);
                    const ex = _cycleChart;
                    if (ex && ex.canvas === cv && ex._themeSig === themeSig) {
                        ex.$ctx = dctx;
                        ex.data.labels = labels;
                        ex.data.datasets = datasets;
                        ex.update('none');
                        return;
                    }
                    if (ex) { ex.destroy(); _cycleChart = null; }
                    const self = this;
                    const fmtMs = (s) => self.fmt(s * 1000);
                    const ch = new Chart(cv, {
                        type: 'bar',
                        data: { labels, datasets },
                        options: {
                            responsive: true, maintainAspectRatio: false, animation: false,
                            interaction: { mode: 'index', intersect: false },
                            plugins: {
                                legend: { position: 'top', labels: { color: txt, boxWidth: 12, font: { size: 11 } } },
                                tooltip: {
                                    filter: (it) => it.dataset.type !== 'line' && (it.parsed.y || 0) > 0,
                                    callbacks: {
                                    title: (items) => { const x = items[0].chart.$ctx, i = items[0].dataIndex; return x.labels[i] + ' · ' + (x.times[i] || ''); },
                                    label: (c) => `${c.dataset.label}: ${fmtMs(c.parsed.y || 0)}`,
                                    afterBody: (items) => {
                                        const x = items[0].chart.$ctx, idx = items[0].dataIndex;
                                        const atVal = x.at[idx] ?? 0;
                                        const idleVal = x.idle[idx] ?? 0;
                                        const ctVal = atVal + idleVal;
                                        const r = x.ratios[idx];
                                        const lines = ['가동시간 (전체): ' + fmtMs(ctVal)];
                                        if (x.avg != null) lines.push('평균 가동시간: ' + fmtMs(x.avg));
                                        if (r !== null) lines.push('가동률: ' + r + '%');
                                        return lines;
                                    },
                                } },
                            },
                            scales: {
                                x: { stacked: true, grid: { display: false }, title: { display: true, text: '가동', color: txt }, ticks: { color: txt, font: { size: 10 }, maxRotation: 0, autoSkip: false, callback: edgeTickCallback } },
                                y: { stacked: true, beginAtZero: true, min: 0, grid: { color: grid }, ticks: { color: txt, font: { size: 10 }, callback: (v) => fmtMs(v) }, title: { display: true, text: '시간', color: txt } },
                            },
                        },
                    });
                    ch.$ctx = dctx;
                    ch._themeSig = themeSig;
                    _cycleChart = ch;
                },
                renderHistChart() {
                    // $refs 가 중첩 x-if mount 시 간헐적으로 비는 문제 → DOM 에서 직접 조회
                    const cv = (this.$root || document).querySelector('canvas[x-ref="histChart"]') || this.$refs.histChart;
                    if (!cv || !window.Chart) return;
                    const css = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
                    const rows = this.visibleHistory.slice().reverse();
                    const times = rows.map(h => h._time || '');
                    const labels = rows.map(h => h._time ? h._time.slice(6) : '');
                    const toS = (ms) => Math.round((ms ?? 0) / 100) / 10;
                    const mt = rows.map(h => toS(h.mt));
                    const wt = rows.map(h => toS(h.wt));
                    const avg = this.histAvgCt > 0 ? toS(this.histAvgCt) : null;
                    const cMt = css('--dash-mt') || css('--color-primary') || '#12A594';
                    const cWt = css('--dash-wt') || '#AEB9C6';
                    const cRed = css('--red') || css('--color-error') || '#D8392B';
                    const grid = css('--color-lines') || 'rgba(14,27,42,0.10)';
                    const txt = css('--color-text-secondary') || '#51637A';
                    const datasets = [
                        { label: '동작시간', data: mt, backgroundColor: cMt, stack: 'ct', borderWidth: 0 },
                        { label: '대기시간', data: wt, backgroundColor: cWt, stack: 'ct', borderWidth: 0 },
                    ];
                    if (avg != null) {
                        datasets.push({ type: 'line', label: '평균 가동시간', data: labels.map(() => avg),
                            borderColor: cRed, borderWidth: 1.5, borderDash: [5, 4], pointRadius: 0, fill: false });
                    }
                    // 차트 1개만 유지하고 update('none') 로 갱신 — destroy()+new Chart() 의 canvas/GPU churn 제거(dashboard2 와 동일 정책).
                    // 툴팁 콜백이 렌더별 배열을 클로저로 캡처하면 stale 되므로, 최신 컨텍스트는 chart.$ctx 에 실어 콜백이 거기서 읽는다.
                    const dctx = { times, mt, wt, avg };
                    const themeSig = txt + '|' + cMt + '|' + cWt + '|' + cRed + '|' + grid;  // 테마 바뀌면 색 갱신 위해 recreate
                    const ex = _histChart;
                    if (ex && ex.canvas === cv && ex._themeSig === themeSig) {
                        ex.$ctx = dctx;
                        ex.data.labels = labels;
                        ex.data.datasets = datasets;
                        ex.update('none');
                        return;
                    }
                    if (ex) { ex.destroy(); _histChart = null; }
                    const self = this;
                    const fmtMs = (s) => self.fmt(s * 1000);
                    const ch = new Chart(cv, {
                        type: 'bar',
                        data: { labels, datasets },
                        options: {
                            responsive: true, maintainAspectRatio: false, animation: false,
                            interaction: { mode: 'index', intersect: false },
                            plugins: {
                                legend: { position: 'top', labels: { color: txt, boxWidth: 12, font: { size: 11 } } },
                                tooltip: {
                                    filter: (it) => it.dataset.type !== 'line',
                                    callbacks: {
                                        title: (items) => (items[0].chart.$ctx.times[items[0].dataIndex]) || '',
                                        label: (c) => `${c.dataset.label}: ${fmtMs(c.parsed.y || 0)}`,
                                        afterBody: (items) => {
                                            const x = items[0].chart.$ctx, idx = items[0].dataIndex;
                                            const ctVal = (x.mt[idx] ?? 0) + (x.wt[idx] ?? 0);
                                            const lines = ['가동시간 (전체): ' + fmtMs(ctVal)];
                                            if (x.avg != null) lines.push('평균 가동시간: ' + fmtMs(x.avg));
                                            return lines;
                                        },
                                    }
                                },
                            },
                            scales: {
                                x: { stacked: true, grid: { display: false }, title: { display: true, text: '가동 발생 시각', color: txt }, ticks: { color: txt, font: { size: 10 }, maxRotation: 0, autoSkip: false, callback: edgeTickCallback } },
                                y: { stacked: true, beginAtZero: true, min: 0, grid: { color: grid }, ticks: { color: txt, font: { size: 10 }, callback: (v) => fmtMs(v) }, title: { display: true, text: '시간', color: txt } },
                            },
                        },
                    });
                    ch.$ctx = dctx;
                    ch._themeSig = themeSig;
                    _histChart = ch;
                },
                // ── 이상치 필터: 이 Flow 의 최소·최대 CT 범위 (팝업으로 입력) ──
                _rangeFieldSec(field) {
                    const v = field === 'min' ? this.rangeForm.min : this.rangeForm.max;
                    const unit = field === 'min' ? this.rangeForm.minUnit : this.rangeForm.maxUnit;
                    const n = parseFloat(v), mult = this._unitSec[unit] || 1;
                    return (v !== '' && v != null && isFinite(n) && n >= 0) ? Math.round(n * mult * 1000) / 1000 : null;
                },
                get rangePreviewLabel() {
                    const lo = this._rangeFieldSec('min'), hi = this._rangeFieldSec('max');
                    if (lo == null && hi == null) return '';
                    const loS = lo != null ? this.fmt(lo * 1000) : null, hiS = hi != null ? this.fmt(hi * 1000) : null;
                    if (loS != null && hiS != null) return '적용: ' + loS + ' ~ ' + hiS;
                    return loS != null ? ('적용: ≥ ' + loS) : ('적용: ≤ ' + hiS);
                },
                openRangeModal() {
                    if (!this.histFlowName) return;
                    const r = this.rangeByFlow[this.histFlowName];
                    this.rangeForm.minUnit = 's';
                    this.rangeForm.maxUnit = 's';
                    const toStr = (sec) => (sec != null) ? String(sec) : '';
                    this.rangeForm.min = toStr(r?.min);
                    this.rangeForm.max = toStr(r?.max);
                    this.rangeModalOpen = true;
                },
                changeUnit(field, u) {
                    const cur = field === 'min' ? this.rangeForm.minUnit : this.rangeForm.maxUnit;
                    if (u === cur) return;
                    const from = this._unitSec[cur] || 1, to = this._unitSec[u] || 1;
                    const v = field === 'min' ? this.rangeForm.min : this.rangeForm.max;
                    const n = parseFloat(v);
                    const conv = (v === '' || v == null || !isFinite(n)) ? v : String(Math.round((n * from / to) * 1000) / 1000);
                    if (field === 'min') { this.rangeForm.min = conv; this.rangeForm.minUnit = u; }
                    else { this.rangeForm.max = conv; this.rangeForm.maxUnit = u; }
                },
                closeRangeModal() { this.rangeModalOpen = false; },
                // 이상치 범위 변경 후 사이클 차트(보이면)도 갱신 — 목록/요약은 getter 라 자동 반영
                _afterExclusionChange() {
                    if (this.tab === 'cycle' && this.cycleView === 'chart') this.$nextTick(() => this.renderCycleChart());
                },
                // 미완료 제외 토글 — 로컬 선호 저장 + 차트(사이클/히스토리) 재렌더. 테이블/요약/배지는 getter 자동 반영.
                onExcludeIncompleteChanged() {
                    localStorage.setItem('dspilot-flow-exclude-incomplete', this.excludeIncomplete ? '1' : '0');
                    this.recomputeHist();           // 히스토리 평균/상태 재계산 + (보이면) 히스토리 차트 재렌더
                    this._afterExclusionChange();    // 사이클 차트(보이면) 재렌더
                },
                async applyRange() {
                    const flow = this.histFlowName;
                    if (!flow) { this.rangeModalOpen = false; return; }
                    let min = this._rangeFieldSec('min'), max = this._rangeFieldSec('max');
                    if (min != null && max != null && min > max) { const t = min; min = max; max = t; }
                    if (min == null && max == null) {
                        const { [flow]: _drop, ...rest } = this.rangeByFlow;
                        this.rangeByFlow = rest;
                    } else {
                        this.rangeByFlow = { ...this.rangeByFlow, [flow]: { min, max } };
                    }
                    this.recomputeHist();
                    this._afterExclusionChange();
                    this.rangeModalOpen = false;
                    await this.saveExclusion(flow, min, max);
                },
                async resetExclusions() {
                    const flow = this.histFlowName;
                    if (!flow || !this.rangeByFlow[flow]) return;
                    const { [flow]: _drop, ...rest } = this.rangeByFlow;
                    this.rangeByFlow = rest;
                    this.recomputeHist();
                    this._afterExclusionChange();
                    await this.saveExclusion(flow, null, null);
                },
                _exclusionsToMap(rows) {
                    const map = {};
                    for (const r of (rows || [])) {
                        if (r && r.flowName) map[r.flowName] = { min: r.minSec ?? null, max: r.maxSec ?? null };
                    }
                    return map;
                },
                async loadExclusions() {
                    if (this.view === 'trend') return;   // 추이 전용 페이지 — 이상치 제외 필터 미사용
                    try {
                        const rows = await this.apiGet('/api/dashboard/exclusions');
                        this.rangeByFlow = this._exclusionsToMap(rows);
                        this.recomputeHist();
                        this._afterExclusionChange();
                    } catch (e) { /* 미수신 시 기존값 유지 */ }
                },
                async saveExclusion(flowName, minSec, maxSec) {
                    try {
                        const res = await fetch('/api/dashboard/exclusions', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
                            body: JSON.stringify({ flowName, minSec, maxSec })
                        });
                        if (!res.ok) throw new Error('HTTP ' + res.status);
                        this.rangeByFlow = this._exclusionsToMap(await res.json());
                        this.recomputeHist();
                    } catch (e) {
                        this.errorMessage = '이상치 제외 저장 실패: ' + e.message;
                    }
                },

                histTime(iso) {
                    const d = new Date(iso); if (isNaN(d)) return '';
                    const p = (x) => String(x).padStart(2, '0');
                    return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
                },

                // 히스토리 표시용 포맷 (한글 단위 — 대시보드 동일)
                fmt(ms) {
                    if (ms <= 0) return '0초';
                    if (ms < 1000) return Math.round(ms) + 'ms';
                    if (ms < 60000) return (ms / 1000).toFixed(1) + '초';
                    if (ms < 3600000) return Math.floor(ms / 60000) + '분 ' + Math.floor(ms % 60000 / 1000) + '초';
                    return Math.floor(ms / 3600000) + '시간 ' + Math.floor(ms % 3600000 / 60000) + '분';
                }
            };
        }
