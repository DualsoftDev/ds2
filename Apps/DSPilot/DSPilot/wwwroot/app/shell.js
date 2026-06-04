/*
 * DSPilot 정적 셸 (Shared App-Shell) — Swiss Technical
 * ------------------------------------------------------------------
 * /app/*.html 정적 페이지에 사이드바 + 앱바(스위스 테크니컬)를 입힌다.
 * 전역 디자인 시스템(/css/swiss.css)의 .layout/.app-bar/.drawer/.main-content/
 * .nav-menu/.nav-link/.btn-icon/.dsp-brand + 사이드바 4-섹션 클래스를 사용한다.
 *
 * 포함 방법(매 페이지, alpine.min.js 보다 먼저):
 *   <script src="/app/shell.js" defer></script>
 *   <script defer src="/lib/alpine.min.js"></script>
 *
 * 사이드바 구성(Blazor NavMenu.razor 와 동일):
 *   1) 페이지 바로가기 — 정적 페이지 링크 + (옵션) PLC 디버그
 *   2) 라인요약        — 가동/대기 + 가동률 (/api/nav/summary 폴링)
 *   3) 시스템          — per-system flow 트리 (/api/nav 1회)
 *   4) agent 통신 상태 — Promaker Hub + PLC 어댑터 연결 (/api/nav/summary 폴링)
 *   + 마지막 갱신 푸터
 *
 * 동작 개요:
 *   - 테마: <html>+layout 에 dark-theme(localStorage 'dspilot-theme').
 *   - .dsp-page(Alpine 루트) 를 main-content 로 이동, 슬림 헤더 제거.
 *   - /api/nav      : showPlcDebug + 시스템/flow 트리 (구조, 1회).
 *   - /api/nav/summary : 라인요약·agent 통신·이상발생 건수 (4초 폴링).
 *
 * 의존성 없음(no framework). defer 라 실행 시점에 DOM 은 이미 파싱 완료.
 */
(function () {
    'use strict';

    try {
        // ── 1) 테마: <html> 에도 dark-theme 적용 ──
        var dark = localStorage.getItem('dspilot-theme') === 'dark';
        document.documentElement.classList.toggle('dark-theme', dark);

        // ── 2) Alpine 루트(.dsp-page) 탐색 ──
        var page = document.querySelector('.dsp-page');
        if (!page) return;

        var slimBar = page.querySelector('.dsp-appbar');
        if (slimBar) slimBar.remove();

        var pageTitle = (document.title || '')
            .replace(/\s*[—-]\s*DSPilot\s*$/, '')
            .trim();

        // ── 작은 DOM 헬퍼 ──
        function el(tag, className, text) {
            var e = document.createElement(tag);
            if (className) e.className = className;
            if (text != null) e.textContent = text;
            return e;
        }
        function pad2(n) { return (n < 10 ? '0' : '') + n; }
        function fmtTime(d) { return pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds()); }

        // ── 네비게이션 정의 (라우트/아이콘 — Blazor NavMenu 와 동일). 설정은 앱바 기어 아이콘. ──
        var NAV_ITEMS = [
            { label: '대시보드',     href: '/',                    icon: 'space_dashboard', match: 'all',    legacy: '/app/dashboard.html' },
            { label: '동작편차',     href: '/heatmap',             icon: 'gradient',        match: 'prefix', legacy: '/app/heatmap.html' },
            { label: '사이클 분석',  href: '/cycle-time-analysis', icon: 'equalizer',       match: 'prefix', legacy: '/app/cycle-time-analysis.html' },
            { label: '가동시간·이상',  href: '/uptime',     icon: 'monitor_heart',   match: 'prefix', legacy: '/app/uptime.html' },
            { label: 'CCTV',         href: '/cctv',                icon: 'videocam',        match: 'prefix', legacy: '/app/cctv.html' }
        ];
        var PLC_DEBUG_ITEM = { label: 'PLC 디버그', href: '/plc-debug', icon: 'bug_report', match: 'prefix', legacy: '/app/plc-debug.html' };

        var path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        function isActive(item) {
            var candidates = [item.href, item.legacy].filter(Boolean).map(function (p) {
                return p.replace(/\/+$/, '') || '/';
            });
            for (var i = 0; i < candidates.length; i++) {
                var c = candidates[i];
                if (item.match === 'all') { if (path === c) return true; }
                else { if (path === c || path.indexOf(c + '/') === 0) return true; }
            }
            return false;
        }

        // material-icons + 라벨로 구성된 .nav-link <a> 생성
        function buildNavLink(item) {
            var a = document.createElement('a');
            a.className = 'nav-link' + (isActive(item) ? ' active' : '');
            a.href = item.href;
            var icon = el('span', 'material-icons');
            icon.textContent = item.icon;
            a.appendChild(icon);
            a.appendChild(document.createTextNode(item.label));
            return a;
        }
        function sectionHead(label) {
            var head = el('div', 'nav-section-head');
            head.appendChild(el('span', 'eyebrow', label));
            return head;
        }

        // ── 3) 셸 DOM 구성 ──
        var layout = el('div', 'layout' + (dark ? ' dark-theme' : ''));

        // header.app-bar
        var appBar = el('header', 'app-bar');

        var hamburger = el('button', 'btn-icon');
        hamburger.title = '메뉴';
        hamburger.innerHTML = '<span class="material-icons">menu</span>';

        var brand = el('a', 'dsp-brand');
        brand.href = '/';
        brand.title = 'DSPilot';
        var brandLogo = el('img');
        brandLogo.src = '/images/logo.png';
        brandLogo.alt = 'DSPilot';
        brand.appendChild(brandLogo);

        var titleSpan = el('span', 'header-page-title', pageTitle);
        var spacer = el('div', 'spacer');

        // 테마 전환·설정 버튼은 앱바에서 제거됨:
        //   - 테마 전환 → 설정 페이지(/settings)의 "사용자 인터페이스" 카드로 이관
        //   - 설정      → 사이드바 "설정" 섹션 링크로 접근
        appBar.appendChild(hamburger);
        appBar.appendChild(brand);
        appBar.appendChild(titleSpan);
        appBar.appendChild(spacer);

        // aside.drawer > nav.nav-menu
        var drawer = el('aside', 'drawer');
        var navMenu = el('nav', 'nav-menu');
        drawer.appendChild(navMenu);

        // main.main-content
        var main = el('main', 'main-content');

        layout.appendChild(appBar);
        layout.appendChild(drawer);
        layout.appendChild(main);

        // .dsp-page 를 main 안으로 이동 (Alpine init 전이라 안전)
        page.classList.add('dsp-in-shell');
        var style = el('style');
        style.textContent =
            '.dsp-in-shell{min-height:0 !important}' +
            '.dsp-in-shell .dsp-appbar{display:none}';
        document.head.appendChild(style);

        document.body.insertBefore(layout, page);
        main.appendChild(page);

        // ── 4) 사이드바 4-섹션 스캐폴드 ──

        // (1) 페이지 바로가기
        navMenu.appendChild(sectionHead('페이지 바로가기'));
        var anomalyLink = null;
        NAV_ITEMS.forEach(function (item) {
            var link = buildNavLink(item);
            if (item.href === '/uptime') anomalyLink = link;
            navMenu.appendChild(link);
        });

        // (2) 라인요약 — 가동/대기 + 가동률
        var lineSummaryHead = sectionHead('라인요약');
        navMenu.appendChild(lineSummaryHead);
        var lsBody = el('div', 'nav-section-body line-summary');
        var lsRunning = el('b', null, '0');
        var lsIdle = el('b', null, '0');
        var counts = el('div', 'ls-counts');
        var runWrap = el('span', 'ls-count ls-running');
        runWrap.appendChild(el('span', 'ls-dot'));
        runWrap.appendChild(document.createTextNode('가동 '));
        runWrap.appendChild(lsRunning);
        var idleWrap = el('span', 'ls-count ls-idle');
        idleWrap.appendChild(el('span', 'ls-dot'));
        idleWrap.appendChild(document.createTextNode('대기 '));
        idleWrap.appendChild(lsIdle);
        counts.appendChild(runWrap);
        counts.appendChild(idleWrap);
        var effWrap = el('div');
        var effRow = el('div', 'ls-eff-row');
        effRow.appendChild(el('span', null, '라인 효율'));
        var lsEffVal = el('span', 'ls-eff-val', '—');
        effRow.appendChild(lsEffVal);
        var bar = el('div', 'progress-bar');
        var lsEffFill = el('div', 'progress-bar-fill');
        lsEffFill.style.width = '0%';
        bar.appendChild(lsEffFill);
        effWrap.appendChild(effRow);
        effWrap.appendChild(bar);
        lsBody.appendChild(counts);
        lsBody.appendChild(effWrap);
        navMenu.appendChild(lsBody);

        // (3) 시스템 — flow 트리 (/api/nav 로 채움)
        navMenu.appendChild(sectionHead('시스템'));
        var systemContainer = el('div');
        navMenu.appendChild(systemContainer);

        // (4) 설정
        navMenu.appendChild(sectionHead('설정'));
        var settingsNavLink = buildNavLink({ label: '설정', href: '/settings', icon: 'settings', match: 'prefix', legacy: '/app/settings.html' });
        navMenu.appendChild(settingsNavLink);

        // (5) agent 통신 상태 — Hub / PLC
        navMenu.appendChild(sectionHead('agent 통신 상태'));
        var agBody = el('div', 'nav-section-body agent-status');
        function agentRow(label) {
            var row = el('div', 'ag-row');
            var dot = el('span', 'ag-dot');
            var lab = el('span', 'ag-label', label);
            var state = el('span', 'ag-state', '—');
            row.appendChild(dot);
            row.appendChild(lab);
            row.appendChild(state);
            return { row: row, dot: dot, state: state };
        }
        var hubRow = agentRow('Promaker Hub');
        var plcRow = agentRow('PLC 어댑터');
        agBody.appendChild(hubRow.row);
        agBody.appendChild(plcRow.row);
        navMenu.appendChild(agBody);

        // 마지막 갱신 푸터
        var updated = el('div', 'nav-updated');
        var updatedDot = el('span', 'nav-updated-dot');
        updated.appendChild(updatedDot);
        updated.appendChild(document.createTextNode('마지막 갱신 '));
        var updatedTime = el('b', null, '—');
        updated.appendChild(updatedTime);
        navMenu.appendChild(updated);

        // ── 5) 시스템 트리 빌더 ──
        var selectedFlow = null;
        try {
            if (path === '/flow' || path === '/app/flow.html') {
                var qp = new URLSearchParams(location.search);
                selectedFlow = qp.get('name');
            }
        } catch (e) { /* ignore */ }

        function buildSystemTree(systems) {
            systemContainer.textContent = '';
            if (!systems || systems.length === 0) {
                systemContainer.appendChild(el('div', 'nav-section-empty', '프로젝트 미로드'));
                return;
            }
            systems.forEach(function (sys) {
                var head = el('div', 'nav-sys');
                var aas = el('img');
                aas.src = '/images/aas.png';
                aas.alt = 'AAS';
                head.appendChild(aas);
                head.appendChild(el('span', 'nav-sys-name', sys.name));
                systemContainer.appendChild(head);

                var flows = sys.flows || [];
                var wrap = el('div', 'nav-flows');
                flows.forEach(function (flowName, i) {
                    var isLast = i === flows.length - 1;
                    var isSelected = selectedFlow === flowName;
                    var a = el('a', 'nav-flow' + (isSelected ? ' is-selected' : '') + (isLast ? ' is-last' : ''));
                    a.href = '/flow?name=' + encodeURIComponent(flowName);
                    a.appendChild(el('span', 'nav-flow-line-v'));
                    a.appendChild(el('span', 'nav-flow-line-h'));
                    a.appendChild(el('span', 'nav-flow-name', flowName));
                    wrap.appendChild(a);
                });
                systemContainer.appendChild(wrap);
            });
        }

        // ── 6) /api/nav: ShowPlcDebug + per-system flow 트리 (1회) ──
        fetch('/api/nav', { headers: { 'Accept': 'application/json' } })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (data) {
                if (!data) { buildSystemTree([]); return; }
                if (data.showPlcDebug) {
                    navMenu.insertBefore(buildNavLink(PLC_DEBUG_ITEM), lineSummaryHead);
                }
                buildSystemTree(data.systems || []);
            })
            .catch(function () { buildSystemTree([]); });

        // ── 7) /api/nav/summary: 라인요약 + agent 통신 + 이상발생 (주기 폴링) ──
        var HUB_INFO = {
            connected:    ['ok', '정상', 'Promaker 모니터링 허브 연결됨'],
            connecting:   ['pending', '연결 중', 'Promaker 허브 연결 시도 중'],
            reconnecting: ['pending', '재연결 중', 'Promaker 허브 재연결 중'],
            disconnected: ['down', '끊김', 'Promaker 5051 모니터링 허브 연결 없음 — Promaker 실행/모니터링 확인']
        };

        function setRow(r, level, label) {
            r.row.className = 'ag-row is-' + level;
            r.dot.className = 'ag-dot ag-' + level;
            r.state.textContent = label;
        }

        function plcTooltip(agent) {
            var adapters = agent.adapters || [];
            if (agent.hub !== 'connected') return '허브 연결 후 PLC 상태가 표시됩니다';
            if (!adapters.length) return '보고된 PLC 어댑터가 없습니다';
            var down = adapters.filter(function (a) { return !a.connected; });
            if (down.length) {
                return down.map(function (a) {
                    return a.name + ' (' + a.vendor + ' ' + a.ip + ':' + a.port + ') — ' + (a.error || '');
                }).join('\n');
            }
            return adapters.map(function (a) { return a.name + ' (' + a.ip + ':' + a.port + ')'; }).join('\n');
        }

        function setAnomalyBadge(count) {
            if (!anomalyLink) return;
            var badge = anomalyLink.querySelector('.nav-link-badge');
            if (count > 0) {
                if (!badge) {
                    badge = el('span', 'nav-link-badge');
                    badge.title = '최근 10분 내 Error 알림';
                    anomalyLink.appendChild(badge);
                }
                badge.textContent = count;
            } else if (badge) {
                badge.remove();
            }
        }

        function applySummary(data) {
            // 라인요약
            var lines = data.lines || {};
            var total = lines.total || 0;
            lsRunning.textContent = lines.running || 0;
            lsIdle.textContent = lines.idle || 0;
            var eff = lines.efficiencyPct || 0;
            lsEffVal.textContent = total > 0 ? eff + '%' : '—';
            lsEffFill.style.width = (total > 0 ? eff : 0) + '%';

            // agent 통신
            var agent = data.agent || {};
            var hubInfo = HUB_INFO[agent.hub] || HUB_INFO.disconnected;
            setRow(hubRow, hubInfo[0], hubInfo[1]);
            hubRow.row.title = hubInfo[2];

            var hub = agent.hub, plcTotal = agent.plcTotal || 0, plcDown = agent.plcDisconnected || 0;
            var lvl, lbl;
            if (hub !== 'connected') { lvl = 'muted'; lbl = '—'; }
            else if (plcTotal === 0) { lvl = 'muted'; lbl = '보고 없음'; }
            else if (plcDown > 0) { lvl = 'down'; lbl = plcDown + '대 끊김'; }
            else { lvl = 'ok'; lbl = plcTotal + '대 연결'; }
            setRow(plcRow, lvl, lbl);
            plcRow.row.title = plcTooltip(agent);

            // 이상발생 배지
            setAnomalyBadge(data.anomalyActiveCount || 0);

            // 마지막 갱신 (서버 시각 파싱 실패 시 현재 시각으로 폴백 — NaN:NaN:NaN 방지)
            var t = new Date();
            if (data.serverTimeUtc) {
                var parsed = new Date(data.serverTimeUtc);
                if (!isNaN(parsed.getTime())) t = parsed;
            }
            updatedTime.textContent = fmtTime(t);
            updatedDot.classList.toggle('is-stale', !!(agent.hub && agent.hub !== 'connected'));
        }

        function pollSummary() {
            fetch('/api/nav/summary', { headers: { 'Accept': 'application/json' } })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (data) { if (data) applySummary(data); })
                .catch(function () { /* 폴링 실패 시 마지막 값 유지 */ });
        }
        pollSummary();
        setInterval(pollSummary, 4000);

        // ── 8) 테마 동기화 (변경은 설정 페이지에서 수행; 여기서는 다른 탭·페이지 반영만) ──
        window.addEventListener('storage', function (e) {
            if (e.key !== 'dspilot-theme') return;
            var d = e.newValue === 'dark';
            document.documentElement.classList.toggle('dark-theme', d);
            layout.classList.toggle('dark-theme', d);
        });

        // ── 9) drawer 토글 (햄버거) ──
        function setDrawerClosed(closed) {
            drawer.classList.toggle('drawer-closed', closed);
            main.classList.toggle('drawer-closed', closed);
        }
        hamburger.addEventListener('click', function () {
            var closed = !drawer.classList.contains('drawer-closed');
            setDrawerClosed(closed);
        });
        setDrawerClosed(false);

    } catch (err) {
        try { console.error('[shell.js] failed to build app-shell', err); } catch (e) { /* ignore */ }
    }
})();
