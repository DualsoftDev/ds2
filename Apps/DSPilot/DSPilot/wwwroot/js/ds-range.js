/* ============================================================================
   DSPilot — 공용 기간 선택기 <ds-range> + window.dspRange  (2026-10-01)
   ----------------------------------------------------------------------------
   모든 분석 페이지(설비효율·생산효율·이상알람·추이·가동시간 분석·태그 모니터링·PLC 디버그)의
   날짜/시간 범위 UI 를 한 벌로 통일한다. 페이지는 자기 상태(period/startTime …)를 그대로 두고
   이 요소에 값을 바인딩하고 이벤트만 받는다(프레임워크 무관 — Alpine :attr / @event 로 연결).

     <ds-range :from="startTime" :to="endTime"          값 = 'yyyy-MM-ddTHH:mm[:ss]' (로컬)
               :preset="timePreset || ''"               활성 프리셋 키('' = 직접 지정)
               presets="m5,m30,h1,h24,today"            보여줄 프리셋(순서대로) — 키는 아래 PRESETS
               cycles="20,50,100" :cycle="cyclePreset"  (선택) '가동' 버튼 → 확장 카드(최근 N회 가동)
               :disabled="loading"
               @preset="…($event.detail.key)"           프리셋 클릭
               @cycle="…($event.detail.n)"              가동 N회 클릭
               @range="…($event.detail)"                직접 지정 적용 → {from, to}
     ></ds-range>

   화면(2026-10-01 개편 — 날짜/시간 편집칸을 항상 노출, 📅 토글 폐지):
         [오늘 | 7일 | 30일 | 60일] (프리셋 '바로 잡기' 그룹, 있을 때) · [가동 ▾] (가동 카드, 있을 때)
         날짜 [시작]~[끝]  시간 [시작]~[끝]  [종일] [적용]   — 바로 보이는 직접 지정 편집칸.
         편집칸은 활성 범위를 늘 비춘다(편집칸에 포커스가 있을 때만 안 덮어씀). 종료 24:00 = 다음날 00:00.
         '적용' 을 눌러야 범위가 반영된다(프리셋·가동 클릭은 즉시 반영). 가동 ▾ 만 접힌 카드로 남는다.

   기억: 사용자가 고른 범위는 localStorage('dspilot-range')에 {preset, from, to} 로 남기고
         다른 페이지가 열릴 때 되살린다 — dspRange.remember() / dspRange.recall(presetKeys).
         프리셋은 그 페이지가 지원하면 이름으로(열 때 다시 계산), 아니면 절대 범위(from/to)로 적용.

   ★공유 범위(2026-10-01) — 척도가 다른 페이지끼리 범위가 섞여 불편(예: 60일→PLC 디버그, 5분→OEE)해
     *일 단위 분석 페이지끼리만* 기억·복원한다: 설비효율·생산효율·추이·이상알람·태그 모니터링.
     사이클(가동시간 분석)·PLC 디버그·동작편차는 참여하지 않는다(remember/recall 호출 안 함 = 항상 자기 기본값).
     → 참여 규약: 공유하려면 remember()+recall() 둘 다, 독립이면 둘 다 호출 안 함(신규 페이지도 이 기준).
   ============================================================================ */
(function () {
    'use strict';

    var STORE_KEY = 'dspilot-range';

    // 프리셋 카탈로그 — 키는 페이지들의 기존 URL 규약(?period=)과 동일. 라벨은 전 페이지 공통.
    var PRESETS = {
        m1:  { label: '1분',   title: '마지막 신호 시각 기준 최근 1분',  ms: 60e3 },
        m5:  { label: '5분',   title: '마지막 신호 시각 기준 최근 5분',  ms: 5 * 60e3 },
        m30: { label: '30분',  title: '마지막 신호 시각 기준 최근 30분', ms: 30 * 60e3 },
        h1:  { label: '1시간', title: '마지막 신호 시각 기준 최근 1시간', ms: 3600e3 },
        h8:  { label: '8시간', title: '마지막 신호 시각 기준 최근 8시간', ms: 8 * 3600e3 },
        h24: { label: '24시간', title: '최근 24시간', ms: 24 * 3600e3 },
        '24h': { label: '24시간', title: '지금부터 24시간 전까지', ms: 24 * 3600e3 },
        today: { label: '오늘', title: '오늘 00:00 ~ 지금', days: 1 },
        '7d':  { label: '7일',  title: '오늘 포함 지난 7일(00:00 ~ 지금)', days: 7 },
        '30d': { label: '30일', title: '오늘 포함 지난 30일', days: 30 },
        '60d': { label: '60일', title: '오늘 포함 지난 60일', days: 60 }
    };

    function pad(n) { return String(n).padStart(2, '0'); }

    // Date → 'yyyy-MM-ddTHH:mm:ss' (로컬). withSec=false 면 분까지.
    function fmtInput(d, withSec) {
        if (!(d instanceof Date) || isNaN(d)) return '';
        var s = d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes());
        return withSec === false ? s : s + ':' + pad(d.getSeconds());
    }
    // 'yyyy-MM-ddTHH:mm[:ss]' → Date (로컬) | null
    function parseInput(v) {
        if (!v) return null;
        var m = String(v).match(/^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?/);
        if (!m) { var d0 = new Date(v); return isNaN(d0) ? null : d0; }
        var d = new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], m[6] ? +m[6] : 0);
        return isNaN(d) ? null : d;
    }
    function hasSec(v) { var d = parseInput(v); return !!(d && d.getSeconds()); }

    // 입력값 → {date:'yyyy-MM-dd', time:'HH:mm'|'HH:mm:ss'}. asEnd=true 이고 정각 자정이면 전날 24:00 으로.
    function split(v, asEnd) {
        var d = parseInput(v);
        if (!d) return { date: '', time: '' };
        if (asEnd && d.getHours() === 0 && d.getMinutes() === 0 && d.getSeconds() === 0) {
            var p = new Date(d.getTime() - 864e5);
            return { date: p.getFullYear() + '-' + pad(p.getMonth() + 1) + '-' + pad(p.getDate()), time: '24:00' };
        }
        var t = pad(d.getHours()) + ':' + pad(d.getMinutes()) + (d.getSeconds() ? ':' + pad(d.getSeconds()) : '');
        return { date: d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()), time: t };
    }
    // {date, time} → 'yyyy-MM-ddTHH:mm:ss'. time '24:00' = 다음날 00:00:00. 실패 시 ''.
    function join(date, time) {
        var md = String(date || '').match(/^(\d{4})-(\d{2})-(\d{2})$/);
        var t = normTime(time);
        if (!md || !t) return '';
        var tm = t.split(':');
        var d = new Date(+md[1], +md[2] - 1, +md[3], +tm[0], +tm[1], tm[2] ? +tm[2] : 0);
        if (isNaN(d)) return '';
        return fmtInput(d);
    }
    // 시각 텍스트 정규화 — '9'→'09:00', '930'→'09:30', '9:3'→'09:03', '24'/'2400'→'24:00', '10:14:03' 유지. 틀리면 null.
    function normTime(txt) {
        var s = String(txt == null ? '' : txt).trim().replace(/\s+/g, '');
        if (!s) return null;
        var h, mi, se = null, m;
        if ((m = s.match(/^(\d{1,2}):(\d{1,2})(?::(\d{1,2}))?$/))) { h = +m[1]; mi = +m[2]; se = m[3] != null ? +m[3] : null; }
        else if ((m = s.match(/^(\d{1,2})$/))) { h = +m[1]; mi = 0; }
        else if ((m = s.match(/^(\d{1,2})(\d{2})$/))) { h = +m[1]; mi = +m[2]; }
        else if ((m = s.match(/^(\d{1,2})(\d{2})(\d{2})$/))) { h = +m[1]; mi = +m[2]; se = +m[3]; }
        else if ((m = s.match(/^(\d{1,2})시(?:(\d{1,2})분?)?$/))) { h = +m[1]; mi = m[2] ? +m[2] : 0; }
        else return null;
        if (h === 24) { if (mi !== 0 || (se && se !== 0)) return null; return '24:00'; }
        if (h < 0 || h > 23 || mi < 0 || mi > 59 || (se != null && (se < 0 || se > 59))) return null;
        return pad(h) + ':' + pad(mi) + (se ? ':' + pad(se) : '');
    }

    // 프리셋 → 절대 범위(기준 now). 분/시간 프리셋은 now 앵커(가동시간 분석은 자체적으로 마지막 신호 시각에 앵커 — 거기선 안 씀).
    function presetRange(key, now) {
        var p = PRESETS[key]; if (!p) return null;
        now = now || new Date();
        var from;
        if (p.days) { from = new Date(now.getFullYear(), now.getMonth(), now.getDate()); from.setDate(from.getDate() - (p.days - 1)); }
        else from = new Date(now.getTime() - p.ms);
        return { from: fmtInput(from), to: fmtInput(now) };
    }

    // 범위 한 줄 표기 — 같은 날 '10/1 10:14 ~ 10:19', 다른 날 '9/24 00:00 ~ 10/1 24:00', 올해 아니면 연도 포함.
    // 초는 1시간 미만 창(가동시간 분석 '최근 5분' 류 — 마지막 신호 시각 앵커라 초가 의미 있다)에서만 둘 다 HH:mm:ss.
    // 하루짜리 '오늘 00:00:00 ~ 14:10:40' 에서 초는 소음이라 뺀다(편집 카드에는 초까지 그대로). 종료 자정은 전날 24:00.
    function label(from, to) {
        var a = split(from, false), b = split(to, true);
        if (!a.date || !b.date) return '기간 선택';
        var da = parseInput(from), db = parseInput(to);
        var shortWin = da && db && (db.getTime() - da.getTime()) < 3600e3;
        var sec = shortWin && (hasSec(from) || hasSec(to));
        var md = function (ds) { var m = ds.split('-'); var y = +m[0]; return (y !== new Date().getFullYear() ? y + '/' : '') + (+m[1]) + '/' + (+m[2]); };
        var tm = function (t) { if (!sec) return t.slice(0, 5); return t.length === 5 ? t + ':00' : t; };
        if (a.date === b.date) return md(a.date) + ' ' + tm(a.time) + ' ~ ' + tm(b.time);
        return md(a.date) + ' ' + tm(a.time) + ' ~ ' + md(b.date) + ' ' + tm(b.time);
    }
    // 길이 표기 — shell.js dspFmt.dur 가 있으면 그걸(일/시간/분/초 SSOT), 없으면 분.
    function durLabel(from, to) {
        var a = parseInput(from), b = parseInput(to);
        if (!a || !b) return '';
        var ms = b.getTime() - a.getTime();
        if (ms <= 0) return '';
        if (window.dspFmt && typeof window.dspFmt.dur === 'function') return window.dspFmt.dur(ms, '');
        return Math.round(ms / 60000) + '분';
    }

    // ── 기억(페이지 간 공유) ──
    function remember(s) {
        try {
            if (!s || !s.from || !s.to) return;
            localStorage.setItem(STORE_KEY, JSON.stringify({ preset: s.preset || null, from: s.from, to: s.to, at: Date.now() }));
        } catch (e) { /* 사생활 모드 등 — 기억 없이 진행 */ }
    }
    function recallRaw() {
        try { var v = localStorage.getItem(STORE_KEY); if (!v) return null; var o = JSON.parse(v); return (o && o.from && o.to) ? o : null; }
        catch (e) { return null; }
    }
    // 이 페이지에 맞게 해석 — presetKeys 에 있으면 {preset}, 'cN'(가동 N회)이고 cycleKeys 에 있으면 {cycle:N},
    // 아니면 절대 범위 {from, to}. 아무것도 없으면 null.
    function recall(presetKeys, cycleKeys) {
        var o = recallRaw(); if (!o) return null;
        if (o.preset) {
            if (Array.isArray(presetKeys) && presetKeys.indexOf(o.preset) !== -1) return { preset: o.preset, from: o.from, to: o.to };
            var m = String(o.preset).match(/^c(\d+)$/);
            if (m && Array.isArray(cycleKeys) && cycleKeys.indexOf(+m[1]) !== -1) return { cycle: +m[1], from: o.from, to: o.to };
        }
        // rawPreset = 이 페이지엔 없는 프리셋 키(예: 'm5' 를 설비효율에서) — 상대 프리셋을 지금 기준으로 다시 계산하고 싶을 때 참고
        return { from: o.from, to: o.to, rawPreset: o.preset || null };
    }

    window.dspRange = { PRESETS: PRESETS, fmtInput: fmtInput, parseInput: parseInput, split: split, join: join, normTime: normTime,
                        presetRange: presetRange, label: label, durLabel: durLabel, remember: remember, recall: recall, STORE_KEY: STORE_KEY };

    // ═══════════════════════════ <ds-range> ═══════════════════════════
    if (typeof customElements === 'undefined' || customElements.get('ds-range')) return;

    var ICON_CAL = '<span class="material-icons">calendar_today</span>';
    var ICON_CYC = '<span class="material-icons">replay</span>';
    var ICON_DN = '<span class="material-icons ds-range-caret">expand_more</span>';

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

    var DsRange = function () { return Reflect.construct(HTMLElement, [], DsRange); };
    DsRange.prototype = Object.create(HTMLElement.prototype);
    DsRange.prototype.constructor = DsRange;
    Object.setPrototypeOf(DsRange, HTMLElement);
    Object.defineProperty(DsRange, 'observedAttributes', { get: function () { return ['from', 'to', 'preset', 'presets', 'cycles', 'cycle', 'disabled']; } });

    DsRange.prototype.connectedCallback = function () {
        if (this._root) { this._update(); return; }
        var self = this;
        this._open = null;          // null | 'cycles' — 편집칸은 항상 노출이라 'range' 토글은 없다
        this._ed = { sd: '', ed: '', st: '', et: '' };   // 편집 중 값
        this._edBase = this._edKey();   // 편집칸을 채운 시점의 from|to (dirty 판정 — 적용 버튼 '*')
        var root = document.createElement('div');
        root.className = 'ds-range';
        root._x_ignore = true;      // Alpine 이 내부 DOM 을 건드리지 않게(요소 자체의 :attr/@event 바인딩은 그대로 산다)
        this.appendChild(root);
        this._root = root;
        this._render();
        this._onDocClick = function (e) { if (self._open && !self.contains(e.target)) self._setOpen(null); };
        this._onKey = function (e) { if (e.key === 'Escape' && self._open) { self._setOpen(null); } };
        document.addEventListener('mousedown', this._onDocClick, true);
        document.addEventListener('keydown', this._onKey);
    };
    DsRange.prototype.disconnectedCallback = function () {
        document.removeEventListener('mousedown', this._onDocClick, true);
        document.removeEventListener('keydown', this._onKey);
    };
    DsRange.prototype.attributeChangedCallback = function () { if (this._root) this._update(); };

    DsRange.prototype._presets = function () {
        return (this.getAttribute('presets') || '').split(',').map(function (s) { return s.trim(); }).filter(function (k) { return k && PRESETS[k]; });
    };
    DsRange.prototype._cycles = function () {
        return (this.getAttribute('cycles') || '').split(',').map(function (s) { return parseInt(s, 10); }).filter(function (n) { return n > 0; });
    };

    // 전체 골격은 한 번만 만들고, 이후엔 _update 가 라벨/활성/입력값만 고친다(열린 카드·포커스 유지).
    DsRange.prototype._render = function () {
        var self = this;
        var presets = this._presets(), cycles = this._cycles();
        var h = '';
        // '바로 잡기' 그룹 — 프리셋(+ 가동)을 한 그룹 버튼으로. 둘 다 없으면 그룹 자체를 안 그린다(plc-debug 류).
        if (presets.length || cycles.length) {
            h += '<div class="segmented ds-range-seg" role="group" aria-label="조회 기간">';
            presets.forEach(function (k) { h += '<button type="button" data-preset="' + k + '" title="' + esc(PRESETS[k].title) + '">' + esc(PRESETS[k].label) + '</button>'; });
            if (cycles.length) h += '<button type="button" class="ds-range-cyc" title="최근 N회 가동이 들어오도록 날짜·시간을 자동으로 맞춥니다">' + ICON_CYC + '가동<span class="ds-range-cyc-n"></span>' + ICON_DN + '</button>';
            h += '</div>';
        }
        // 직접 지정 편집칸 — 항상 노출(바로 보임). 날짜·시간을 고치고 '적용'. 캘린더 아이콘으로 묶음을 표시.
        h += '<div class="ds-range-edit">'
           + '<span class="ds-range-k ds-range-k-cal" aria-hidden="true">' + ICON_CAL + '</span>'
           + '<span class="ds-range-k">날짜</span>'
           + '<input type="date" class="form-field" data-k="sd" aria-label="시작 날짜" />'
           + '<span class="ds-range-tilde">~</span>'
           + '<input type="date" class="form-field" data-k="ed" aria-label="종료 날짜" />'
           + '<span class="ds-range-k ds-range-k-time">시간</span>'
           + '<input type="text" class="form-field" data-k="st" inputmode="numeric" placeholder="00:00" aria-label="시작 시각" autocomplete="off" />'
           + '<span class="ds-range-tilde">~</span>'
           + '<input type="text" class="form-field" data-k="et" inputmode="numeric" placeholder="24:00" aria-label="종료 시각" autocomplete="off" />'
           + '<button type="button" class="ds-range-link ds-range-allday" title="시간을 00:00 ~ 24:00 으로">종일</button>'
           + '<button type="button" class="btn btn-sm ds-range-apply">적용</button>'
           + '<span class="ds-range-dur"></span>'
           + '</div>';
        // 가동 N회 카드 — 접힌 채 남는 유일한 팝오버
        if (cycles.length) {
            h += '<div class="ds-range-pop ds-range-cycpop" hidden><div class="ds-range-cyc-grid">';
            cycles.forEach(function (n) { h += '<button type="button" data-n="' + n + '"><b>최근 ' + n + '회</b><span>가동</span></button>'; });
            h += '</div></div>';
        }
        this._root.innerHTML = h;

        this._root.querySelectorAll('[data-preset]').forEach(function (b) {
            b.addEventListener('click', function () { self._setOpen(null); self._emit('preset', { key: b.getAttribute('data-preset') }); });
        });
        var cyc = this._root.querySelector('.ds-range-cyc');
        if (cyc) cyc.addEventListener('click', function () { self._setOpen(self._open === 'cycles' ? null : 'cycles'); });
        this._root.querySelectorAll('[data-n]').forEach(function (b) {
            b.addEventListener('click', function () { self._setOpen(null); self._emit('cycle', { n: parseInt(b.getAttribute('data-n'), 10) }); });
        });
        this._root.querySelectorAll('.ds-range-edit input').forEach(function (inp) {
            var k = inp.getAttribute('data-k');
            inp.addEventListener('input', function () { self._ed[k] = inp.value; self._refreshEdit(false); });
            inp.addEventListener('change', function () {
                if (k === 'st' || k === 'et') { var t = normTime(inp.value); if (t) { inp.value = t; self._ed[k] = t; } }
                self._refreshEdit(false);
            });
            inp.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); self._apply(); } });
        });
        this._root.querySelector('.ds-range-allday').addEventListener('click', function () {
            self._ed.st = '00:00'; self._ed.et = '24:00'; self._fillEdit(); self._refreshEdit(false);
        });
        this._root.querySelector('.ds-range-apply').addEventListener('click', function () { self._apply(); });
        this._update();
    };

    DsRange.prototype._update = function () {
        var root = this._root; if (!root) return;
        var from = this.getAttribute('from') || '', to = this.getAttribute('to') || '';
        var preset = this.getAttribute('preset') || '';
        var cycle = this.getAttribute('cycle') || '';
        var disabled = this.hasAttribute('disabled');
        // 프리셋/가동 목록이 바뀌었으면 골격부터 다시
        var want = this._presets().join(',') + '|' + this._cycles().join(',');
        if (this._shape !== undefined && this._shape !== want) { this._shape = want; this._render(); return; }
        this._shape = want;

        root.querySelectorAll('[data-preset]').forEach(function (b) {
            b.classList.toggle('is-active', !!preset && b.getAttribute('data-preset') === preset);
            b.disabled = disabled;
        });
        var cyc = root.querySelector('.ds-range-cyc');
        if (cyc) {
            cyc.classList.toggle('is-active', !!cycle);
            cyc.disabled = disabled;
            cyc.querySelector('.ds-range-cyc-n').textContent = cycle ? ' 최근 ' + cycle : '';
            root.querySelectorAll('[data-n]').forEach(function (b) { b.classList.toggle('is-active', String(b.getAttribute('data-n')) === String(cycle)); });
        }
        root.querySelectorAll('.ds-range-edit input').forEach(function (inp) { inp.disabled = disabled; });
        root.querySelector('.ds-range-allday').disabled = disabled;
        // 편집칸은 활성 범위를 늘 비춘다 — 단, 사용자가 편집칸에 포커스를 두고 고치는 중이면 안 덮어쓴다.
        if (!this._editing()) this._seedEdit(from, to);
        else this._refreshEdit(true);
        if (disabled) root.querySelector('.ds-range-apply').disabled = true;
    };
    // 편집칸(날짜/시간 input)에 포커스가 있는가 — 있으면 _seedEdit 로 입력을 덮어쓰지 않는다.
    DsRange.prototype._editing = function () {
        var a = document.activeElement;
        return !!(a && a.tagName === 'INPUT' && this._root.contains(a));
    };

    DsRange.prototype._seedEdit = function (from, to) {
        var a = split(from, false), b = split(to, true);
        this._ed = { sd: a.date, ed: b.date, st: a.time || '00:00', et: b.time || '24:00' };
        this._edBase = this._edKey();
        this._fillEdit(); this._refreshEdit(true);
    };
    DsRange.prototype._edKey = function () { return [this._ed.sd, this._ed.ed, this._ed.st, this._ed.et].join('|'); };
    DsRange.prototype._dirty = function () { return this._edKey() !== this._edBase; };
    DsRange.prototype._fillEdit = function () {
        var ed = this._ed;
        this._root.querySelectorAll('.ds-range-edit input').forEach(function (inp) { var k = inp.getAttribute('data-k'); if (inp.value !== ed[k]) inp.value = ed[k]; });
    };
    // 적용 버튼 dirty 규약(저장 버튼과 동일): 평소 중립, 바뀌면 PRIMARY + '*'. 길이/오류 표시 갱신.
    DsRange.prototype._refreshEdit = function (quiet) {
        var r = this._compose();
        var btn = this._root.querySelector('.ds-range-apply');
        var dirty = this._dirty();
        btn.classList.toggle('btn-primary', dirty && !r.error);
        btn.textContent = dirty ? '적용*' : '적용';
        btn.disabled = !!r.error;
        var dur = this._root.querySelector('.ds-range-dur');
        dur.textContent = r.error ? r.error : (durLabel(r.from, r.to) ? '기간 ' + durLabel(r.from, r.to) : '');
        dur.classList.toggle('is-err', !!r.error && !quiet);
    };
    DsRange.prototype._compose = function () {
        var ed = this._ed;
        if (!ed.sd || !ed.ed) return { error: '날짜를 고르세요' };
        if (normTime(ed.st) == null) return { error: '시작 시각 형식 (예: 09:30)' };
        if (normTime(ed.et) == null) return { error: '종료 시각 형식 (예: 18:00, 24:00)' };
        if (normTime(ed.st) === '24:00') return { error: '시작 시각은 24:00 이 될 수 없습니다' };
        var from = join(ed.sd, ed.st), to = join(ed.ed, ed.et);
        if (!from || !to) return { error: '날짜·시각을 확인하세요' };
        if (parseInput(to) <= parseInput(from)) return { error: '종료가 시작보다 앞섭니다' };
        return { from: from, to: to };
    };
    DsRange.prototype._apply = function () {
        var r = this._compose();
        if (r.error) { this._refreshEdit(false); return; }
        this._edBase = this._edKey();
        this._setOpen(null);
        this._emit('range', { from: r.from, to: r.to });
    };
    DsRange.prototype._setOpen = function (which) {
        // 편집칸은 항상 노출 — 여기서 여닫는 건 가동 N회 카드뿐이다.
        this._open = which === 'cycles' ? 'cycles' : null;
        var cp = this._root.querySelector('.ds-range-cycpop');
        if (cp) cp.hidden = this._open !== 'cycles';
        var cyc = this._root.querySelector('.ds-range-cyc'); if (cyc) cyc.setAttribute('aria-expanded', this._open === 'cycles' ? 'true' : 'false');
    };
    DsRange.prototype._emit = function (name, detail) { this.dispatchEvent(new CustomEvent(name, { detail: detail })); };

    customElements.define('ds-range', DsRange);
})();
