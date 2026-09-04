// ═══════════════════════════════════════════════════════════════════════════════
//  간트 사이드바(CALL 목록) 보조 — ① 잘린 텍스트 툴팁  ② 사이드바 폭 리사이저
//  Alpine 컴포넌트와 무관한 document 위임 방식 → 단일/분기 카드/전체 편집 세 템플릿에 공통 적용,
//  x-if/x-for 재렌더로 행이 갈아 끼워져도 재바인딩 불필요.
// ═══════════════════════════════════════════════════════════════════════════════
(function () {
    'use strict';

    // ── ① 잘린 텍스트 툴팁 ──────────────────────────────────────────────────────
    // 사이드바 행 폭이 고정이라 Call/Work/태그/ApiCall 이름이 ellipsis 로 잘린다. 실제로 잘린 행에만
    // 전체 텍스트 카드를 띄운다. 레이아웃 컨테이너(.ct-gantt-layout)가 overflow 라 내부 absolute 는
    // 잘리므로 body 직속 position:fixed 한 장을 공유한다.
    const ROW_SEL = '.ct-lane-row, .ct-api-row';
    const TRUNC_SEL = '.ct-lane-name, .ct-lane-name > span, .ct-lane-work, .ct-lane-row-leg span, .ct-api-name-text';
    const SHOW_DELAY = 220;
    let tipEl = null, tipTimer = 0, tipRow = null, pendingRow = null;

    function tip() {
        if (tipEl) return tipEl;
        tipEl = document.createElement('div');
        tipEl.className = 'ct-lane-tip';
        tipEl.setAttribute('role', 'tooltip');
        document.body.appendChild(tipEl);
        return tipEl;
    }
    function isTruncated(el) { return el && el.scrollWidth > el.clientWidth + 1; }
    function rowTruncated(row) {
        const els = row.querySelectorAll(TRUNC_SEL);
        for (const el of els) { if (el.offsetParent !== null && isTruncated(el)) return true; }
        return false;
    }
    function text(el) { return el ? (el.textContent || '').trim() : ''; }
    function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

    // 행 DOM 에서 카드 내용 조립 — Call 행: 이름/Work/OUT·IN 태그, ApiCall 서브행: 이름/OUT·IN 태그
    function buildHtml(row) {
        let title, sub = '', out = '', inn = '', hint = '';
        if (row.classList.contains('ct-api-row')) {
            title = text(row.querySelector('.ct-api-name-text'));
            out = text(row.querySelector('.ct-api-tags .t.out b'));
            inn = text(row.querySelector('.ct-api-tags .t.in b'));
        } else {
            title = text(row.querySelector('.ct-lane-name > span:first-child'));
            sub = text(row.querySelector('.ct-lane-work'));
            out = text(row.querySelector('.ct-lane-row-leg .leg-out span'));
            inn = text(row.querySelector('.ct-lane-row-leg .leg-in span'));
            const more = row.querySelector('.ct-lane-row-leg .leg-more');
            if (more && more.offsetParent !== null) hint = more.getAttribute('title') || '';
        }
        if (!title) return '';
        let h = `<div class="lt-title">${esc(title)}</div>`;
        if (sub) h += `<div class="lt-sub">${esc(sub)}</div>`;
        if (out || inn) {
            h += '<div class="lt-tags">';
            if (out) h += `<span class="lt-out"><i></i>OUT ${esc(out)}</span>`;
            if (inn) h += `<span class="lt-in"><i></i>IN ${esc(inn)}</span>`;
            h += '</div>';
        }
        if (hint) h += `<div class="lt-hint">${esc(hint)}</div>`;
        return h;
    }

    function place(row) {
        const t = tip();
        const r = row.getBoundingClientRect();
        const pad = 8;
        // 기본: 행 오른쪽(간트 본체 위)에 행 상단 정렬. 화면 밖이면 좌/상으로 되돌린다.
        t.style.left = '0px'; t.style.top = '0px';
        const tw = t.offsetWidth, th = t.offsetHeight;
        let x = r.right + pad, y = r.top;
        if (x + tw > window.innerWidth - pad) x = Math.max(pad, r.left);
        if (y + th > window.innerHeight - pad) y = Math.max(pad, window.innerHeight - pad - th);
        t.style.left = Math.round(x) + 'px';
        t.style.top = Math.round(y) + 'px';
    }
    function showFor(row) {
        if (!rowTruncated(row)) return hide();
        const html = buildHtml(row);
        if (!html) return hide();
        const t = tip();
        t.innerHTML = html;
        tipRow = row;
        place(row);
        t.classList.add('is-on');
    }
    function hide() {
        clearTimeout(tipTimer); tipTimer = 0; tipRow = null; pendingRow = null;
        if (tipEl) tipEl.classList.remove('is-on');
    }

    document.addEventListener('mouseover', (e) => {
        const row = e.target.closest && e.target.closest(ROW_SEL);
        if (!row) return;
        // 같은 행 안에서 자식 사이를 오가는 mouseover 는 무시 — 매번 타이머를 리셋하면 마우스가 조금씩
        // 움직이는 동안 툴팁이 영영 안 뜬다.
        if (row === tipRow || row === pendingRow) return;
        clearTimeout(tipTimer);
        pendingRow = row;
        tipTimer = setTimeout(() => { pendingRow = null; showFor(row); }, SHOW_DELAY);
    });
    document.addEventListener('mouseout', (e) => {
        const row = e.target.closest && e.target.closest(ROW_SEL);
        if (!row) return;
        const to = e.relatedTarget;
        if (to && row.contains(to)) return;
        hide();
    });
    // 터치(hover 없음): 이름 블록 탭 → 토글. 버튼(시작/끝/편집/▸)은 자기 동작 유지.
    document.addEventListener('click', (e) => {
        if (!window.matchMedia || !window.matchMedia('(hover: none)').matches) return;
        if (e.target.closest('button, a, input')) return;
        const row = e.target.closest && e.target.closest(ROW_SEL);
        if (!row) return hide();
        if (row === tipRow) return hide();
        showFor(row);
    });
    document.addEventListener('scroll', hide, true);
    document.addEventListener('mousedown', (e) => { if (!(e.target.closest && e.target.closest(ROW_SEL))) hide(); });
    window.addEventListener('resize', hide);

    // ── ② 사이드바 폭 리사이저 ─────────────────────────────────────────────────
    // .ct-side-resizer(사이드바 오른쪽 flex 형제)를 드래그 → :root --ct-side-w 갱신. 페이지 내 모든
    // 간트가 같은 변수를 쓰므로 일괄 반영. 놓을 때 localStorage 저장 + window resize 발화로 각 앱이
    // 플롯 폭을 재측정(measurePlotWidth)해 줌 100% 가 다시 '폭맞춤'이 된다. 더블클릭 = 기본폭 복원.
    const KEY = 'dsp.ct.sideW', DEF = 330, MIN = 200, MAX_ABS = 720;
    const rootStyle = document.documentElement.style;
    function setW(w) { rootStyle.setProperty('--ct-side-w', Math.round(w) + 'px'); }
    function clearW() { rootStyle.removeProperty('--ct-side-w'); }
    function currentW(sidebar) { return sidebar ? sidebar.getBoundingClientRect().width : DEF; }
    function maxW(layout) { return Math.min(MAX_ABS, Math.max(MIN, Math.floor(layout.clientWidth * 0.6))); }
    function notifyResize() { try { window.dispatchEvent(new Event('resize')); } catch (e) { /* ignore */ } }

    try {
        const saved = parseInt(localStorage.getItem(KEY), 10);
        if (saved >= MIN && saved <= MAX_ABS && saved !== DEF) setW(saved);
    } catch (e) { /* storage 불가 → 기본폭 */ }

    document.addEventListener('pointerdown', (e) => {
        const h = e.target.closest && e.target.closest('.ct-side-resizer');
        if (!h || e.button !== 0) return;
        const layout = h.closest('.ct-gantt-layout');
        const sidebar = layout && layout.querySelector('.ct-lane-sidebar');
        if (!layout || !sidebar) return;
        e.preventDefault();
        hide();
        const startX = e.clientX, startW = currentW(sidebar), max = maxW(layout);
        let w = startW, moved = false;
        h.classList.add('is-dragging');
        document.body.classList.add('ct-side-resizing');
        try { h.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
        const onMove = (ev) => {
            const nw = Math.min(max, Math.max(MIN, startW + (ev.clientX - startX)));
            if (Math.abs(nw - w) < 0.5) return;
            w = nw; moved = true; setW(w);
        };
        const onUp = () => {
            h.removeEventListener('pointermove', onMove);
            h.removeEventListener('pointerup', onUp);
            h.removeEventListener('pointercancel', onUp);
            h.classList.remove('is-dragging');
            document.body.classList.remove('ct-side-resizing');
            if (!moved) return;
            try { localStorage.setItem(KEY, String(Math.round(w))); } catch (err) { /* ignore */ }
            notifyResize();
        };
        h.addEventListener('pointermove', onMove);
        h.addEventListener('pointerup', onUp);
        h.addEventListener('pointercancel', onUp);
    });
    document.addEventListener('dblclick', (e) => {
        const h = e.target.closest && e.target.closest('.ct-side-resizer');
        if (!h) return;
        e.preventDefault();
        clearW();
        try { localStorage.removeItem(KEY); } catch (err) { /* ignore */ }
        notifyResize();
    });
})();
