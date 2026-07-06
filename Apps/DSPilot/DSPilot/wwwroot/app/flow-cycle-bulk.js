/*
 * flow-cycle-bulk.js — 사이클 분석 "전체" (bulkCycleApp).
 * ------------------------------------------------------------------------------
 * /flow-cycle 에 ?name= 없이 진입했을 때(전체/시스템 스코프) 쓰는 Alpine 컴포넌트.
 * 한 화면에 여러 Flow 사이클 간트를 세로로 쌓아 조회·일괄 편집한다.
 *   - ?system=<시스템명> → 그 시스템의 Flow 만.  매개변수 없음 → 전 시스템 모든 Flow.
 * 렌더/파생은 /app/cycle-gantt.js(window.CycleGantt) 를 재사용 — 단일 flow-cycle(flowApp)과
 *   동일한 SVG 엔진을 쓰도록 재동기화되어 있어 카드 간트가 단일 페이지와 픽셀 동등하다.
 * 편집은 전부 "스테이징": 카드에서 이상치/Head·Tail/실측을 바꿔두고 상단 "일괄 적용" 으로 커밋.
 *   · 이상치(CT 범위) : POST /api/dashboard/exclusions (Flow별, 초, 서버 공유) — 가벼움.
 *   · Head/Tail 경계  : POST /api/flow/{name}/cycle-override → 전체 이력 백그라운드 재계산(단일잡).
 *                       재계산은 단일 슬롯 → 변경된 Flow 를 순차 저장·폴링(병렬 금지 = 누락 방지).
 *   · 실측 duration   : POST /api/call-test/apply-durations (모든 Flow 변경 1회 병합 — 단일 AASX export).
 * Excel: "전체 Excel 다운로드" → 로드된 모든 Flow 의 화면 상태(buildSliceExportModel)를 배열로
 *   POST /api/cycle-analysis/export-excel-bulk → 한 시트에 Flow 블록을 세로로 쌓은 xlsx.
 * Chart.js 미사용(간트=순수 SVG) → N개 차트 인스턴스 반응형 폭주 위험 원천 차단.
 *
 * ※ 구 flow-all.html(bulkApp)에서 이관. /flow-all 은 /flow-cycle 로 리다이렉트된다.
 */
function bulkCycleApp() {
    const CG = window.CycleGantt;
    const LEFT_PAD = CG.LEFT_PAD, RIGHT_PAD = CG.RIGHT_PAD, MIN = CG.MIN_PLOT_WIDTH, MAX_ZOOM = CG.MAX_ZOOM;
    const _unitSec = { s: 1, m: 60, h: 3600 };
    // 사이클 프리셋용 히스토리 캐시 (closure, Alpine 반응형 밖 — 단순 캐시)
    const histCache = {};

    return {
        TOP_MARGIN: CG.TOP_MARGIN,
        RIBBON_H: CG.RIBBON_H,

        systemName: '',
        flows: [],
        startTime: '', endTime: '',
        excludeIncomplete: true,
        controlsOpen: true,
        // 시간/사이클 프리셋 활성 표시 (단일 페이지 call-test-controls 와 동일 UX)
        timePreset: null, cyclePreset: null, rangePopupOpen: false,
        rt: { connected: false },
        saving: false, saveMsg: '', saveError: false,
        exportingAll: false,
        // 공유 보기/줌 — 상단 툴바가 모든 Flow 간트에 일괄 적용(단일 페이지와 동일한 컨트롤 1벌).
        viewMode: 'bar', zoom: 1,
        _conn: null, _timer: null,

        async init() {
            // 테마는 shell.js 가 <html> 에 적용. 미완료 제외 토글만 복원.
            this.excludeIncomplete = localStorage.getItem('dspilot-flow-exclude-incomplete') !== '0';
            // 상단 컨트롤 접힘 상태 복원(영속). 미저장이면 펼침.
            this.controlsOpen = localStorage.getItem('dspilot-flowall-controls-open') !== '0';
            this.systemName = new URLSearchParams(location.search).get('system') || '';

            // 컨테이너 폭 변화 → 간트 폭맞춤(디바운스)
            let _rt;
            window.addEventListener('resize', () => {
                clearTimeout(_rt);
                _rt = setTimeout(() => {
                    for (const s of this.flows) {
                        if (s.callLanes.length) { this.measurePlotWidth(s); s.svgMarkup = CG.buildSvg(s); }
                    }
                }, 180);
            });

            await this.loadFlows();
            await this.loadExclusions();
            // 최초 범위 — URL 기간 파라미터(?period/from/to, 같은 페이지 나브 이동 시 shell 이 실어 보냄)
            // 복원, 없으면 기본 최근 5분 프리셋 (단일 페이지와 동일).
            await this.applyRangeFromUrl();
            this.connectSignalR();
            // 더티 가드 등록 — 전체 편집에서 미적용 변경 이탈 방지
            window.dspDirtyRegister(() => this.hasPending);
        },

        destroy() { clearTimeout(this._timer); if (this._conn) this._conn.stop(); },

        // 브레드크럼/제목 보조
        scopeLabel() { return this.systemName ? this.systemName : '전체 시스템'; },

        // ── 상단 컨트롤 접기/펼치기 (영속) ──
        toggleControls() {
            this.controlsOpen = !this.controlsOpen;
            try { localStorage.setItem('dspilot-flowall-controls-open', this.controlsOpen ? '1' : '0'); } catch (e) { /* ignore */ }
            if (this.controlsOpen) {
                this.$nextTick(() => {
                    for (const s of this.flows) {
                        if (s.callLanes.length) { this.measurePlotWidth(s); s.svgMarkup = CG.buildSvg(s); }
                    }
                });
            }
        },
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
            try {
                const nav = await this.apiGet('/api/nav');
                const systems = (nav && nav.systems) || [];
                if (this.systemName) {
                    const sys = systems.find(s => s.name === this.systemName);
                    names = sys ? (sys.flows || []) : [];
                } else {
                    // 시스템 미지정 → 모든 시스템의 모든 Flow
                    for (const sys of systems) for (const fn of (sys.flows || [])) names.push(fn);
                    names = Array.from(new Set(names));
                }
            } catch (e) { /* 실패 시 빈 목록 */ }
            this.flows = names.map((n, i) => this.makeSlice(n, i));
        },

        makeSlice(flowName, idx) {
            return {
                id: idx, flowName,
                loading: true, error: null, overlayBusy: false,
                callLanesRaw: [], callLanes: [],
                cycleBoundaries: [], tailEdges: [], tailCompletionSource: null,
                cycleBoundariesIso: [], tailEdgesIso: [],   // Excel 내보내기용 원본 ISO
                chartStart: null, chartEnd: null, chartStartIso: '', chartEndIso: '',
                headCallId: null, tailCallId: null,
                savedHeadCallId: null, savedTailCallId: null, userOverrodeHeadTail: false,
                projectHeadId: null, projectTailId: null,
                isOverride: false, avgCycleMs: null, avgActiveMs: null,
                plotWidth: 1200, baseWidth: 1200, zoom: this.zoom, viewMode: this.viewMode,
                expandedCalls: {}, topGaps: [], showMaxGap: false, selectedGapIndex: 0,
                svgMarkup: '', selectedRange: null, _geo: null, _drag: null,
                rangeForm: { min: '', max: '', minUnit: 's', maxUnit: 's' }, savedRange: null,
                stageDurations: false, applyDurBusy: false, applyDurMsg: '', showCycles: false
            };
        },

        get loadingAny() { return this.flows.some(s => s.loading); },

        // ── 분석 기간 URL 동기화 (?period=프리셋 | ?from/?to=직접 범위) — 단일 페이지(flow-workspace)와 동일 규약 ──
        // shell 나브의 같은 페이지 전체/FLOW 이동(withPeriodCarry)이 이 파라미터를 실어 가 기간이 유지된다.
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
                this.timePreset = null; this.cyclePreset = null;
                return await this.loadAll();
            }
            return await this.setRecentMinutes(5);
        },

        // ── 로드 (동시성 제한) ──
        async loadAll() {
            this.syncRangeUrl(); // 모든 범위 변경(프리셋/수동/드래그)이 여기로 수렴 — 현재 기간을 URL 반영
            const list = this.flows.slice();
            const CONC = 4;
            let idx = 0;
            const worker = async () => { while (idx < list.length) { const s = list[idx++]; await this.loadSlice(s); } };
            if (window.dspLoading) window.dspLoading.begin('전체 Flow 불러오는 중…');
            try {
                const workers = [];
                for (let i = 0; i < Math.min(CONC, list.length); i++) workers.push(worker());
                await Promise.all(workers);
            } finally {
                if (window.dspLoading) window.dspLoading.end();
            }
        },

        async loadSlice(slice) {
            const start = this.inputToDate(this.startTime), end = this.inputToDate(this.endTime);
            if (end <= start) { slice.error = '종료 시각은 시작 시각보다 커야 합니다.'; slice.loading = false; return; }
            slice.loading = true; slice.error = null;
            try {
                const body = {
                    flowName: slice.flowName, start: this.startTime, end: this.endTime,
                    headCallId: slice.headCallId, tailCallId: slice.tailCallId,
                    headSpecified: slice.userOverrodeHeadTail, tailSpecified: slice.userOverrodeHeadTail
                };
                const d = await this.apiPost('/api/call-test/load', body);
                this.applyLoadResult(slice, d);
            } catch (e) {
                slice.error = '데이터 로딩 실패: ' + e.message;
            } finally { slice.loading = false; }
        },

        applyLoadResult(slice, d) {
            slice.callLanesRaw = d.lanes || [];
            slice.chartStart = new Date(d.chartStart);
            slice.chartEnd = new Date(d.chartEnd);
            slice.chartStartIso = d.chartStart; slice.chartEndIso = d.chartEnd;
            slice.headCallId = d.headCallId || null;
            slice.tailCallId = d.tailCallId || null;
            slice.savedHeadCallId = slice.headCallId;   // 변경 감지 기준선
            slice.savedTailCallId = slice.tailCallId;
            slice.userOverrodeHeadTail = false;
            slice.projectHeadId = d.projectHeadCallId || null;
            slice.projectTailId = d.projectTailCallId || null;
            slice.cycleBoundariesIso = d.cycleBoundaries || [];
            slice.tailEdgesIso = d.tailEdges || [];
            slice.cycleBoundaries = slice.cycleBoundariesIso.map(s => new Date(s));
            slice.tailEdges = slice.tailEdgesIso.map(s => new Date(s));
            slice.tailCompletionSource = d.tailCompletionSource ?? null;
            slice.avgCycleMs = d.avgCycleMs ?? null;
            slice.avgActiveMs = d.avgActiveMs ?? null;
            slice.isOverride = !!d.isOverride;
            slice.selectedRange = null;
            slice.callLanes = CG.sortLanes(slice.callLanesRaw, slice.headCallId, slice.tailCallId);
            slice.topGaps = CG.topGapsOf(slice);
            if (slice.selectedGapIndex >= slice.topGaps.length) slice.selectedGapIndex = 0;
            slice.svgMarkup = CG.buildSvg(slice);
            this.$nextTick(() => { this.measurePlotWidth(slice); slice.svgMarkup = CG.buildSvg(slice); });
        },

        // ── 간트 지오메트리 / 줌 ──
        areaEl(slice) { return document.getElementById('cta-' + slice.id); },
        measurePlotWidth(slice) {
            const el = this.areaEl(slice);
            const avail = el ? el.clientWidth : 1100;
            slice.baseWidth = Math.max(MIN, Math.round(avail - LEFT_PAD - RIGHT_PAD - 4));
            slice.plotWidth = Math.max(MIN, Math.round(slice.baseWidth * slice.zoom));
        },
        rebuild(slice) { slice.svgMarkup = CG.buildSvg(slice); },
        setView(slice, mode) { if (slice.viewMode === mode) return; slice.viewMode = mode; slice.svgMarkup = CG.buildSvg(slice); },
        // ── 공유(전체 일괄) 보기/줌 — 상단 툴바 ──
        setViewAll(mode) {
            if (this.viewMode === mode) return;
            this.viewMode = mode;
            for (const s of this.flows) { s.viewMode = mode; if (s.callLanes.length) s.svgMarkup = CG.buildSvg(s); }
        },
        zoomAll(factor) {
            const nz = Math.min(MAX_ZOOM, Math.max(1, this.zoom * factor));
            if (Math.abs(nz - this.zoom) < 1e-6) return;
            this.zoom = nz;
            for (const s of this.flows) { s.zoom = nz; this.measurePlotWidth(s); if (s.callLanes.length) s.svgMarkup = CG.buildSvg(s); }
        },
        resetZoomAll() {
            this.zoom = 1;
            for (const s of this.flows) { s.zoom = 1; this.measurePlotWidth(s); if (s.callLanes.length) s.svgMarkup = CG.buildSvg(s); }
            this.$nextTick(() => { for (const s of this.flows) { const el = this.areaEl(s); if (el) el.scrollLeft = 0; } });
        },
        toggleExpand(slice, callId) {
            slice.expandedCalls = { ...slice.expandedCalls, [callId]: !slice.expandedCalls[callId] };
            slice.svgMarkup = CG.buildSvg(slice);
        },
        onWheel(e, slice) {
            if (!slice.callLanes.length) return;
            const el = e.currentTarget; if (!el) return;
            e.preventDefault();
            const screenX = e.clientX - el.getBoundingClientRect().left;
            const factor = e.deltaY < 0 ? 1.25 : 1 / 1.25;
            this.applyZoom(slice, this.zoom * factor, screenX, el);
        },
        // 휠 줌도 상단 툴바(zoomAll)와 같은 "공유 줌" 하나로 통일 — 모든 카드에 일괄 적용된다.
        // 앵커(휠 커서 아래 시각) 보존은 이벤트가 난 카드 기준으로 계산하고, 나머지 카드는
        // 같은 스크롤 비율로 맞춰 전 간트가 같은 구간을 보여준다.
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
                const left = frac * slice.plotWidth + LEFT_PAD - anchorX;
                el.scrollLeft = left;
                const ratio = slice.plotWidth > 0 ? Math.max(0, left) / slice.plotWidth : 0;
                for (const s of this.flows) {
                    if (s === slice) continue;
                    const other = this.areaEl(s);
                    if (other) other.scrollLeft = ratio * s.plotWidth;
                }
            });
        },
        focusMaxGap(slice) {
            if (!slice.topGaps.length) return;
            slice.showMaxGap = true; slice.selectedGapIndex = 0;
            slice.svgMarkup = CG.buildSvg(slice);
            this.$nextTick(() => {
                const area = this.areaEl(slice), g = slice.topGaps[0];
                if (!area || !slice._geo || !g) return;
                const midX = LEFT_PAD + ((g.startMs + g.endMs) / 2 - slice._geo.cs) * slice._geo.xScale;
                const target = Math.max(0, midX - area.clientWidth / 2);
                try { area.scrollTo({ left: target, behavior: 'smooth' }); } catch (_) { area.scrollLeft = target; }
            });
        },

        // ── Head/Tail 토글 (스테이징 — 저장은 일괄 적용에서) ──
        toggleHead(slice, callId) {
            if (slice.headCallId === callId) return;
            slice.headCallId = callId;
            slice.userOverrodeHeadTail = true;
            this.resolveOverlays(slice);
        },
        toggleTail(slice, callId) {
            if (slice.tailCallId === callId) return;
            slice.tailCallId = callId;
            slice.userOverrodeHeadTail = true;
            this.resolveOverlays(slice);
        },
        restoreToAasx(slice) {
            if (!slice.projectHeadId || !slice.projectTailId) return;
            slice.headCallId = slice.projectHeadId;
            slice.tailCallId = slice.projectTailId;
            slice.userOverrodeHeadTail = true;
            this.resolveOverlays(slice);
        },
        async resolveOverlays(slice) {
            if (!slice.flowName) return;
            slice.overlayBusy = true;
            try {
                const headLane = slice.headCallId ? slice.callLanes.find(l => l.callId === slice.headCallId) : null;
                const tailLane = slice.tailCallId ? slice.callLanes.find(l => l.callId === slice.tailCallId) : null;
                const body = {
                    flowName: slice.flowName, start: this.startTime, end: this.endTime,
                    headCallId: slice.headCallId, tailCallId: slice.tailCallId,
                    headStartTag: headLane ? headLane.outTag : null,
                    tailFinishTag: tailLane ? tailLane.inTag : null,
                    tailOutTag: tailLane ? tailLane.outTag : null
                };
                const d = await this.apiPost('/api/call-test/resolve-overlays', body);
                slice.cycleBoundariesIso = d.cycleBoundaries || [];
                slice.tailEdgesIso = d.tailEdges || [];
                slice.cycleBoundaries = slice.cycleBoundariesIso.map(s => new Date(s));
                slice.tailEdges = slice.tailEdgesIso.map(s => new Date(s));
                slice.avgCycleMs = d.avgCycleMs ?? null;
                slice.avgActiveMs = d.avgActiveMs ?? null;
                slice.tailCompletionSource = d.tailCompletionSource ?? null;
                slice.callLanes = CG.sortLanes(slice.callLanesRaw, slice.headCallId, slice.tailCallId);
                slice.topGaps = CG.topGapsOf(slice);
                slice.svgMarkup = CG.buildSvg(slice);
            } catch (e) {
                slice.error = '오버레이 갱신 실패: ' + e.message;
            } finally { slice.overlayBusy = false; }
        },

        // ── 변경 감지 (템플릿 바인딩) ──
        callNameOf(slice, id) { const l = id ? slice.callLanes.find(x => x.callId === id) : null; return l ? l.callName : null; },
        headName(slice) { return this.callNameOf(slice, slice.headCallId); },
        tailName(slice) { return this.callNameOf(slice, slice.tailCallId); },
        headTailChanged(slice) {
            return slice.headCallId !== slice.savedHeadCallId || slice.tailCallId !== slice.savedTailCallId;
        },
        durCount(slice) { return CG.collectAllDurationChanges(slice).length; },

        // ── 이상치 범위 (스테이징, 초 단위) ──
        rangeFieldSec(slice, field) {
            const v = field === 'min' ? slice.rangeForm.min : slice.rangeForm.max;
            const unit = field === 'min' ? slice.rangeForm.minUnit : slice.rangeForm.maxUnit;
            const n = parseFloat(v), mult = _unitSec[unit] || 1;
            return (v !== '' && v != null && isFinite(n) && n >= 0) ? Math.round(n * mult * 1000) / 1000 : null;
        },
        stagedRangeSec(slice) {
            let min = this.rangeFieldSec(slice, 'min'), max = this.rangeFieldSec(slice, 'max');
            if (min != null && max != null && min > max) { const t = min; min = max; max = t; }
            return { min, max };
        },
        exRangeMs(slice) {
            const r = this.stagedRangeSec(slice);
            if (r.min == null && r.max == null) return null;
            return { min: r.min != null ? r.min * 1000 : null, max: r.max != null ? r.max * 1000 : null };
        },
        outlierChanged(slice) {
            const st = this.stagedRangeSec(slice);
            const sv = slice.savedRange || { min: null, max: null };
            return (st.min ?? null) !== (sv.min ?? null) || (st.max ?? null) !== (sv.max ?? null);
        },
        changeUnit(slice, field, u) {
            const cur = field === 'min' ? slice.rangeForm.minUnit : slice.rangeForm.maxUnit;
            if (u === cur) return;
            const from = _unitSec[cur] || 1, to = _unitSec[u] || 1;
            const v = field === 'min' ? slice.rangeForm.min : slice.rangeForm.max;
            const n = parseFloat(v);
            const conv = (v === '' || v == null || !isFinite(n)) ? v : String(Math.round((n * from / to) * 1000) / 1000);
            if (field === 'min') { slice.rangeForm.min = conv; slice.rangeForm.minUnit = u; }
            else { slice.rangeForm.max = conv; slice.rangeForm.maxUnit = u; }
        },
        resetRange(slice) { slice.rangeForm.min = ''; slice.rangeForm.max = ''; },

        // ── 사이클 목록 / 포맷 래퍼 ──
        visibleCycles(slice) { return CG.visibleCycleRows(slice, this.exRangeMs(slice), this.excludeIncomplete); },
        excludedCount(slice) { return CG.excludedCycleCount(slice, this.exRangeMs(slice)); },
        laneRows(slice) { return CG.laneRows(slice); },
        rowClass(slice, row) { return CG.rowClass(slice, row); },
        hasApiCalls(lane) { return CG.hasApiCalls(lane); },
        ratioCls(r) { return CG.ratioCls(r); },
        fmtMs(ms) { return CG.formatMs(ms); },
        fmtAasx(ms) { return CG.fmtAasx(ms); },
        hms(d) { return CG.hms(d); },
        hms2k(d) { return d.getHours() + '시 ' + d.getMinutes() + '분 ' + d.getSeconds() + '초'; },
        canApplyApi(lane, ac) { return CG.canApplyApi(lane, ac); },

        // ── 드래그 구간 선택 (e.currentTarget 로 $refs 대체) ──
        onDragStart(e, slice) {
            if (e.pointerType === 'mouse' && e.button !== 0) return;
            if (!slice.callLanes.length) return;
            const area = e.currentTarget;
            const svg = area && area.querySelector('svg');
            if (!svg || !slice._geo) return;
            const rectOf = () => svg.getBoundingClientRect();
            const layout = CG.laneLayout(slice);
            const laneTop = CG.TOP_MARGIN + (slice.cycleBoundaries.length ? CG.RIBBON_H : 0);
            const laneAreaH = layout.totalH;
            const laneBottom = laneTop + laneAreaH;
            const r0 = rectOf();
            const x0 = e.clientX - r0.left, y0 = e.clientY - r0.top;
            if (x0 < LEFT_PAD || x0 > LEFT_PAD + slice.plotWidth || y0 < laneTop - 8 || y0 > laneBottom + 8) return;
            e.preventDefault();
            const pid = e.pointerId;
            try { area.setPointerCapture(pid); } catch (_) {}
            const { cs, xScale } = slice._geo;
            const edgePx = [];
            for (const lane of slice.callLanes) {
                for (const arr of [lane.intervals, lane.outIntervals, lane.inIntervals]) {
                    for (const iv of (arr || [])) {
                        edgePx.push(LEFT_PAD + (new Date(iv.start).getTime() - cs) * xScale);
                        edgePx.push(LEFT_PAD + (new Date(iv.end).getTime() - cs) * xScale);
                    }
                }
            }
            for (const b of (slice.cycleBoundaries || [])) edgePx.push(LEFT_PAD + (b.getTime() - cs) * xScale);
            const SNAP_PX = 10;
            const clampX = (x) => Math.max(LEFT_PAD, Math.min(LEFT_PAD + slice.plotWidth, x));
            const snapCurX = (x) => {
                let best = x, bestDist = SNAP_PX;
                for (const ex of edgePx) { const d = Math.abs(ex - x); if (d < bestDist) { bestDist = d; best = ex; } }
                return best;
            };
            const sx = clampX(snapCurX(x0));
            const startMs = cs + (sx - LEFT_PAD) / xScale;
            const ns = 'http://www.w3.org/2000/svg';
            const live = document.createElementNS(ns, 'rect');
            live.setAttribute('x', CG.f(sx)); live.setAttribute('width', '0');
            live.setAttribute('y', String(laneTop)); live.setAttribute('height', String(laneAreaH));
            live.setAttribute('fill', 'rgba(126,87,194,0.16)');
            live.setAttribute('stroke', '#7e57c2'); live.setAttribute('stroke-dasharray', '4 3');
            live.setAttribute('pointer-events', 'none');
            svg.appendChild(live);
            let moved = false;
            const onMove = (ev) => {
                if (ev.pointerId !== pid) return;
                if (ev.cancelable) ev.preventDefault();
                const cur = clampX(snapCurX(ev.clientX - rectOf().left));
                const x = Math.min(sx, cur), w = Math.abs(cur - sx);
                live.setAttribute('x', CG.f(x)); live.setAttribute('width', CG.f(w));
                if (w > 3) moved = true;
            };
            const onUp = (ev) => {
                if (ev.pointerId !== pid) return;
                document.removeEventListener('pointermove', onMove, true);
                document.removeEventListener('pointerup', onUp, true);
                document.removeEventListener('pointercancel', onUp, true);
                try { area.releasePointerCapture(pid); } catch (_) {}
                if (live.parentNode) live.parentNode.removeChild(live);
                slice._drag = null;
                if (!moved) { if (slice.selectedRange) this.clearRangeSelection(slice); return; }
                const cur = clampX(snapCurX(ev.clientX - rectOf().left));
                const endMs = cs + (cur - LEFT_PAD) / xScale;
                slice.selectedRange = { startMs: Math.min(startMs, endMs), endMs: Math.max(startMs, endMs) };
                slice.svgMarkup = CG.buildSvg(slice);
            };
            slice._drag = { onMove, onUp };
            document.addEventListener('pointermove', onMove, true);
            document.addEventListener('pointerup', onUp, true);
            document.addEventListener('pointercancel', onUp, true);
        },
        clearRangeSelection(slice) { slice.selectedRange = null; slice.svgMarkup = CG.buildSvg(slice); },
        async applyRangeAsPeriod(slice) {
            const r = slice.selectedRange; if (!r) return;
            const s = new Date(r.startMs), e = new Date(r.endMs);
            if (e <= s) return;
            this.timePreset = null; this.cyclePreset = null;   // 수동 범위 → 프리셋 해제 (단일 페이지와 동일)
            this.startTime = this.dateToInput(s);
            this.endTime = this.dateToInput(e);
            for (const fl of this.flows) fl.selectedRange = null;
            await this.loadAll();
        },
        selRangeLenMs(slice) { return slice.selectedRange ? Math.max(0, slice.selectedRange.endMs - slice.selectedRange.startMs) : 0; },
        selRunMs(slice) {
            const r = slice.selectedRange; if (!r) return 0;
            let sum = 0;
            for (const lane of slice.callLanes) for (const iv of (lane.intervals || [])) {
                const sv = new Date(iv.start).getTime(), ev = new Date(iv.end).getTime();
                const lo = Math.max(sv, r.startMs), hi = Math.min(ev, r.endMs);
                if (hi > lo) sum += (hi - lo);
            }
            return sum;
        },
        selGapMs(slice) {
            const r = slice.selectedRange; if (!r) return 0;
            let sum = 0;
            for (const g of CG.computeAllGaps(slice)) {
                const lo = Math.max(g.startMs, r.startMs), hi = Math.min(g.endMs, r.endMs);
                if (hi > lo) sum += (hi - lo);
            }
            return sum;
        },
        selActiveDevices(slice) {
            const r = slice.selectedRange; if (!r) return 0;
            const set = new Set();
            for (const lane of slice.callLanes) {
                for (const iv of (lane.intervals || [])) {
                    const sv = new Date(iv.start).getTime(), ev = new Date(iv.end).getTime();
                    if (Math.min(ev, r.endMs) > Math.max(sv, r.startMs)) { set.add(lane.callId); break; }
                }
            }
            return set.size;
        },
        // ── 선택 구간 플로팅 툴팁 (개별 페이지 ct-sel-tip 과 동일 지표/위치) ──
        selHasCycles(slice) { return slice.cycleBoundaries.length > 0; },
        _selCycleAgg(slice) {
            const r = slice.selectedRange; const out = { mt: 0, wt: 0, n: 0 };
            if (!r) return out;
            for (const c of CG.cycleRows(slice)) {
                const s = c.startMs, e = c.startMs + (c.ctMs || 0);
                if (Math.min(e, r.endMs) > Math.max(s, r.startMs)) {
                    out.n++;
                    if (!c.isOpen && c.atMs != null) { out.mt += c.atMs; out.wt += (c.wtMs || 0); }
                }
            }
            return out;
        },
        selCycleCount(slice) { return this._selCycleAgg(slice).n; },
        selActiveMs(slice) { return this._selCycleAgg(slice).mt; },
        selWaitMs(slice) { return this._selCycleAgg(slice).wt; },
        selTipCenterPx(slice) {
            const r = slice.selectedRange; if (!r || !slice._geo) return 0;
            const cs = slice._geo.cs, xScale = slice._geo.xScale;
            const raw = LEFT_PAD + ((r.startMs + r.endMs) / 2 - cs) * xScale;
            const chartW = LEFT_PAD + slice.plotWidth + RIGHT_PAD;
            return Math.round(Math.max(170, Math.min(chartW - 170, raw)));
        },
        selTipTopPx(slice) { return CG.TOP_MARGIN + (slice.cycleBoundaries.length ? CG.RIBBON_H : 0) + 6; },

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

        // ── 실측 → AASX duration 즉시 적용 (개별 행 버튼 — 일괄 적용과 별도) ──
        async applyApiCallDuration(slice, row) {
            const ch = CG.buildDurationChange(row.lane, row.ac);
            if (!ch) return;
            await this._applyDurations(slice, [ch], `'${row.ac.name}' 실측 duration 을 AASX 에 적용`);
        },
        async _applyDurations(slice, changes, label) {
            if (!changes.length) return;
            if (!window.confirm(label + '합니다.\n\n공유 project.aasx 의 Device Work(Duration/Min/Max)를 덮어씁니다 — Promaker 와 공유되는 파일입니다. 계속할까요?')) return;
            slice.applyDurBusy = true; slice.applyDurMsg = 'AASX 적용 중…'; slice.error = null;
            try {
                const r = await this.apiPost('/api/call-test/apply-durations', { changes });
                slice.applyDurMsg = 'AASX 적용 완료 (' + (r != null ? r.applied ?? 0 : 0) + '건)';
                await this.loadSlice(slice);   // '현재 AASX' 값 재조회
                setTimeout(() => { slice.applyDurMsg = ''; }, 5000);
            } catch (e) {
                slice.error = '실측 적용 실패: ' + e.message;
                slice.applyDurMsg = '';
            } finally { slice.applyDurBusy = false; }
        },

        // ── 변경 요약 (일괄 적용 바) ──
        get pendingHt() { return this.flows.filter(s => this.headTailChanged(s) && s.headCallId && s.tailCallId).length; },
        get pendingOutlier() { return this.flows.filter(s => this.outlierChanged(s)).length; },
        get pendingDur() { return this.flows.filter(s => s.stageDurations && this.durCount(s) > 0).length; },
        get hasPending() { return (this.pendingHt + this.pendingOutlier + this.pendingDur) > 0; },
        get loadedFlowCount() { return this.flows.filter(s => !s.loading && !s.error && s.callLanes.length).length; },

        resetAllStaged() {
            if (!confirm('적용하지 않은 모든 변경을 되돌립니다. 계속할까요?')) return;
            for (const s of this.flows) {
                s.stageDurations = false;
                this.initRangeFromSaved(s);
                if (this.headTailChanged(s)) { s.headCallId = s.savedHeadCallId; s.tailCallId = s.savedTailCallId; s.userOverrodeHeadTail = false; this.resolveOverlays(s); }
            }
        },

        // ── 일괄 적용 (순차) ──
        async saveAll() {
            if (this.saving) return;
            const htFlows = this.flows.filter(s => this.headTailChanged(s) && s.headCallId && s.tailCallId);
            const outFlows = this.flows.filter(s => this.outlierChanged(s));
            const durChanges = [];
            const durFlows = [];
            for (const s of this.flows) {
                if (s.stageDurations) { const ch = CG.collectAllDurationChanges(s); if (ch.length) { durChanges.push(...ch); durFlows.push(s); } }
            }
            if (!htFlows.length && !outFlows.length && !durChanges.length) {
                this.saveMsg = '변경된 내용이 없습니다.'; this.saveError = false;
                setTimeout(() => { this.saveMsg = ''; }, 3000); return;
            }
            if ((htFlows.length || durChanges.length) &&
                !confirm('일괄 적용:\n· Head/Tail ' + htFlows.length + '개 (과거 이력 재계산 동반)\n· 실측 duration ' + durChanges.length + '건\n\n공유 project.aasx 를 덮어씁니다 — Promaker 와 공유되는 파일입니다. 계속할까요?')) return;

            this.saving = true; this.saveError = false; this.saveMsg = '';
            try {
                // 1) 이상치 (가벼움 · 재계산 없음)
                if (outFlows.length) {
                    this.saveMsg = '이상치 범위 저장 중…';
                    for (const s of outFlows) { const r = this.stagedRangeSec(s); await this.saveExclusion(s.flowName, r.min, r.max); }
                }
                // 2) 실측 duration (전 Flow 병합 1회 POST → 단일 AASX export)
                if (durChanges.length) {
                    this.saveMsg = '실측 duration 적용 중… (' + durChanges.length + '건)';
                    await this.apiPost('/api/call-test/apply-durations', { changes: durChanges });
                }
                // 3) Head/Tail — 단일잡 재계산이라 변경된 Flow 를 순차 저장·폴링
                for (let i = 0; i < htFlows.length; i++) {
                    const s = htFlows[i];
                    const headName = this.callNameOf(s, s.headCallId), tailName = this.callNameOf(s, s.tailCallId);
                    this.saveMsg = '[' + (i + 1) + '/' + htFlows.length + '] ' + s.flowName + ' 경계 저장…';
                    await this.apiPost('/api/flow/' + encodeURIComponent(s.flowName) + '/cycle-override', { startCallName: headName, endCallName: tailName });
                    await this.pollRecompute(s.flowName, i + 1, htFlows.length);
                }
                // 4) 영향받은 Flow 재로드 + 이상치 동기화
                this.saveMsg = '새로고침 중…';
                await this.loadExclusions();
                const affected = new Set();
                htFlows.forEach(s => affected.add(s));
                outFlows.forEach(s => affected.add(s));
                durFlows.forEach(s => affected.add(s));
                for (const s of affected) { s.stageDurations = false; await this.loadSlice(s); }
                this.saveMsg = '일괄 적용 완료'; this.saveError = false;
                setTimeout(() => { if (!this.saving) this.saveMsg = ''; }, 6000);
            } catch (e) {
                this.saveError = true;
                this.saveMsg = '일괄 적용 실패: ' + e.message;
            } finally { this.saving = false; }
        },

        async pollRecompute(flow, idx, total) {
            let sawRunning = false;
            for (let i = 0; i < 600; i++) {
                let st = null;
                try { const r = await fetch('/api/flow/recompute-status'); if (r.ok) st = await r.json(); } catch (_) {}
                if (st && st.flow === flow) {
                    if (st.running) {
                        sawRunning = true;
                        this.saveMsg = '[' + idx + '/' + total + '] ' + flow + ' 재계산 중… (' + (st.phase || '') + (st.cyclesFound ? ', ' + st.cyclesFound + '회' : '') + ')';
                    } else if (st.done && (sawRunning || i === 0)) {
                        break;
                    }
                } else if (st && st.running) {
                    this.saveMsg = '[' + idx + '/' + total + '] 다른 작업 대기 중…';
                }
                await new Promise(r => setTimeout(r, 500));
            }
        },

        // ── 전체 Excel 다운로드 (로드된 모든 Flow 간트를 한 시트에 세로로 쌓음) ──
        buildSliceExportModel(slice) {
            const csMs = slice.chartStart ? slice.chartStart.getTime() : 0;
            return {
                flowName: slice.flowName,
                chartStart: slice.chartStartIso, chartEnd: slice.chartEndIso,
                viewMode: slice.viewMode,
                headCallId: slice.headCallId, tailCallId: slice.tailCallId,
                headName: this.headName(slice), tailName: this.tailName(slice),
                avgCycleMs: slice.avgCycleMs, avgActiveMs: slice.avgActiveMs,
                lanes: slice.callLanes.map(l => ({
                    callId: l.callId, callName: l.callName, workName: l.workName, laneIndex: l.laneIndex,
                    inTag: l.inTag, outTag: l.outTag,
                    intervals: l.intervals, outIntervals: l.outIntervals, inIntervals: l.inIntervals
                })),
                cycleBoundaries: slice.cycleBoundariesIso, tailEdges: slice.tailEdgesIso,
                showMaxGap: slice.showMaxGap, selectedGapIndex: slice.selectedGapIndex,
                topGaps: (slice.topGaps || []).map(g => ({
                    callId: g.callId, durMs: g.durMs,
                    startOffMs: g.startMs - csMs, endOffMs: g.endMs - csMs
                }))
            };
        },
        _stamp() { const t = new Date(); const p = (x) => String(x).padStart(2, '0'); return `${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}`; },
        async exportAllExcel() {
            if (this.exportingAll) return;
            const models = this.flows
                .filter(s => !s.loading && !s.error && s.callLanes.length)
                .map(s => this.buildSliceExportModel(s));
            if (!models.length) { this.saveMsg = '내보낼 간트가 없습니다.'; this.saveError = true; setTimeout(() => { this.saveMsg = ''; }, 3000); return; }
            this.exportingAll = true; this.saveError = false; this.saveMsg = '전체 Excel 생성 중… (' + models.length + '개 Flow)';
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
                this.saveMsg = '전체 Excel 다운로드 완료 (' + models.length + '개 Flow)';
                setTimeout(() => { this.saveMsg = ''; }, 5000);
            } catch (e) {
                this.saveError = true; this.saveMsg = '전체 Excel 내보내기 실패: ' + e.message;
            } finally { this.exportingAll = false; }
        },

        // ── 이상치 서버 공유 ──
        initRangeFromSaved(slice) {
            slice.rangeForm.minUnit = 's'; slice.rangeForm.maxUnit = 's';
            slice.rangeForm.min = (slice.savedRange && slice.savedRange.min != null) ? String(slice.savedRange.min) : '';
            slice.rangeForm.max = (slice.savedRange && slice.savedRange.max != null) ? String(slice.savedRange.max) : '';
        },
        async loadExclusions() {
            try {
                const rows = await this.apiGet('/api/dashboard/exclusions');
                const map = {};
                for (const r of (rows || [])) { if (r && r.flowName) map[r.flowName] = { min: r.minSec ?? null, max: r.maxSec ?? null }; }
                for (const s of this.flows) {
                    const had = !!s.savedRange;
                    s.savedRange = map[s.flowName] || null;
                    if (!this.outlierChanged(s) || !had) this.initRangeFromSaved(s);
                }
            } catch (e) { /* 미수신 시 기존값 유지 */ }
        },
        async saveExclusion(flowName, minSec, maxSec) {
            const res = await fetch('/api/dashboard/exclusions', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
                body: JSON.stringify({ flowName, minSec, maxSec })
            });
            if (!res.ok) throw new Error('HTTP ' + res.status);
        },

        onExcludeIncompleteChanged() { localStorage.setItem('dspilot-flow-exclude-incomplete', this.excludeIncomplete ? '1' : '0'); },

        // ── 시간범위 컨트롤 ──
        onTimeChanged() {
            this.timePreset = null; this.cyclePreset = null;
            clearTimeout(this._timer);
            this._timer = setTimeout(() => {
                if (this.inputToDate(this.endTime) <= this.inputToDate(this.startTime)) return;
                this.loadAll();
            }, 350);
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
        async effectiveLatest() {
            try { const t = await this.apiGet('/api/call-test/latest-time'); return this.inputToDate(this.toInputValue(t.end)); }
            catch (e) { return new Date(); }
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
        },

        // ── 실시간 (이상치 동기화만) ──
        // 전체 편집은 "사용자가 고정한 시간창" 위에서 편집하는 스냅샷 페이지다.
        // DatabaseRebuilt/FlowHistoryCleared 로 loadAll() 을 자동 호출하면 편집이 통째로 풀리므로
        // 자동 재로드는 하지 않고, 갱신은 새로고침 버튼/시간범위 변경/일괄 적용에만 맡긴다.
        connectSignalR() {
            if (!window.signalR) return;
            const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/monitoring').withAutomaticReconnect([0, 0, 1000, 3000, 5000, 10000]).build();
            conn.on('ExclusionsChanged', () => { this.loadExclusions(); });   // 서버 공유 이상치 동기화(가벼움)
            conn.onreconnected(() => { this.rt.connected = true; });
            conn.onreconnecting(() => { this.rt.connected = false; });
            conn.onclose(() => { this.rt.connected = false; });
            conn.start().then(() => { this.rt.connected = true; }).catch(() => { this.rt.connected = false; });
            this._conn = conn;
        }
    };
}
