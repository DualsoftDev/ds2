/*
 * DSPilot 정적 셸 (Shared App-Shell) — stitch "Industrial Insight"
 * ------------------------------------------------------------------
 * /app/*.html 정적 페이지에 dashboard2.html 의 stitch 사이드바 + 상단 헤더를 입힌다.
 * dashboard2.html 의 stitch 셸 마크업을 그대로 생성하고, 스타일은 self-host 정적 CSS 로 입힌다.
 *
 * 포함 방법(매 페이지, alpine.min.js 보다 먼저):
 *   <script src="/app/shell.js" defer></script>
 *   <script defer src="/lib/alpine.min.js"></script>
 *
 * 사이드바 구성(dashboard2 와 동일 — 축소판):
 *   · 브랜드 (DUAL 로고 이미지 /images/logo.png + Industrial Monitoring)
 *   · 페이지 링크 (대시보드/동작편차/사이클분석/가동시간·이상/CCTV [+PLC 디버그])
 *   · Line Summary (가동/대기)  — /api/nav/summary 폴링
 *   · Settings (푸터)
 *   ※ 구 shell.js 의 "시스템 flow 트리 · agent 통신 상태 · 마지막 갱신" 섹션은 제거됨.
 *
 * 상단 헤더(main 내부, sticky):
 *   · 페이지 제목 + 브레드크럼(Home › 제목)
 *   · 연결 배지(.dash-live, hub 상태)
 *
 * 동작 개요:
 *   - 테마: <html> 에 dark + dark-theme 동시 토글(Tailwind dark: 변형 + ds.css 다크).
 *     localStorage 'dspilot-theme'. 로드 시 적용 + 설정 페이지/다른 탭 storage 동기화(헤더 토글 버튼 제거됨).
 *   - .dsp-page(Alpine 루트) 를 main(ml-60) 안으로 이동, 슬림 헤더 제거.
 *   - /api/nav         : showPlcDebug (PLC 디버그 링크, 1회).
 *   - /api/nav/summary : Line Summary · 이상발생 배지 · 연결 배지 (4초 폴링).
 *
 * 스타일 의존성: /css/stitch-shell.css (Tailwind 빌드 산출물, self-host·오프라인).
 *   stitch 유틸리티 + 셸 스코프 리셋 + 연결배지 + 다크 리맵을 모두 담는다.
 *   클래스/디자인 변경 시 → `npm run build:css` 로 재생성(tailwind.shell.config.js + tailwind/shell.input.css).
 */
(function () {
    'use strict';

    // 사이드바 링크 클래스 (dashboard2 와 동일; 비활성에 dark:hover 보강)
    var LINK_ACTIVE = 'flex items-center gap-3 px-4 py-3 rounded bg-secondary-container dark:bg-secondary text-on-secondary-container dark:text-on-secondary border-l-4 border-secondary transition-colors';
    var LINK_IDLE = 'flex items-center gap-3 px-4 py-3 rounded text-on-surface-variant dark:text-surface-variant hover:bg-surface-container-high dark:hover:bg-inverse-surface transition-colors';
    var SET_ACTIVE = 'flex items-center gap-3 px-4 py-2 rounded bg-secondary-container dark:bg-secondary text-on-secondary-container dark:text-on-secondary transition-colors';
    var SET_IDLE = 'flex items-center gap-3 px-4 py-2 rounded text-on-surface-variant dark:text-surface-variant hover:bg-surface-container-high dark:hover:bg-inverse-surface transition-colors';

    try {
        // ── 1) 테마: <html> 에 dark(Tailwind) + dark-theme(ds.css) 동시 적용 ──
        var dark = localStorage.getItem('dspilot-theme') === 'dark';
        document.documentElement.classList.toggle('dark-theme', dark);
        document.documentElement.classList.toggle('dark', dark);

        // ── 1.5) stitch 셸 스타일(self-host 정적 CSS) 로드 — 빠른 로드 위해 DOM 빌드 전에 주입(중복 방지) ──
        if (!document.querySelector('link[data-dsp-shell-css]')) {
            var shellCss = document.createElement('link');
            shellCss.rel = 'stylesheet';
            shellCss.href = '/css/stitch-shell.css';
            shellCss.setAttribute('data-dsp-shell-css', '');
            document.head.appendChild(shellCss);
        }

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
        function icon(name) { return el('span', 'material-icons', name); }

        // ── 3) 네비게이션 정의 (라우트/아이콘 — 라이브 대시보드는 '/'). ──
        var NAV_ITEMS = [
            { label: '대시보드',    href: '/',                    icon: 'space_dashboard', match: 'all',    legacy: ['/app/dashboard.html', '/app/dashboard2.html'] },
            { label: '동작편차',    href: '/heatmap',             icon: 'gradient',        match: 'prefix', legacy: '/app/heatmap.html' },
            { label: '가동시간·이상', href: '/uptime',            icon: 'monitor_heart',   match: 'prefix', legacy: '/app/uptime.html' },
            { label: 'CCTV',        href: '/cctv',                icon: 'videocam',        match: 'prefix', legacy: '/app/cctv.html' }
        ];
        var PLC_DEBUG_ITEM = { label: 'PLC 디버그', href: '/plc-debug', icon: 'bug_report', match: 'prefix', legacy: '/app/plc-debug.html' };
        var SETTINGS_ITEM = { label: '설정', href: '/settings', icon: 'settings', match: 'prefix', legacy: '/app/settings.html' };

        var path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        function isActive(item) {
            var candidates = [item.href].concat(item.legacy || []).filter(Boolean).map(function (p) {
                return p.replace(/\/+$/, '') || '/';
            });
            for (var i = 0; i < candidates.length; i++) {
                var c = candidates[i];
                if (item.match === 'all') { if (path === c) return true; }
                else { if (path === c || path.indexOf(c + '/') === 0) return true; }
            }
            return false;
        }

        function buildNavLink(item, activeCls, idleCls) {
            var a = el('a', isActive(item) ? activeCls : idleCls);
            a.href = item.href;
            a.appendChild(icon(item.icon));
            a.appendChild(el('span', 'font-label-sm text-label-sm', item.label));
            return a;
        }

        // ── 4) 사이드바 (stitch 축소판) ──
        var aside = el('aside', 'dsp-shell flex flex-col pt-gutter bg-surface-container-low dark:bg-inverse-surface border-r border-outline-variant dark:border-outline shadow-sm w-60 z-50');
        // FOUC 방지: stitch-shell.css 페인트 전에도 위치/크기 고정(값은 fixed/w-60/z-50 과 동일 → 레이아웃 점프 없음). 색은 미지정.
        aside.style.cssText = 'position:fixed;left:0;top:0;height:100%;width:240px;z-index:50;';

        var brand = el('div', 'px-6 mb-8 flex flex-col items-center text-center');
        var brandLink = el('a', 'block');
        brandLink.href = '/';
        var brandLogo = el('img');
        brandLogo.src = '/images/logo.png';
        brandLogo.alt = 'DUAL';
        brandLogo.style.cssText = 'height:34px;width:auto;display:block;margin:0 auto;';
        brandLink.appendChild(brandLogo);
        brand.appendChild(brandLink);
        brand.appendChild(el('p', 'font-label-sm text-label-sm text-on-surface-variant opacity-70 mt-2', 'Industrial Monitoring'));
        aside.appendChild(brand);

        var navMenu = el('nav', 'flex-1 flex flex-col gap-1 px-3 overflow-y-auto custom-scrollbar');
        aside.appendChild(navMenu);

        var anomalyBadge = null;
        var flowsAnchor = null;   // 시스템→Flow 서브메뉴를 삽입할 앵커(동작편차 링크 다음 = 구 '사이클 분석' 자리).
        NAV_ITEMS.forEach(function (item) {
            var link = buildNavLink(item, LINK_ACTIVE, LINK_IDLE);
            if (item.href === '/uptime') {
                anomalyBadge = el('span', 'ml-auto inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full bg-error text-white text-[10px] font-bold');
                anomalyBadge.title = '최근 10분 내 Error 알림';
                anomalyBadge.style.display = 'none';
                link.appendChild(anomalyBadge);
            }
            if (item.href === '/heatmap') flowsAnchor = link;
            navMenu.appendChild(link);
        });

        // ── 4.5) 시스템별 Flow(사이클 분석) 서브메뉴 컨테이너 ──
        //   구 '사이클 분석' 메뉴 항목은 제거됨 — 각 시스템 행(사이클 분석 아이콘)이 진입점.
        //   시스템 행 클릭 → 사이드바 우측에 플라이아웃 패널로 Flow 목록 → Flow 클릭 시 /flow?name= 이동.
        //   데이터는 아래 /api/nav fetch 의 systems 트리로 채운다(이미 PLC 디버그용으로 호출 중).
        var cycleSubWrap = el('div', 'flex flex-col gap-0.5');
        if (flowsAnchor && flowsAnchor.parentNode === navMenu) {
            navMenu.insertBefore(cycleSubWrap, flowsAnchor);
        }

        // /flow 페이지에 있을 때: 해당 시스템/Flow 행을 강조(아래 buildSystemSubmenu 에서 처리).
        var onFlowPage = path === '/flow';
        var curFlowName = onFlowPage
            ? ((new URLSearchParams(location.search)).get('name') || '')
            : '';

        function buildSystemSubmenu(systems) {
            cycleSubWrap.innerHTML = '';
            if (!systems || !systems.length) return;

            var openPanel = null, openRow = null, openChev = null;
            // preflight(전역 리셋) 꺼진 셸 빌드 → <button> 네이티브 테두리·배경 제거(nav 링크와 동일한 룩).
            var BTN_RESET = 'appearance:none;-webkit-appearance:none;background:transparent;border:0;cursor:pointer;font:inherit;';

            function closeFlyout() {
                if (openPanel) { openPanel.remove(); openPanel = null; }
                if (openChev) { openChev.style.transform = ''; openChev = null; }
                if (openRow) { openRow.setAttribute('aria-expanded', 'false'); openRow = null; }
                document.removeEventListener('click', onDocClick, true);
                window.removeEventListener('resize', reposition);
                navMenu.removeEventListener('scroll', reposition);
            }
            function reposition() {
                if (!openPanel || !openRow) return;
                var r = openRow.getBoundingClientRect();
                openPanel.style.left = (r.right + 6) + 'px';
                openPanel.style.top = Math.max(8, Math.min(r.top, window.innerHeight - 60)) + 'px';
            }
            function onDocClick(e) {
                if (openPanel && openPanel.contains(e.target)) return;
                if (openRow && openRow.contains(e.target)) return;
                closeFlyout();
            }
            function dot(color, op) {
                var d = el('span');
                d.style.cssText = 'flex:0 0 auto;width:5px;height:5px;border-radius:50%;background:' + color + ';opacity:' + op + ';';
                return d;
            }

            systems.forEach(function (sys) {
                var sysHasCurrent = onFlowPage && (sys.flows || []).indexOf(curFlowName) !== -1;

                var row = el('button', 'w-full flex items-center gap-3 px-4 py-3 rounded text-on-surface-variant dark:text-surface-variant hover:bg-surface-container-high dark:hover:bg-inverse-surface transition-colors');
                row.type = 'button';
                row.style.cssText = 'text-align:left;' + BTN_RESET;
                row.setAttribute('aria-expanded', 'false');
                // 구 '사이클 분석' 항목을 대체 — 시스템 행 아이콘 = 사이클 분석 아이콘(equalizer)
                var sysIcon = icon('equalizer');
                sysIcon.style.cssText = 'flex:0 0 auto;font-size:20px;' + (sysHasCurrent ? 'color:#2170e4;' : 'opacity:0.75;');
                row.appendChild(sysIcon);
                var sysLabel = el('span', 'font-label-sm text-label-sm', sys.name || '(이름 없음)');
                sysLabel.style.cssText = 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                row.appendChild(sysLabel);
                var chev = icon('chevron_right');
                chev.style.cssText = 'flex:0 0 auto;font-size:18px;transition:transform 0.12s;';
                row.appendChild(chev);

                row.addEventListener('click', function (e) {
                    e.stopPropagation();
                    var wasOpen = (openRow === row);
                    closeFlyout();
                    if (wasOpen) return;

                    var panel = el('div', 'bg-surface-container-low dark:bg-inverse-surface border border-outline-variant dark:border-outline rounded-lg py-2');
                    panel.style.cssText = 'position:fixed;z-index:60;min-width:200px;max-width:320px;max-height:70vh;overflow-y:auto;padding-left:6px;padding-right:6px;box-shadow:0 6px 20px rgba(0,0,0,0.12);';

                    var head = el('div', 'px-3 pb-2 mb-1 border-b border-outline-variant dark:border-outline text-[10px] uppercase font-bold tracking-wider text-outline');
                    head.textContent = sys.name || '';
                    panel.appendChild(head);

                    // ── 전체 편집 — 이 시스템의 모든 Flow 를 한 화면에서 일괄 조회·편집(사이클 분석/이상치/Head·Tail/duration). ──
                    //   플라이아웃 맨 위(시스템 헤더 바로 아래). /flow-all?system= 으로 시스템 스코프 전달.
                    var editAll = el('button', 'w-full flex items-center gap-2 px-3 py-2 mb-1 rounded transition-colors text-on-surface-variant dark:text-surface-variant hover:bg-surface-container-high dark:hover:bg-inverse-surface');
                    editAll.type = 'button';
                    editAll.style.cssText = 'text-align:left;font-weight:700;' + BTN_RESET;
                    var eaIcon = icon('edit_note');
                    eaIcon.style.cssText = 'flex:0 0 auto;font-size:18px;color:#2170e4;';
                    editAll.appendChild(eaIcon);
                    var eaLabel = el('span', 'font-label-sm text-label-sm', '전체 편집');
                    eaLabel.style.cssText = 'flex:1;min-width:0;color:#2170e4;';
                    editAll.appendChild(eaLabel);
                    editAll.addEventListener('click', function (ev) {
                        ev.stopPropagation();
                        location.href = '/flow-all?system=' + encodeURIComponent(sys.name || '');
                    });
                    panel.appendChild(editAll);

                    (sys.flows || []).forEach(function (flowName) {
                        var isCur = onFlowPage && flowName === curFlowName;
                        var fb = el('button', 'w-full flex items-center gap-2 px-3 py-2 rounded transition-colors text-on-surface-variant dark:text-surface-variant'
                            + (isCur ? '' : ' hover:bg-surface-container-high dark:hover:bg-inverse-surface'));
                        fb.type = 'button';
                        fb.style.cssText = 'text-align:left;' + BTN_RESET;
                        // BTN_RESET 의 background:transparent 가 Tailwind 활성 bg 클래스를 덮으므로 활성 색은 인라인으로 지정.
                        if (isCur) { fb.style.backgroundColor = '#2170e4'; fb.style.color = '#fff'; }
                        fb.appendChild(dot(isCur ? '#fff' : 'currentColor', isCur ? '1' : '0.55'));
                        var fl = el('span', 'font-label-sm text-label-sm', flowName);
                        fl.style.cssText = 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                        fb.appendChild(fl);
                        fb.addEventListener('click', function (ev) {
                            ev.stopPropagation();
                            location.href = '/flow?name=' + encodeURIComponent(flowName);
                        });
                        panel.appendChild(fb);
                    });

                    document.body.appendChild(panel);
                    openPanel = panel; openRow = row; openChev = chev;
                    chev.style.transform = 'rotate(90deg)';
                    row.setAttribute('aria-expanded', 'true');
                    reposition();
                    // 같은 클릭 이벤트가 즉시 닫지 않도록 다음 틱에 바인딩.
                    setTimeout(function () { document.addEventListener('click', onDocClick, true); }, 0);
                    window.addEventListener('resize', reposition);
                    navMenu.addEventListener('scroll', reposition);
                });

                cycleSubWrap.appendChild(row);
            });
        }

        // Line Summary 블록 (PLC 디버그 링크는 이 블록 앞에 삽입)
        var lineSummaryBlock = el('div', 'mt-8 mb-4 px-4');
        lineSummaryBlock.appendChild(el('div', 'text-[10px] uppercase font-bold tracking-wider text-outline mb-2', 'Line Summary'));
        var lsCard = el('div', 'flex items-center justify-between p-3 bg-surface dark:bg-inverse-surface rounded-lg border border-outline-variant dark:border-outline');
        var lsLeft = el('div');
        var lsRunRow = el('div', 'flex items-center gap-2');
        lsRunRow.appendChild(el('span', 'w-2 h-2 rounded-full bg-green-500'));
        var lsRunLabel = el('span', 'font-label-sm text-label-sm');
        lsRunLabel.appendChild(document.createTextNode('가동: '));
        var lsRunning = el('b', null, '0');
        lsRunLabel.appendChild(lsRunning);
        lsRunRow.appendChild(lsRunLabel);
        var lsIdleRow = el('div', 'flex items-center gap-2');
        lsIdleRow.appendChild(el('span', 'w-2 h-2 rounded-full bg-orange-400'));
        var lsIdleLabel = el('span', 'font-label-sm text-label-sm');
        lsIdleLabel.appendChild(document.createTextNode('대기: '));
        var lsIdle = el('b', null, '0');
        lsIdleLabel.appendChild(lsIdle);
        lsIdleRow.appendChild(lsIdleLabel);
        lsLeft.appendChild(lsRunRow);
        lsLeft.appendChild(lsIdleRow);
        lsCard.appendChild(lsLeft);
        lineSummaryBlock.appendChild(lsCard);
        navMenu.appendChild(lineSummaryBlock);


        // 푸터 (설정)
        var footer = el('div', 'p-4 border-t border-outline-variant dark:border-outline flex flex-col gap-1');
        footer.appendChild(buildNavLink(SETTINGS_ITEM, SET_ACTIVE, SET_IDLE));
        aside.appendChild(footer);

        // ── 5) 메인 + 상단 헤더 ──
        var main = el('main', 'ml-60 min-h-screen');
        main.style.cssText = 'margin-left:240px;min-height:100vh;';

        var header = el('header', 'dsp-shell h-16 flex justify-between items-center w-full px-container-margin bg-surface dark:bg-background border-b border-outline-variant dark:border-outline sticky top-0 z-40');
        header.style.cssText = 'position:sticky;top:0;z-index:40;height:64px;';

        var headLeft = el('div', 'flex items-center gap-4');
        headLeft.appendChild(el('h2', 'font-headline-lg text-headline-lg font-bold text-on-surface', pageTitle || 'DSPilot'));
        var crumb = el('div', 'flex items-center gap-2 text-on-surface-variant font-body-md text-body-md opacity-60');
        crumb.appendChild(el('span', null, 'Home'));
        crumb.appendChild(el('span', 'material-icons text-[16px]', 'chevron_right'));
        crumb.appendChild(el('span', 'text-primary font-semibold', pageTitle || 'DSPilot'));
        headLeft.appendChild(crumb);

        var headRight = el('div', 'flex items-center gap-3');

        // ── Agent 상태 팝오버 (헤더 배지 클릭 시 펼쳐짐) ──
        var AG_DOT = { green: '#22c55e', orange: '#fb923c', red: '#ef4444', gray: '#9ca3af' };
        function agentRow() {
            var row = el('div', 'flex items-center gap-2');
            var dot = el('span', 'w-2 h-2 rounded-full');
            dot.style.cssText = 'flex:0 0 auto;background:' + AG_DOT.gray + ';';
            var text = el('span', 'font-label-sm text-label-sm');
            text.style.cssText = 'min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
            row.appendChild(dot);
            row.appendChild(text);
            return { row: row, dot: dot, text: text };
        }
        var agHub = agentRow();
        var agPlc = agentRow();
        var agData = agentRow();
        agHub.text.textContent = 'PROMAKER HUB: —';
        agPlc.text.textContent = 'PLC 어댑터: —';
        agData.text.textContent = 'PLC 데이터 대기';

        var agPopover = el('div', 'bg-surface dark:bg-inverse-surface border border-outline-variant dark:border-outline rounded-lg');
        agPopover.style.cssText = 'position:fixed;z-index:100;min-width:210px;display:none;box-shadow:0 6px 20px rgba(0,0,0,0.15);padding:10px 14px 12px;';
        agPopover.appendChild(el('div', 'text-[10px] uppercase font-bold tracking-wider text-outline mb-2', 'Agent 상태'));
        var agPopCard = el('div', 'flex flex-col gap-2');
        agPopCard.appendChild(agHub.row);
        agPopCard.appendChild(agPlc.row);
        agPopCard.appendChild(agData.row);
        agPopover.appendChild(agPopCard);
        document.body.appendChild(agPopover);

        var liveBadge = el('span', 'dash-live is-poll');
        liveBadge.style.cursor = 'pointer';
        liveBadge.setAttribute('role', 'button');
        liveBadge.setAttribute('aria-expanded', 'false');
        var liveDot = el('span', 'dash-live-dot');
        var liveText = el('span', null, '연결 확인 중');
        var liveChev = el('span', 'material-icons');
        liveChev.style.cssText = 'font-size:15px;transition:transform 0.15s;margin-left:2px;';
        liveChev.textContent = 'expand_more';
        liveBadge.appendChild(liveDot);
        liveBadge.appendChild(liveText);
        liveBadge.appendChild(liveChev);
        headRight.appendChild(liveBadge);

        var _agPopOpen = false;
        function closeAgPopover() {
            _agPopOpen = false;
            agPopover.style.display = 'none';
            liveChev.style.transform = '';
            liveBadge.setAttribute('aria-expanded', 'false');
            document.removeEventListener('click', closeAgPopover, true);
        }
        liveBadge.addEventListener('click', function (e) {
            e.stopPropagation();
            if (_agPopOpen) { closeAgPopover(); return; }
            _agPopOpen = true;
            var r = liveBadge.getBoundingClientRect();
            agPopover.style.top = (r.bottom + 6) + 'px';
            agPopover.style.right = (window.innerWidth - r.right) + 'px';
            agPopover.style.display = 'block';
            liveChev.style.transform = 'rotate(180deg)';
            liveBadge.setAttribute('aria-expanded', 'true');
            setTimeout(function () { document.addEventListener('click', closeAgPopover, true); }, 0);
        });

        header.appendChild(headLeft);
        header.appendChild(headRight);

        // ── 6) DOM 삽입: .dsp-page 를 main(헤더 다음)으로 이동 ──
        page.classList.add('dsp-in-shell');
        document.body.insertBefore(aside, page);
        document.body.insertBefore(main, page);
        main.appendChild(header);
        main.appendChild(page);

        // ── 7) 테마 동기화 (설정 페이지 변경 + 다른 탭 동기화) ──
        //  헤더 토글 버튼은 제거됨. 테마는 로드 시 localStorage 에서 적용되며, 설정 페이지/타 탭 변경은 storage 이벤트로 반영.
        function applyTheme(d) {
            document.documentElement.classList.toggle('dark', d);
            document.documentElement.classList.toggle('dark-theme', d);
        }
        window.addEventListener('storage', function (e) {
            if (e.key === 'dspilot-theme') applyTheme(e.newValue === 'dark');
        });

        // ── 8) /api/nav: ShowPlcDebug → PLC 디버그 링크 (1회) + 시스템별 Flow 서브메뉴 ──
        fetch('/api/nav', { headers: { 'Accept': 'application/json' } })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (data) {
                if (!data) return;
                if (data.showPlcDebug) {
                    navMenu.insertBefore(buildNavLink(PLC_DEBUG_ITEM, LINK_ACTIVE, LINK_IDLE), lineSummaryBlock);
                }
                buildSystemSubmenu(data.systems);
            })
            .catch(function () { /* ignore */ });

        // ── 9) /api/nav/summary: Line Summary + 이상발생 배지 + 연결 배지 (주기 폴링) ──
        var HUB_LIVE = {
            connected:    ['is-live', '실시간'],
            connecting:   ['is-poll', '연결 중'],
            reconnecting: ['is-poll', '재연결 중'],
            disconnected: ['is-poll', '연결 끊김']
        };

        function applySummary(data) {
            var lines = data.lines || {};
            lsRunning.textContent = lines.running || 0;
            lsIdle.textContent = lines.idle || 0;

            var count = data.anomalyActiveCount || 0;
            if (anomalyBadge) {
                anomalyBadge.textContent = count;
                anomalyBadge.style.display = count > 0 ? '' : 'none';
            }

            var agent = data.agent || {};
            var live = HUB_LIVE[agent.hub] || HUB_LIVE.disconnected;
            liveBadge.className = 'dash-live ' + live[0];
            liveText.textContent = live[1];

            // ── Agent 상태 블록 (dashboard2 footer 와 동일 로직) ──
            var hub = agent.hub || 'disconnected';
            var plcTotal = agent.plcTotal || 0;
            var plcDown = agent.plcDisconnected || 0;
            var hasData = !!data.hasData;

            var hubLabel = hub === 'connected' ? '정상'
                : (hub === 'connecting' ? '연결 중'
                : (hub === 'reconnecting' ? '재연결 중' : '끊김'));
            agHub.text.textContent = 'PROMAKER HUB: ' + hubLabel;
            agHub.dot.style.background = hub === 'connected' ? AG_DOT.green
                : ((hub === 'connecting' || hub === 'reconnecting') ? AG_DOT.orange : AG_DOT.gray);

            var plcLabel = hub !== 'connected' ? 'PLC 어댑터: —'
                : (plcTotal === 0 ? 'PLC 어댑터: 보고 없음'
                : (plcDown > 0 ? 'PLC 어댑터: ' + plcDown + '대 끊김'
                : 'PLC 어댑터: ' + plcTotal + '대 연결'));
            agPlc.text.textContent = plcLabel;
            agPlc.dot.style.background = (hub === 'connected' && plcDown === 0 && plcTotal > 0) ? AG_DOT.green
                : (plcDown > 0 ? AG_DOT.red : AG_DOT.gray);

            agData.text.textContent = hasData ? 'PLC 데이터 수신중' : 'PLC 데이터 대기';
            agData.dot.style.background = hasData ? AG_DOT.green : AG_DOT.gray;
        }

        function pollSummary() {
            fetch('/api/nav/summary', { headers: { 'Accept': 'application/json' } })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (data) { if (data) applySummary(data); })
                .catch(function () { /* 폴링 실패 시 마지막 값 유지 */ });
        }
        pollSummary();
        setInterval(pollSummary, 4000);

    } catch (err) {
        try { console.error('[shell.js] failed to build app-shell', err); } catch (e) { /* ignore */ }
    }
})();
