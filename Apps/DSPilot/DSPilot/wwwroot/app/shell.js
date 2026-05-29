/*
 * DSPilot 정적 셸 (Shared App-Shell)
 * ------------------------------------------------------------------
 * /app/*.html 정적 페이지에 Blazor MainLayout 과 동일한 사이드바 + 앱바를 입힌다.
 * 전역 CSS(/css/components.css)의 .layout/.app-bar/.drawer/.main-content/.nav-menu/.nav-link/.btn-icon
 * 을 그대로 재사용하므로 Blazor 와 픽셀 동일하게 보인다.
 *
 * 포함 방법(매 페이지, alpine.min.js 보다 먼저):
 *   <script src="/app/shell.js" defer></script>
 *   <script defer src="/lib/alpine.min.js"></script>
 *
 * 동작 개요:
 *   1) <html> 에 dark-theme 적용(셸 chrome 도 다크 토큰을 받도록) — localStorage 'dspilot-theme' 기준.
 *   2) .dsp-page(Alpine 루트)를 찾고, 페이지의 슬림 헤더 .dsp-appbar 를 제거.
 *   3) .layout > (app-bar + drawer + main-content) 셸 DOM 생성, .dsp-page 를 main-content 로 이동.
 *      (alpine.min.js 가 아직 평가 전이므로 노드 이동해도 x-data init 은 정상 동작)
 *   4) /api/nav 로 시스템·flow 트리 + ShowPlcDebug 를 가져와 사이드바 렌더.
 *   5) 테마 토글 → localStorage + dark-theme 토글 + StorageEvent 발행(페이지 Alpine 리스너 동기화).
 *   6) 햄버거 → .layout 에 drawer-closed 토글(MainLayout 의 .drawer.drawer-closed / .main-content.drawer-closed).
 *
 * 의존성 없음(no framework). defer 라 실행 시점에 DOM 은 이미 파싱 완료.
 */
(function () {
    'use strict';

    try {
        // ── 1) 테마: <html> 에도 dark-theme 적용 (셸 chrome 은 .dsp-page 밖이므로 직접 적용) ──
        var dark = localStorage.getItem('dspilot-theme') === 'dark';
        document.documentElement.classList.toggle('dark-theme', dark);

        // ── 2) Alpine 루트(.dsp-page) 탐색 ──
        var page = document.querySelector('.dsp-page');
        if (!page) return; // 방어적: 셸 대상 페이지가 아니면 아무것도 하지 않음.

        // 페이지의 슬림 헤더 제거 (셸 app-bar 로 대체)
        var slimBar = page.querySelector('.dsp-appbar');
        if (slimBar) slimBar.remove();

        // ── 페이지 제목 (document.title 에서 " — DSPilot" / " - DSPilot" 접미사 제거) ──
        var pageTitle = (document.title || '')
            .replace(/\s*[—-]\s*DSPilot\s*$/, '')
            .trim();

        // ── 네비게이션 정의 (NavMenu.razor 순서/아이콘 동일) ──
        // legacy: 정적 페이지 경로 (/app/*.html) 도 같은 페이지로 간주하여 active 매칭.
        var NAV_ITEMS = [
            { label: '대시보드',     href: '/',                       icon: 'space_dashboard', match: 'all',    legacy: '/app/dashboard.html' },
            { label: '동작편차',     href: '/heatmap',                icon: 'gradient',        match: 'prefix', legacy: '/app/heatmap.html' },
            { label: '사이클 분석',  href: '/cycle-time-analysis',    icon: 'equalizer',       match: 'prefix', legacy: '/app/cycle-time-analysis.html' },
            { label: 'Call 사이클',  href: '/call-test',              icon: 'play_circle',     match: 'prefix', legacy: '/app/call-test.html' },
            { label: '이상발생 관리', href: '/user-tags',             icon: 'crisis_alert',    match: 'prefix', legacy: '/app/user-tags.html' },
            { label: 'CCTV',         href: '/cctv',                   icon: 'videocam',        match: 'prefix', legacy: '/app/cctv.html' }
            // PLC 디버그(showPlcDebug 시) + 설정은 /api/nav 응답 후 동적 삽입.
        ];
        var SETTINGS_ITEM = { label: '설정', href: '/settings', icon: 'settings', match: 'prefix', legacy: '/app/settings.html' };
        var PLC_DEBUG_ITEM = { label: 'PLC 디버그', href: '/plc-debug', icon: 'bug_report', match: 'prefix', legacy: '/app/plc-debug.html' };

        // ── active 판정: 현재 경로를 canonical 과 legacy 둘 다 비교 ──
        var path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        function isActive(item) {
            var candidates = [item.href, item.legacy].filter(Boolean).map(function (p) {
                return p.replace(/\/+$/, '') || '/';
            });
            for (var i = 0; i < candidates.length; i++) {
                var c = candidates[i];
                if (item.match === 'all') {
                    if (path === c) return true;
                } else {
                    // prefix: 정확히 일치하거나 하위 경로
                    if (path === c || path.indexOf(c + '/') === 0) return true;
                }
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

        // 로고 → "/"
        var logoLink = document.createElement('a');
        logoLink.href = '/';
        logoLink.style.display = 'flex';
        logoLink.style.alignItems = 'center';
        logoLink.style.cursor = 'pointer';
        var logo = document.createElement('img');
        logo.src = '/images/logo.png';
        logo.alt = 'DSPilot Logo';
        logo.height = 28;
        logoLink.appendChild(logo);

        // 페이지 제목
        var titleSpan = document.createElement('span');
        titleSpan.className = 'header-page-title';
        titleSpan.textContent = pageTitle;

        // spacer
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

        // 설정 (app-bar 우측 아이콘)
        var settingsBtn = document.createElement('a');
        settingsBtn.className = 'btn-icon';
        settingsBtn.title = '설정';
        settingsBtn.href = '/settings';
        settingsBtn.innerHTML = '<span class="material-icons">settings</span>';

        appBar.appendChild(hamburger);
        appBar.appendChild(logoLink);
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

        // main.main-content (여기로 .dsp-page 이동)
        var main = document.createElement('main');
        main.className = 'main-content';

        layout.appendChild(appBar);
        layout.appendChild(drawer);
        layout.appendChild(main);

        // .dsp-page 를 main 안으로 이동(노드 이동: Alpine init 전이라 안전)
        // 셸 안에서는 .dsp-page 의 min-height:100vh 가 .main-content 내부 이중 스크롤을 만들므로 무력화.
        page.classList.add('dsp-in-shell');
        var style = document.createElement('style');
        style.textContent =
            '.dsp-in-shell{min-height:0 !important}' +
            '.dsp-in-shell .dsp-appbar{display:none}';
        document.head.appendChild(style);

        // 셸을 body 최상단에 삽입한 뒤 page 를 main 으로 이동.
        // (page 는 보통 body 직속이므로 먼저 layout 을 붙이고 page 를 옮긴다.)
        document.body.insertBefore(layout, page);
        main.appendChild(page);

        // ── 기본 nav 링크 렌더 (대시보드~CCTV) ──
        function renderBaseNav() {
            NAV_ITEMS.forEach(function (item) {
                navMenu.appendChild(buildNavLink(item));
            });
        }
        renderBaseNav();

        // 설정은 항상 마지막. PLC 디버그(옵션)는 설정 앞.
        // /api/nav 응답 전에도 설정 링크는 보이도록 일단 추가하고, 응답 후 PLC 디버그를 그 앞에 삽입.
        var settingsLink = buildNavLink(SETTINGS_ITEM);
        navMenu.appendChild(settingsLink);

        // ── 4) /api/nav: ShowPlcDebug + per-system flow 트리 ──
        // 현재 선택된 flow (NavMenu 의 _selectedFlow 동작) — /flow?name=... 일 때 강조.
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

                // PLC 디버그: showPlcDebug 시 설정 링크 앞에 삽입.
                if (data.showPlcDebug) {
                    navMenu.insertBefore(buildNavLink(PLC_DEBUG_ITEM), settingsLink);
                }

                // per-system flow 트리는 설정 위, 구분선 다음에 그린다.
                // NavMenu 는 디바이더 → 시스템 헤더 → 들여쓴 flow 버튼 순서.
                var systems = data.systems || [];
                if (systems.length === 0) return;

                var divider = document.createElement('hr');
                divider.className = 'divider';
                divider.style.margin = '8px 0';
                navMenu.insertBefore(divider, settingsLink);

                systems.forEach(function (sys) {
                    // 시스템 헤더 (typo-h6 + aas.png) — NavMenu.razor inline 스타일 미러.
                    var header = document.createElement('div');
                    header.className = 'typo-h6';
                    header.style.padding = '8px 16px 4px';
                    header.style.fontWeight = '700';
                    header.style.display = 'flex';
                    header.style.alignItems = 'center';
                    header.style.gap = '8px';
                    var aas = document.createElement('img');
                    aas.src = '/images/aas.png';
                    aas.alt = 'AAS';
                    aas.style.width = '20px';
                    aas.style.height = '20px';
                    aas.style.objectFit = 'contain';
                    header.appendChild(aas);
                    header.appendChild(document.createTextNode(sys.name));
                    navMenu.insertBefore(header, settingsLink);

                    // flow 목록 (들여쓰기 컨테이너)
                    var flows = sys.flows || [];
                    var indent = document.createElement('div');
                    indent.style.marginLeft = '24px';
                    flows.forEach(function (flowName, i) {
                        var isLast = i === flows.length - 1;
                        var isSelected = selectedFlow === flowName;
                        var btn = document.createElement('a');
                        btn.href = '/flow?name=' + encodeURIComponent(flowName);
                        btn.style.position = 'relative';
                        btn.style.padding = '4px 0 4px 16px';
                        btn.style.cursor = 'pointer';
                        btn.style.borderRadius = '4px';
                        btn.style.textDecoration = 'none';
                        btn.style.display = 'block';
                        btn.style.width = '100%';
                        btn.style.border = '0';
                        btn.style.textAlign = 'left';
                        btn.style.background = isSelected ? 'rgba(var(--color-primary-rgb), 0.15)' : 'transparent';

                        // 트리 라인(수직 + 수평) — NavMenu.razor 와 동일.
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
                        label.style.fontSize = '0.875rem';
                        label.style.fontWeight = isSelected ? '700' : '400';
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
            .catch(function () { /* nav 데이터 실패해도 기본 링크는 유지 */ });

        // ── 5) 테마 토글 동작 ──
        themeBtn.addEventListener('click', function () {
            var next = !document.documentElement.classList.contains('dark-theme');
            localStorage.setItem('dspilot-theme', next ? 'dark' : 'light');
            document.documentElement.classList.toggle('dark-theme', next);
            layout.classList.toggle('dark-theme', next);
            themeIcon.textContent = next ? 'light_mode' : 'dark_mode';
            // 페이지의 기존 Alpine storage 리스너가 .dsp-page :class 와 아이콘을 동기화하도록 이벤트 발행.
            window.dispatchEvent(new StorageEvent('storage', {
                key: 'dspilot-theme',
                newValue: next ? 'dark' : 'light'
            }));
        });

        // 다른 탭/창에서 테마가 바뀌면 셸 chrome 도 따라가도록 동기화.
        window.addEventListener('storage', function (e) {
            if (e.key !== 'dspilot-theme') return;
            var d = e.newValue === 'dark';
            document.documentElement.classList.toggle('dark-theme', d);
            layout.classList.toggle('dark-theme', d);
            themeIcon.textContent = d ? 'light_mode' : 'dark_mode';
        });

        // ── 6) drawer 토글 (햄버거) — MainLayout 의 .drawer.drawer-closed / .main-content.drawer-closed ──
        // 데스크톱 기본 열림. drawer-closed 클래스를 두 노드(drawer/main-content)에 토글.
        function setDrawerClosed(closed) {
            drawer.classList.toggle('drawer-closed', closed);
            main.classList.toggle('drawer-closed', closed);
        }
        hamburger.addEventListener('click', function () {
            var closed = !drawer.classList.contains('drawer-closed');
            setDrawerClosed(closed);
        });
        // 기본 상태: 데스크톱 열림 / 좁은 화면(<=1024px)은 CSS 미디어쿼리가 자동으로 숨김 처리.
        setDrawerClosed(false);

    } catch (err) {
        // 셸 구성 실패가 페이지 자체를 깨뜨리지 않도록 삼킨다.
        try { console.error('[shell.js] failed to build app-shell', err); } catch (e) { /* ignore */ }
    }
})();
