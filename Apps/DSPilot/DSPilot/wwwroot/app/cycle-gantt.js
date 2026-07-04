/*
 * cycle-gantt.js — 사이클 분석 간트(신호 SVG) + 사이클 파생 순수 함수 모듈.
 * ------------------------------------------------------------------------------
 * 구 flow.html(flowApp) 의 단일 Flow 사이클 분석 렌더링/파생 로직을 "그대로" 추출한 것.
 * 전체 편집(flow-cycle.html ?system=, bulkCycleApp)이 N개 Flow 를 한 화면에 그리기 위해 Flow 별 상태
 * 슬라이스(s)를 인자로 받는 순수 함수로 재구성했다. 원본의 this.X 는 모두 s.X 로, this.method() 는
 * 모듈 함수 method(s, ...) 로 치환됐을 뿐 계산 로직은 1:1 동일.
 *
 *   - 의존 없음(Chart.js/Alpine 불필요). DOM 도 만지지 않는다(SVG 문자열만 생성).
 *   - 클래식 스크립트(IIFE) → window.CycleGantt 전역. flow-cycle.html 이 alpine 보다 먼저 로드.
 *
 * 슬라이스(s) 가 들고 있어야 하는 필드(렌더가 읽는 것):
 *   callLanes[], cycleBoundaries(Date[]), tailEdges(Date[]), chartStart(Date), chartEnd(Date),
 *   plotWidth, viewMode('bar'|'line'), headCallId, tailCallId, expandedCalls{}, showMaxGap,
 *   topGaps[], selectedGapIndex, (선택) selectedRange{startMs,endMs}.
 *   _geo 는 buildSvg 가 세팅(드래그/스크롤 좌표 변환용).
 *
 * 좌표/색/마진은 flow-workspace.js(단일 Flow 경로)와 동일. 간트 배경은 항상 흰색(다크에서도) — 의도된 것.
 */
(function () {
    'use strict';

    // ── 레이아웃 상수 (flow.html:1056-1057 동일) ──
    // 모바일(≤480px): MIN_PLOT_WIDTH 640px 는 360px 폰에서 과도한 가로 스크롤을 강제한다.
    //   좁은 화면에서는 플롯 최소 폭을 컨테이너에 맞춰 줄인다(데스크톱은 640 유지).
    var TOP_MARGIN = 50, LANE_HEIGHT = 44, BAR_HEIGHT = 18, RIBBON_H = 48,
        LEFT_PAD = 12, RIGHT_PAD = 40, BOTTOM_PAD = 20, MAX_ZOOM = 24;
    var MIN_PLOT_WIDTH = (typeof window !== 'undefined' && window.matchMedia &&
        window.matchMedia('(max-width: 480px)').matches) ? 300 : 640;
    var API_ROW_HEIGHT = 64;   // 실측/AASX 메트릭 칩이 좁은 사이드바에서 wrap 될 여유(사이드바·SVG 공통)

    // ════════════════════════════════════════════════════════════════════════
    //  포맷 유틸 (flow.html 동일)
    // ════════════════════════════════════════════════════════════════════════
    function f(v) { return String(Math.round(v * 100) / 100); }
    function esc(s) {
        if (s == null || s === '') return '';
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
    }
    function formatMs(ms) {
        if (ms <= 0) return '0초';
        if (ms < 1000) return Math.round(ms) + 'ms';
        var totalSec = ms / 1000.0;
        var h = Math.floor(totalSec / 3600);
        var m = Math.floor((totalSec % 3600) / 60);
        var s = totalSec % 60;
        var parts = [];
        if (h) parts.push(h + '시간');
        if (m) parts.push(m + '분');
        if (h || m) { var rs = Math.round(s); if (rs > 0) parts.push(rs + '초'); }
        else parts.push(s.toFixed(2) + '초');
        return parts.join(' ');
    }
    // 히스토리/요약용 한글 단위 (flow.html fmt 동일)
    function fmt(ms) {
        if (ms <= 0) return '0초';
        if (ms < 1000) return Math.round(ms) + 'ms';
        if (ms < 60000) return (ms / 1000).toFixed(1) + '초';
        if (ms < 3600000) return Math.floor(ms / 60000) + '분 ' + Math.floor(ms % 60000 / 1000) + '초';
        return Math.floor(ms / 3600000) + '시간 ' + Math.floor(ms % 3600000 / 60000) + '분';
    }
    function fmtAasx(ms) { return (ms === null || ms === undefined) ? '—' : formatMs(ms); }
    function hms(d) {
        var p = function (x) { return String(x).padStart(2, '0'); };
        return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds()) + '.' + String(d.getMilliseconds()).padStart(3, '0');
    }
    function hms2(d) {
        var p = function (x) { return String(x).padStart(2, '0'); };
        return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Call lane 확장 레이아웃 (사이드바 ↔ SVG 공통) — flow.html:1999-2016
    // ════════════════════════════════════════════════════════════════════════
    function hasApiCalls(lane) { return !!(lane && lane.apiCalls && lane.apiCalls.length); }

    function laneLayout(s) {
        var rows = [];
        var y = 0;
        for (var li = 0; li < s.callLanes.length; li++) {
            var lane = s.callLanes[li];
            rows.push({ kind: 'call', key: 'c:' + lane.callId, lane: lane, y: y, h: LANE_HEIGHT });
            y += LANE_HEIGHT;
            if (s.expandedCalls && s.expandedCalls[lane.callId] && hasApiCalls(lane)) {
                var m = apiMeasured(lane);
                lane.apiCalls.forEach(function (ac, idx) {
                    rows.push({ kind: 'api', key: 'a:' + lane.callId + ':' + (ac.apiCallId || idx), lane: lane, ac: ac, y: y, h: API_ROW_HEIGHT, m: m });
                    y += API_ROW_HEIGHT;
                });
            }
        }
        return { rows: rows, totalH: y };
    }
    function laneRows(s) { return laneLayout(s).rows; }
    function laneRowClass(s, lane) {
        if (s.headCallId === lane.callId) return 'ct-lane-row is-head';
        if (s.tailCallId === lane.callId) return 'ct-lane-row is-tail';
        return 'ct-lane-row';
    }
    function rowClass(s, row) { return row.kind === 'call' ? laneRowClass(s, row.lane) : 'ct-api-row'; }

    // ════════════════════════════════════════════════════════════════════════
    //  실측 duration 페어링 + AASX 변경 빌드 — flow.html:2021-2062
    // ════════════════════════════════════════════════════════════════════════
    function apiSpans(lane) {
        var outs = (lane.outIntervals || []).map(function (iv) { return new Date(iv.start).getTime(); }).sort(function (a, b) { return a - b; });
        var ins = (lane.inIntervals || []).map(function (iv) { return new Date(iv.start).getTime(); }).sort(function (a, b) { return a - b; });
        if (!outs.length || !ins.length) return [];
        var spans = [];
        var j = 0;
        for (var i = 0; i < outs.length; i++) {
            var o = outs[i];
            var nextO = (i + 1 < outs.length) ? outs[i + 1] : Infinity;
            while (j < ins.length && ins[j] < o) j++;
            if (j < ins.length && ins[j] < nextO) { spans.push(ins[j] - o); j++; }
        }
        return spans;
    }
    function apiMeasured(lane) {
        var spans = apiSpans(lane);
        if (!spans.length) return { count: 0, min: null, max: null, mean: null };
        var mn = Infinity, mx = -Infinity, sum = 0;
        for (var i = 0; i < spans.length; i++) { var x = spans[i]; if (x < mn) mn = x; if (x > mx) mx = x; sum += x; }
        return { count: spans.length, min: mn, max: mx, mean: sum / spans.length };
    }
    function buildDurationChange(lane, ac) {
        var m = apiMeasured(lane);
        if (m.count === 0 || !ac || !ac.targetWorkId) return null;
        return { workId: ac.targetWorkId, durationMs: Math.round(m.mean), minMs: Math.round(m.min), maxMs: Math.round(m.max) };
    }
    function canApplyApi(lane, ac) { return !!buildDurationChange(lane, ac); }
    function collectAllDurationChanges(s) {
        var out = [];
        for (var li = 0; li < s.callLanes.length; li++) {
            var lane = s.callLanes[li];
            var acs = lane.apiCalls || [];
            for (var ai = 0; ai < acs.length; ai++) {
                var ch = buildDurationChange(lane, acs[ai]);
                if (ch) out.push(ch);
            }
        }
        return out;   // 같은 Work 중복은 백엔드가 distinct 처리
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Gap(병목) 계산 — flow.html:1738-1757
    // ════════════════════════════════════════════════════════════════════════
    function computeAllGaps(s) {
        var gaps = [];
        for (var li = 0; li < s.callLanes.length; li++) {
            var lane = s.callLanes[li];
            var ivs = (lane.intervals || []).map(function (iv) { return { s: new Date(iv.start).getTime(), e: new Date(iv.end).getTime() }; }).sort(function (a, b) { return a.s - b.s; });
            for (var i = 0; i < ivs.length - 1; i++) {
                var gs = ivs[i].e, ge = ivs[i + 1].s;
                if (ge > gs) gaps.push({ callId: lane.callId, callName: lane.callName, startMs: gs, endMs: ge, durMs: ge - gs });
            }
        }
        return gaps;
    }
    function topGapsOf(s) {
        return computeAllGaps(s).sort(function (a, b) { return b.durMs - a.durMs; }).slice(0, 5);
    }
    function activeGap(s) {
        if (!s.showMaxGap || !s.topGaps || !s.topGaps.length) return null;
        var i = (s.selectedGapIndex >= 0 && s.selectedGapIndex < s.topGaps.length) ? s.selectedGapIndex : 0;
        return s.topGaps[i];
    }

    // ════════════════════════════════════════════════════════════════════════
    //  정렬 — Head 맨 위, Tail 맨 아래로 고정. 그 사이 Call 은 첫 신호(InTag/OutTag)
    //  시각 순으로 배열해 신호 흐름(head→tail)이 위→아래로 흐르게 한다.
    //  정렬 선택기 제거(2026-07-01) — head/tail 지정이 유일한 순서 기준.
    // ════════════════════════════════════════════════════════════════════════
    function sortLanes(rawLanes, headCallId, tailCallId) {
        var lanes = (rawLanes || []).slice();
        var firstStart = function (l) {
            var m = Infinity;
            (l.intervals || []).forEach(function (iv) { var st = new Date(iv.start).getTime(); if (st < m) m = st; });
            return m;
        };
        var li = function (l) { return (typeof l.laneIndex === 'number' ? l.laneIndex : 0); };
        // 신호 순서(첫 신호 시각 → laneIndex) 기본 배열.
        lanes.sort(function (a, b) { return (firstStart(a) - firstStart(b)) || (li(a) - li(b)); });
        // Head 는 맨 위, Tail 은 맨 아래로 끌어낸다(head==tail 이면 맨 위 1행).
        var head = [], mid = [], tail = [];
        lanes.forEach(function (l) {
            if (headCallId && l.callId === headCallId) head.push(l);
            else if (tailCallId && l.callId === tailCallId) tail.push(l);
            else mid.push(l);
        });
        return head.concat(mid, tail);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  사이클 목록 파생 — flow.html:1779-1815
    // ════════════════════════════════════════════════════════════════════════
    function cycleRows(s) {
        if (!s.chartStart || s.cycleBoundaries.length === 0) return [];
        var ce = s.chartEnd ? s.chartEnd.getTime() : 0;
        var bnd = s.cycleBoundaries.map(function (d) { return d.getTime(); });
        var tails = s.tailEdges.map(function (d) { return d.getTime(); }).slice().sort(function (a, b) { return a - b; });
        var spans = [];
        for (var i = 0; i < bnd.length - 1; i++) spans.push({ start: bnd[i], end: bnd[i + 1], number: i + 1, isOpen: false });
        if (bnd.length > 0 && ce && bnd[bnd.length - 1] < ce) spans.push({ start: bnd[bnd.length - 1], end: ce, number: bnd.length, isOpen: true });
        var tIdx = 0;
        return spans.map(function (span) {
            while (tIdx < tails.length && tails[tIdx] <= span.start) tIdx++;
            var tailIn = null;
            if (tIdx < tails.length && tails[tIdx] < span.end) tailIn = tails[tIdx];
            var ctMs = span.end - span.start;
            var atMs = tailIn !== null ? tailIn - span.start : null;
            var wtMs = atMs !== null ? Math.max(0, ctMs - atMs) : null;
            var ratio = (atMs !== null && ctMs > 0) ? +(atMs / ctMs * 100).toFixed(1) : null;
            return { number: span.number, isOpen: span.isOpen, startMs: span.start, ctMs: ctMs, atMs: atMs, wtMs: wtMs, ratio: ratio };
        });
    }
    function isIncompleteCycle(s, c) { return !c.isOpen && c.atMs === null && s.tailCallId !== null; }
    // exRangeMs = {min,max} (ms) 또는 null. excludeIncomplete = bool.
    function visibleCycleRows(s, exRangeMs, excludeIncomplete) {
        return cycleRows(s).filter(function (c) {
            if (c.isOpen) return true;
            if (excludeIncomplete && isIncompleteCycle(s, c)) return false;
            if (exRangeMs) {
                var ct = c.ctMs == null ? 0 : c.ctMs;
                if (exRangeMs.min != null && ct < exRangeMs.min) return false;
                if (exRangeMs.max != null && ct > exRangeMs.max) return false;
            }
            return true;
        });
    }
    function excludedCycleCount(s, exRangeMs) {
        if (!exRangeMs) return 0;
        var n = 0;
        cycleRows(s).forEach(function (c) {
            if (c.isOpen) return;
            var ct = c.ctMs == null ? 0 : c.ctMs;
            if ((exRangeMs.min != null && ct < exRangeMs.min) || (exRangeMs.max != null && ct > exRangeMs.max)) n++;
        });
        return n;
    }
    function incompleteCycleCount(s) {
        return cycleRows(s).filter(function (c) { return isIncompleteCycle(s, c); }).length;
    }
    function ratioCls(ratio) {
        if (ratio === null) return '';
        if (ratio >= 80) return 'ct-ratio-good';
        if (ratio >= 50) return 'ct-ratio-mid';
        return 'ct-ratio-low';
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SVG 렌더 — flow.html:2090-2349 (this.→s. / this.method→module)
    // ════════════════════════════════════════════════════════════════════════
    function buildSvg(s) {
        if (s.callLanes.length === 0) return '';
        if (!s.chartStart || !s.chartEnd) return '';   // 로드 완료 전 호출 방어
        var cs = s.chartStart.getTime(), ce = s.chartEnd.getTime();
        var totalMs = Math.max(1.0, ce - cs);
        var PW = s.plotWidth;
        var chartW = LEFT_PAD + PW + RIGHT_PAD;
        var ribbonH = s.cycleBoundaries.length ? RIBBON_H : 0;
        var laneAreaTop = TOP_MARGIN + ribbonH;
        var layout = laneLayout(s);
        var laneAreaBottom = laneAreaTop + layout.totalH;
        var chartH = laneAreaBottom + BOTTOM_PAD;
        var xScale = PW / totalMs;
        s._geo = { cs: cs, xScale: xScale };
        var msOf = function (d) { return d.getTime() - cs; };

        var sb = '';
        sb += '<svg class="ct-gantt" width="' + chartW + '" height="' + chartH + '" xmlns="http://www.w3.org/2000/svg">';
        sb += '<rect width="100%" height="100%" fill="#ffffff"/>';

        if (ribbonH > 0) sb += appendCycleRibbon(s, xScale, TOP_MARGIN, ribbonH, cs, ce);
        sb += appendCycleBands(s, xScale, laneAreaTop, laneAreaBottom, cs, ce);
        sb += appendTimeAxis(s, totalMs, xScale, cs, laneAreaTop, laneAreaBottom);

        var plotRightX = LEFT_PAD + PW;
        for (var ri = 0; ri < layout.rows.length; ri++) {
            var row = layout.rows[ri];
            var lane = row.lane;
            var rowY = laneAreaTop + row.y;

            if (row.kind === 'api') {
                sb += '<rect x="0" y="' + f(rowY) + '" width="' + chartW + '" height="' + API_ROW_HEIGHT + '" fill="#f5f7fa" opacity="0.7"/>';
                sb += '<line x1="0" y1="' + f(rowY + API_ROW_HEIGHT) + '" x2="' + chartW + '" y2="' + f(rowY + API_ROW_HEIGHT) + '" stroke="#e3e6ea" stroke-width="1"/>';
                sb += '<rect x="0" y="' + f(rowY) + '" width="3" height="' + API_ROW_HEIGHT + '" fill="#90a4ae" opacity="0.5"/>';
                sb += appendSignalTrace(row.ac.outIntervals || lane.outIntervals, '#fb8c00', rowY + 22, rowY + 9, cs, xScale, plotRightX, row.ac.name, row.ac.outTag, 'OUT 명령');
                sb += appendSignalTrace(row.ac.inIntervals || lane.inIntervals, '#1e88e5', rowY + API_ROW_HEIGHT - 8, rowY + 26, cs, xScale, plotRightX, row.ac.name, row.ac.inTag, 'IN 응답');
                continue;
            }

            var laneY = rowY;
            var laneCY = laneY + LANE_HEIGHT / 2.0;
            var isHead = s.headCallId === lane.callId;
            var isTail = s.tailCallId === lane.callId;

            if (isHead || isTail) {
                var stripeFill = isHead ? '#c8e6c9' : '#e1bee7';
                sb += '<rect x="0" y="' + laneY + '" width="' + chartW + '" height="' + LANE_HEIGHT + '" fill="' + stripeFill + '" opacity="0.35"/>';
            }
            sb += '<line x1="0" y1="' + (laneY + LANE_HEIGHT) + '" x2="' + chartW + '" y2="' + (laneY + LANE_HEIGHT) + '" stroke="#e3e6ea" stroke-width="1"/>';

            if (s.viewMode === 'bar') {
                // OUTTAG/INTAG 기준 2색 분할(프로메이커 간트와 동일 색언어) — head/tail 역할색 대신
                // 합집합 막대를 OUT(명령=주황) 베이스로 깔고 IN(응답=파랑) 구간을 덮는다.
                // 보이는 주황 = OUT-only(union\IN) = 명령 후 응답 전 구간, 파랑 = IN(응답) 구간.
                var barTopB = laneCY - BAR_HEIGHT / 2.0;
                var ivArr = (lane.intervals || []);
                for (var bi = 0; bi < ivArr.length; bi++) {
                    var ivb = ivArr[bi];
                    var sB = new Date(ivb.start), eB = new Date(ivb.end);
                    var xB = LEFT_PAD + msOf(sB) * xScale;
                    var wB = Math.max(2, (eB.getTime() - sB.getTime()) * xScale);
                    var durMsB = eB.getTime() - sB.getTime();
                    var tipB = lane.callName + ' · OUT 명령' + (lane.outTag ? ' (' + lane.outTag + ')' : '') + '  ' + hms(sB) + ' ~ ' + hms(eB) + '  (' + formatMs(durMsB) + ')';
                    sb += '<g><title>' + esc(tipB) + '</title>';
                    sb += '<rect x="' + f(xB) + '" y="' + f(barTopB) + '" width="' + f(wB) + '" height="' + BAR_HEIGHT + '" rx="2" fill="#fb8c00" stroke="#e65100" stroke-width="0.5"/>';
                    sb += '</g>';
                }
                var inArr = (lane.inIntervals || []);
                for (var iii = 0; iii < inArr.length; iii++) {
                    var ivi = inArr[iii];
                    var sI = new Date(ivi.start), eI = new Date(ivi.end);
                    var xI = LEFT_PAD + msOf(sI) * xScale;
                    var wI = Math.max(2, (eI.getTime() - sI.getTime()) * xScale);
                    var durMsI = eI.getTime() - sI.getTime();
                    var tipI = lane.callName + ' · IN 응답' + (lane.inTag ? ' (' + lane.inTag + ')' : '') + '  ' + hms(sI) + ' ~ ' + hms(eI) + '  (' + formatMs(durMsI) + ')';
                    sb += '<g><title>' + esc(tipI) + '</title>';
                    sb += '<rect x="' + f(xI) + '" y="' + f(barTopB) + '" width="' + f(wI) + '" height="' + BAR_HEIGHT + '" rx="2" fill="#1e88e5"/>';
                    sb += '</g>';
                }
            } else {
                var unionFill = isHead ? '#4caf50' : isTail ? '#ab47bc' : '#5b9bd5';
                var ivArrL = (lane.intervals || []);
                for (var ui = 0; ui < ivArrL.length; ui++) {
                    var ivu = ivArrL[ui];
                    var sU = new Date(ivu.start), eU = new Date(ivu.end);
                    var xU = LEFT_PAD + msOf(sU) * xScale;
                    var wU = Math.max(2, (eU.getTime() - sU.getTime()) * xScale);
                    sb += '<rect x="' + f(xU) + '" y="' + f(laneY + 6) + '" width="' + f(wU) + '" height="' + (LANE_HEIGHT - 12) + '" rx="2" fill="' + unionFill + '" opacity="0.10"/>';
                }
                sb += appendSignalTrace(lane.outIntervals, '#fb8c00', laneY + 20, laneY + 7, cs, xScale, plotRightX, lane.callName, lane.outTag, 'OUT 명령');
                sb += appendSignalTrace(lane.inIntervals, '#1e88e5', laneY + 37, laneY + 24, cs, xScale, plotRightX, lane.callName, lane.inTag, 'IN 응답');
            }
        }

        if (s.selectedRange) {
            var a = Math.max(cs, s.selectedRange.startMs), b = Math.min(ce, s.selectedRange.endMs);
            if (b > a) {
                var rx = LEFT_PAD + (a - cs) * xScale;
                var rw = Math.max(1, (b - a) * xScale);
                sb += '<rect x="' + f(rx) + '" y="' + laneAreaTop + '" width="' + f(rw) + '" height="' + f(laneAreaBottom - laneAreaTop) + '" fill="rgba(126,87,194,0.16)" pointer-events="none"/>';
                sb += '<line x1="' + f(rx) + '" y1="' + laneAreaTop + '" x2="' + f(rx) + '" y2="' + laneAreaBottom + '" stroke="#7e57c2" stroke-width="1.2" stroke-dasharray="4 3" pointer-events="none"/>';
                sb += '<line x1="' + f(rx + rw) + '" y1="' + laneAreaTop + '" x2="' + f(rx + rw) + '" y2="' + laneAreaBottom + '" stroke="#7e57c2" stroke-width="1.2" stroke-dasharray="4 3" pointer-events="none"/>';
            }
        }
        var gap = activeGap(s);
        if (gap) {
            var gapRow = layout.rows.find(function (r) { return r.kind === 'call' && r.lane.callId === gap.callId; });
            if (gapRow) {
                var gy = laneAreaTop + gapRow.y;
                var gx = LEFT_PAD + (gap.startMs - cs) * xScale;
                var gw = Math.max(2, (gap.endMs - gap.startMs) * xScale);
                sb += '<rect x="' + f(gx) + '" y="' + f(gy) + '" width="' + f(gw) + '" height="' + LANE_HEIGHT + '" rx="3" fill="rgba(245,166,35,0.28)" stroke="#e5494f" stroke-width="2" pointer-events="none"/>';
                if (gw > 40) {
                    var label = '⚠ ' + formatMs(gap.durMs);
                    var fs = 11, tw = label.length * (fs * 0.62), padX = 6, padY = 3;
                    var bgW = tw + padX * 2, bgH = fs + padY * 2;
                    var cx = gx + gw / 2, cy = gy + LANE_HEIGHT / 2;
                    sb += '<rect x="' + f(cx - bgW / 2) + '" y="' + f(cy - bgH / 2) + '" width="' + f(bgW) + '" height="' + f(bgH) + '" rx="3" fill="#ffffff" stroke="#e5494f" stroke-width="1.2" pointer-events="none"/>';
                    sb += '<text x="' + f(cx) + '" y="' + f(cy) + '" text-anchor="middle" dominant-baseline="central" font-size="' + fs + '" font-weight="700" fill="#e5494f" pointer-events="none">' + esc(label) + '</text>';
                }
            }
        }

        sb += '</svg>';
        return sb;
    }

    function appendSignalTrace(intervals, color, yLow, yHigh, cs, xScale, plotRightX, callName, tagName, kindLabel) {
        var sb = '';
        sb += '<line x1="' + LEFT_PAD + '" y1="' + f(yLow) + '" x2="' + f(plotRightX) + '" y2="' + f(yLow) + '" stroke="' + color + '" stroke-width="0.75" opacity="0.28"/>';
        if (!intervals || intervals.length === 0) return sb;
        var msOf = function (d) { return d.getTime() - cs; };
        var segs = intervals.map(function (iv) { return { s: new Date(iv.start), e: new Date(iv.end) }; }).sort(function (a, b) { return a.s - b.s; });
        var pts = f(LEFT_PAD) + ',' + f(yLow);
        for (var i = 0; i < segs.length; i++) {
            var seg = segs[i];
            var xs = LEFT_PAD + msOf(seg.s) * xScale;
            var xe = Math.max(LEFT_PAD + msOf(seg.e) * xScale, xs + 1.5);
            var durMs = seg.e.getTime() - seg.s.getTime();
            var tip = callName + ' · ' + kindLabel + (tagName ? ' (' + tagName + ')' : '') + '  ' + hms(seg.s) + ' ~ ' + hms(seg.e) + '  (' + formatMs(durMs) + ')';
            sb += '<g><title>' + esc(tip) + '</title><rect x="' + f(xs) + '" y="' + f(yHigh) + '" width="' + f(xe - xs) + '" height="' + f(yLow - yHigh) + '" fill="' + color + '" opacity="0.20"/></g>';
            pts += ' ' + f(xs) + ',' + f(yLow) + ' ' + f(xs) + ',' + f(yHigh) + ' ' + f(xe) + ',' + f(yHigh) + ' ' + f(xe) + ',' + f(yLow);
        }
        pts += ' ' + f(plotRightX) + ',' + f(yLow);
        sb += '<polyline points="' + pts + '" fill="none" stroke="' + color + '" stroke-width="1.4"/>';
        return sb;
    }

    function appendCycleRibbon(s, xScale, ribbonTop, ribbonH, cs, ce) {
        if (s.cycleBoundaries.length === 0) return '';
        var sb = '';
        var bnd = s.cycleBoundaries.map(function (d) { return d.getTime(); });
        var msOf = function (t) { return t - cs; };
        var plotRight = LEFT_PAD + s.plotWidth;

        var spans = [];
        for (var i = 0; i < bnd.length - 1; i++) spans.push({ start: bnd[i], end: bnd[i + 1], number: i + 1, isOpen: false });
        if (bnd[bnd.length - 1] < ce) spans.push({ start: bnd[bnd.length - 1], end: ce, number: bnd.length, isOpen: true });

        var tails = s.tailEdges.map(function (d) { return d.getTime(); });
        var tailIdx = 0;

        var barY = ribbonTop + 16;
        var barH = Math.max(14, ribbonH - 20);
        var barCY = barY + barH / 2.0;

        sb += '<rect x="' + f(LEFT_PAD) + '" y="' + ribbonTop + '" width="' + f(plotRight - LEFT_PAD) + '" height="' + ribbonH + '" fill="#fafbfc"/>';
        sb += '<line x1="0" y1="' + f(ribbonTop + ribbonH) + '" x2="' + f(plotRight) + '" y2="' + f(ribbonTop + ribbonH) + '" stroke="#cfd8dc" stroke-width="1"/>';

        for (var si = 0; si < spans.length; si++) {
            var span = spans[si];
            var sx = LEFT_PAD + msOf(span.start) * xScale;
            var ex = LEFT_PAD + msOf(span.end) * xScale;
            var bandW = Math.max(1, ex - sx);
            var isEven = span.number % 2 === 0;
            var dim = span.isOpen ? 0.55 : 1;

            while (tailIdx < tails.length && tails[tailIdx] <= span.start) tailIdx++;
            var tailIn = null;
            if (tailIdx < tails.length && tails[tailIdx] < span.end) tailIn = tails[tailIdx];
            var tailX = tailIn !== null ? LEFT_PAD + msOf(tailIn) * xScale : null;

            var ctMs = span.end - span.start;
            var atMs = tailIn !== null ? tailIn - span.start : null;
            var idleMs = atMs !== null ? ctMs - atMs : null;
            var ratio = (atMs !== null && ctMs > 0) ? Math.round(atMs / ctMs * 100) : null;

            var tip = tailIn !== null
                ? '가동 #' + span.number + (span.isOpen ? ' (진행중)' : '') + ' · 동작시간 ' + formatMs(atMs) + ' · 대기시간 ' + formatMs(idleMs) + ' / 가동시간 ' + formatMs(ctMs) + ' · 동작률 ' + ratio + '%'
                : '가동 #' + span.number + (span.isOpen ? ' (진행중)' : '') + ' · 가동시간 ' + formatMs(ctMs);
            var g = '<g><title>' + esc(tip) + '</title>';

            if (tailX !== null) {
                var aw = Math.max(0, tailX - sx);
                var iw = Math.max(0, ex - tailX);
                g += '<rect x="' + f(sx) + '" y="' + barY + '" width="' + f(aw) + '" height="' + barH + '" fill="#ffa726" opacity="' + (0.95 * dim) + '"/>';
                g += '<rect x="' + f(tailX) + '" y="' + barY + '" width="' + f(iw) + '" height="' + barH + '" fill="#AEB9C6" opacity="' + (0.9 * dim) + '"/>';
                if (aw > 54) g += '<text x="' + f(sx + aw / 2.0) + '" y="' + f(barCY) + '" text-anchor="middle" dominant-baseline="central" font-size="9.5" font-weight="700" fill="#5a3200" font-family="Inter,ui-monospace,Cascadia Code,Consolas,monospace">' + esc(formatMs(atMs)) + '</text>';
                if (iw > 54) g += '<text x="' + f(tailX + iw / 2.0) + '" y="' + f(barCY) + '" text-anchor="middle" dominant-baseline="central" font-size="9.5" fill="#37474f" font-family="Inter,ui-monospace,Cascadia Code,Consolas,monospace">' + esc(formatMs(idleMs)) + '</text>';
            } else {
                var bfill = isEven ? '#9fa8da' : '#ce93d8';
                g += '<rect x="' + f(sx) + '" y="' + barY + '" width="' + f(bandW) + '" height="' + barH + '" fill="' + bfill + '" opacity="' + (0.85 * dim) + '"/>';
                if (bandW > 54) g += '<text x="' + f(sx + bandW / 2.0) + '" y="' + f(barCY) + '" text-anchor="middle" dominant-baseline="central" font-size="9.5" fill="#37474f" font-family="Inter,ui-monospace,Cascadia Code,Consolas,monospace">가동시간 ' + esc(formatMs(ctMs)) + '</text>';
            }
            g += '<rect x="' + f(sx) + '" y="' + barY + '" width="' + f(bandW) + '" height="' + barH + '" fill="none" stroke="#90a4ae" stroke-width="0.75"/>';
            if (span.isOpen) g += '<line x1="' + f(ex) + '" y1="' + barY + '" x2="' + f(ex) + '" y2="' + f(barY + barH) + '" stroke="#90a4ae" stroke-width="1" stroke-dasharray="3 2"/>';

            if (bandW > 22) {
                var num = span.isOpen ? '#' + span.number + ' ↻' : '#' + span.number;
                g += '<text x="' + f(sx + 4) + '" y="' + f(ribbonTop + 12) + '" font-size="11" font-weight="800" fill="#263238">' + num + '</text>';
            }
            if (ratio !== null && bandW > 86) {
                var rc = ratio >= 80 ? '#2e7d32' : ratio >= 50 ? '#e65100' : '#c62828';
                g += '<text x="' + f(ex - 4) + '" y="' + f(ribbonTop + 12) + '" text-anchor="end" font-size="10" font-weight="700" fill="' + rc + '">동작 ' + ratio + '%</text>';
            }
            g += '</g>';
            sb += g;
        }
        return sb;
    }

    function appendCycleBands(s, xScale, laneAreaTop, laneAreaBottom, cs, ce) {
        if (s.cycleBoundaries.length === 0) return '';
        var sb = '';
        var bnd = s.cycleBoundaries.map(function (d) { return d.getTime(); });
        var msOf = function (t) { return t - cs; };

        var spans = [];
        for (var i = 0; i < bnd.length - 1; i++) spans.push({ start: bnd[i], end: bnd[i + 1], number: i + 1, isOpen: false });
        if (bnd[bnd.length - 1] < ce) spans.push({ start: bnd[bnd.length - 1], end: ce, number: bnd.length, isOpen: true });

        var tails = s.tailEdges.map(function (d) { return d.getTime(); });
        var tailIdx = 0;
        var laneAreaH = laneAreaBottom - laneAreaTop;

        for (var si = 0; si < spans.length; si++) {
            var span = spans[si];
            var sx = LEFT_PAD + msOf(span.start) * xScale;
            var ex = LEFT_PAD + msOf(span.end) * xScale;
            var bandW = Math.max(1, ex - sx);
            var isEven = span.number % 2 === 0;
            var dim = span.isOpen ? 0.6 : 1;

            while (tailIdx < tails.length && tails[tailIdx] <= span.start) tailIdx++;
            var tailIn = null;
            if (tailIdx < tails.length && tails[tailIdx] < span.end) tailIn = tails[tailIdx];
            var tailX = tailIn !== null ? LEFT_PAD + msOf(tailIn) * xScale : null;

            if (tailX !== null) {
                sb += '<rect x="' + f(sx) + '" y="' + laneAreaTop + '" width="' + f(tailX - sx) + '" height="' + laneAreaH + '" fill="#fb8c00" opacity="' + (0.10 * dim) + '"/>';
                sb += '<rect x="' + f(tailX) + '" y="' + laneAreaTop + '" width="' + f(ex - tailX) + '" height="' + laneAreaH + '" fill="#AEB9C6" opacity="' + (0.08 * dim) + '"/>';
            } else {
                var bandFill = isEven ? '#5c6bc0' : '#8e24aa';
                sb += '<rect x="' + f(sx) + '" y="' + laneAreaTop + '" width="' + f(bandW) + '" height="' + laneAreaH + '" fill="' + bandFill + '" opacity="' + (0.07 * dim) + '"/>';
            }

            sb += '<line x1="' + f(sx) + '" y1="' + TOP_MARGIN + '" x2="' + f(sx) + '" y2="' + laneAreaBottom + '" stroke="#455a64" stroke-width="1.8" opacity="0.9"/>';
            if (tailX !== null) {
                sb += '<line x1="' + f(tailX) + '" y1="' + laneAreaTop + '" x2="' + f(tailX) + '" y2="' + laneAreaBottom + '" stroke="#ab47bc" stroke-width="1.2" stroke-dasharray="3 2" opacity="0.85"/>';
            }
        }

        var lastEdge = bnd[bnd.length - 1];
        if (lastEdge >= cs && lastEdge <= ce) {
            var lx = LEFT_PAD + msOf(lastEdge) * xScale;
            sb += '<line x1="' + f(lx) + '" y1="' + TOP_MARGIN + '" x2="' + f(lx) + '" y2="' + laneAreaBottom + '" stroke="#455a64" stroke-width="1.8" opacity="0.9"/>';
        }
        return sb;
    }

    function appendTimeAxis(s, totalMs, xScale, cs, laneAreaTop, laneAreaBottom) {
        var sb = '';
        sb += '<line x1="' + LEFT_PAD + '" y1="' + (TOP_MARGIN - 6) + '" x2="' + (LEFT_PAD + s.plotWidth) + '" y2="' + (TOP_MARGIN - 6) + '" stroke="#888" stroke-width="1"/>';
        var tickStep = chooseTickStepMs(totalMs);
        for (var t = 0; t <= totalMs + 1e-6; t += tickStep) {
            var x = LEFT_PAD + t * xScale;
            sb += '<line x1="' + f(x) + '" y1="' + f(laneAreaTop) + '" x2="' + f(x) + '" y2="' + f(laneAreaBottom) + '" stroke="#e9ecef" stroke-width="1" stroke-dasharray="2 4"/>';
            var labelTime = new Date(cs + t);
            sb += '<text x="' + f(x) + '" y="' + (TOP_MARGIN - 12) + '" text-anchor="middle" font-size="10" fill="#666" font-family="Inter,ui-monospace,Cascadia Code,Consolas,monospace">' + esc(hms2(labelTime)) + '</text>';
        }
        return sb;
    }

    function chooseTickStepMs(totalMs) {
        var targetCount = 10;
        var rough = totalMs / targetCount;
        var mag = Math.pow(10, Math.floor(Math.log10(Math.max(1, rough))));
        var norm = rough / mag;
        var mult;
        if (norm <= 1) mult = 1; else if (norm <= 2) mult = 2; else if (norm <= 5) mult = 5; else mult = 10;
        return Math.max(1, mult * mag);
    }

    // ── 공개 API ──
    window.CycleGantt = {
        // 상수
        TOP_MARGIN: TOP_MARGIN, LANE_HEIGHT: LANE_HEIGHT, BAR_HEIGHT: BAR_HEIGHT, RIBBON_H: RIBBON_H,
        LEFT_PAD: LEFT_PAD, RIGHT_PAD: RIGHT_PAD, BOTTOM_PAD: BOTTOM_PAD,
        MIN_PLOT_WIDTH: MIN_PLOT_WIDTH, MAX_ZOOM: MAX_ZOOM, API_ROW_HEIGHT: API_ROW_HEIGHT,
        // 포맷
        f: f, esc: esc, formatMs: formatMs, fmt: fmt, fmtAasx: fmtAasx, hms: hms, hms2: hms2,
        // 레이아웃/파생
        hasApiCalls: hasApiCalls, laneLayout: laneLayout, laneRows: laneRows,
        laneRowClass: laneRowClass, rowClass: rowClass,
        apiSpans: apiSpans, apiMeasured: apiMeasured, buildDurationChange: buildDurationChange,
        canApplyApi: canApplyApi, collectAllDurationChanges: collectAllDurationChanges,
        computeAllGaps: computeAllGaps, topGapsOf: topGapsOf, activeGap: activeGap,
        sortLanes: sortLanes,
        cycleRows: cycleRows, isIncompleteCycle: isIncompleteCycle, visibleCycleRows: visibleCycleRows,
        excludedCycleCount: excludedCycleCount, incompleteCycleCount: incompleteCycleCount, ratioCls: ratioCls,
        // SVG
        buildSvg: buildSvg
    };
})();
