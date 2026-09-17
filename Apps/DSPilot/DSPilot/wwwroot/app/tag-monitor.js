// 태그 모니터링 (/tag-monitor) — UserTag 값·추이 화면.
//
// 서버는 두 곳만 부른다:
//   GET /api/tag-monitor/tags                                  목록 + 현재값 (5초 폴링)
//   GET /api/tag-monitor/series?tagIds&from&to&maxPoints       선택 태그의 구간 시계열 + 통계
//
// 규약 메모
//   · 시각은 정수 epoch ms 로 오간다(차트 축이 주 소비자). 화면 표기만 로컬로 바꾼다.
//   · 신호는 값이 바뀔 때만 기록된다 → 선은 반드시 계단(stepped)이다. 선형 보간은 없는 사실을 그린다.
//   · 서버가 주는 startValue = 구간 직전 마지막 값. 차트의 왼쪽 끝을 이것으로 세운다.
//   · Chart.js 인스턴스는 Alpine 반응형 객체에 넣지 않는다(프록시가 내부 상태를 건드리면 폭주한다 —
//     reference: alpine reactive chartjs update crash). 클로저 변수에 보관한다.

function tagMonitorApp() {
    let numChart = null;          // Chart.js 인스턴스 — 반응형 밖에 둔다
    let pollTimer = null;
    let seriesSeq = 0;            // 응답 역전 방지(늦게 온 옛 응답이 새 것을 덮지 않게)

    // 태그 색 — 대시보드 팔레트와 같은 계열. 선택 순서가 아니라 태그 키 해시라 재선택해도 색이 유지된다.
    const PALETTE = ['#2170e4', '#e2725b', '#16a34a', '#9333ea', '#ea580c', '#0891b2', '#c2185b', '#65a30d'];

    return {
        dark: document.documentElement.classList.contains('dark-theme'),
        loaded: false, busy: false, projectLoaded: true, error: '', toast: '',
        tags: [], series: [],
        selected: [],                     // tagId 배열
        q: '', sysFilter: '', kind: 'Info',
        period: 'today', rangeOpen: false, customFrom: '', customTo: '',
        fromMs: 0, toMs: 0,
        evtPage: 0, evtPageSize: 100,

        init() {
            // 나브 시스템 행에서 들어오면 ?system=<시스템명> 으로 스코프가 실려 온다(shell.js buildScopeTrees).
            const qs = new URLSearchParams(location.search);
            this.sysFilter = qs.get('system') || '';
            this.applyPeriod();
            this.loadTags(true);
            // 현재값만 가볍게 갱신한다. 추이는 사용자가 기간을 바꾸거나 새로고침할 때만 다시 읽는다
            // (구간이 고정된 그래프를 5초마다 다시 그리면 읽는 사람이 따라가지 못한다).
            pollTimer = setInterval(() => this.loadTags(false), 5000);
            window.addEventListener('beforeunload', () => clearInterval(pollTimer));
            // 셸의 테마 토글에 맞춰 차트 색을 다시 만든다.
            const obs = new MutationObserver(() => {
                const d = document.documentElement.classList.contains('dark-theme');
                if (d !== this.dark) { this.dark = d; this.renderChart(); }
            });
            obs.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
        },

        // ── 기간 ──────────────────────────────────────────────────────────────
        applyPeriod() {
            const now = new Date();
            const to = now;
            let from;
            if (this.period === 'today') { from = new Date(now.getFullYear(), now.getMonth(), now.getDate()); }
            else if (this.period === '24h') { from = new Date(now.getTime() - 24 * 3600e3); }
            else if (this.period === '7d') { from = new Date(now.getTime() - 7 * 24 * 3600e3); }
            else if (this.period === '30d') { from = new Date(now.getTime() - 30 * 24 * 3600e3); }
            else return; // custom 은 applyCustom 이 정한다
            this.fromMs = from.getTime();
            this.toMs = to.getTime();
        },
        setPeriod(p) {
            this.period = p; this.rangeOpen = false;
            this.applyPeriod();
            this.loadSeries();
        },
        applyCustom() {
            const f = this.customFrom ? new Date(this.customFrom).getTime() : NaN;
            const t = this.customTo ? new Date(this.customTo).getTime() : NaN;
            if (!isFinite(f) || !isFinite(t) || t <= f) { this.flash('시작·종료 시각을 확인하세요.'); return; }
            this.period = 'custom'; this.fromMs = f; this.toMs = t; this.rangeOpen = false;
            this.loadSeries();
        },
        get rangeLabel() {
            if (!this.fromMs) return '';
            return this.fmtTime(this.fromMs) + '  ~  ' + this.fmtTime(this.toMs);
        },

        // ── 로딩 ──────────────────────────────────────────────────────────────
        async loadTags(first) {
            try {
                const res = await fetch('/api/tag-monitor/tags');
                if (!res.ok) throw new Error('HTTP ' + res.status);
                const d = await res.json();
                this.projectLoaded = d.projectLoaded !== false;
                this.tags = (d.tags || []).map(t => ({ ...t, key: (t.systemGuid || '') + '|' + t.address }));
                this.error = '';
                if (first) {
                    // 첫 진입 기본 선택 — 지금 필터(기본 모니터링TAG, ?system= 스코프)에서 값이 있는 것 최대 4개.
                    // 빈 화면으로 맞이하지 않는다.
                    const pick = this.filteredTags.filter(t => t.tagId).slice(0, 4);
                    this.selected = pick.map(t => t.tagId);
                    this.loaded = true;
                    if (this.selected.length) this.loadSeries();
                }
            } catch (e) {
                if (first) { this.error = '태그 목록을 불러오지 못했습니다: ' + e.message; this.loaded = true; }
            }
        },

        async loadSeries() {
            if (this.selected.length === 0) { this.series = []; this.destroyChart(); return; }
            const seq = ++seriesSeq;
            this.busy = true;
            try {
                const qs = new URLSearchParams({
                    tagIds: this.selected.join(','),
                    from: new Date(this.fromMs).toISOString(),
                    to: new Date(this.toMs).toISOString(),
                });
                const res = await fetch('/api/tag-monitor/series?' + qs.toString());
                if (!res.ok) throw new Error('HTTP ' + res.status);
                const d = await res.json();
                if (seq !== seriesSeq) return;      // 더 새로운 요청이 이미 나갔다
                this.series = d.series || [];
                this.evtPage = 0;
                this.error = '';
                this.$nextTick(() => this.renderChart());
            } catch (e) {
                if (seq === seriesSeq) this.flash('추이를 불러오지 못했습니다: ' + e.message);
            } finally {
                if (seq === seriesSeq) this.busy = false;
            }
        },

        reload() { this.applyPeriod(); this.loadTags(false); this.loadSeries(); },

        // ── 선택 ──────────────────────────────────────────────────────────────
        isSelected(t) { return !!t.tagId && this.selected.includes(t.tagId); },
        toggle(t) {
            if (!t.tagId) return;
            const i = this.selected.indexOf(t.tagId);
            if (i >= 0) this.selected.splice(i, 1);
            // 한 화면에 여덟 개를 넘기면 색도 축도 읽히지 않는다 — 서버 상한(20)과 별개의 화면 규칙.
            else if (this.selected.length >= 8) { this.flash('한 번에 최대 8개까지 볼 수 있습니다.'); return; }
            else this.selected.push(t.tagId);
            this.loadSeries();
        },
        selectVisible() {
            const ids = this.filteredTags.filter(t => t.tagId).slice(0, 8).map(t => t.tagId);
            this.selected = ids;
            this.loadSeries();
        },
        clearSelection() { this.selected = []; this.series = []; this.destroyChart(); },

        // ── 파생 ──────────────────────────────────────────────────────────────
        get systems() {
            return [...new Set(this.tags.map(t => t.systemName).filter(Boolean))].sort();
        },
        get filteredTags() {
            const q = (this.q || '').trim().toLowerCase();
            return this.tags.filter(t => {
                if (this.kind !== 'all' && t.level !== this.kind) return false;
                if (this.sysFilter && t.systemName !== this.sysFilter) return false;
                if (q && !((t.name || '') + ' ' + (t.address || '')).toLowerCase().includes(q)) return false;
                return true;
            });
        },
        get selectedTags() {
            return this.selected.map(id => this.tags.find(t => t.tagId === id)).filter(Boolean);
        },
        get numericSeries() { return this.series.filter(s => !this.isBit(s) && !this.isText(s)); },
        get bitSeries() { return this.series.filter(s => this.isBit(s)); },
        get anyTruncated() { return this.series.some(s => s.truncated); },
        // 축은 둘까지 나눠 준다 — 셋 이상이면 남는 단위가 왼쪽 축에 얹히므로 그때만 경고한다.
        get unitsMixed() {
            return new Set(this.numericSeries.map(s => s.unit || '')).size > 2;
        },
        // 선택 태그의 변화점을 시간 역순으로 합친 목록.
        get events() {
            const out = [];
            for (const s of this.series) {
                const color = this.colorOfSeries(s);
                for (const p of s.points || []) {
                    out.push({
                        key: s.tagId + '|' + p.atMs + '|' + out.length,
                        atMs: p.atMs, name: s.name, color,
                        display: this.displayPoint(p, s),
                    });
                }
            }
            out.sort((a, b) => b.atMs - a.atMs);
            return out;
        },
        get evtMaxPage() { return Math.max(1, Math.ceil(this.events.length / this.evtPageSize)); },
        get pagedEvents() {
            const s = this.evtPage * this.evtPageSize;
            return this.events.slice(s, s + this.evtPageSize);
        },

        isBit(s) { return (s.valueType || '').toLowerCase() === 'bit'; },
        isText(s) { return (s.valueType || '').toLowerCase() === 'string'; },

        // ── Bit 상태 띠 ───────────────────────────────────────────────────────
        // 계단값을 구간으로 바꾼다. 시작값(startValue)이 없으면 첫 기록 전까지는 "모름"으로 빗금 처리한다 —
        // 0 으로 칠하면 관측하지 않은 시간을 OFF 였다고 주장하는 셈이다.
        bitSegments(s) {
            const span = Math.max(1, this.toMs - this.fromMs);
            const pts = (s.points || []).slice();
            const segs = [];
            const pct = ms => ((ms - this.fromMs) / span) * 100;

            let cursor = this.fromMs;
            let on = null;
            if (s.startValue) on = this.pointOn(s.startValue);
            else if (pts.length > 0) {
                segs.push({ left: 0, width: Math.max(0.2, pct(pts[0].atMs)), unknown: true, title: '기록 없음(값 모름)' });
                cursor = pts[0].atMs;
                on = this.pointOn(pts[0]);
            }
            for (const p of pts) {
                if (p.atMs > cursor) {
                    if (on) segs.push({ left: pct(cursor), width: Math.max(0.2, pct(p.atMs) - pct(cursor)), unknown: false,
                                        title: 'ON ' + this.fmtTime(cursor) + ' ~ ' + this.fmtTime(p.atMs) });
                    cursor = p.atMs;
                }
                on = this.pointOn(p);
            }
            if (on && this.toMs > cursor)
                segs.push({ left: pct(cursor), width: Math.max(0.2, 100 - pct(cursor)), unknown: false,
                            title: 'ON ' + this.fmtTime(cursor) + ' ~ 현재' });
            return segs;
        },
        pointOn(p) {
            if (p.num != null) return p.num !== 0;
            const t = (p.text || '').trim().toLowerCase();
            return t === '1' || t === 'true' || t === 'on';
        },

        // ── 차트 ──────────────────────────────────────────────────────────────
        destroyChart() { if (numChart) { try { numChart.destroy(); } catch (e) { /* ignore */ } numChart = null; } },
        renderChart() {
            const list = this.numericSeries;
            this.destroyChart();
            if (list.length === 0) return;
            const el = this.$refs.numChart;
            if (!el || typeof Chart === 'undefined') return;

            const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
            const grid = css('--color-lines') || 'rgba(127,127,127,0.15)';
            const text = css('--color-text-secondary') || '#5b6b7d';

            // 단위가 다른 태그를 한 축에 얹으면 스케일이 큰 쪽이 작은 쪽을 바닥에 깔아 버린다(압력 4bar vs 카운터 1400ea).
            // 단위별로 묶어 축을 둘까지 나눈다. 셋 이상이면 남는 것은 첫 축으로 보내고 화면이 경고를 띄운다.
            const unitGroups = [...new Set(list.map(s => s.unit || ''))];
            const axisOf = unit => (unitGroups.length > 1 && unit === unitGroups[1]) ? 'y1' : 'y';
            const useRight = unitGroups.length > 1;

            const datasets = list.map(s => {
                const color = this.colorOfSeries(s);
                const data = [];
                // 계단의 시작점 — 구간 직전 마지막 값을 구간 시작 시각으로 당겨 찍는다.
                if (s.startValue && s.startValue.num != null) data.push({ x: this.fromMs, y: s.startValue.num });
                for (const p of s.points || []) if (p.num != null) data.push({ x: p.atMs, y: p.num });
                // 마지막 값은 구간 끝까지 유지된다(계단의 오른쪽 끝).
                if (data.length > 0) data.push({ x: this.toMs, y: data[data.length - 1].y });
                return {
                    label: s.name + (s.unit ? ' (' + s.unit + ')' : ''),
                    data, borderColor: color, backgroundColor: color, yAxisID: axisOf(s.unit || ''),
                    stepped: 'after', borderWidth: 1.6, pointRadius: 0, pointHitRadius: 6, tension: 0,
                };
            }).filter(d => d.data.length > 0);
            if (datasets.length === 0) return;

            numChart = new Chart(el.getContext('2d'), {
                type: 'line',
                data: { datasets },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    interaction: { mode: 'nearest', axis: 'x', intersect: false },
                    scales: {
                        x: {
                            type: 'time', min: this.fromMs, max: this.toMs,
                            time: { tooltipFormat: 'MM-dd HH:mm:ss' },
                            grid: { color: grid }, ticks: { color: text, maxRotation: 0, autoSkipPadding: 24 },
                        },
                        y: {
                            position: 'left', grid: { color: grid }, ticks: { color: text },
                            title: { display: !!unitGroups[0], text: unitGroups[0], color: text, font: { size: 10 } },
                        },
                        y1: {
                            display: useRight, position: 'right',
                            grid: { drawOnChartArea: false }, ticks: { color: text },
                            title: { display: !!unitGroups[1], text: unitGroups[1], color: text, font: { size: 10 } },
                        },
                    },
                    plugins: {
                        legend: { labels: { color: text, boxWidth: 10, boxHeight: 10, usePointStyle: true } },
                        tooltip: { callbacks: { label: c => c.dataset.label + ': ' + c.parsed.y } },
                    },
                },
            });
        },

        // ── 표기 ──────────────────────────────────────────────────────────────
        // 색 키는 목록·타일·차트·이력이 모두 같아야 한다 — 하나라도 다르면 같은 태그가 화면마다 다른 색이 된다.
        // 시계열 응답에는 systemGuid 가 없으므로 공통 분모인 (시스템명, 주소)로 맞춘다.
        colorKey(systemName, address) { return (systemName || '') + '|' + (address || ''); },
        colorOf(t) { return PALETTE[this.hash(this.colorKey(t.systemName, t.address)) % PALETTE.length]; },
        colorOfSeries(s) { return PALETTE[this.hash(this.colorKey(s.systemName, s.address)) % PALETTE.length]; },
        hash(str) {
            let h = 0;
            for (let i = 0; i < (str || '').length; i++) h = (h * 31 + str.charCodeAt(i)) >>> 0;
            return h;
        },
        displayPoint(p, s) {
            if (this.isBit(s)) return this.pointOn(p) ? 'ON' : 'OFF';
            if (p.num != null) return this.trimNum(p.num) + (s.unit ? ' ' + s.unit : '');
            return p.text == null ? '—' : p.text;
        },
        fmtValue(raw, valueType) {
            if (raw == null || raw === '') return '—';
            if ((valueType || '').toLowerCase() === 'bit') {
                const t = String(raw).trim().toLowerCase();
                return (t === '1' || t === 'true' || t === 'on') ? 'ON' : 'OFF';
            }
            const n = Number(raw);
            return isFinite(n) && String(raw).trim() !== '' ? this.trimNum(n) : String(raw);
        },
        trimNum(n) {
            if (!isFinite(n)) return '—';
            if (Number.isInteger(n)) return n.toLocaleString();
            return Number(n.toFixed(3)).toLocaleString();
        },
        fmtNum(n) { return n == null ? '—' : this.trimNum(n); },
        fmtTime(ms) {
            if (!ms) return '—';
            const d = new Date(ms);
            const p = v => String(v).padStart(2, '0');
            return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
        },
        // 지속시간 표기는 전역 규약(dspFmt)이 있으면 그것을 따른다 — 없으면 같은 모양으로 직접 만든다.
        fmtDur(ms) {
            if (window.dspFmt && typeof window.dspFmt.duration === 'function') return window.dspFmt.duration(ms);
            const s = Math.floor(ms / 1000);
            if (s < 60) return s + '초';
            const m = Math.floor(s / 60), rs = s % 60;
            if (m < 60) return m + '분 ' + rs + '초';
            const h = Math.floor(m / 60), rm = m % 60;
            if (h < 24) return h + '시간 ' + rm + '분';
            return Math.floor(h / 24) + '일 ' + (h % 24) + '시간';
        },
        flash(msg) {
            this.toast = msg;
            setTimeout(() => { this.toast = ''; }, 3200);
        },
    };
}
