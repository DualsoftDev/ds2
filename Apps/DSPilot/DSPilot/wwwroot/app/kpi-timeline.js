// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
//
// 한줄 연표(KPI timeline) — 설비효율(OEE)·생산효율(TEEP)·가동시간 분석 세 페이지 공용 컴포넌트.
// doc/30 §8.1.
//
// 구간을 정하면 그 안의 CT 가 몇 개이고 각각 어떤 상태였는지를 한 줄로 보여 준다.
// 사이클 행이 시간축을 빈틈없이 타일링하므로 계산이 아니라 조회다 — 서버가 인접한 같은 상태를
// 이미 묶어 보내므로(세그먼트) 긴 구간도 DOM 수십 개로 그려진다.
//
// 쓰는 법:
//   <div id="tl"></div>
//   const tl = dspKpiTimeline.create(document.getElementById('tl'), { onSelect: seg => ... });
//   await tl.load({ from, to, flow, branch });   // → 서버 응답 객체를 그대로 반환(페이지가 KPI 로 재사용)
//
// 프레임워크 비의존(순수 DOM) — Alpine 페이지 안에서도 x-init 에서 create/load 만 부르면 된다.
(function () {
    'use strict';

    const STATE = {
        Run: { label: '가동', cls: 'kt-run' },
        Down: { label: '비가동', cls: 'kt-down' },
        NonProd: { label: '비생산', cls: 'kt-nonprod' },
        Excluded: { label: '제외', cls: 'kt-excluded' },
    };

    const REASON = {
        Cut: '구간에 잘림',
        Unknown: '이전 사이클 미상',
        InProgress: '진행 중',
        NoBaseline: '기준 표본 부족',
        Unclassified: '분기 미분류',
        Overflow: '경계 초과(모델링 확인)',
        Gap: '기록 공백',            // fillHoles 가 만드는 사이클 사이 빈 구간 — 빠져 있어 툴팁에 'Gap' 원문이 노출됐다(2026-09-18)
        None: '',
    };

    // ── 구간 비례 합산(여러 설비를 한 줄로 볼 때) ──────────────────────────────
    /// 가로 한 칸의 목표 픽셀 폭. 작을수록 시간 해상도가 높고 DOM 이 는다(같은 모양은 어차피 합쳐진다).
    const BUCKET_PX = 3;
    /// 비가동·비생산이 있으면 최소 이 높이(%)는 준다 — 34px 스트립에서 약 3px. 12대 중 1대(8.3%)와
    /// 거의 같은 값이라 비율을 왜곡하지 않으면서 "한 대만 멈춤" 도 눈에 들어온다.
    const MIN_BAND = 9;

    /// 세그먼트에 실린 설비 수. 클라이언트가 만든 합성 구간(flow 없음)은 세지 않는다.
    /// ★분기로 나누지 않는다 — 분기는 한 설비의 사이클을 <b>분류</b>하는 축이라 사이클 하나는 분기 하나에만
    /// 속한다(doc/30 §2.3). 분기를 계열로 세면 분기 2개짜리 설비의 분모가 2배가 되어 높이가 절반이 된다.
    /// 세로 비율의 분모는 "동시에 돌 수 있는 설비 수" 여야 한다.
    function seriesCount(segs) {
        const set = new Set();
        for (const s of segs || []) if (s.flow) set.add(s.flow);
        return set.size;
    }

    // 지속시간 표기는 공용 SSOT(shell.js dspFmt)를 따른다 — 없으면 최소 폴백.
    function dur(ms) {
        if (window.dspFmt && typeof window.dspFmt.dur === 'function') return window.dspFmt.dur(ms);
        if (!(ms > 0)) return '0초';
        const s = Math.round(ms / 1000);
        if (s < 60) return s + '초';
        const m = Math.floor(s / 60);
        if (m < 60) return m + '분 ' + (s % 60) + '초';
        return Math.floor(m / 60) + '시간 ' + (m % 60) + '분';
    }

    function pct(v) { return (Math.max(0, Math.min(100, v))).toFixed(4) + '%'; }

    function hhmm(ms) {
        const d = new Date(ms);
        const p = n => String(n).padStart(2, '0');
        return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    }

    function injectStyleOnce() {
        if (document.getElementById('kpi-timeline-style')) return;
        const css = `
        /* --kt-unknown = 합산 칸에서 판정되지 않은 높이(제외). 상태 색과 경쟁하지 않게 흐린 무채색으로 둔다 —
           스펙상 제외는 본체가 아니라 오버레이다(doc/30 §9.1). 아무것도 안 칠하면 "데이터 없음" 과 구분이 안 된다. */
        .kt-wrap { --kt-run:#1E9BE8; --kt-down:#B22F22; --kt-nonprod:#334E7B; --kt-excluded:#C9CED6;
                   --kt-unknown:rgba(122,134,150,.26);
                   display:flex; flex-direction:column; gap:8px; }
        .dark-theme .kt-wrap, .dark .kt-wrap { --kt-run:#52C4FF; --kt-down:#D14738; --kt-nonprod:#41608F; --kt-excluded:#4A5160;
                   --kt-unknown:rgba(150,162,180,.24); }
        .kt-chips { display:flex; flex-wrap:wrap; gap:6px; align-items:center; font-size:12px; }
        /* 색 토큰 정정(2026-09-21) — 종전엔 --surface-2/--border-color/--text-muted 를 썼는데 이 앱에 그런 토큰이
           없다. 즉 다크 테마에서도 배경이 폴백 #fff(흰색)로 고정되고 글자만 밝아져 칩 전체가 안 읽혔다(실측).
           실제로 존재하는 ds.css 토큰(--color-surface/--color-lines/--color-text-*)으로 바꾸고 글자색도 명시한다. */
        .kt-chip { display:inline-flex; align-items:center; gap:5px; padding:3px 9px; border-radius:999px;
                   border:1px solid var(--color-lines,#d7dbe0); background:var(--color-surface,#fff);
                   color:var(--color-text-primary,#0E1B2A); white-space:nowrap; }
        .kt-chip b { font-weight:600; font-variant-numeric:tabular-nums; }
        .kt-dot { width:9px; height:9px; border-radius:2px; flex:none; }
        .kt-chip.kt-muted { opacity:.72; }
        /* 전부 제외된 구간의 안내 칩(kt-why) — 이 줄이 카드의 유일한 내용이 되므로 흐리게 두면 안 된다.
           kt-muted 의 opacity 를 물려받으면 다크에서 거의 안 읽힌다(2026-09-21 실측). 정상 대비로 되돌리고
           줄바꿈을 허용한다(문장이라 nowrap 이면 좁은 폭에서 잘린다). */
        .kt-chip.kt-why { opacity:1; white-space:normal; line-height:1.45; }
        .kt-axis { position:relative; height:14px; font-size:10px; color:var(--color-text-secondary,#6b7280); }
        .kt-axis span { position:absolute; transform:translateX(-50%); white-space:nowrap; }
        .kt-strip { position:relative; height:34px; border-radius:6px; overflow:hidden;
                    background:var(--color-surface-variant,#eef1f4); border:1px solid var(--color-lines,#d7dbe0); }
        .kt-seg { position:absolute; top:0; bottom:0; cursor:pointer; }
        .kt-seg.kt-run { background:var(--kt-run); }
        .kt-seg.kt-down { background:var(--kt-down);
                          background-image:repeating-linear-gradient(45deg, rgba(255,255,255,.55) 0 1.4px, transparent 1.4px 6px); }
        .kt-seg.kt-nonprod { background:var(--kt-nonprod);
                             background-image:repeating-linear-gradient(45deg, rgba(150,185,235,.5) 0 1.4px, transparent 1.4px 6px); }
        .kt-seg.kt-excluded { background:var(--kt-excluded);
                              background-image:repeating-linear-gradient(90deg, rgba(255,255,255,.7) 0 2px, transparent 2px 5px); }
        /* 구간 비례 합산 칸 — 색은 gradient 로 직접 준다(상태 클래스 없음). 바탕은 비워 두어
           칠하지 않은 높이가 그대로 "모르는 시간"(제외·공백)으로 읽히게 한다. */
        .kt-seg.kt-roll { background:none; cursor:default; }
        .kt-seg:hover { filter:brightness(1.12); outline:1px solid rgba(0,0,0,.25); outline-offset:-1px; }
        .kt-seg.kt-sel { outline:2px solid var(--color-primary,#1E9BE8); outline-offset:-2px; }
        .kt-seg-n { position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
                    font-size:10px; font-weight:600; color:#fff; text-shadow:0 1px 2px rgba(0,0,0,.45);
                    pointer-events:none; overflow:hidden; }
        .kt-links { position:relative; height:7px; margin-top:-1px; }
        .kt-link { position:absolute; top:0; bottom:0; background:repeating-linear-gradient(45deg,
                   rgba(180,60,60,.85) 0 3px, rgba(255,255,255,.25) 3px 6px); border-radius:2px; }
        .kt-empty { padding:14px; text-align:center; color:var(--color-text-secondary,#6b7280); font-size:13px; }
        .kt-tip { position:fixed; z-index:9999; pointer-events:none; max-width:320px; padding:8px 10px;
                  border-radius:6px; background:rgba(17,24,39,.96); color:#fff; font-size:12px; line-height:1.5;
                  box-shadow:0 6px 20px rgba(0,0,0,.28); }
        .kt-tip b { color:#fff; }
        .kt-tip .kt-tip-k { color:#9fb4d6; }
        `;
        const st = document.createElement('style');
        st.id = 'kpi-timeline-style';
        st.textContent = css;
        document.head.appendChild(st);
    }

    let _tip = null;
    function showTip(html, ev) {
        if (!_tip) {
            _tip = document.createElement('div');
            _tip.className = 'kt-tip';
            document.body.appendChild(_tip);
        }
        _tip.innerHTML = html;
        _tip.style.display = 'block';
        const pad = 14;
        let x = ev.clientX + pad, y = ev.clientY + pad;
        const r = _tip.getBoundingClientRect();
        if (x + r.width > window.innerWidth - 8) x = ev.clientX - r.width - pad;
        if (y + r.height > window.innerHeight - 8) y = ev.clientY - r.height - pad;
        _tip.style.left = x + 'px';
        _tip.style.top = y + 'px';
    }
    function hideTip() { if (_tip) _tip.style.display = 'none'; }

    /**
     * 세그먼트 사이의 빈 구간을 채운다. 서버는 행이 있는 구간만 보내므로,
     * 행이 없는 시간은 클라이언트가 의미를 붙인다 — 맨 앞은 '이전 사이클 미상',
     * 중간은 '기록 공백', 맨 뒤(지금까지)는 '진행 중'.
     */
    function fillHoles(segments, fromMs, toMs, nowMs) {
        const out = [];
        const sorted = (segments || []).slice().sort((a, b) => a.startMs - b.startMs);
        let cursor = fromMs;
        const MIN = 1; // 1ms 미만 틈은 렌더 노이즈라 무시

        for (const s of sorted) {
            if (s.startMs - cursor > MIN) {
                out.push(hole(cursor, s.startMs, cursor === fromMs ? 'Unknown' : 'Gap'));
            }
            out.push(s);
            cursor = Math.max(cursor, s.endMs);
        }
        const tail = Math.min(toMs, nowMs);
        if (tail - cursor > MIN) {
            out.push(hole(cursor, tail, sorted.length ? 'InProgress' : 'Unknown'));
        }
        return out;

        function hole(s, e, reason) {
            return {
                startMs: s, endMs: e, state: 'Excluded', reason,
                cycles: 0, ctMs: e - s, mtMs: 0, rMs: 0, mtMedianMs: 0, axis: null,
                worstWork: null, worstRatio: 0, mtRatio: 0, synthetic: true,
            };
        }
    }

    function segTip(seg) {
        const st = STATE[seg.state] || STATE.Excluded;
        const rows = [];
        rows.push(`<b>${st.label}</b>${seg.reason ? ' · ' + (REASON[seg.reason] || seg.reason) : ''}`);
        rows.push(`<span class="kt-tip-k">구간</span> ${hhmm(seg.startMs)} ~ ${hhmm(seg.endMs)}`);
        rows.push(`<span class="kt-tip-k">길이</span> ${dur(seg.endMs - seg.startMs)}`);
        if (seg.cycles > 0) {
            rows.push(`<span class="kt-tip-k">사이클</span> ${seg.cycles}회 · 합 ${dur(seg.ctMs)}`);
            if (seg.rMs > 0) rows.push(`<span class="kt-tip-k">기준 R</span> ${dur(seg.rMs)}`);
            if (seg.mtMs > 0) rows.push(`<span class="kt-tip-k">MT</span> ${dur(seg.mtMs)}${seg.mtMedianMs > 0 ? ' · 기준 ' + dur(seg.mtMedianMs) : ''}`);
        }
        if (seg.state === 'Down') {
            // 비가동 축 — MT(work 사이 공백이 벌어진 정지) 또는 개별 work 초과(doc/30 §6).
            if (seg.axis === 'Mt') {
                rows.push(`<span class="kt-tip-k">MT 초과</span> 평소의 ${(seg.mtRatio || 0).toFixed(1)}배 — work 사이 공백이 벌어졌습니다`);
            } else if (seg.worstWork) {
                rows.push(`<span class="kt-tip-k">초과 work</span> ${seg.worstWork} · 평소의 ${seg.worstRatio.toFixed(1)}배`);
            }
        }
        if (seg.state === 'Excluded') rows.push('<span class="kt-tip-k">계산에서 빠짐</span>');
        return rows.join('<br>');
    }

    function create(host, opts) {
        injectStyleOnce();
        opts = opts || {};
        host.classList.add('kt-wrap');
        host.innerHTML = `
            <div class="kt-chips"></div>
            <div class="kt-axis"></div>
            <div class="kt-strip"></div>
            <div class="kt-links"></div>`;
        const elChips = host.querySelector('.kt-chips');
        const elAxis = host.querySelector('.kt-axis');
        const elStrip = host.querySelector('.kt-strip');
        const elLinks = host.querySelector('.kt-links');

        let data = null;
        let selected = null;

        function renderChips(d) {
            const c = d.counts, ex = c.excluded;
            const total = c.run + c.down + c.nonProd;
            const chip = (cls, label, n, extra) =>
                `<span class="kt-chip ${extra || ''}"><i class="kt-dot" style="background:var(--${cls})"></i>${label} <b>${n}</b>회</span>`;
            const why = [];
            if (ex.cut) why.push('잘림 ' + ex.cut);
            if (ex.unknown) why.push('미상 ' + ex.unknown);
            if (ex.inProgress) why.push('진행 중 ' + ex.inProgress);
            if (ex.noBaseline) why.push('기준 없음 ' + ex.noBaseline);

            // 전부 제외된 구간(2026-09-21) — '0회' 칩 넷을 나란히 두면 화면이 고장난 것처럼 읽힌다.
            //   숫자 대신 "왜 비었는지"를 말한다.
            // ★약속하지 않는다(2026-09-21 정정). 종전엔 "학습하지 못해… N개 대기 중" 으로 곧 판정이 시작된다고
            //   말했는데, 기준선 표본 쿼리가 제외 행을 세지 않아 새 DB 에서는 영원히 시작되지 않았다
            //   (현장: 사이클 1,679개 전부 기준 없음. KpiRepository 표본 쿼리 수정으로 별도 해결).
            //   화면은 관측된 사실만 말하고 앞일을 예고하지 않는다 — 고쳐지지 않는 상태를 대기라고 부르면
            //   사용자는 기다리기만 하고 아무도 원인을 찾지 않는다.
            if (total === 0 && ex.total > 0) {
                elChips.innerHTML = `<span class="kt-chip kt-why">판정된 사이클이 없습니다 · 제외 <b>${ex.total}</b>개`
                    + (why.length ? `(${why.join(' · ')})` : '') + `</span>`;
                return;
            }

            const parts = [
                `<span class="kt-chip"><b>${total}</b>개 CT</span>`,
                chip('kt-run', '가동', c.run),
                chip('kt-down', '비가동', c.down),
                chip('kt-nonprod', '비생산', c.nonProd),
            ];
            if (ex.total > 0)
                parts.push(`<span class="kt-chip kt-muted" title="계산에서 빠진 구간">제외 <b>${ex.total}</b> · ${why.join(' · ')}</span>`);
            elChips.innerHTML = parts.join('');
        }

        function renderAxis(fromMs, toMs) {
            const span = toMs - fromMs;
            const ticks = 6;
            let html = '';
            for (let i = 0; i <= ticks; i++) {
                const at = fromMs + (span * i) / ticks;
                html += `<span style="left:${pct((i / ticks) * 100)}">${hhmm(at)}</span>`;
            }
            elAxis.innerHTML = html;
        }

        function renderStrip(segs, fromMs, toMs) {
            const span = Math.max(1, toMs - fromMs);
            elStrip.innerHTML = '';
            selected = null;              // 앞선 선택의 DOM 은 방금 지워졌다
            if (!segs.length) {
                elStrip.innerHTML = '<div class="kt-empty">이 구간에 사이클이 없습니다.</div>';
                return;
            }
            // 설비가 둘 이상이면 한 줄에 겹쳐 그릴 수 없다 → 구간 비례 합산으로 넘어간다.
            if (seriesCount(segs) > 1) {
                // 합산 칸은 사이클이 아니라 시간 구간이라 선택 대상이 아니다. 설비 화면에서 고른 사이클이
                // 남아 있으면 라인 화면 아래에 엉뚱한 사이클 설명이 붙으므로 여기서 지운다.
                if (typeof opts.onSelect === 'function') opts.onSelect(null);
                renderRollup(segs, fromMs, toMs);
                return;
            }
            const frag = document.createDocumentFragment();
            segs.forEach((s, i) => {
                const left = ((s.startMs - fromMs) / span) * 100;
                const width = ((s.endMs - s.startMs) / span) * 100;
                if (width <= 0) return;
                const el = document.createElement('div');
                const st = STATE[s.state] || STATE.Excluded;
                el.className = 'kt-seg ' + st.cls;
                el.style.left = pct(left);
                el.style.width = pct(width);
                el.dataset.idx = String(i);
                if (width > 4 && s.cycles > 1) {
                    const n = document.createElement('span');
                    n.className = 'kt-seg-n';
                    n.textContent = s.cycles + '회';
                    el.appendChild(n);
                }
                el.addEventListener('mousemove', ev => showTip(segTip(s), ev));
                el.addEventListener('mouseleave', hideTip);
                el.addEventListener('click', () => {
                    if (selected) selected.classList.remove('kt-sel');
                    selected = el; el.classList.add('kt-sel');
                    if (typeof opts.onSelect === 'function') opts.onSelect(s);
                });
                frag.appendChild(el);
            });
            elStrip.appendChild(frag);
        }

        /**
         * 구간 비례 합산(doc/30 §9.1) — 여러 설비를 한 줄로 볼 때.
         *
         * 사이클 행은 (설비, 분기) 하나에 대해서만 시간축을 빈틈없이 타일링한다. 설비 12대를 한 줄에
         * 그대로 얹으면 12겹이 되고, 절대배치 DOM 은 나중에 시작한 것이 앞선 것을 덮는다. 색 우선순위를
         * 어떻게 정하든 12대의 상태를 한 색으로 줄이는 순간 정보가 사라진다 — 가동 우선이면 고장이 안 보이고,
         * 고장 우선이면 한 대가 라인을 빨갛게 칠한다.
         *
         * 그래서 색을 고르지 않고 <b>면적으로 합산</b>한다. 가로 한 칸(수 px)마다 그 시간 동안 각 상태가
         * 차지한 시간을 모두 더해, 칸 안에서 세로 비율로 쌓는다. 분모는 (칸 길이 × 계열 수)라 "라인의 몇 %가
         * 돌고 있었나" 가 그대로 높이가 된다. 제외·공백은 칠하지 않는다 — 스트립 바탕이 곧 "모르는 시간" 이다.
         */
        function renderRollup(segs, fromMs, toMs) {
            const span = Math.max(1, toMs - fromMs);
            const width = Math.max(120, elStrip.clientWidth || 1200);
            const cols = Math.max(80, Math.min(1400, Math.round(width / BUCKET_PX)));
            const bw = span / cols;
            const series = seriesCount(segs);

            const buckets = new Array(cols);
            for (let i = 0; i < cols; i++) buckets[i] = { run: 0, down: 0, nonProd: 0, excluded: 0, cycles: 0 };

            for (const s of segs) {
                const a = Math.max(fromMs, s.startMs), b = Math.min(toMs, s.endMs);
                if (!(b > a)) continue;
                const key = s.state === 'Run' ? 'run' : s.state === 'Down' ? 'down'
                    : s.state === 'NonProd' ? 'nonProd' : 'excluded';
                const i0 = Math.max(0, Math.floor((a - fromMs) / bw));
                const i1 = Math.min(cols, Math.ceil((b - fromMs) / bw));
                // 사이클 수는 세그먼트가 걸친 칸 중 시작 칸에만 센다(칸마다 더하면 긴 세그먼트가 부풀린다).
                if (i0 < cols) buckets[i0].cycles += s.cycles || 0;
                for (let i = i0; i < i1; i++) {
                    const bs = fromMs + i * bw;
                    const ov = Math.min(b, bs + bw) - Math.max(a, bs);
                    if (ov > 0) buckets[i][key] += ov;
                }
            }

            const denom = bw * series;
            const shape = b => {
                let r = (b.run / denom) * 100, d = (b.down / denom) * 100, n = (b.nonProd / denom) * 100;
                // 한 대만 멈춰도 보이게 — 12대 중 1대면 8%(2.7px)라 그냥 두면 눈에 안 들어온다.
                if (d > 0 && d < MIN_BAND) d = MIN_BAND;
                if (n > 0 && n < MIN_BAND) n = MIN_BAND;
                if (r + d + n > 100) r = Math.max(0, 100 - d - n);
                return [Math.round(r * 10) / 10, Math.round(d * 10) / 10, Math.round(n * 10) / 10];
            };

            // 위에서부터 비가동 · 비생산 · 가동 — 문제를 윗변에 붙여 두면 윗줄만 훑어도 고장 유무가 읽힌다.
            // 남는 높이는 판정되지 않은 시간이다. 제외가 있으면 흐린 무채색으로 덮어 "데이터가 있는데 판정 못 함"
            // 과 "아예 없음" 을 구분한다.
            const paint = ([r, d, n], hasExcluded) => {
                if (r + d + n <= 0 && !hasExcluded) return '';
                const stops = [];
                let top = 0;
                if (d > 0) { stops.push(`var(--kt-down) ${top}% ${top + d}%`); top += d; }
                if (n > 0) { stops.push(`var(--kt-nonprod) ${top}% ${top + n}%`); top += n; }
                if (r > 0) { stops.push(`var(--kt-run) ${top}% ${top + r}%`); top += r; }
                if (top < 100) stops.push(`${hasExcluded ? 'var(--kt-unknown)' : 'transparent'} ${top}% 100%`);
                return `linear-gradient(to bottom, ${stops.join(',')})`;
            };

            const frag = document.createDocumentFragment();
            let i = 0;
            while (i < cols) {
                const sh = shape(buckets[i]);
                const ex = buckets[i].excluded > 0;
                let j = i + 1;
                // 같은 모양이 이어지면 한 칸으로 합친다 — 정지 구간이 길어도 DOM 이 늘지 않는다.
                while (j < cols) {
                    const t = shape(buckets[j]);
                    if (t[0] !== sh[0] || t[1] !== sh[1] || t[2] !== sh[2] || (buckets[j].excluded > 0) !== ex) break;
                    j++;
                }
                const bg = paint(sh, ex);
                if (bg) {
                    const el = document.createElement('div');
                    el.className = 'kt-seg kt-roll';
                    el.style.left = pct((i / cols) * 100);
                    el.style.width = pct(((j - i) / cols) * 100);
                    el.style.backgroundImage = bg;
                    const range = [i, j];
                    el.addEventListener('mousemove', ev => showTip(rollTip(buckets, range, fromMs, bw, series), ev));
                    el.addEventListener('mouseleave', hideTip);
                    frag.appendChild(el);
                }
                i = j;
            }
            elStrip.appendChild(frag);
        }

        function rollTip(buckets, [i0, i1], fromMs, bw, series) {
            let run = 0, down = 0, nonProd = 0, excluded = 0, cycles = 0;
            for (let i = i0; i < i1; i++) {
                run += buckets[i].run; down += buckets[i].down;
                nonProd += buckets[i].nonProd; excluded += buckets[i].excluded; cycles += buckets[i].cycles;
            }
            const denom = bw * (i1 - i0) * series;
            const p = v => denom > 0 ? Math.round((v / denom) * 100) : 0;
            const rows = [
                `<b>${hhmm(fromMs + i0 * bw)} ~ ${hhmm(fromMs + i1 * bw)}</b>`,
                `<span class="kt-tip-k">설비</span> ${series}대 기준`,
                `<span class="kt-tip-k">가동</span> ${p(run)}% · <span class="kt-tip-k">비가동</span> ${p(down)}%`
                + ` · <span class="kt-tip-k">비생산</span> ${p(nonProd)}%`,
            ];
            if (excluded > 0) rows.push(`<span class="kt-tip-k">제외</span> ${p(excluded)}% (계산 밖)`);
            if (cycles > 0) rows.push(`<span class="kt-tip-k">사이클</span> ${cycles}회`);
            return rows.join('<br>');
        }

        function renderLinks(links, fromMs, toMs) {
            const span = Math.max(1, toMs - fromMs);
            const nowMs = Date.now();
            const bad = (links || []).filter(l => !l.isConnected);
            if (!bad.length) { elLinks.innerHTML = ''; return; }
            elLinks.innerHTML = bad.map(l => {
                const s = Math.max(fromMs, Date.parse(l.at));
                const e = Math.min(toMs, l.end ? Date.parse(l.end) : nowMs);
                if (!(e > s)) return '';
                const title = `${l.system === '*' ? '수집 심박' : l.system} 끊김 · ${hhmm(s)} ~ ${hhmm(e)}`
                    + (l.detail ? ' · ' + l.detail : '') + ' (대조용 — 계산에는 쓰지 않음)';
                return `<div class="kt-link" style="left:${pct(((s - fromMs) / span) * 100)};width:${pct(((e - s) / span) * 100)}" title="${title}"></div>`;
            }).join('');
        }

        async function load(q) {
            const p = new URLSearchParams();
            if (q && q.from) p.set('from', typeof q.from === 'string' ? q.from : q.from.toISOString());
            if (q && q.to) p.set('to', typeof q.to === 'string' ? q.to : q.to.toISOString());
            if (q && q.flow) p.set('flow', q.flow);
            if (q && q.branch) p.set('branch', q.branch);
            if (q && q.system) p.set('system', q.system);

            const res = await fetch('/api/kpi/timeline?' + p.toString());
            if (!res.ok) throw new Error('timeline ' + res.status);
            data = await res.json();

            const fromMs = Date.parse(data.from);
            const toMs = Date.parse(data.to);
            const segs = fillHoles(data.segments, fromMs, toMs, Date.now());

            renderChips(data);
            renderAxis(fromMs, toMs);
            renderStrip(segs, fromMs, toMs);
            renderLinks(data.links, fromMs, toMs);
            return data;
        }

        return {
            load,
            get data() { return data; },
            destroy() { hideTip(); host.innerHTML = ''; },
        };
    }

    window.dspKpiTimeline = { create, fillHoles, STATE, REASON };
})();
