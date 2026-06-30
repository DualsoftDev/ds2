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
 *     이상발생 배지는 /uptime 방문 시 초기화 — serverTimeUtc 를 localStorage ack 로 박제,
 *     이후 폴링이 ?anomalyAck= 로 보내 ack 이전 Error 는 카운트에서 제외.
 *
 * 스타일 의존성: /css/stitch-shell.css (Tailwind 빌드 산출물, self-host·오프라인).
 *   stitch 유틸리티 + 셸 스코프 리셋 + 연결배지 + 다크 리맵을 모두 담는다.
 *   클래스/디자인 변경 시 → `npm run build:css` 로 재생성(tailwind.shell.config.js + tailwind/shell.input.css).
 */
(function () {
    'use strict';

    // 사이드바 너비(px). aside.width 와 main.margin-left 단일 소스. 인라인 style 이 w-60/ml-60 클래스를 덮음.
    var SHELL_W = 300;
    // 모바일/데스크톱 분기 기준(px). 미만이면 사이드바를 드로어로 전환.
    var MOBILE_BP = 768;

    // 이상코드 피드 레벨→색 (uptime.html 과 동일: Error 빨강 / Warning 주황 / Info 파랑)
    var LEVEL_COLOR = { Error: '#ef4444', Warning: '#fb923c', Info: '#3b82f6' };

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

        // ── 1.6) 모바일 셸 스타일(주입) — 폰(≤480px) 헤더 정리 + 드로어(≤768px) 폭 클램프/터치 타깃 ──
        //   build 산출물(stitch-shell.css)을 손대지 않고 셸 전용 반응형 규칙을 주입한다(리사이즈/회전에 CSS 가 알아서 대응).
        //   훅: aside/header 의 .dsp-shell, 그리고 아래에서 부여하는 .dsp-shell-crumb / .dsp-shell-live-text / .dsp-shell-linestatus.
        if (!document.getElementById('dsp-shell-mobile-css')) {
            var mcss = document.createElement('style');
            mcss.id = 'dsp-shell-mobile-css';
            mcss.textContent =
                '@media (max-width:768px){' +
                  /* 드로어 폭: 폰에서 300px 가 화면을 다 가리지 않도록 85vw 로 클램프(인라인 width 를 !important 로 덮음). 숨김 오프셋(-300px)은 항상 폭 이상이라 완전히 가려짐. */
                  'aside.dsp-shell{width:min(300px,85vw)!important;}' +
                  /* 터치 타깃: 네비 링크/시스템 행/Flow 버튼 최소 44px. */
                  'aside.dsp-shell nav a,aside.dsp-shell nav button{min-height:44px;}' +
                '}' +
                '@media (max-width:480px){' +
                  /* 헤더 정리: 브레드크럼 숨김(제목 중복/공간 경쟁 제거), 실시간 배지 텍스트 숨김(점+꺾쇠 유지), 좌우 패딩 축소. */
                  '.dsp-shell-crumb{display:none!important;}' +
                  '.dsp-shell-live-text{display:none!important;}' +
                  'header.dsp-shell{padding-left:12px!important;padding-right:12px!important;}' +
                  '.dsp-shell-linestatus{padding:4px 8px!important;letter-spacing:0!important;}' +
                  /* 헤더 좌측 영역 간격 축소(햄버거↔제목). */
                  'header.dsp-shell .dsp-shell-headleft{gap:10px!important;}' +
                '}';
            document.head.appendChild(mcss);
        }

        // ── 1.7) 전체화면 가로(landscape) 잠금 헬퍼 — CCTV 영상벽·도면 등 전체화면 진입 시 모바일에서 가로 고정 ──
        //   전체화면(requestFullscreen)이 성공한 뒤에만 screen.orientation.lock('landscape') 가능.
        //   데스크톱/미지원 브라우저는 lock 이 throw/reject → 조용히 무시(데스크톱 동작 무변경).
        //   .dsp-page 유무와 무관하게 전 페이지에서 쓰도록 early-return 위에서 window 에 노출.
        if (!window.dspFullscreen) {
            window.dspFullscreen = {
                _lock: function () {
                    try {
                        if (screen.orientation && screen.orientation.lock) {
                            var p = screen.orientation.lock('landscape');
                            if (p && p.catch) p.catch(function () { /* 데스크톱/미지원 무시 */ });
                        }
                    } catch (e) { /* ignore */ }
                },
                _unlock: function () {
                    try { if (screen.orientation && screen.orientation.unlock) screen.orientation.unlock(); } catch (e) { /* ignore */ }
                },
                enter: function (el) {
                    el = el || document.documentElement;
                    var req = el.requestFullscreen || el.webkitRequestFullscreen || el.msRequestFullscreen;
                    if (!req) return;
                    var p;
                    try { p = req.call(el); } catch (e) { return; }
                    var self = this;
                    if (p && p.then) p.then(function () { self._lock(); }, function () { /* 진입 실패 무시 */ });
                    else setTimeout(function () { self._lock(); }, 60); // webkit(프로미스 미반환) 폴백
                },
                exit: function () {
                    this._unlock();
                    var ex = document.exitFullscreen || document.webkitExitFullscreen || document.msExitFullscreen;
                    if (ex && (document.fullscreenElement || document.webkitFullscreenElement)) {
                        try { ex.call(document); } catch (e) { /* ignore */ }
                    }
                },
                toggle: function (el) {
                    if (document.fullscreenElement || document.webkitFullscreenElement) this.exit();
                    else this.enter(el);
                }
            };
            // 어떤 경로로든 전체화면이 해제되면 방향 잠금도 해제(세로 복귀).
            document.addEventListener('fullscreenchange', function () {
                if (!document.fullscreenElement) window.dspFullscreen._unlock();
            });
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
            // OEE 메뉴 숨김 — 페이지(/oee)는 URL 로 접근 가능, 네비에서만 제외. 복구는 이 줄 주석 해제.
            // { label: 'OEE',         href: '/oee',                 icon: 'precision_manufacturing', match: 'prefix', legacy: '/app/oee.html' },
            // CCTV 메뉴 숨김 — 실시간 시청은 대시보드 레이아웃 카드의 'CCTV' 토글에서 사용. /cctv 는 설정(카메라·오버레이 편집) 페이지로 URL/[설정] 버튼 접근. 복구는 이 줄 주석 해제.
            // { label: 'CCTV',        href: '/cctv',                icon: 'videocam',        match: 'prefix', legacy: '/app/cctv.html' }
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
        var aside = el('aside', 'dsp-shell flex flex-col pt-gutter bg-surface-container-low dark:bg-inverse-surface border-r border-outline-variant dark:border-outline shadow-sm z-50');
        // FOUC 방지: stitch-shell.css 페인트 전에도 위치/크기 고정. 인라인 width 가 최종값(클래스 덮음) → SHELL_W 단일소스.
        aside.style.cssText = 'position:fixed;left:0;top:0;height:100%;width:' + SHELL_W + 'px;z-index:50;';

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
        // /uptime 진입 = 배지 읽음 처리. serverTimeUtc 를 localStorage(전 페이지/탭 공유)에 ack 로 기록하고,
        // 이후 폴링은 anomalyAck 파라미터로 보내 그 시각 이전 Error 를 배지에서 제외한다(알람 이력 피드는 영향 없음).
        var ANOMALY_ACK_KEY = 'dspilot-anomaly-ack';
        var onUptimePage = false;
        var flowsAnchor = null;   // 시스템→Flow 서브메뉴를 삽입할 앵커(동작편차 링크 다음 = 구 '사이클 분석' 자리).
        NAV_ITEMS.forEach(function (item) {
            var link = buildNavLink(item, LINK_ACTIVE, LINK_IDLE);
            if (item.href === '/uptime') {
                onUptimePage = isActive(item);
                anomalyBadge = el('span', 'ml-auto inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full bg-error text-white text-[10px] font-bold');
                anomalyBadge.title = '최근 10분 내 Error 알림 (가동시간·이상 방문 시 초기화)';
                anomalyBadge.style.display = 'none';
                link.appendChild(anomalyBadge);
            }
            if (item.href === '/heatmap') flowsAnchor = link;
            navMenu.appendChild(link);
        });

        // ── 4.5) 시스템별 Flow(사이클 분석) 서브메뉴 컨테이너 ──
        //   구 '사이클 분석' 메뉴 항목은 제거됨 — 각 시스템 행(사이클 분석 아이콘)이 진입점.
        //   시스템 행 클릭 → 사이드바(NAVMENU) 내부에서 바로 아래로 Flow 목록을 펼침(아코디언) → Flow 클릭 시 /flow?name= 이동.
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

            // 인라인 아코디언: 한 번에 한 시스템만 펼침(다른 시스템을 펼치면 이전 것은 접힘).
            var openRow = null, openChev = null, openSub = null;
            // preflight(전역 리셋) 꺼진 셸 빌드 → <button> 네이티브 테두리·배경 제거(nav 링크와 동일한 룩).
            var BTN_RESET = 'appearance:none;-webkit-appearance:none;background:transparent;border:0;cursor:pointer;font:inherit;';

            function collapse() {
                if (openSub) { openSub.style.display = 'none'; openSub = null; }
                if (openChev) { openChev.style.transform = ''; openChev = null; }
                if (openRow) { openRow.setAttribute('aria-expanded', 'false'); openRow = null; }
            }
            function expand(row, sub, chev) {
                collapse();
                sub.style.display = '';
                openRow = row; openSub = sub; openChev = chev;
                chev.style.transform = 'rotate(90deg)';
                row.setAttribute('aria-expanded', 'true');
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

                // ── 인라인 확장 컨테이너 — 시스템 행 바로 아래(NAVMENU 내부)에서 아코디언으로 펼침. ──
                //   별도 플라이아웃/오버레이 없이 사이드바 콘텐츠를 밀어내며 펼쳐진다(nav 가 스크롤 처리).
                var sub = el('div', 'flex flex-col gap-0.5');
                // display:none = 접힘. padding-left 로 시스템 행 아래 들여쓰기(중첩 표시). pl-* 유틸은 빌드에 없어 인라인 지정.
                sub.style.cssText = 'display:none;padding-left:18px;';

                // ── 전체 편집 — 이 시스템의 모든 Flow 를 한 화면에서 일괄 조회·편집(사이클 분석/이상치/Head·Tail/duration). ──
                //   확장 영역 맨 위. /flow-all?system= 으로 시스템 스코프 전달.
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
                sub.appendChild(editAll);

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
                    sub.appendChild(fb);
                });

                row.addEventListener('click', function (e) {
                    e.stopPropagation();
                    if (openRow === row) { collapse(); return; }
                    expand(row, sub, chev);
                });

                cycleSubWrap.appendChild(row);
                cycleSubWrap.appendChild(sub);

                // 현재 보고 있는 Flow(/flow?name=)가 이 시스템에 속하면 자동으로 펼쳐 위치를 보여준다.
                if (sysHasCurrent) expand(row, sub, chev);
            });
        }

        // Line Summary 변수 — 헤더 가동/대기 위젯에서 사용
        var lsRunning = el('b', null, '0');
        var lsIdle = el('b', null, '0');

        // ── 이상코드 실시간 피드 ── Line Summary 아래 빈 공간을 채움(flex-1+자체 스크롤).
        //   데이터: /api/nav/summary 의 recentAnomalies (최신 N건, 레벨 무관). 4초 폴링 공유.
        //   행 클릭 → /uptime?utSystem=&utLevel= (필터 적용). 출처(source) 무관 동일 렌더 → 추후 ds-error 4종 합류.
        var anomalyBlock = el('div', 'mt-6 px-4 flex-1 flex flex-col min-h-0');
        anomalyBlock.appendChild(el('div', 'text-[10px] uppercase font-bold tracking-wider text-outline mb-2', '알람 이력'));
        var anomalyList = el('div', 'flex flex-col gap-1 overflow-y-auto custom-scrollbar pr-1');
        anomalyList.style.cssText = 'flex:1 1 0;min-height:0;';
        anomalyBlock.appendChild(anomalyList);
        navMenu.appendChild(anomalyBlock);

        var anomalyEmpty = el('div', 'font-label-sm text-label-sm text-outline opacity-60 py-2', '이상 없음');

        function renderAnomalies(items) {
            anomalyList.innerHTML = '';
            if (!items || !items.length) { anomalyList.appendChild(anomalyEmpty); return; }
            items.forEach(function (a) {
                var color = LEVEL_COLOR[a.level] || LEVEL_COLOR.Info;
                var row = el('button', 'w-full flex items-start gap-2 px-2 py-2 rounded transition-colors text-on-surface-variant dark:text-surface-variant hover:bg-surface-container-high dark:hover:bg-inverse-surface');
                row.type = 'button';
                row.style.cssText = 'text-align:left;appearance:none;-webkit-appearance:none;background:transparent;border:0;cursor:pointer;font:inherit;';

                var d = el('span');
                d.style.cssText = 'flex:0 0 auto;width:7px;height:7px;border-radius:50%;margin-top:5px;background:' + color + ';';
                row.appendChild(d);

                var body = el('div');
                body.style.cssText = 'flex:1;min-width:0;';
                var label = el('div', 'font-label-sm text-label-sm font-semibold', (a.label || a.code || '(이름 없음)') + (a.source && a.source.startsWith('ds-error') && a.code ? ' (' + a.code + ')' : ''));
                label.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                body.appendChild(label);
                var sub = el('div', 'text-[10px] text-outline opacity-80', (a.system || '') + ' · ' + (a.occurredAtLocal || ''));
                sub.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                body.appendChild(sub);
                row.appendChild(body);

                row.addEventListener('click', function () {
                    var q = [];
                    if (a.system) q.push('utSystem=' + encodeURIComponent(a.system));
                    if (a.level) q.push('utLevel=' + encodeURIComponent(a.level));
                    // 클릭한 알람을 콕 집어 보여주기 위해 이름(검색 시드)·발생시각(at)도 전달한다.
                    //   utSearch : 그 태그로 좁혀 페이지당 10건 안에 들어오게 함(목록 페이징 한계 회피).
                    //   at       : uptime 이 그 '날'을 기간으로 자동 맞추고(기본 '오늘'이라 과거 알람이면 0건이던 문제 해결) 해당 행을 스크롤·하이라이트.
                    //   label(=usertag Name / ds-error Label)·occurredAtLocal('yyyy-MM-dd HH:mm:ss') 은 알림 이력 행과 동일 소스라 그대로 매칭됨.
                    var nm = a.label || a.code;
                    if (nm) q.push('utSearch=' + encodeURIComponent(nm));
                    if (a.occurredAtLocal) q.push('at=' + encodeURIComponent(a.occurredAtLocal));
                    location.href = '/uptime' + (q.length ? '?' + q.join('&') : '');
                });
                anomalyList.appendChild(row);
            });
        }
        renderAnomalies(null);


        // 푸터 (설정)
        var footer = el('div', 'p-4 border-t border-outline-variant dark:border-outline flex flex-col gap-1');
        footer.appendChild(buildNavLink(SETTINGS_ITEM, SET_ACTIVE, SET_IDLE));
        aside.appendChild(footer);

        // ── 5) 메인 + 상단 헤더 ──
        var main = el('main', 'min-h-screen');
        main.style.cssText = 'margin-left:' + SHELL_W + 'px;min-height:100vh;';

        var header = el('header', 'dsp-shell h-16 flex justify-between items-center w-full px-container-margin bg-surface dark:bg-background border-b border-outline-variant dark:border-outline sticky top-0 z-40');
        header.style.cssText = 'position:sticky;top:0;z-index:40;height:64px;';

        var headLeft = el('div', 'dsp-shell-headleft flex items-center gap-4');
        // 헤더(justify-between)의 좌측 영역이 우측 위젯에 밀려 제목을 짓이기지 않도록 축소 허용(min-width:0 필수).
        headLeft.style.cssText = 'flex:1 1 auto;min-width:0;';
        var menuBtn = el('button');
        menuBtn.type = 'button';
        menuBtn.setAttribute('aria-label', '메뉴 열기');
        menuBtn.style.cssText = 'display:none;background:transparent;border:0;cursor:pointer;padding:8px;color:inherit;border-radius:6px;line-height:1;flex:0 0 auto;min-width:40px;min-height:40px;';
        menuBtn.appendChild(icon('menu'));
        headLeft.appendChild(menuBtn);
        // 제목: 폭이 부족하면 글자가 한 글자씩 세로로 접혀 "중복/세로 텍스트"로 보이던 버그 → nowrap+ellipsis+min-width:0 으로 한 줄 말줄임.
        var headTitle = el('h2', 'font-headline-lg text-headline-lg font-bold text-on-surface', pageTitle || 'DSPilot');
        headTitle.style.cssText = 'flex:0 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
        headLeft.appendChild(headTitle);
        var crumb = el('div', 'dsp-shell-crumb flex items-center gap-2 text-on-surface-variant font-body-md text-body-md opacity-60');
        crumb.appendChild(el('span', null, 'Home'));
        crumb.appendChild(el('span', 'material-icons text-[16px]', 'chevron_right'));
        crumb.appendChild(el('span', 'text-primary font-semibold', pageTitle || 'DSPilot'));
        headLeft.appendChild(crumb);

        var headRight = el('div', 'flex items-center gap-3');
        // 우측 위젯(가동/대기·실시간)은 축소 금지 — 좌측 제목이 먼저 말줄임되도록.
        headRight.style.cssText = 'flex:0 0 auto;';

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

        // PLC 어댑터 행 우측에 '상세' 토글 — 누르면 대상 PLC 의 IP/Port·상태·출처를 펼쳐 보여준다.
        var agPlcDetailBtn = el('button', null, '상세');
        agPlcDetailBtn.type = 'button';
        agPlcDetailBtn.style.cssText = 'flex:0 0 auto;margin-left:auto;font-size:10px;line-height:1;'
            + 'padding:2px 7px;border:1px solid var(--color-lines-strong,#cbd5e1);border-radius:5px;'
            + 'background:transparent;color:inherit;cursor:pointer;opacity:0.75;';
        agPlc.text.style.flex = '0 1 auto'; // '상세' 버튼이 우측으로 밀리도록 텍스트가 남는 폭을 차지하지 않게.
        agPlc.row.appendChild(agPlcDetailBtn);

        // 어댑터 IP 상세 패널 (PLC 어댑터 행 바로 아래, 기본 접힘).
        var agPlcDetail = el('div');
        agPlcDetail.style.cssText = 'display:none;margin:-2px 0 0 16px;padding-left:9px;'
            + 'border-left:2px solid var(--color-lines,#e2e8f0);font-size:11px;line-height:1.5;';

        var agPopover = el('div', 'bg-surface dark:bg-inverse-surface border border-outline-variant dark:border-outline rounded-lg');
        agPopover.style.cssText = 'position:fixed;z-index:100;min-width:210px;max-width:calc(100vw - 12px);display:none;box-shadow:0 6px 20px rgba(0,0,0,0.15);padding:10px 14px 12px;';
        agPopover.appendChild(el('div', 'text-[10px] uppercase font-bold tracking-wider text-outline mb-2', 'Agent 상태'));
        var agPopCard = el('div', 'flex flex-col gap-2');
        agPopCard.appendChild(agHub.row);
        agPopCard.appendChild(agPlc.row);
        agPopCard.appendChild(agPlcDetail);
        agPopCard.appendChild(agData.row);
        agPopover.appendChild(agPopCard);
        document.body.appendChild(agPopover);

        // ── PLC 어댑터 상세(IP) 렌더 — applySummary 가 채우는 최신 어댑터/출처를 사용. ──
        var _agAdapters = [];   // [{name, ip, port, connected, error, vendor}]
        var _agPlcSource = '';  // 'agent' | 'ping' | 'none'
        var _plcDetailOpen = false;
        function renderPlcDetail() {
            agPlcDetail.innerHTML = '';
            if (!_agAdapters.length) {
                var none = el('div', null, _agPlcSource === 'none'
                    ? '대상 PLC 가 설정되어 있지 않습니다.'
                    : 'PLC 정보 없음');
                none.style.opacity = '0.7';
                agPlcDetail.appendChild(none);
                return;
            }
            var srcNote = el('div', null, _agPlcSource === 'ping'
                ? '출처: DSPilot 직접 핑(TCP)'
                : '출처: Promaker 에이전트');
            srcNote.style.cssText = 'opacity:0.6;margin-bottom:3px;';
            agPlcDetail.appendChild(srcNote);
            _agAdapters.forEach(function (a) {
                var line = el('div');
                line.style.cssText = 'display:flex;align-items:center;gap:6px;';
                var d = el('span');
                d.style.cssText = 'flex:0 0 auto;width:7px;height:7px;border-radius:50%;background:'
                    + (a.connected ? AG_DOT.green : AG_DOT.red) + ';';
                var label = (a.name || 'PLC') + ' · ' + (a.ip || '?') + ':' + (a.port || 0);
                var nm = el('span', null, label);
                nm.style.cssText = 'font-variant-numeric:tabular-nums;';
                line.appendChild(d);
                line.appendChild(nm);
                if (a.error) { line.title = a.error; }
                agPlcDetail.appendChild(line);
            });
        }
        agPlcDetailBtn.addEventListener('click', function (e) {
            // 팝오버 내부 클릭이라 닫히지 않도록(closeAgPopover 가 contains 로 무시) — 토글만.
            e.stopPropagation();
            _plcDetailOpen = !_plcDetailOpen;
            agPlcDetail.style.display = _plcDetailOpen ? 'block' : 'none';
            agPlcDetailBtn.style.opacity = _plcDetailOpen ? '1' : '0.75';
            if (_plcDetailOpen) renderPlcDetail();
        });

        var liveBadge = el('span', 'dash-live is-poll');
        liveBadge.style.cursor = 'pointer';
        liveBadge.setAttribute('role', 'button');
        liveBadge.setAttribute('aria-expanded', 'false');
        var liveDot = el('span', 'dash-live-dot');
        var liveText = el('span', 'dsp-shell-live-text', '연결 확인 중');
        var liveChev = el('span', 'material-icons');
        liveChev.style.cssText = 'font-size:15px;transition:transform 0.15s;margin-left:2px;';
        liveChev.textContent = 'expand_more';
        liveBadge.appendChild(liveDot);
        liveBadge.appendChild(liveText);
        liveBadge.appendChild(liveChev);

        // ── 가동/대기 헤더 위젯 (실시간 배지 왼쪽) ──
        var headerLineStatus = el('span', 'dsp-shell-linestatus');
        headerLineStatus.style.cssText = 'display:inline-flex;align-items:center;gap:7px;padding:5px 11px;border:1px solid var(--color-lines-strong);border-radius:var(--radius-sm);font-size:.66rem;font-weight:700;letter-spacing:.05em;font-variant-numeric:tabular-nums;flex:0 0 auto;';
        var hlsRunSpan = el('span');
        hlsRunSpan.style.cssText = 'display:inline-flex;align-items:center;gap:5px;';
        var hlsRunDot = el('span');
        hlsRunDot.style.cssText = 'flex:0 0 auto;width:8px;height:8px;border-radius:50%;background:#22c55e;';
        hlsRunSpan.appendChild(hlsRunDot);
        hlsRunSpan.appendChild(document.createTextNode('가동 '));
        hlsRunSpan.appendChild(lsRunning);
        var hlsSep = el('span');
        hlsSep.style.cssText = 'display:inline-block;width:1px;height:10px;background:currentColor;opacity:0.25;vertical-align:middle;';
        var hlsIdleSpan = el('span');
        hlsIdleSpan.style.cssText = 'display:inline-flex;align-items:center;gap:5px;';
        var hlsIdleDot = el('span');
        hlsIdleDot.style.cssText = 'flex:0 0 auto;width:8px;height:8px;border-radius:50%;background:#fb923c;';
        hlsIdleSpan.appendChild(hlsIdleDot);
        hlsIdleSpan.appendChild(document.createTextNode('대기 '));
        hlsIdleSpan.appendChild(lsIdle);
        headerLineStatus.appendChild(hlsRunSpan);
        headerLineStatus.appendChild(hlsSep);
        headerLineStatus.appendChild(hlsIdleSpan);
        headRight.appendChild(headerLineStatus);

        headRight.appendChild(liveBadge);

        var _agPopOpen = false;
        function closeAgPopover(e) {
            // 팝오버 내부 클릭('상세' 토글 등)은 닫지 않는다 — 바깥 클릭에서만 닫힘.
            if (e && agPopover.contains(e.target)) return;
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
            // 우측 정렬이 화면 밖으로 넘치면(좁은 폰) 좌측 정렬로 뒤집어 화면 안에 고정.
            var popW = agPopover.offsetWidth || 210;
            if ((window.innerWidth - r.right) + popW > window.innerWidth - 6) {
                agPopover.style.right = 'auto';
                agPopover.style.left = Math.max(6, window.innerWidth - popW - 6) + 'px';
            } else {
                agPopover.style.left = 'auto';
                agPopover.style.right = (window.innerWidth - r.right) + 'px';
            }
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

        // ── 6.5) 모바일 드로어: 오버레이 + 햄버거 토글 ──
        //   MOBILE_BP 미만에서 사이드바가 -SHELL_W 로 숨겨지고, 햄버거 클릭 시 슬라이드 인.
        //   데스크톱으로 복귀 시 aside/main 원위치, 오버레이 제거.
        var drawerOpen = false;
        // 데스크톱(>=MOBILE_BP) 접힘 상태 — localStorage 영속(탭/페이지 공유). 모바일은 drawerOpen 사용.
        // 터치(coarse pointer) 기기는 기본 접힘: 폰을 가로로 돌려 폭이 MOBILE_BP 를 넘어가도 사이드바가
        // 자동으로 펼쳐져 편집 공간을 가리지 않게 한다. 사용자가 한 번이라도 명시적으로 토글하면(localStorage)
        // 그 값을 우선한다(데스크톱은 종전대로 기본 펼침).
        var _navPref = localStorage.getItem('dspilot-nav-collapsed');
        var _isCoarse = !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
        var deskCollapsed = _navPref === null ? _isCoarse : (_navPref === '1');
        var overlay = document.createElement('div');
        overlay.style.cssText = 'display:none;position:fixed;inset:0;background:rgba(0,0,0,0.4);z-index:49;';
        document.body.appendChild(overlay);

        function setMenuIcon(name, labelOpen) {
            var ic = menuBtn.querySelector('.material-icons');
            if (ic) ic.textContent = name;
            menuBtn.setAttribute('aria-label', labelOpen ? '메뉴 열기' : '메뉴 닫기');
        }

        function openDrawer() {
            drawerOpen = true;
            aside.style.left = '0';
            overlay.style.display = 'block';
            // 드로어 뒤 페이지 스크롤 잠금(모달 패턴). closeDrawer/applyLayout 에서 해제.
            document.body.style.overflow = 'hidden';
            setMenuIcon('close', false);
        }
        function closeDrawer() {
            drawerOpen = false;
            aside.style.left = '-' + SHELL_W + 'px';
            overlay.style.display = 'none';
            document.body.style.overflow = '';
            setMenuIcon('menu', true);
        }
        overlay.addEventListener('click', closeDrawer);
        menuBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            if (window.innerWidth < MOBILE_BP) {
                // 모바일: 드로어 열기/닫기
                if (drawerOpen) closeDrawer(); else openDrawer();
            } else {
                // 데스크톱: 사이드바 접기/펼치기(영속)
                deskCollapsed = !deskCollapsed;
                localStorage.setItem('dspilot-nav-collapsed', deskCollapsed ? '1' : '0');
                applyLayout();
            }
        });

        var _layoutInit = false;
        function applyLayout() {
            // 부드러운 전환(접힘/펼침 모두). 초기 1회는 깜빡임 방지를 위해 transition 비활성.
            if (_layoutInit) {
                aside.style.transition = 'left 0.22s ease';
                main.style.transition = 'margin-left 0.22s ease';
            } else {
                aside.style.transition = '';
                main.style.transition = '';
                _layoutInit = true;
            }
            if (window.innerWidth < MOBILE_BP) {
                // 모바일: 드로어 모드. menuBtn = 햄버거.
                menuBtn.style.display = '';
                main.style.marginLeft = '0';
                if (!drawerOpen) aside.style.left = '-' + SHELL_W + 'px';
                else aside.style.left = '0';
                setMenuIcon(drawerOpen ? 'close' : 'menu', !drawerOpen);
            } else {
                // 데스크톱: 접힘 토글. menuBtn = 접기/펼치기.
                menuBtn.style.display = '';
                overlay.style.display = 'none';
                drawerOpen = false;
                document.body.style.overflow = '';
                if (deskCollapsed) {
                    aside.style.left = '-' + SHELL_W + 'px';
                    main.style.marginLeft = '0';
                    setMenuIcon('menu', true);
                } else {
                    aside.style.left = '0';
                    main.style.marginLeft = SHELL_W + 'px';
                    setMenuIcon('menu_open', false);
                }
            }
        }
        window.addEventListener('resize', applyLayout);
        // 회전/주소창 표시·숨김으로 innerWidth 가 바뀌어도 모바일 분기가 갱신되도록 보강.
        window.addEventListener('orientationchange', function () { setTimeout(applyLayout, 100); });
        if (window.visualViewport) {
            try { window.visualViewport.addEventListener('resize', applyLayout); } catch (e) { /* ignore */ }
        }
        applyLayout();

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
                    navMenu.insertBefore(buildNavLink(PLC_DEBUG_ITEM, LINK_ACTIVE, LINK_IDLE), anomalyBlock);
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
            // uptime 페이지를 보고 있는 동안은 화면에 이미 알림이 보이므로 배지를 0 으로 두고,
            // 서버 시각을 ack 로 갱신해 다른 페이지로 나가도 지금까지의 Error 는 다시 안 뜨게 한다.
            if (onUptimePage) {
                count = 0;
                if (data.serverTimeUtc) {
                    try { localStorage.setItem(ANOMALY_ACK_KEY, data.serverTimeUtc); } catch (e) { /* ignore */ }
                }
            }
            if (anomalyBadge) {
                anomalyBadge.textContent = count;
                anomalyBadge.style.display = count > 0 ? '' : 'none';
            }

            // 이상코드 피드 갱신
            renderAnomalies(data.recentAnomalies);

            var agent = data.agent || {};
            var live = HUB_LIVE[agent.hub] || HUB_LIVE.disconnected;
            liveBadge.className = 'dash-live ' + live[0];
            liveText.textContent = live[1];

            // ── Agent 상태 블록 ──
            var hub = agent.hub || 'disconnected';
            var plcTotal = agent.plcTotal || 0;
            var plcDown = agent.plcDisconnected || 0;
            var plcSource = agent.plcSource || '';
            var hasData = !!data.hasData;

            var hubLabel = hub === 'connected' ? '정상'
                : (hub === 'connecting' ? '연결 중'
                : (hub === 'reconnecting' ? '재연결 중' : '끊김'));
            agHub.text.textContent = 'PROMAKER HUB: ' + hubLabel;
            agHub.dot.style.background = hub === 'connected' ? AG_DOT.green
                : ((hub === 'connecting' || hub === 'reconnecting') ? AG_DOT.orange : AG_DOT.gray);

            // PLC 어댑터: agent=에이전트 보고 / ping=DSPilot 직접 핑 폴백 / none=대상 미설정.
            var plcLabel, plcColor;
            if (plcSource === 'ping') {
                if (plcTotal === 0) { plcLabel = 'PLC 어댑터: 대상 미설정'; plcColor = AG_DOT.gray; }
                else if (plcDown > 0) { plcLabel = 'PLC 어댑터: 응답 없음 (직접확인)'; plcColor = AG_DOT.red; }
                else { plcLabel = 'PLC 어댑터: 연결됨 (직접확인)'; plcColor = AG_DOT.green; }
            } else if (plcSource === 'agent') {
                plcLabel = plcTotal === 0 ? 'PLC 어댑터: 보고 없음'
                    : (plcDown > 0 ? 'PLC 어댑터: ' + plcDown + '대 끊김'
                    : 'PLC 어댑터: ' + plcTotal + '대 연결');
                plcColor = (plcDown === 0 && plcTotal > 0) ? AG_DOT.green
                    : (plcDown > 0 ? AG_DOT.red : AG_DOT.gray);
            } else {
                plcLabel = 'PLC 어댑터: 대상 미설정';
                plcColor = AG_DOT.gray;
            }
            agPlc.text.textContent = plcLabel;
            agPlc.dot.style.background = plcColor;

            // 상세(IP) 패널 데이터 갱신 — 열려 있으면 즉시 다시 렌더.
            _agAdapters = agent.adapters || [];
            _agPlcSource = plcSource;
            if (_plcDetailOpen) renderPlcDetail();

            agData.text.textContent = hasData ? 'PLC 데이터 수신중' : 'PLC 데이터 대기';
            agData.dot.style.background = hasData ? AG_DOT.green : AG_DOT.gray;
        }

        function pollSummary() {
            var ack = null;
            try { ack = localStorage.getItem(ANOMALY_ACK_KEY); } catch (e) { /* ignore */ }
            var url = '/api/nav/summary' + (ack ? '?anomalyAck=' + encodeURIComponent(ack) : '');
            fetch(url, { headers: { 'Accept': 'application/json' } })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (data) { if (data) applySummary(data); })
                .catch(function () { /* 폴링 실패 시 마지막 값 유지 */ });
        }
        pollSummary();
        setInterval(pollSummary, 4000);

        // ── 10) 실측 duration 자동 보정 완료 토스트 (전역) ──
        //   AutoCalibrationService 가 자동 실행으로 디바이스 duration 을 project.aasx 에 기록하면 SignalR
        //   "AutoCalibrationApplied"(요약 문자열)를 브로드캐스트한다. 어느 페이지에 있든 셸이 한 번 알림을 띄운다.
        //   (수동 "지금 실측값 채우기" 는 설정 페이지가 HTTP 응답으로 직접 토스트하므로 자동 실행만 브로드캐스트됨.)
        function showShellToast(msg) {
            var t = el('div', null, msg);
            t.style.cssText = 'position:fixed;bottom:24px;left:50%;transform:translateX(-50%);z-index:3000;'
                + 'max-width:90vw;padding:12px 18px;border-radius:8px;background:#16a34a;color:#fff;'
                + 'font-size:0.86rem;font-weight:600;box-shadow:0 6px 20px rgba(0,0,0,0.25);';
            document.body.appendChild(t);
            setTimeout(function () {
                t.style.transition = 'opacity 0.4s';
                t.style.opacity = '0';
                setTimeout(function () { t.remove(); }, 400);
            }, 6000);
        }
        function connectAutoCalToast() {
            if (!window.signalR) return;
            try {
                var conn = new signalR.HubConnectionBuilder()
                    .withUrl('/hubs/monitoring').withAutomaticReconnect().build();
                conn.on('AutoCalibrationApplied', function (summary) {
                    showShellToast('실측 duration 자동 보정 완료 — ' + (summary || '디바이스 duration 기록됨'));
                });
                conn.start().catch(function () { /* 연결 실패 시 토스트만 비활성(치명 아님) */ });
            } catch (e) { /* ignore */ }
        }
        if (window.signalR) {
            connectAutoCalToast();
        } else {
            // signalr 미로드 페이지(일부 정적 페이지)에서도 알림 받도록 동적 로드 후 연결.
            var sr = document.createElement('script');
            sr.src = '/lib/signalr.min.js';
            sr.onload = connectAutoCalToast;
            sr.onerror = function () { /* 오프라인/미존재 — 비활성 */ };
            document.head.appendChild(sr);
        }

    } catch (err) {
        try { console.error('[shell.js] failed to build app-shell', err); } catch (e) { /* ignore */ }
    }
})();
