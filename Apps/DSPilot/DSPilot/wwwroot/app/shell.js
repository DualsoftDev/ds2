/*
 * DSPilot 정적 셸 (Shared App-Shell) — Swiss Technical
 * ------------------------------------------------------------------
 * /app/*.html 정적 페이지에 사이드바 + 앱바(스위스 테크니컬)를 입힌다.
 * 전역 디자인 시스템(/css/swiss.css)의 .layout/.app-bar/.drawer/.main-content/
 * .nav-menu/.nav-link/.btn-icon/.dsp-brand 를 사용한다.
 *
 * 포함 방법(매 페이지, alpine.min.js 보다 먼저):
 *   <script src="/app/shell.js" defer></script>
 *   <script defer src="/lib/alpine.min.js"></script>
 *
 * 동작 개요:
 *   1) <html> 에 dark-theme 적용 — localStorage 'dspilot-theme' 기준.
 *   2) .dsp-page(Alpine 루트)를 찾고, 페이지의 슬림 헤더 .dsp-appbar 를 제거.
 *   3) .layout > (app-bar + drawer + main-content) 셸 DOM 생성, .dsp-page 를 main-content 로 이동.
 *   4) /api/nav 로 시스템·flow 트리 + ShowPlcDebug 를 가져와 사이드바 렌더.
 *   5) 테마 토글 → localStorage + dark-theme 토글 + StorageEvent 발행(페이지 Alpine 동기화).
 *   6) 햄버거 → drawer-closed 토글.
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

        // ── 네비게이션 정의 (라우트/아이콘 — Blazor NavMenu 와 동일) ──
        var NAV_ITEMS = [
            { label: '대시보드',     href: '/',                    icon: 'space_dashboard', match: 'all',    legacy: '/app/dashboard.html' },
            { label: '동작편차',     href: '/heatmap',             icon: 'gradient',        match: 'prefix', legacy: '/app/heatmap.html' },
            { label: '사이클 분석',  href: '/cycle-time-analysis', icon: 'equalizer',       match: 'prefix', legacy: '/app/cycle-time-analysis.html' },
            { label: 'Call 사이클',  href: '/call-test',           icon: 'play_circle',     match: 'prefix', legacy: '/app/call-test.html' },
            { label: '이상발생 관리', href: '/user-tags',          icon: 'crisis_alert',    match: 'prefix', legacy: '/app/user-tags.html' },
            { label: 'CCTV',         href: '/cctv',                icon: 'videocam',        match: 'prefix', legacy: '/app/cctv.html' }
        ];
        var SETTINGS_ITEM = { label: '설정', href: '/settings', icon: 'settings', match: 'prefix', legacy: '/app/settings.html' };
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
            var icon = document.createElement('span');
            icon.className = 'material-icons';
            icon.textContent = item.icon;
            a.appendChild(icon);
            a.appendChild(document.createTextNode(item.label));
            return a;
        }

        // ── 3) 셸 DOM 구성 ──
        var layout = document.createElement('div');
        layout.className = 'layout' + (dark ? ' dark-theme' : '');

        // header.app-bar
        var appBar = document.createElement('header');
        appBar.className = 'app-bar';

        // 햄버거 (drawer 토글)
        var hamburger = document.createElement('button');
        hamburger.className = 'btn-icon';
        hamburger.title = '메뉴';
        hamburger.innerHTML = '<span class="material-icons">menu</span>';

        // 브랜드 워드마크 → "/"  (스위스: 레드 사각 마크 + DSPilot)
        var brand = document.createElement('a');
        brand.className = 'dsp-brand';
        brand.href = '/';
        brand.title = 'DSPilot';
        var mark = document.createElement('span');
        mark.className = 'dsp-brand-mark';
        var word = document.createElement('span');
        word.className = 'dsp-brand-word';
        word.innerHTML = 'DS<b>Pilot</b>';
        brand.appendChild(mark);
        brand.appendChild(word);

        // 페이지 제목 (아이브로우)
        var titleSpan = document.createElement('span');
        titleSpan.className = 'header-page-title';
        titleSpan.textContent = pageTitle;

        var spacer = document.createElement('div');
        spacer.className = 'spacer';

        // 테마 토글
        var themeBtn = document.createElement('button');
        themeBtn.className = 'btn-icon';
        themeBtn.title = '테마 전환';
        var themeIcon = document.createElement('span');
        themeIcon.className = 'material-icons';
        themeIcon.textContent = dark ? 'light_mode' : 'dark_mode';
        themeBtn.appendChild(themeIcon);

        // 설정
        var settingsBtn = document.createElement('a');
        settingsBtn.className = 'btn-icon';
        settingsBtn.title = '설정';
        settingsBtn.href = '/settings';
        settingsBtn.innerHTML = '<span class="material-icons">settings</span>';

        appBar.appendChild(hamburger);
        appBar.appendChild(brand);
        appBar.appendChild(titleSpan);
        appBar.appendChild(spacer);
        appBar.appendChild(themeBtn);
        appBar.appendChild(settingsBtn);

        // aside.drawer > nav.nav-menu
        var drawer = document.createElement('aside');
        drawer.className = 'drawer';
        var navMenu = document.createElement('nav');
        navMenu.className = 'nav-menu';
        drawer.appendChild(navMenu);

        // main.main-content
        var main = document.createElement('main');
        main.className = 'main-content';

        layout.appendChild(appBar);
        layout.appendChild(drawer);
        layout.appendChild(main);

        // .dsp-page 를 main 안으로 이동 (Alpine init 전이라 안전)
        page.classList.add('dsp-in-shell');
        var style = document.createElement('style');
        style.textContent =
            '.dsp-in-shell{min-height:0 !important}' +
            '.dsp-in-shell .dsp-appbar{display:none}';
        document.head.appendChild(style);

        document.body.insertBefore(layout, page);
        main.appendChild(page);

        // ── 기본 nav 링크 렌더 ──
        NAV_ITEMS.forEach(function (item) { navMenu.appendChild(buildNavLink(item)); });

        var settingsLink = buildNavLink(SETTINGS_ITEM);
        navMenu.appendChild(settingsLink);

        // ── 4) /api/nav: ShowPlcDebug + per-system flow 트리 ──
        var selectedFlow = null;
        try {
            if (path === '/flow' || path === '/app/flow.html') {
                var qp = new URLSearchParams(location.search);
                selectedFlow = qp.get('name');
            }
        } catch (e) { /* ignore */ }

        fetch('/api/nav', { headers: { 'Accept': 'application/json' } })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (data) {
                if (!data) return;

                if (data.showPlcDebug) {
                    navMenu.insertBefore(buildNavLink(PLC_DEBUG_ITEM), settingsLink);
                }

                var systems = data.systems || [];
                if (systems.length === 0) return;

                var divider = document.createElement('hr');
                divider.className = 'divider';
                divider.style.margin = '14px 18px';
                navMenu.insertBefore(divider, settingsLink);

                systems.forEach(function (sys) {
                    // 시스템 헤더 — 아이브로우 스타일
                    var header = document.createElement('div');
                    header.style.padding = '6px 22px 4px';
                    header.style.display = 'flex';
                    header.style.alignItems = 'center';
                    header.style.gap = '8px';
                    header.style.fontSize = '0.64rem';
                    header.style.fontWeight = '700';
                    header.style.textTransform = 'uppercase';
                    header.style.letterSpacing = '0.14em';
                    header.style.color = 'var(--color-text-secondary)';
                    var aas = document.createElement('img');
                    aas.src = '/images/aas.png';
                    aas.alt = 'AAS';
                    aas.style.width = '16px';
                    aas.style.height = '16px';
                    aas.style.objectFit = 'contain';
                    header.appendChild(aas);
                    header.appendChild(document.createTextNode(sys.name));
                    navMenu.insertBefore(header, settingsLink);

                    var flows = sys.flows || [];
                    var indent = document.createElement('div');
                    indent.style.marginLeft = '30px';
                    indent.style.marginBottom = '4px';
                    flows.forEach(function (flowName, i) {
                        var isLast = i === flows.length - 1;
                        var isSelected = selectedFlow === flowName;
                        var btn = document.createElement('a');
                        btn.href = '/flow?name=' + encodeURIComponent(flowName);
                        btn.style.position = 'relative';
                        btn.style.padding = '5px 0 5px 16px';
                        btn.style.cursor = 'pointer';
                        btn.style.textDecoration = 'none';
                        btn.style.display = 'block';
                        btn.style.width = '100%';
                        btn.style.border = '0';
                        btn.style.textAlign = 'left';
                        btn.style.background = isSelected ? 'color-mix(in srgb, var(--color-primary) 9%, transparent)' : 'transparent';

                        var vLine = document.createElement('span');
                        vLine.style.position = 'absolute';
                        vLine.style.left = '0';
                        vLine.style.top = '0';
                        vLine.style.width = '1px';
                        vLine.style.height = isLast ? '50%' : '100%';
                        vLine.style.background = 'var(--color-lines)';
                        var hLine = document.createElement('span');
                        hLine.style.position = 'absolute';
                        hLine.style.left = '0';
                        hLine.style.top = '50%';
                        hLine.style.width = '12px';
                        hLine.style.height = '1px';
                        hLine.style.background = 'var(--color-lines)';

                        var label = document.createElement('span');
                        label.style.fontSize = '0.8rem';
                        label.style.fontWeight = isSelected ? '700' : '500';
                        label.style.color = isSelected ? 'var(--color-primary)' : 'var(--color-drawer-text)';
                        label.textContent = flowName;

                        btn.appendChild(vLine);
                        btn.appendChild(hLine);
                        btn.appendChild(label);
                        indent.appendChild(btn);
                    });
                    navMenu.insertBefore(indent, settingsLink);
                });
            })
            .catch(function () { /* nav 실패해도 기본 링크 유지 */ });

        // ── 5) 테마 토글 ──
        themeBtn.addEventListener('click', function () {
            var next = !document.documentElement.classList.contains('dark-theme');
            localStorage.setItem('dspilot-theme', next ? 'dark' : 'light');
            document.documentElement.classList.toggle('dark-theme', next);
            layout.classList.toggle('dark-theme', next);
            themeIcon.textContent = next ? 'light_mode' : 'dark_mode';
            window.dispatchEvent(new StorageEvent('storage', {
                key: 'dspilot-theme',
                newValue: next ? 'dark' : 'light'
            }));
        });

        window.addEventListener('storage', function (e) {
            if (e.key !== 'dspilot-theme') return;
            var d = e.newValue === 'dark';
            document.documentElement.classList.toggle('dark-theme', d);
            layout.classList.toggle('dark-theme', d);
            themeIcon.textContent = d ? 'light_mode' : 'dark_mode';
        });

        // ── 6) drawer 토글 (햄버거) ──
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
