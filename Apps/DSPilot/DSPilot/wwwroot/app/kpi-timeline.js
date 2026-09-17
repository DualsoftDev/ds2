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
        None: '',
    };

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
        .kt-wrap { --kt-run:#1E9BE8; --kt-down:#B22F22; --kt-nonprod:#334E7B; --kt-excluded:#C9CED6;
                   display:flex; flex-direction:column; gap:8px; }
        .dark-theme .kt-wrap, .dark .kt-wrap { --kt-run:#52C4FF; --kt-down:#D14738; --kt-nonprod:#41608F; --kt-excluded:#4A5160; }
        .kt-chips { display:flex; flex-wrap:wrap; gap:6px; align-items:center; font-size:12px; }
        .kt-chip { display:inline-flex; align-items:center; gap:5px; padding:3px 9px; border-radius:999px;
                   border:1px solid var(--border-color,#d7dbe0); background:var(--surface-2,#fff); white-space:nowrap; }
        .kt-chip b { font-weight:600; font-variant-numeric:tabular-nums; }
        .kt-dot { width:9px; height:9px; border-radius:2px; flex:none; }
        .kt-chip.kt-muted { opacity:.72; }
        .kt-axis { position:relative; height:14px; font-size:10px; color:var(--text-muted,#6b7280); }
        .kt-axis span { position:absolute; transform:translateX(-50%); white-space:nowrap; }
        .kt-strip { position:relative; height:34px; border-radius:6px; overflow:hidden;
                    background:var(--surface-3,#eef1f4); border:1px solid var(--border-color,#d7dbe0); }
        .kt-seg { position:absolute; top:0; bottom:0; cursor:pointer; }
        .kt-seg.kt-run { background:var(--kt-run); }
        .kt-seg.kt-down { background:var(--kt-down);
                          background-image:repeating-linear-gradient(45deg, rgba(255,255,255,.55) 0 1.4px, transparent 1.4px 6px); }
        .kt-seg.kt-nonprod { background:var(--kt-nonprod);
                             background-image:repeating-linear-gradient(45deg, rgba(150,185,235,.5) 0 1.4px, transparent 1.4px 6px); }
        .kt-seg.kt-excluded { background:var(--kt-excluded);
                              background-image:repeating-linear-gradient(90deg, rgba(255,255,255,.7) 0 2px, transparent 2px 5px); }
        .kt-seg:hover { filter:brightness(1.12); outline:1px solid rgba(0,0,0,.25); outline-offset:-1px; }
        .kt-seg.kt-sel { outline:2px solid var(--primary,#1E9BE8); outline-offset:-2px; }
        .kt-seg-n { position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
                    font-size:10px; font-weight:600; color:#fff; text-shadow:0 1px 2px rgba(0,0,0,.45);
                    pointer-events:none; overflow:hidden; }
        .kt-links { position:relative; height:7px; margin-top:-1px; }
        .kt-link { position:absolute; top:0; bottom:0; background:repeating-linear-gradient(45deg,
                   rgba(180,60,60,.85) 0 3px, rgba(255,255,255,.25) 3px 6px); border-radius:2px; }
        .kt-empty { padding:14px; text-align:center; color:var(--text-muted,#6b7280); font-size:13px; }
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
                cycles: 0, ctMs: e - s, rMs: 0, worstWork: null, worstRatio: 0, synthetic: true,
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
        }
        if (seg.state === 'Down' && seg.worstWork) {
            rows.push(`<span class="kt-tip-k">초과 work</span> ${seg.worstWork} · 평소의 ${seg.worstRatio.toFixed(1)}배`);
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
            const parts = [
                `<span class="kt-chip"><b>${total}</b>개 CT</span>`,
                chip('kt-run', '가동', c.run),
                chip('kt-down', '비가동', c.down),
                chip('kt-nonprod', '비생산', c.nonProd),
            ];
            if (ex.total > 0) {
                const why = [];
                if (ex.cut) why.push('잘림 ' + ex.cut);
                if (ex.unknown) why.push('미상 ' + ex.unknown);
                if (ex.inProgress) why.push('진행 중 ' + ex.inProgress);
                if (ex.noBaseline) why.push('기준 없음 ' + ex.noBaseline);
                parts.push(`<span class="kt-chip kt-muted" title="계산에서 빠진 구간">제외 <b>${ex.total}</b> · ${why.join(' · ')}</span>`);
            }
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
            if (!segs.length) {
                elStrip.innerHTML = '<div class="kt-empty">이 구간에 사이클이 없습니다.</div>';
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
