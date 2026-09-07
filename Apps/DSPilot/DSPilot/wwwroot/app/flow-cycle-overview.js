/*
 * flow-cycle-overview.js — 가동시간 분석 "시스템 개요" (overviewCycleApp). 조회 전용.
 * ------------------------------------------------------------------------------
 * /flow-cycle 에 ?name= 없이 진입했을 때(전체/시스템 스코프 — 좌측 나브 '가동시간 분석' 그룹 헤더) 쓰는
 * Alpine 컴포넌트. 시스템 안 모든 Flow 의 상태를 한 화면에서 훑어보는 개요다(2026-09-07 사용자 결정):
 *   - ?system=<시스템명> → 그 시스템의 Flow 만.  매개변수 없음 → 전 시스템 모든 Flow.
 *   - 카드 = Flow 이름 + 요약 칩 + 컴팩트 간트(CT 리본 + 시작/끝 call lane 만). 다른 call 은 그리지 않는다.
 *   - 분기 flow 는 리본이 3상태(해당 분기색 / CT 중복 빨간 해치 / 정상 CT 없음 회색) + 분기별 횟수 칩.
 *   - 편집 없음. 카드(헤더·간트) 클릭 → /flow-cycle?name=<flow> (현재 기간 동반) 로 이동해 거기서 편집한다.
 *     (구 '전체 편집' bulkCycleApp 의 일괄 적용/스테이징은 폐기 — 편집 진입점은 flow 단일 페이지 하나.)
 * 렌더/파생은 /app/cycle-gantt.js(window.CycleGantt) 재사용 — 단일 페이지와 같은 SVG 엔진·같은 분기 판별기
 *   (CycleGantt.classifyBranches)를 쓰므로 개요에서 본 그림/판별이 flow 페이지에서 그대로 재현된다.
 * 데이터 = POST /api/call-test/load (flow 별, 동시 4개) + GET /api/flow/{name}/branches (분기 활성 flow 만).
 * Excel: "Excel 다운로드" → 로드된 모든 Flow 의 화면 상태를 POST /api/cycle-analysis/export-excel-bulk.
 */
function overviewCycleApp() {
    const CG = window.CycleGantt;
    const LEFT_PAD = CG.LEFT_PAD, RIGHT_PAD = CG.RIGHT_PAD, MIN = CG.MIN_PLOT_WIDTH, MAX_ZOOM = CG.MAX_ZOOM;
    // 개요 기본 기간 = 최근 1시간(단일 페이지 기본 5분과 다름 — 상태 훑어보기 용도). URL 에는 기본값 생략.
    const DEFAULT_PRESET = 'h1';
    // 사이클 프리셋용 히스토리 캐시 (closure, Alpine 반응형 밖 — 단순 캐시)
    const histCache = {};

    return {
        TOP_MARGIN: CG.TOP_MARGIN,
        RIBBON_H: CG.RIBBON_H,

        systemName: '',
        flows: [],
        flowBranchNames: {},     // flow → 분기 이름 목록(/api/nav) — 있는 flow 만 분기 정의를 따로 조회
        startTime: '', endTime: '',
        timePreset: null, cyclePreset: null, rangePopupOpen: false,
        dataLatestAt: null,
        dataAnchorHint: '',
        msg: '', msgError: false,
        exportingAll: false,
        // 공유 이동/확대 — 상단 슬라이더가 모든 카드 간트에 일괄 적용(Ctrl+휠 = 커서 앵커 줌).
        zoom: 1,
        panPct: 0,
        canPan: false,
        _panSync: false,
        _timer: null, _hintTimer: null,

        async init() {
            this.systemName = new URLSearchParams(location.search).get('system') || '';

            let _rt;
            window.addEventListener('resize', () => {
                clearTimeout(_rt);
                _rt = setTimeout(() => {
                    for (const s of this.flows) {
                        if (s.callLanes.length) { this.measurePlotWidth(s); s.svgMarkup = CG.buildSvg(s); }
                    }
                    this.syncPanAllSoon();
                }, 180);
            });

            await this.loadFlows();
            await this.applyRangeFromUrl();
            this._hintTimer = setInterval(() => { if (!document.hidden) this.refreshAnchorHint(); }, 30000);
        },

        destroy() { clearTimeout(this._timer); clearInterval(this._hintTimer); },

        scopeLabel() { return this.systemName ? this.systemName : '전체 시스템'; },
        rangeSummary() {
            const f = (v) => v ? v.slice(5, 19).replace('T', ' ') : '—';
            return f(this.startTime) + ' ~ ' + f(this.endTime);
        },

        // ── API ──
        async apiGet(url) {
            const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
            if (!res.ok) throw new Error('HTTP ' + res.status);
            return await res.json();
        },
        async apiPost(url, body) {
            const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' }, body: JSON.stringify(body) });
            if (!res.ok) throw new Error('HTTP ' + res.status);
            return await res.json();
        },

        // ── Flow 목록 (시스템 스코프 또는 전체) ──
        async loadFlows() {
            let names = [];
            const brMap = {};
            try {
                const nav = await this.apiGet('/api/nav');
                const systems = (nav && nav.systems) || [];
                const pick = systems.filter(s => !this.systemName || s.name === this.systemName);
                for (const sys of pick) {
                    for (const fn of (sys.flows || [])) names.push(fn);
                    const fb = sys.flowBranches || {};
                    Object.keys(fb).forEach(k => { brMap[k] = fb[k]; });
                }
                names = Array.from(new Set(names));
            } catch (e) { /* 실패 시 빈 목록 */ }
            this.flowBranchNames = brMap;
            this.flows = names.map((n, i) => this.makeSlice(n, i));
        },

        makeSlice(flowName, idx) {
            return {
                id: idx, flowName,
                loading: true, error: null,
                // state: 'ok' | 'nosignal'(기간 내 신호 0) | 'noedge'(신호는 있으나 시작/끝 call 신호 없음)
                state: 'ok',
                callLanesRaw: [], callLanes: [],
                laneRoles: {},           // callId → [{ kind:'head'|'tail', branches:[{ bi, color, name }] }] — 역할(시작/끝)당 1배지, 분기 flow 는 분기 색 점을 덧붙임
                noWorkRows: true,        // 시작/끝 2행만 그리므로 Work 헤더 행 불필요
                unmeasuredRegions: [], unmeasuredMs: 0,
                cycleBoundaries: [], tailEdges: [], tailCompletionSource: null,
                cycleBoundariesIso: [], tailEdgesIso: [],
                cycleSpans: null, unionMode: false, hasRibbon: false,
                branches: [], bp: null,  // bp = CycleGantt.classifyBranches 결과(분기 flow 만) — 이름을 branchPreview 로 두지 않는다
                                         // (CycleGantt.appendBranchOverlay 가 s.branchPreview 를 분기 간트 색 바로 해석 — 개요 리본은 합산 1벌만).
                sum: { cycles: 0, open: false },
                chartStart: null, chartEnd: null, chartStartIso: '', chartEndIso: '',
                headCallId: null, tailCallId: null,
                isOverride: false, avgCycleMs: null, avgActiveMs: null,
                plotWidth: 1200, baseWidth: 1200, zoom: this.zoom, viewMode: 'bar',
                expandedCalls: {}, topGaps: [], showMaxGap: false, selectedGapIndex: 0,
                svgMarkup: '', selectedRange: null, _geo: null
            };
        },

        get loadingAny() { return this.flows.some(s => s.loading); },
        get loadedFlowCount() { return this.flows.filter(s => !s.loading && !s.error && s.callLanes.length).length; },
        // 페이지 상단 합계 — 로드된 카드 기준(분기 flow 의 사이클 = 판별 스팬 수).
        get totals() {
            const t = { flows: this.flows.length, loaded: 0, cycles: 0, branched: 0, dup: 0, un: 0, unmeasured: 0 };
            for (const s of this.flows) {
                if (s.loading || s.error) continue;
                t.loaded++;
                t.cycles += s.sum.cycles;
                if (s.branches.length) { t.branched++; if (s.bp) { t.dup += s.bp.dup; t.un += s.bp.un; } }
                if (s.unmeasuredMs > 0) t.unmeasured++;
            }
            return t;
        },

        // ── 분석 기간 URL 동기화 (?period=프리셋 | ?from/?to=직접 범위) — 단일 페이지(flow-workspace)와 동일 규약 ──
        // shell 나브의 같은 페이지 이동(withPeriodCarry)이 이 파라미터를 실어 가 기간이 유지된다. 기본(h1)은 생략.
        periodParams() {
            const qp = new URLSearchParams();
            if (this.timePreset) qp.set('period', this.timePreset);
            else if (this.cyclePreset) qp.set('period', 'c' + this.cyclePreset);
            else if (this.startTime && this.endTime) { qp.set('from', this.startTime); qp.set('to', this.endTime); }
            return qp;
        },
        syncRangeUrl() {
            const qp = new URLSearchParams(location.search);
            qp.delete('period'); qp.delete('from'); qp.delete('to');
            const pp = this.periodParams();
            pp.forEach((v, k) => { if (!(k === 'period' && v === DEFAULT_PRESET)) qp.set(k, v); });
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
                this.clampTimeRange();
                this.timePreset = null; this.cyclePreset = null;
                return await this.loadAll();
            }
            return await this.setRecentHours(1);
        },
        // 카드 클릭 이동 주소 — 단일 flow 가동시간 분석(편집) + 현재 기간(기본 h1 도 명시 — 단일 페이지 기본은 5분이라 생략하면 기간이 바뀐다).
        flowHref(slice) {
            const qp = new URLSearchParams();
            qp.set('name', slice.flowName);
            this.periodParams().forEach((v, k) => qp.set(k, v));
            return '/flow-cycle?' + qp.toString();
        },
        goFlow(slice) { location.href = this.flowHref(slice); },

        // ── 로드 (동시성 제한) ──
        async loadAll() {
            this.syncRangeUrl();
            const list = this.flows.slice();
            const CONC = 4;
            let idx = 0;
            const worker = async () => { while (idx < list.length) { const s = list[idx++]; await this.loadSlice(s); } };
            if (window.dspLoading) window.dspLoading.begin('시스템 Flow 불러오는 중…');
            try {
                const workers = [];
                for (let i = 0; i < Math.min(CONC, list.length); i++) workers.push(worker());
                await Promise.all(workers);
            } finally {
                if (window.dspLoading) window.dspLoading.end();
            }
        },

        async loadBranchDefs(slice) {
            const names = this.flowBranchNames[slice.flowName];
            if (!names || !names.length) return [];
            try {
                const r = await this.apiGet('/api/flow/' + encodeURIComponent(slice.flowName) + '/branches');
                return (r.branches || []).map(b => ({
                    name: b.name || '', startCallName: b.startCallName || '', endCallName: b.endCallName || '',
                    excludedCallNames: (b.excludedCallNames || []).slice(),
                }));
            } catch (_) { return []; }   // 분기 API 실패 → 단일 Head/Tail 로 표시(가동 분석 자체는 무관)
        },

        async loadSlice(slice) {
            const start = this.inputToDate(this.startTime), end = this.inputToDate(this.endTime);
            if (end <= start) { slice.error = '종료 시각은 시작 시각보다 커야 합니다.'; slice.loading = false; return; }
            slice.loading = true; slice.error = null;
            try {
                const body = {
                    flowName: slice.flowName, start: this.startTime, end: this.endTime,
                    headCallId: null, tailCallId: null, headSpecified: false, tailSpecified: false
                };
                const [d, branches] = await Promise.all([this.apiPost('/api/call-test/load', body), this.loadBranchDefs(slice)]);
                slice.branches = branches;
                this.applyLoadResult(slice, d);
            } catch (e) {
                slice.error = '데이터 로딩 실패: ' + e.message;
            } finally { slice.loading = false; }
        },

        // 로드 응답 → 개요 슬라이스. 간트에 그릴 lane 은 시작/끝 call 만 고른다(분기 flow = 분기들의 시작/끝 call 합집합).
        applyLoadResult(slice, d) {
            const raw = d.lanes || [];
            slice.callLanesRaw = raw;
            slice.chartStart = new Date(d.chartStart);
            slice.chartEnd = new Date(d.chartEnd);
            slice.chartStartIso = d.chartStart; slice.chartEndIso = d.chartEnd;
            const cs = slice.chartStart.getTime(), ce = slice.chartEnd.getTime();
            slice.tailCompletionSource = d.tailCompletionSource ?? null;
            slice.isOverride = !!d.isOverride;
            slice.unmeasuredRegions = (d.unmeasuredRegions || []).map(r => ({
                startMs: new Date(r.start).getTime(), endMs: new Date(r.end).getTime(), cause: r.cause || 'unknown'
            }));
            slice.unmeasuredMs = slice.unmeasuredRegions.reduce((acc, r) => acc + Math.max(0, Math.min(ce, r.endMs) - Math.max(cs, r.startMs)), 0);
            slice.expandedCalls = {}; slice.topGaps = []; slice.showMaxGap = false; slice.selectedGapIndex = 0; slice.selectedRange = null;

            const byId = {}, byName = {};
            raw.forEach(l => { byId[l.callId] = l; byName[l.callName] = l; });
            const roles = {};
            // 역할 배지는 lane×kind 당 1개 — 분기 flow 에서 공유 head(분기 전부의 시작)에 '시작' 이 n번 찍히지 않게 한다.
            //   어느 분기의 경계인지는 배지 옆 분기 색 점(branches[])으로 표시.
            const addRole = (lane, kind, bi) => {
                if (!lane) return;
                const list = (roles[lane.callId] = roles[lane.callId] || []);
                let r = list.find(x => x.kind === kind);
                if (!r) { r = { kind, branches: [] }; list.push(r); }
                if (bi >= 0) r.branches.push({ bi, color: CG.brColor(bi), name: CG.brNameOf(slice.branches, bi) });
            };

            if (slice.branches.length === 0) {
                // 단일 Head/Tail — 서버 경계/평균 그대로. 표시 lane = head, tail(같으면 1행).
                slice.headCallId = d.headCallId || null;
                slice.tailCallId = d.tailCallId || null;
                slice.cycleBoundariesIso = d.cycleBoundaries || [];
                slice.tailEdgesIso = d.tailEdges || [];
                slice.cycleBoundaries = slice.cycleBoundariesIso.map(s => new Date(s));
                slice.tailEdges = slice.tailEdgesIso.map(s => new Date(s));
                slice.cycleSpans = null; slice.unionMode = false; slice.bp = null;
                slice.avgCycleMs = d.avgCycleMs ?? null;
                slice.avgActiveMs = d.avgActiveMs ?? null;
                const picked = [];
                if (slice.headCallId && byId[slice.headCallId]) picked.push(byId[slice.headCallId]);
                if (slice.tailCallId && slice.tailCallId !== slice.headCallId && byId[slice.tailCallId]) picked.push(byId[slice.tailCallId]);
                slice.callLanes = CG.sortLanes(picked, slice.headCallId, slice.tailCallId);
                addRole(byId[slice.headCallId], 'head', -1);
                addRole(byId[slice.tailCallId], 'tail', -1);
            } else {
                // 분기 flow — flow 자체 Head/Tail 은 경계가 아니다(경계 = 분기별 head/tail). 합산 리본 = 판별 스팬.
                slice.headCallId = null; slice.tailCallId = null;
                slice.cycleBoundaries = []; slice.tailEdges = [];
                const bp = CG.classifyBranches(raw, slice.branches, cs, ce);
                slice.bp = bp;
                slice.unionMode = true;
                slice.cycleSpans = CG.unionSpansOf(bp);
                slice.avgCycleMs = null; slice.avgActiveMs = null;
                // Excel 내보내기용 경계/완료 마커 = 판별 스팬 시작/승자 분기 완료(로컬 ISO).
                slice.cycleBoundariesIso = bp.spans.map(sp => this.dateToInput(new Date(sp.sMs)));
                slice.tailEdgesIso = bp.spans.filter(sp => sp.tailIn !== null && sp.tailIn !== undefined).map(sp => this.dateToInput(new Date(sp.tailIn)));
                // 표시 lane = 분기 시작 call(정의 순서, 중복 제거) → 분기 끝 call(아직 없는 것만).
                const order = [];
                const pushName = (n) => { if (n && byName[n] && order.indexOf(n) === -1) order.push(n); };
                slice.branches.forEach(b => pushName(b.startCallName));
                slice.branches.forEach(b => pushName(b.endCallName));
                slice.callLanes = order.map(n => byName[n]);
                slice.branches.forEach((b, bi) => { addRole(byName[b.startCallName], 'head', bi); addRole(byName[b.endCallName], 'tail', bi); });
            }
            slice.laneRoles = roles;

            const spans = CG.cycleSpansOf(slice);
            slice.hasRibbon = spans.length > 0;
            slice.sum = { cycles: spans.filter(sp => !sp.isOpen).length, open: spans.some(sp => sp.isOpen) };
            slice.state = raw.length === 0 ? 'nosignal' : (slice.callLanes.length === 0 ? 'noedge' : 'ok');

            slice.svgMarkup = CG.buildSvg(slice);
            this.$nextTick(() => {
                this.measurePlotWidth(slice); slice.svgMarkup = CG.buildSvg(slice);
                this.syncPanAllSoon();
            });
        },

        // ── 카드 표시 헬퍼 ──
        laneRows(slice) { return CG.laneRows(slice); },
        rowClass(slice, row) { return CG.rowClass(slice, row); },
        rolesOf(slice, lane) { return (slice.laneRoles && slice.laneRoles[lane.callId]) || []; },
        roleTitle(r) {
            const k = r.kind === 'head' ? '시작' : '끝';
            return r.branches.length ? r.branches.map(b => b.name).join(', ') + ' 분기의 ' + k + ' call' : '가동 ' + k + ' call';
        },
        fmtMs(ms) { return CG.formatMs(ms); },
        // 단일 flow 페이지의 ct-sh-stat 규약과 같은 요약 문구(리본 사이드 부제)
        ribbonSub(slice) { return slice.unionMode ? '어느 분기의 CT · 중복 · 정상 CT 없음' : '동작시간 · 대기시간 · 동작률'; },

        // ── 간트 지오메트리 / 줌 ──
        areaEl(slice) { return document.getElementById('cta-' + slice.id); },
        measurePlotWidth(slice) {
            const el = this.areaEl(slice);
            const avail = el ? el.clientWidth : 1100;
            slice.baseWidth = Math.max(MIN, Math.round(avail - LEFT_PAD - RIGHT_PAD - 4));
            slice.plotWidth = Math.max(MIN, Math.round(slice.baseWidth * slice.zoom));
        },
        resetZoomAll() {
            this.zoom = 1;
            for (const s of this.flows) { s.zoom = 1; this.measurePlotWidth(s); if (s.callLanes.length) s.svgMarkup = CG.buildSvg(s); }
            this.$nextTick(() => {
                this._panSync = true;
                for (const s of this.flows) { const el = this.areaEl(s); if (el) el.scrollLeft = 0; }
                this.panPct = 0; this.canPan = false;
                requestAnimationFrame(() => { this._panSync = false; this.syncPanAll(); });
            });
        },
        // 확대는 로그 스케일(0=100%, 1000=MAX_ZOOM) — 선형이면 100~200% 가 왼쪽 끝 몇 px 에 뭉친다.
        get zoomPct() {
            const z = Math.min(MAX_ZOOM, Math.max(1, this.zoom));
            return Math.round(Math.log(z) / Math.log(MAX_ZOOM) * 1000);
        },
        onZoomSliderAll(value) {
            const t = Math.min(1, Math.max(0, Number(value) / 1000));
            const z = Math.exp(t * Math.log(MAX_ZOOM));
            const slice = this.flows.find(s => s.callLanes.length);
            if (!slice) { this.zoom = Math.min(MAX_ZOOM, Math.max(1, z)); return; }
            const el = this.areaEl(slice);
            if (!el) return;
            this.applyZoom(slice, z, el.clientWidth / 2, el);
        },
        syncPanAllSoon() { this.$nextTick(() => requestAnimationFrame(() => this.syncPanAll())); },
        observePan(el) {
            if (!el || !window.ResizeObserver) return;
            const ro = new ResizeObserver(() => this.syncPanAll());
            ro.observe(el);
            if (el.firstElementChild) ro.observe(el.firstElementChild);
        },
        syncPanAll() {
            const slice = this.flows.find(s => s.callLanes.length);
            const el = slice ? this.areaEl(slice) : null;
            if (!el) { this.panPct = 0; this.canPan = false; return; }
            const max = el.scrollWidth - el.clientWidth;
            this.canPan = max > 1;
            this.panPct = max > 1 ? Math.round(el.scrollLeft / max * 1000) : 0;
        },
        onPanSliderAll(value) {
            const t = Math.min(1, Math.max(0, Number(value) / 1000));
            this.panPct = Math.round(t * 1000);
            this._panSync = true;
            for (const s of this.flows) {
                const el = this.areaEl(s); if (!el) continue;
                const max = el.scrollWidth - el.clientWidth;
                el.scrollLeft = max > 0 ? t * max : 0;
            }
            requestAnimationFrame(() => { this._panSync = false; });
        },
        // Ctrl+휠 = 간트 확대/축소(커서 아래 시각 고정) — 전 카드 일괄. 일반 휠은 페이지 스크롤 유지.
        onGanttWheel(slice, e) {
            if (!e.ctrlKey || !slice.callLanes.length) return;
            e.preventDefault();
            const el = this.areaEl(slice); if (!el) return;
            const dy = e.deltaMode === 1 ? e.deltaY * 24 : e.deltaY;
            const factor = Math.exp(-dy * 0.0022);
            const anchorX = e.clientX - el.getBoundingClientRect().left;
            this.applyZoom(slice, this.zoom * factor, anchorX, el);
        },
        onAreaScroll(slice) {
            if (this._panSync) return;
            const el = this.areaEl(slice); if (!el) return;
            const max = el.scrollWidth - el.clientWidth;
            const t = max > 1 ? el.scrollLeft / max : 0;
            this.canPan = max > 1;
            this.panPct = Math.round(t * 1000);
            this._panSync = true;
            for (const s of this.flows) {
                if (s === slice) continue;
                const o = this.areaEl(s); if (!o) continue;
                const m = o.scrollWidth - o.clientWidth;
                o.scrollLeft = m > 0 ? t * m : 0;
            }
            requestAnimationFrame(() => { this._panSync = false; });
        },
        applyZoom(slice, targetZoom, anchorX, el) {
            el = el || this.areaEl(slice); if (!el) return;
            const newZoom = Math.min(MAX_ZOOM, Math.max(1, targetZoom));
            if (Math.abs(newZoom - this.zoom) < 1e-6) return;
            const plotAreaX = Math.max(0, anchorX + el.scrollLeft - LEFT_PAD);
            const frac = slice.plotWidth > 0 ? Math.min(1, plotAreaX / slice.plotWidth) : 0;
            this.zoom = newZoom;
            for (const s of this.flows) {
                s.zoom = newZoom;
                this.measurePlotWidth(s);
                if (s.callLanes.length) s.svgMarkup = CG.buildSvg(s);
            }
            this.$nextTick(() => {
                this._panSync = true;
                const left = frac * slice.plotWidth + LEFT_PAD - anchorX;
                el.scrollLeft = left;
                const ratio = slice.plotWidth > 0 ? Math.max(0, left) / slice.plotWidth : 0;
                for (const s of this.flows) {
                    if (s === slice) continue;
                    const other = this.areaEl(s);
                    if (other) other.scrollLeft = ratio * s.plotWidth;
                }
                requestAnimationFrame(() => { this._panSync = false; this.syncPanAll(); });
            });
        },

        // ── 사이클 기준 프리셋 (첫 번째 Flow 히스토리로 역산) ──
        async setRecentCycles(n) {
            this.cyclePreset = n; this.timePreset = null; this.rangePopupOpen = false;
            const target = this.flows[0];
            if (!target) return;
            const name = target.flowName;
            let rows = histCache[name];
            if (!Array.isArray(rows) || rows.length < n + 1) {
                try {
                    rows = await this.apiGet('/api/dashboard/flows/' + encodeURIComponent(name) + '/history?limit=' + Math.max(n + 1, 50));
                    histCache[name] = rows;
                } catch (e) { return; }
            }
            rows = Array.isArray(rows) ? rows : [];
            if (!rows.length) return;
            const end = await this.effectiveLatest();
            let startDate;
            if (rows.length > n) {
                startDate = new Date(rows[n].recordedAt);
            } else {
                const oldest = rows[rows.length - 1];
                const ctMs = (oldest && oldest.ct) ? oldest.ct : 5000;
                startDate = new Date(new Date(oldest.recordedAt).getTime() - ctMs - 2000);
            }
            this.endTime = this.dateToInput(end);
            this.startTime = this.dateToInput(startDate);
            await this.loadAll();
        },

        // ── Excel 다운로드 (로드된 모든 Flow 의 화면 상태를 한 시트에 세로로 쌓음 — 개요라 시작/끝 lane 만 실림) ──
        callNameOf(slice, id) { const l = id ? slice.callLanes.find(x => x.callId === id) : null; return l ? l.callName : null; },
        buildSliceExportModel(slice) {
            const csMs = slice.chartStart ? slice.chartStart.getTime() : 0;
            return {
                flowName: slice.flowName,
                chartStart: slice.chartStartIso, chartEnd: slice.chartEndIso,
                viewMode: slice.viewMode,
                headCallId: slice.headCallId, tailCallId: slice.tailCallId,
                headName: this.callNameOf(slice, slice.headCallId), tailName: this.callNameOf(slice, slice.tailCallId),
                avgCycleMs: slice.avgCycleMs, avgActiveMs: slice.avgActiveMs,
                lanes: slice.callLanes.map(l => ({
                    callId: l.callId, callName: l.callName, workName: l.workName, laneIndex: l.laneIndex,
                    inTag: l.inTag, outTag: l.outTag,
                    intervals: l.intervals, outIntervals: l.outIntervals, inIntervals: l.inIntervals
                })),
                cycleBoundaries: slice.cycleBoundariesIso, tailEdges: slice.tailEdgesIso,
                showMaxGap: false, selectedGapIndex: 0,
                topGaps: (slice.topGaps || []).map(g => ({
                    callId: g.callId, durMs: g.durMs,
                    startOffMs: g.startMs - csMs, endOffMs: g.endMs - csMs
                }))
            };
        },
        _stamp() { const t = new Date(); const p = (x) => String(x).padStart(2, '0'); return `${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}`; },
        _flash(text, isErr, ms) {
            this.msg = text; this.msgError = !!isErr;
            clearTimeout(this._msgTimer);
            if (ms) this._msgTimer = setTimeout(() => { this.msg = ''; }, ms);
        },
        async exportAllExcel() {
            if (this.exportingAll) return;
            const models = this.flows
                .filter(s => !s.loading && !s.error && s.callLanes.length)
                .map(s => this.buildSliceExportModel(s));
            if (!models.length) { this._flash('내보낼 간트가 없습니다.', true, 3000); return; }
            this.exportingAll = true; this._flash('Excel 생성 중… (' + models.length + '개 Flow)', false, 0);
            try {
                const res = await fetch('/api/cycle-analysis/export-excel-bulk', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(models)
                });
                if (!res.ok) throw new Error('HTTP ' + res.status);
                let fn = 'CycleTime_ALL_' + this._stamp() + '.xlsx';
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
                this._flash('Excel 다운로드 완료 (' + models.length + '개 Flow)', false, 5000);
            } catch (e) {
                this._flash('Excel 내보내기 실패: ' + e.message, true, 6000);
            } finally { this.exportingAll = false; }
        },

        // ── 시간범위 컨트롤 ──
        onTimeChanged() {
            this.timePreset = null; this.cyclePreset = null;
            this.clampTimeRange();
            clearTimeout(this._timer);
            this._timer = setTimeout(() => {
                if (this.inputToDate(this.endTime) <= this.inputToDate(this.startTime)) return;
                this.loadAll();
            }, 350);
        },
        // 시간창(start~end) 상한 — 종료 기준으로 시작을 당기고 토스트 안내(shell.js SSOT, 단일 페이지와 동일).
        clampTimeRange() {
            if (!window.dspClampRange || !this.startTime || !this.endTime) return;
            const r = window.dspClampRange(this.inputToDate(this.startTime), this.inputToDate(this.endTime), 'end');
            if (!r.clamped) return;
            this.startTime = this.dateToInput(r.start);
            this.endTime = this.dateToInput(r.end);
            if (window.dspToast) window.dspToast(window.dspRangeClampMsg, 'warning');
        },
        async setRecentMinutes(min) {
            this.timePreset = 'm' + min; this.cyclePreset = null; this.rangePopupOpen = false;
            const end = await this.effectiveLatest();
            this.endTime = this.dateToInput(end);
            this.startTime = this.dateToInput(new Date(end.getTime() - min * 60000));
            await this.loadAll();
        },
        async setRecentHours(h) {
            this.timePreset = 'h' + h; this.cyclePreset = null; this.rangePopupOpen = false;
            const end = await this.effectiveLatest();
            this.endTime = this.dateToInput(end);
            this.startTime = this.dateToInput(new Date(end.getTime() - h * 3600000));
            await this.loadAll();
        },
        // 현재 기간을 그대로 다시 로드(프리셋이면 앵커(최신 신호 시각)도 새로 잡는다).
        async refreshAll() {
            if (this.loadingAny) return;
            let m;
            if (this.timePreset && (m = this.timePreset.match(/^m(\d+)$/))) return await this.setRecentMinutes(+m[1]);
            if (this.timePreset && (m = this.timePreset.match(/^h(\d+)$/))) return await this.setRecentHours(+m[1]);
            if (this.cyclePreset) return await this.setRecentCycles(this.cyclePreset);
            await this.loadAll();
        },
        // 프리셋("최근 N분") 의 끝점 = 벽시계 now 가 아니라 *DB 최신 로그 시각*(신호 없는 창에 앵커하면
        // 빈 화면이 되는 것을 피하는 기존 설계). 그 사실을 dataAnchorHint 로 화면에 노출한다.
        async effectiveLatest() {
            try {
                const t = await this.apiGet('/api/call-test/latest-time');
                const d = this.inputToDate(this.toInputValue(t.end));
                this.dataLatestAt = d;
                this.refreshAnchorHint();
                return d;
            } catch (e) { this.dataLatestAt = null; this.dataAnchorHint = ''; return new Date(); }
        },
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
        }
    };
}
