// ── 더티 가드 API — 페이지 init 에서 window.dspDirtyRegister(() => isDirty) 로 등록 ──
// shell.js 보다 먼저 불리는 경우(e.g. defer 역전)도 있으므로 IIFE 밖에 노출한다.
window._dspDirtyChecker = null;
window.dspDirtyRegister = function (fn) { window._dspDirtyChecker = fn; };

// ── 지속시간 표기 SSOT — window.dspFmt (한국식 일/시간/분/초) ──
// 종전에는 durShort(OEE·대시보드)/cctvFmtDuration(CCTV 오버레이)이 파일마다 복붙돼 있었고
// 'ms/s/m/h/d' 영문 단위를 뿌렸다. 표기 규약을 한곳으로 모은다.
//   · 상위 2단위까지만 (일·시간 / 시간·분 / 분·초) — KPI 카드·툴팁 폭 유지
//   · 1초 미만은 ms 유지 — 정밀 진단 화면에서 '0.1초'보다 '123ms'가 읽기 쉽다
// 호출부는 반드시 호출 시점에 window.dspFmt 를 참조할 것. shell.js 는 defer 라
// 비-defer 스크립트(uptime-workspace.js 등)의 로드 시점엔 아직 없다(호출 시점엔 항상 있음).
window.dspFmt = {
    // 짧은 지속시간 — 구 durShort/cctvFmtDuration 대체. 값 없음/0 이하는 empty(기본 '—').
    //   opts.maxUnit: 'h' 를 주면 일(日) 단위로 올리지 않고 '68시간 12분' 으로 표기한다.
    //   설비 합산(Σ_flow) 값 전용 — 24h 를 넘는 합산을 '2일 20시간' 으로 쓰면 달력 날짜로 오독된다
    //   (설비 7대 라인은 하루에 최대 168시간이 정상). opts 를 2번째 인자로 바로 줘도 된다.
    dur(ms, empty, opts) {
        if (empty !== null && typeof empty === 'object') { opts = empty; empty = undefined; }
        const e = (empty === undefined) ? '—' : empty;
        if (ms == null) return e;
        const n = Number(ms);
        if (!isFinite(n) || n <= 0) return e;
        if (n < 1000) return Math.round(n) + 'ms';
        if (n < 60000) return (n / 1000).toFixed(1) + '초';
        if (n < 3600000) return Math.floor(n / 60000) + '분 ' + Math.floor(n % 60000 / 1000) + '초';
        const capHours = !!(opts && (opts.maxUnit === 'h' || opts.maxUnit === '시간'));
        if (n < 86400000 || capHours) return Math.floor(n / 3600000) + '시간 ' + Math.floor(n % 3600000 / 60000) + '분';
        return Math.floor(n / 86400000) + '일 ' + Math.floor(n % 86400000 / 3600000) + '시간';
    },
    // 시(hour) 실수값 → '2시간 15분' / '15분'. 차트 툴팁처럼 이미 시간 단위인 축에서 사용.
    durHours(h) {
        if (h == null || !isFinite(Number(h)) || Number(h) <= 0) return '0분';
        const hi = Math.floor(Number(h));
        const mi = Math.round((Number(h) - hi) * 60);
        return hi > 0 ? (hi + '시간 ' + mi + '분') : (mi + '분');
    },
};

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
 *   · 페이지 링크 (대시보드 + 기능별 3범위 트리[전체 → 시스템 → FLOW]: 생산효율/설비효율/추이/가동시간/동작편차/이상·알람 [+PLC 디버그])
 *     트리 규약은 NAV_ITEMS 주석(2026-09-08 기능 축 단일화 — 구 시스템 '○○ 관리' 아코디언 제거) 참조.
 *   · Settings (푸터)
 *   ※ 구 shell.js 의 "시스템 flow 트리 · agent 통신 상태 · 마지막 갱신" 섹션은 제거됨.
 *   ※ 사이드바 "알람 이력" 피드와 헤더 "가동/대기" 위젯은 제거됨.
 *
 * 상단 헤더(main 내부, sticky):
 *   · 페이지 제목 + 브레드크럼(Home › 제목)
 *   · 연결 배지(.dash-live, hub 상태)
 *
 * 동작 개요:
 *   - 테마: <html> 에 dark + dark-theme 동시 토글(Tailwind dark: 변형 + ds.css 다크).
 *     localStorage 'dspilot-theme'. 로드 시 적용 + 설정 페이지/다른 탭 storage 동기화(헤더 토글 버튼 제거됨).
 *   - 콘텐츠 폭: localStorage 'dspilot-content-width'(auto|fixed|wide). auto=창 폭 ≥2200px 이면 넓게. 헤더 우측 토글 + 설정 페이지 동기화.
 *   - .dsp-page(Alpine 루트) 를 main(ml-60) 안으로 이동, 슬림 헤더 제거.
 *   - /api/nav         : showPlcDebug (PLC 디버그 링크, 1회) + systems(기능별 트리 본문) + externalShortcuts.
 *   - /api/nav/summary : 이상발생 배지 · 연결 배지 · Agent 상태 (4초 폴링).
 *     이상발생 배지는 /uptime 방문 시 초기화 — serverTimeUtc 를 localStorage ack 로 박제,
 *     이후 폴링이 ?anomalyAck= 로 보내 ack 이전 Error 는 카운트에서 제외.
 *
 * 스타일 의존성: /css/stitch-shell.css (Tailwind 빌드 산출물, self-host·오프라인).
 *   stitch 유틸리티 + 셸 스코프 리셋 + 연결배지 + 다크 리맵을 모두 담는다.
 *   클래스/디자인 변경 시 → `npm run build:css` 로 재생성(tailwind.shell.config.js + tailwind/shell.input.css).
 */
(function () {
    'use strict';

    // 파비콘: 모든 정적 페이지 <head> 에 명시적 링크 주입(브라우저 기본 /favicon.ico 폴백도 존재).
    (function ensureFavicon() {
        try {
            if (document.querySelector('link[rel~="icon"]')) return;
            var head = document.head || document.getElementsByTagName('head')[0];
            if (!head) return;
            var ico = document.createElement('link');
            ico.rel = 'icon';
            ico.href = '/favicon.ico';
            ico.setAttribute('sizes', 'any');
            head.appendChild(ico);
            var png = document.createElement('link');
            png.rel = 'icon';
            png.type = 'image/png';
            png.href = '/images/favicon.png';
            head.appendChild(png);
        } catch (e) { /* noop */ }
    })();

    // 사이드바 너비(px). aside.width 와 main.margin-left 단일 소스. 인라인 style 이 w-60/ml-60 클래스를 덮음.
    var SHELL_W = 300;
    // 모바일/데스크톱 분기 기준(px). 미만이면 사이드바를 드로어로 전환.
    var MOBILE_BP = 768;

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

        // ── 1.1) 콘텐츠 폭: 분석 페이지 본문(.dsp-main)은 기본 1400px 캡(가독 폭) — 와이드 모니터(2560/3440)에서는
        //   좌우 여백이 과해져 '넓게(전체 폭)' 모드를 둔다. localStorage 'dspilot-content-width' = 'auto'|'fixed'|'wide'.
        //   auto(기본) = 창 폭 ≥ WIDE_AUTO_MIN 이면 넓게. 헤더 토글(아래) / 설정▸사용자 인터페이스 / 타 탭 storage 로 동기화.
        //   설정·폼류 페이지는 읽기 폭을 유지하고(제외), 대시보드·CCTV 는 원래 캡이 없어 대상이 아니다.
        //   <html>.dsp-wide 클래스만 켜고 실제 규칙은 1.6 의 주입 CSS(`html.dsp-wide .dsp-main{max-width:none}`).
        var WIDTH_KEY = 'dspilot-content-width';
        var WIDE_AUTO_MIN = 2200;
        var WIDE_EXCLUDED = /^\/(app\/)?(settings|settings-cloud|settings-email|demo-admin|admin-login|cctv|dashboard)(\.html)?$/;
        function widthPref() {
            try { var v = localStorage.getItem(WIDTH_KEY); return (v === 'fixed' || v === 'wide') ? v : 'auto'; } catch (e) { return 'auto'; }
        }
        function widePageCapable() {
            var p = (location.pathname || '/').replace(/\/+$/, '') || '/';
            return p !== '/' && !WIDE_EXCLUDED.test(p);
        }
        function wideEffective() {
            var v = widthPref();
            return v === 'auto' ? window.innerWidth >= WIDE_AUTO_MIN : v === 'wide';
        }
        function applyWide() {
            document.documentElement.classList.toggle('dsp-wide', widePageCapable() && wideEffective());
        }
        applyWide();

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
        //   훅: aside/header 의 .dsp-shell, 그리고 아래에서 부여하는 .dsp-shell-crumb / .dsp-shell-live-text.
        if (!document.getElementById('dsp-shell-mobile-css')) {
            var mcss = document.createElement('style');
            mcss.id = 'dsp-shell-mobile-css';
            mcss.textContent =
                /* 헤더 액션 슬롯 버튼 — 테마 무관 항상 보이는 테두리·텍스트(다크 모드에서 투명도 낮은 border 덮어씀) */
                '#dsp-header-actions .btn{' +
                  'border:1.5px solid rgba(120,140,165,0.55)!important;' +
                  'color:var(--color-text-primary)!important;' +
                  'background:var(--color-surface)!important;' +
                '}' +
                '.dark #dsp-header-actions .btn,.dark-theme #dsp-header-actions .btn{' +
                  'border-color:rgba(180,200,220,0.50)!important;' +
                '}' +
                '#dsp-header-actions .btn:disabled{opacity:0.42;cursor:not-allowed;}' +
                /* 콘텐츠 폭 '넓게': 페이지별 .dsp-main{max-width:1400px}(id 셀렉터 포함) 캡을 해제 — 1.1 의 <html>.dsp-wide 가 스위치 */
                'html.dsp-wide .dsp-main{max-width:none!important;}' +
                /* 헤더 폭 토글(아이콘 버튼) — 실시간 배지 옆, 액션 슬롯 .btn 과 같은 테두리 톤 */
                '.dsp-width-toggle{display:inline-flex;align-items:center;justify-content:center;width:32px;height:30px;padding:0;' +
                  'border:1.5px solid rgba(120,140,165,0.55);border-radius:var(--radius-sm,6px);background:var(--color-surface);' +
                  'color:var(--color-text-secondary,#556);cursor:pointer;line-height:1;transition:color .15s,border-color .15s;}' +
                '.dsp-width-toggle .material-icons{font-size:19px;}' +
                '.dsp-width-toggle:hover{color:var(--color-text-primary);}' +
                '.dsp-width-toggle[aria-pressed="true"]{color:var(--color-primary,#0058be);border-color:var(--color-primary,#0058be);}' +
                '.dark .dsp-width-toggle,.dark-theme .dsp-width-toggle{border-color:rgba(180,200,220,0.50);}' +
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
                  /* 헤더 좌측 영역 간격 축소(햄버거↔제목). */
                  'header.dsp-shell .dsp-shell-headleft{gap:10px!important;}' +
                  /* 헤더 액션 슬롯: 480px 미만에서 텍스트 숨기고 아이콘만 표시 */
                  '#dsp-header-actions .btn span:not(.material-icons){display:none!important;}' +
                '}' +
                /* 폭 토글은 데스크톱 전용(모바일/태블릿은 캡 이하라 의미 없음) */
                '@media (max-width:1500px){.dsp-width-toggle{display:none!important;}}';
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

        // ── 1.8) 공용 로딩 인디케이터 ─────────────────────────────────────────────
        //   기간/시간 범위를 바꿔 데이터를 다시 로드·렌더하는 동안 "로딩 중" 을 알린다.
        //   상단 부정형(indeterminate) 진행바 + 상단 중앙 스피너 pill 두 가지를 함께 표시.
        //   .dsp-page 유무와 무관하게 전 페이지에서 쓰도록 early-return 위에서 window 에 노출.
        //   사용: dspLoading.begin('메시지') / dspLoading.end() (참조 카운트) 또는
        //         await dspLoading.wrap(() => this.load(), '메시지') (권장 — 예외에도 항상 end).
        //   폴링(자동 4/10/60초 갱신)에는 붙이지 말 것 — 사용자가 기간을 바꾼 명시적 재로드에만.
        if (!document.getElementById('dsp-loading-css')) {
            var lcss = document.createElement('style');
            lcss.id = 'dsp-loading-css';
            lcss.textContent =
                /* 로딩 중이 아닐 때: 완전 투명 + 클릭 통과(pointer-events:none). 활성 시에만 화면 차단. */
                '#dsp-loading-host{position:fixed;inset:0;z-index:3000;pointer-events:none;opacity:0;' +
                  'transition:opacity .15s ease;}' +
                '#dsp-loading-host.is-active{opacity:1;pointer-events:auto;cursor:progress;}' +
                /* 전체 화면 스크림 — 로딩 중 날짜선택/버튼 등 모든 조작을 차단(모달처럼). 은은한 딤. */
                '#dsp-loading-host .dsp-load-scrim{position:fixed;inset:0;background:rgba(28,38,54,.08);cursor:progress;}' +
                '.dark-theme #dsp-loading-host .dsp-load-scrim{background:rgba(0,0,0,.42);}' +
                /* 상단 진행바 */
                '#dsp-loading-host .dsp-load-bar{position:fixed;top:0;left:0;right:0;height:3px;overflow:hidden;' +
                  'background:rgba(14,124,203,.18);}' +
                '#dsp-loading-host .dsp-load-bar::before{content:"";position:absolute;top:0;bottom:0;left:-40%;width:40%;' +
                  'background:#0E7CCB;border-radius:2px;animation:dsp-load-slide 1.1s infinite cubic-bezier(.4,0,.2,1);}' +
                /* 상단 중앙 pill */
                '#dsp-loading-host .dsp-load-pill{position:fixed;top:78px;left:50%;transform:translateX(-50%);' +
                  'display:flex;align-items:center;gap:10px;padding:9px 16px;border-radius:999px;' +
                  'background:rgba(255,255,255,.97);border:1px solid rgba(20,30,50,.10);' +
                  'box-shadow:0 6px 22px rgba(20,30,50,.16);' +
                  'font:600 13px/1.2 "Pretendard",system-ui,sans-serif;color:#1b2430;white-space:nowrap;}' +
                '#dsp-loading-host .dsp-load-spin{width:16px;height:16px;border-radius:50%;flex:0 0 auto;' +
                  'border:2px solid rgba(14,124,203,.25);border-top-color:#0E7CCB;' +
                  'animation:dsp-load-spin .7s linear infinite;}' +
                /* 다크 (html.dark-theme) */
                '.dark-theme #dsp-loading-host .dsp-load-bar{background:rgba(54,181,255,.20);}' +
                '.dark-theme #dsp-loading-host .dsp-load-bar::before{background:#36B5FF;}' +
                '.dark-theme #dsp-loading-host .dsp-load-pill{background:rgba(28,33,42,.97);' +
                  'border-color:rgba(255,255,255,.10);color:#e8eef6;box-shadow:0 6px 22px rgba(0,0,0,.45);}' +
                '.dark-theme #dsp-loading-host .dsp-load-spin{border-color:rgba(54,181,255,.28);border-top-color:#36B5FF;}' +
                /* 폰: pill 을 헤더 바로 아래로 살짝 올림 */
                '@media (max-width:480px){#dsp-loading-host .dsp-load-pill{top:64px;font-size:12px;padding:8px 14px;}}' +
                '@keyframes dsp-load-slide{0%{left:-40%;}100%{left:100%;}}' +
                '@keyframes dsp-load-spin{to{transform:rotate(360deg);}}' +
                /* 모션 최소화 선호 시 스피너/슬라이드 정지(투명도 표시는 유지) */
                '@media (prefers-reduced-motion:reduce){#dsp-loading-host .dsp-load-spin,' +
                  '#dsp-loading-host .dsp-load-bar::before{animation:none;}' +
                  '#dsp-loading-host .dsp-load-bar::before{left:0;width:100%;}}';
            document.head.appendChild(lcss);
        }

        if (!window.dspLoading) {
            window.dspLoading = (function () {
                var count = 0, host = null, textEl = null;
                function ensure() {
                    if (host && document.body && document.body.contains(host)) return host;
                    host = document.getElementById('dsp-loading-host');
                    if (!host) {
                        host = document.createElement('div');
                        host.id = 'dsp-loading-host';
                        host.setAttribute('role', 'status');
                        host.setAttribute('aria-live', 'polite');
                        host.innerHTML =
                            '<div class="dsp-load-scrim"></div>' +
                            '<div class="dsp-load-bar"></div>' +
                            '<div class="dsp-load-pill"><span class="dsp-load-spin"></span>' +
                            '<span class="dsp-load-text">불러오는 중…</span></div>';
                        (document.body || document.documentElement).appendChild(host);
                    }
                    textEl = host.querySelector('.dsp-load-text');
                    return host;
                }
                return {
                    // 로딩 표시 시작(참조 카운트 +1). 겹친 호출은 하나로 합쳐진다.
                    begin: function (msg) {
                        count++;
                        var h = ensure();
                        if (textEl) textEl.textContent = msg || '불러오는 중…';
                        h.classList.add('is-active');
                        try { document.documentElement.setAttribute('aria-busy', 'true'); } catch (e) { /* ignore */ }
                    },
                    // 로딩 표시 종료(참조 카운트 -1). 0 이 되면 감추고 조작 차단 해제.
                    end: function () {
                        count = Math.max(0, count - 1);
                        if (count === 0 && host) {
                            host.classList.remove('is-active');
                            try { document.documentElement.removeAttribute('aria-busy'); } catch (e) { /* ignore */ }
                        }
                    },
                    // 약속(또는 함수)이 끝날 때까지 로딩 표시. 예외가 나도 반드시 end 한다.
                    wrap: function (fnOrPromise, msg) {
                        this.begin(msg);
                        var self = this;
                        var done = function () { self.end(); };
                        try {
                            var r = (typeof fnOrPromise === 'function') ? fnOrPromise() : fnOrPromise;
                            if (r && typeof r.then === 'function') return r.then(
                                function (v) { done(); return v; },
                                function (e) { done(); throw e; });
                            done();
                            return r;
                        } catch (e) { done(); throw e; }
                    },
                    // 안전 초기화(카운트 꼬임 방지용). 페이지 전환 등에서 강제 숨김.
                    reset: function () { count = 0; if (host) host.classList.remove('is-active'); try { document.documentElement.removeAttribute('aria-busy'); } catch (e) { /* ignore */ } }
                };
            })();
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
        //   기능 축 트리(2026-09-08): 대시보드를 제외한 분석 기능은 모두 "전체 → 시스템 → FLOW" 3범위 트리.
        //     · 링크 본문 클릭 = 전체(스코프 쿼리 없음, lineScope) / 우측 chevron = 시스템 목록 펼침·접힘(이동 없음)
        //     · 시스템 행 본문 = ?system=<시스템명> / 시스템 chevron = FLOW 목록 펼침 / FLOW 행 = ?<flowParam>=<flow>
        //     · 트리는 /api/nav systems 로 채운다(buildScopeTrees). 평소 접힘, 현재 페이지의 기능 트리만 자동 펼침.
        //   (구: 시스템 '○○ 관리' 아코디언 안에 기능 그룹을 반복하는 "시스템 → 기능 → FLOW" 축. 진입점이 둘이던
        //    생산효율/설비효율/이상·알람과 축을 맞추기 위해 기능 축으로 단일화·제거.)
        //   tree 필드:
        //     flowParam  : FLOW 행 쿼리 키 — 'flow'(생산·설비효율/동작편차/이상·알람) | 'name'(추이/가동시간 분석).
        //     branchRows : 분기 활성 flow 를 "부모_분기" 행으로 치환(부모 행 소멸) — 설비효율만.
        //                  생산효율/추이/가동시간/동작편차는 부모 그대로(2026-08-27 설계 규약).
        //     parentAxis : "부모_분기" 이름으로 진입해도 부모 flow 행을 활성(동작편차 = 부모 flow 축 집계).
        //     sysTitle / flowTitle : 행 툴팁(기능별 의미 안내).
        var NAV_ITEMS = [
            { label: '대시보드',    href: '/',                    icon: 'space_dashboard', match: 'all',    legacy: '/app/dashboard.html' },
            { label: '생산효율 현황', href: '/uptime-teep', icon: 'trending_up', match: 'all', lineScope: true,
              tree: { flowParam: 'flow', sysTitle: '이 시스템 flow 합산 생산효율', flowTitle: '이 설비의 생산효율' } },
            { label: '설비효율 현황', href: '/uptime-oee',  icon: 'speed',       match: 'all', lineScope: true, legacy: ['/uptime', '/oee'],
              tree: { flowParam: 'flow', branchRows: true, sysTitle: '이 시스템 flow 합산 설비효율(OEE)', flowTitle: '이 설비의 설비효율(OEE)' } },
            // 추이 분석 전체 = 전 flow 히스토리 합산(allMode), ?system= = 그 시스템 flow 만 합산(2026-09-08 추가).
            { label: '추이 분석',   href: '/flow-trend',  icon: 'timeline',    match: 'all', lineScope: true,
              tree: { flowParam: 'name', sysTitle: '이 시스템 flow 합산 추이', flowTitle: '이 설비의 기간별 추이' } },
            // 가동시간 분석 전체/시스템 = 조회 전용 개요(카드 = 시작/끝 call + CT 리본), FLOW = 간트·분기 편집 페이지.
            { label: '가동시간 분석', href: '/flow-cycle', icon: 'account_tree', match: 'all', lineScope: true,
              tree: { flowParam: 'name', sysTitle: '이 시스템 flow 개요(조회 전용)', flowTitle: '이 설비의 가동시간 분석(간트·분기 편집)' } },
            // 동작편차 전체 = 전 flow, ?system= = 그 시스템 flow 만(2026-09-08 추가), ?flow= = 설비(분기 이름은 부모로 번역).
            { label: '동작편차',    href: '/heatmap',     icon: 'gradient',    match: 'all', lineScope: true,
              tree: { flowParam: 'flow', parentAxis: true, sysTitle: '이 시스템 flow 의 동작편차', flowTitle: '이 설비의 동작편차' } },
            // 이상·알람: 시스템 행 = 알람 행 systemName 등식(UserTag=AASX System, Abnormal=flow→System 해석) → 둘 다 포함,
            //   FLOW 행 = 자동감지만(UserTag 는 Flow 소속이 아님 — uptime-workspace utQs 주석). badge = 최근 10분 Error 수.
            { label: '이상·알람',    href: '/uptime-alarm', icon: 'warning_amber', match: 'all', lineScope: true, badge: true,
              tree: { flowParam: 'flow', sysTitle: '이 시스템의 이상·알람(자동감지 + 수동등록TAG)', flowTitle: '이 설비의 자동감지 알람만' } },
            // OEE 메뉴 숨김 — 페이지(/oee)는 URL 로 접근 가능, 네비에서만 제외. 복구는 이 줄 주석 해제.
            // { label: 'OEE',         href: '/oee',                 icon: 'precision_manufacturing', match: 'prefix', legacy: '/app/oee.html' },
            // CCTV 메뉴 숨김 — 실시간 시청은 대시보드 레이아웃 카드의 'CCTV' 토글에서 사용. /cctv 는 설정(카메라·오버레이 편집) 페이지로 URL/[설정] 버튼 접근. 복구는 이 줄 주석 해제.
            // { label: 'CCTV',        href: '/cctv',                icon: 'videocam',        match: 'prefix', legacy: '/app/cctv.html' }
        ];
        var PLC_DEBUG_ITEM = { label: 'PLC 디버그', href: '/plc-debug', icon: 'bug_report', match: 'prefix', legacy: '/app/plc-debug.html' };
        var SETTINGS_ITEM = { label: '설정', href: '/settings', icon: 'settings', match: 'prefix', legacy: '/app/settings.html' };
        // 외부 도구 바로가기(설비박사·ReverseAI)는 하드코딩이 아니라 /api/nav 의 externalShortcuts 로 내려온다
        // (데모 전환 활성 + 개별 노출 체크 시에만). 라벨·URL 은 /demo/admin 관리 패널에서 설정한다.

        var path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        function isActive(item) {
            // 전체(라인) 링크는 스코프 쿼리가 붙은 시스템/설비 화면에서 활성 표시하지 않는다.
            if (item.lineScope) {
                var q = new URLSearchParams(location.search);
                if (q.get('flow') || q.get('system') || q.get('name')) return false;
            }
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
            // 외부 도구 바로가기: 새 탭으로 열고(현재 모니터링 페이지 유지), 우측에 open_in_new 아이콘 표시.
            if (item.external) {
                a.target = '_blank';
                a.rel = 'noopener noreferrer';
            }
            a.appendChild(icon(item.icon));
            a.appendChild(el('span', 'font-label-sm text-label-sm', item.label));
            if (item.external) {
                var ext = icon('open_in_new');
                ext.style.cssText = 'margin-left:auto;flex:0 0 auto;font-size:15px;opacity:0.5;';
                a.appendChild(ext);
            }
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

        // 이상·알람 최상위 링크 우측 배지(현재 1개). 폴링(applySummary)이 배열 전체를 갱신한다(배열 유지 = 호환).
        var anomalyBadges = [];
        // 이상·알람 페이지 진입 = 배지 읽음 처리. serverTimeUtc 를 localStorage(전 페이지/탭 공유)에 ack 로 기록하고,
        // 이후 폴링은 anomalyAck 파라미터로 보내 그 시각 이전 Error 를 배지 카운트에서 제외한다.
        // (물리 분리 2026-07-01: 알람은 /uptime-alarm 에만 표시되므로 ack 은 그 페이지에서만.)
        var ANOMALY_ACK_KEY = 'dspilot-anomaly-ack';
        var onAlarmPage = (path === '/uptime-alarm');

        // ── 3.5) 스코프 컨텍스트 — 현재 페이지가 어느 기능 트리에 속하고 어떤 범위(전체/시스템/FLOW)인지. ──
        //   FLOW(설비) 쿼리가 있으면 ?system= 은 무시한다(설비 우선 — 각 페이지 init 과 같은 규약).
        var qs = new URLSearchParams(location.search);
        function onItemPage(item) {
            var cands = [item.href].concat(item.legacy || []).filter(Boolean).map(function (p) { return p.replace(/\/+$/, '') || '/'; });
            return cands.indexOf(path) !== -1;
        }
        var curItem = null;   // 현재 페이지의 기능 항목(트리 있는 것만). 대시보드/설정 등은 null.
        NAV_ITEMS.forEach(function (it) { if (it.tree && !curItem && onItemPage(it)) curItem = it; });
        var curFlow   = curItem ? (qs.get(curItem.tree.flowParam) || '') : '';
        var curSystem = curItem && !curFlow ? (qs.get('system') || '') : '';

        // ── 4) 최상위 링크 + 기능별 트리 컨테이너(링크 바로 뒤). 트리 본문은 /api/nav 도착 후 buildScopeTrees 가 채운다. ──
        //   펼침 규칙: 평소 접힘, 현재 페이지의 기능 트리만 자동 펼침(선택 시스템/FLOW 가 보이게). 수동 토글은 비영속.
        NAV_ITEMS.forEach(function (item) {
            var link = buildNavLink(item, LINK_ACTIVE, LINK_IDLE);
            navMenu.appendChild(link);
            if (!item.tree) return;
            var isCurPage = (item === curItem);
            // 라벨을 flex:1 로 늘려 배지·chevron 을 우측 끝에 정렬.
            if (link.children[1]) link.children[1].style.cssText += 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
            // 시스템/설비 스코프로 이 기능 페이지에 있으면 전체 링크는 활성(lineScope)이 아니지만 트리 항목이 활성이므로,
            // 루트 아이콘·라벨만 파랑으로 컨텍스트를 표시한다.
            if (isCurPage && !isActive(item)) {
                if (link.children[0]) link.children[0].style.color = '#2170e4';
                if (link.children[1]) { link.children[1].style.color = '#2170e4'; link.children[1].style.fontWeight = '600'; }
            }
            // 배지(이상·알람: 최근 10분 Error) — 라벨 뒤, chevron 앞. 0건이면 숨김(applySummary).
            if (item.badge) {
                var badge = el('span', 'inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full bg-error text-white text-[10px] font-bold');
                badge.title = '최근 10분 내 Error 알림 (이상·알람 방문 시 초기화)';
                badge.style.cssText = 'flex:0 0 auto;margin-right:4px;display:none;';
                link.appendChild(badge);
                anomalyBadges.push(badge);
            }
            // chevron = 트리 펼침/접힘만(이동 안 함 — <a> 기본 이동을 막는다). 링크 본문 클릭은 그대로 전체 이동.
            var chev = icon('chevron_right');
            chev.style.cssText = 'flex:0 0 auto;font-size:16px;transition:transform 0.12s;cursor:pointer;padding:2px;margin:-2px;border-radius:4px;';
            chev.setAttribute('role', 'button');
            chev.setAttribute('aria-label', '펼치기/접기');
            link.appendChild(chev);
            var wrap = el('div', 'flex flex-col gap-0.5');
            wrap.style.cssText = 'padding-left:18px;';
            navMenu.appendChild(wrap);
            var open = isCurPage;
            function applyOpen() {
                wrap.style.display = open ? '' : 'none';
                chev.style.transform = open ? 'rotate(90deg)' : '';
                chev.setAttribute('aria-expanded', open ? 'true' : 'false');
            }
            chev.addEventListener('click', function (e) {
                e.preventDefault(); e.stopPropagation();
                open = !open;
                applyOpen();
            });
            applyOpen();
            item._treeWrap = wrap;
        });

        // ── 더티 가드 내부 구현 ──
        // 페이지별 dirty 체크 함수(window._dspDirtyChecker)가 true 를 반환하면,
        // 사이드바 링크·플로우 버튼 클릭 시 커스텀 확인 모달을 띄워 이탈을 한 번 막는다.
        var _dirtyPendingUrl = null;
        var _dirtyModal = null;
        function _isDirty() {
            try {
                var chk = window._dspDirtyChecker;
                if (!chk) return false;
                var v = chk();
                console.debug('[dirty-guard] checker=', !!chk, ' value=', v);
                return !!v;
            } catch (e) { console.warn('[dirty-guard] error', e); return false; }
        }
        function _buildDirtyModal() {
            var ov = document.createElement('div');
            ov.style.cssText = 'display:none;position:fixed;inset:0;background:rgba(0,0,0,0.45);z-index:9999;align-items:center;justify-content:center;';
            var box = document.createElement('div');
            box.style.cssText = 'background:var(--color-surface,#fff);border:1px solid var(--color-lines,#e2e8f0);border-radius:12px;'
                + 'box-shadow:0 8px 32px rgba(0,0,0,0.2);padding:24px 28px;max-width:360px;width:90%;';
            var ttl = document.createElement('div');
            ttl.style.cssText = 'font-size:0.9375rem;font-weight:700;color:var(--color-text,#0f172a);margin-bottom:8px;display:flex;align-items:center;gap:8px;';
            var warnIc = document.createElement('span');
            warnIc.className = 'material-icons';
            warnIc.style.cssText = 'font-size:20px;color:#f59e0b;flex:0 0 auto;';
            warnIc.textContent = 'warning_amber';
            ttl.appendChild(warnIc);
            ttl.appendChild(document.createTextNode('저장하지 않은 변경사항'));
            box.appendChild(ttl);
            var msg = document.createElement('p');
            msg.style.cssText = 'font-size:0.875rem;color:var(--color-text-secondary,#475569);margin:0 0 20px;line-height:1.5;';
            msg.textContent = '이 페이지를 떠나면 변경사항이 사라집니다. 계속하시겠습니까?';
            box.appendChild(msg);
            var btns = document.createElement('div');
            btns.style.cssText = 'display:flex;gap:10px;justify-content:flex-end;';
            var btnLeave = document.createElement('button');
            btnLeave.type = 'button';
            btnLeave.textContent = '페이지 이동';
            btnLeave.style.cssText = 'padding:8px 18px;border-radius:7px;border:0;background:var(--color-primary,#0e7ccb);color:#fff;'
                + 'font-size:0.875rem;font-weight:600;cursor:pointer;';
            var btnStay = document.createElement('button');
            btnStay.type = 'button';
            btnStay.textContent = '취소';
            btnStay.style.cssText = 'padding:8px 18px;border-radius:7px;border:1px solid var(--color-lines,#cbd5e1);background:transparent;'
                + 'font-size:0.875rem;font-weight:600;cursor:pointer;color:var(--color-text,#334155);';
            btns.appendChild(btnLeave);
            btns.appendChild(btnStay);
            box.appendChild(btns);
            ov.appendChild(box);
            document.body.appendChild(ov);
            btnLeave.addEventListener('click', function () {
                ov.style.display = 'none';
                var url = _dirtyPendingUrl; _dirtyPendingUrl = null;
                // 이동 확정: dirty 체크 해제 후 이동(beforeunload 가 다시 막지 않도록)
                if (url) { window._dspDirtyChecker = null; location.href = url; }
            });
            btnStay.addEventListener('click', function () { ov.style.display = 'none'; _dirtyPendingUrl = null; });
            ov.addEventListener('click', function (e) { if (e.target === ov) { ov.style.display = 'none'; _dirtyPendingUrl = null; } });
            document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && ov.style.display === 'flex') { ov.style.display = 'none'; _dirtyPendingUrl = null; } });
            return ov;
        }
        function navigateTo(url) {
            if (_isDirty()) {
                if (!_dirtyModal) _dirtyModal = _buildDirtyModal();
                _dirtyPendingUrl = url;
                _dirtyModal.style.display = 'flex';
            } else {
                location.href = url;
            }
        }

        // ── 4.5) 기능별 스코프 트리 본문(시스템 → FLOW) — /api/nav systems 로 채운다. ──
        //   시스템 행 본문 클릭 = 그 시스템 스코프 이동(base?system=), 우측 chevron = FLOW 목록 펼침/접힘만.
        //   FLOW 행 클릭 = base?<flowParam>=. 시스템은 현재 선택 FLOW 소속이거나 현재 시스템 스코프일 때만 자동 펼침.
        function buildScopeTrees(systems) {
            systems = systems || [];
            // preflight(전역 리셋) 꺼진 셸 빌드 → <button> 네이티브 테두리·배경 제거(nav 링크와 동일한 룩).
            var BTN_RESET = 'appearance:none;-webkit-appearance:none;background:transparent;border:0;cursor:pointer;font:inherit;';
            var ROW_CLS = 'w-full flex items-center gap-2 px-3 py-2 rounded transition-colors text-on-surface-variant dark:text-surface-variant';
            var HOVER_CLS = ' hover:bg-surface-container-high dark:hover:bg-inverse-surface';

            function dot(color, op) {
                var d = el('span');
                d.style.cssText = 'flex:0 0 auto;width:5px;height:5px;border-radius:50%;background:' + color + ';opacity:' + op + ';';
                return d;
            }
            // 같은 페이지 안에서 전체/시스템/FLOW 만 바꿔 이동할 때 현재 URL 의 기간 선택(?period/from/to)을 같이
            // 실어 보낸다 — 대상 페이지 init 이 이 파라미터로 기간을 복원(uptime-workspace syncPeriodUrl 참조).
            // 다른 페이지로의 이동은 기간 의미가 달라질 수 있어 전파하지 않는다.
            function withPeriodCarry(href) {
                var qIdx = href.indexOf('?');
                var p = qIdx === -1 ? href : href.slice(0, qIdx);
                if (location.pathname !== p) return href;
                var q = new URLSearchParams(qIdx === -1 ? '' : href.slice(qIdx + 1));
                var cur = new URLSearchParams(location.search);
                ['period', 'from', 'to'].forEach(function (k) { if (cur.has(k) && !q.has(k)) q.set(k, cur.get(k)); });
                var s = q.toString();
                return p + (s ? '?' + s : '');
            }
            // "부모_분기" 가상 이름 → 부모 flow(없으면 그대로). 시스템 1개 기준.
            function parentFlowOf(name, flows, fbr) {
                if (!name || flows.indexOf(name) !== -1) return name;
                for (var i = 0; i < flows.length; i++) {
                    var brs = fbr[flows[i]] || [];
                    for (var j = 0; j < brs.length; j++)
                        if (flows[i] + '_' + brs[j] === name) return flows[i];
                }
                return name;
            }

            NAV_ITEMS.forEach(function (item) {
                var t = item.tree, wrap = item._treeWrap;
                if (!t || !wrap) return;
                wrap.innerHTML = '';
                var onPage = (item === curItem);
                var base = item.href;

                systems.forEach(function (sys) {
                    var flows = sys.flows || [];
                    var fbr = sys.flowBranches || {};
                    var sysName = sys.name || '';

                    // FLOW 행 값 목록 — branchRows(설비효율)면 분기 활성 flow 를 "부모_분기" 로 치환.
                    var rows = [];
                    flows.forEach(function (f) {
                        var brs = t.branchRows ? (fbr[f] || null) : null;
                        if (brs && brs.length) brs.forEach(function (b) { rows.push(f + '_' + b); });
                        else rows.push(f);
                    });
                    // 활성 FLOW — parentAxis(동작편차)면 "부모_분기" 진입도 부모 행 활성.
                    var effFlow = (onPage && t.parentAxis) ? parentFlowOf(curFlow, flows, fbr) : curFlow;
                    var flowInSys = onPage && !!effFlow && rows.indexOf(effFlow) !== -1;
                    var sysCur = onPage && !!curSystem && curSystem === sysName;

                    var row = el('button', ROW_CLS + (sysCur ? '' : HOVER_CLS));
                    row.type = 'button';
                    row.style.cssText = 'text-align:left;' + BTN_RESET;
                    row.title = (sysName || '(이름 없음)') + ' — ' + t.sysTitle;
                    if (sysCur) { row.style.backgroundColor = '#2170e4'; row.style.color = '#fff'; }
                    var sIcon = icon('equalizer');
                    sIcon.style.cssText = 'flex:0 0 auto;font-size:17px;'
                        + (sysCur ? 'opacity:1;' : (flowInSys ? 'color:#2170e4;opacity:1;' : 'opacity:0.8;'));
                    row.appendChild(sIcon);
                    var sLabel = el('span', 'font-label-sm text-label-sm', sysName || '(이름 없음)');
                    sLabel.style.cssText = 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;'
                        + (flowInSys ? 'color:#2170e4;font-weight:600;' : '');
                    row.appendChild(sLabel);
                    var chev = icon('chevron_right');
                    chev.style.cssText = 'flex:0 0 auto;font-size:16px;transition:transform 0.12s;cursor:pointer;padding:2px;margin:-2px;border-radius:4px;';
                    chev.setAttribute('role', 'button');
                    chev.setAttribute('aria-label', '펼치기/접기');
                    row.appendChild(chev);

                    var list = el('div', 'flex flex-col gap-0.5');
                    list.style.cssText = 'display:none;padding-left:16px;';
                    rows.forEach(function (flowName) {
                        var isCur = onPage && effFlow === flowName;
                        var fb = el('button', ROW_CLS + (isCur ? '' : HOVER_CLS));
                        fb.type = 'button';
                        fb.style.cssText = 'text-align:left;' + BTN_RESET;
                        fb.title = flowName + ' — ' + t.flowTitle;
                        if (isCur) { fb.style.backgroundColor = '#2170e4'; fb.style.color = '#fff'; }
                        fb.appendChild(dot(isCur ? '#fff' : 'currentColor', isCur ? '1' : '0.55'));
                        var fl = el('span', 'font-label-sm text-label-sm', flowName);
                        fl.style.cssText = 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                        fb.appendChild(fl);
                        fb.addEventListener('click', function (ev) {
                            ev.stopPropagation();
                            navigateTo(withPeriodCarry(base + '?' + t.flowParam + '=' + encodeURIComponent(flowName)));
                        });
                        list.appendChild(fb);
                    });

                    var open = flowInSys || sysCur;
                    function applyOpen() {
                        list.style.display = open ? '' : 'none';
                        chev.style.transform = open ? 'rotate(90deg)' : '';
                        row.setAttribute('aria-expanded', open ? 'true' : 'false');
                    }
                    chev.addEventListener('click', function (e) { e.stopPropagation(); open = !open; applyOpen(); });
                    row.addEventListener('click', function (e) {
                        e.stopPropagation();
                        navigateTo(withPeriodCarry(base + '?system=' + encodeURIComponent(sysName)));
                    });
                    applyOpen();

                    wrap.appendChild(row);
                    wrap.appendChild(list);
                });
            });

            // ── 헤더 제목·브레드크럼에 스코프 반영: "<FLOW|시스템> <기능명>" / Home › 기능명 › 시스템 › FLOW ──
            //   스코프 쿼리가 없는 전체 보기는 페이지 기본 제목 그대로(시스템 1개 현장이라도 시스템으로 오표기하지 않음).
            //   headTitle·crumb 은 var 선언 후 async 전에 이미 할당 → 클로저로 접근 가능.
            if (curItem && pageTitle && (curFlow || curSystem)) {
                // "가동시간 분석 · 전체" → "가동시간 분석" (공백으로 둘러싼 ' · ' 뒤 view 한정어만 제거 —
                //   '이상·알람 분석' 처럼 낱말 안의 '·' 는 보존. 구 정규식은 이걸 '이상' 으로 잘랐다.)
                var funcName = pageTitle.replace(/\s+·\s+.+$/, '');
                var sysName = curSystem;
                if (curFlow) {
                    // FLOW 의 소속 시스템(분기 가상 이름은 부모로 번역). 모델에 없는 flow(유령)면 시스템 단계 생략.
                    for (var i = 0; i < systems.length && !sysName; i++) {
                        var fl = systems[i].flows || [];
                        if (fl.indexOf(parentFlowOf(curFlow, fl, systems[i].flowBranches || {})) !== -1) sysName = systems[i].name || '';
                    }
                }
                headTitle.textContent = (curFlow || sysName) + ' ' + funcName;
                crumb.innerHTML = '';
                crumb.appendChild(el('span', null, 'Home'));
                crumb.appendChild(el('span', 'material-icons text-[16px]', 'chevron_right'));
                crumb.appendChild(el('span', null, funcName));
                if (sysName) {
                    crumb.appendChild(el('span', 'material-icons text-[16px]', 'chevron_right'));
                    crumb.appendChild(el('span', curFlow ? null : 'text-primary font-semibold', sysName));
                }
                if (curFlow) {
                    crumb.appendChild(el('span', 'material-icons text-[16px]', 'chevron_right'));
                    crumb.appendChild(el('span', 'text-primary font-semibold', curFlow));
                }
            }
        }

        // 푸터 (외부 바로가기[데모 게이트 활성 시] + 설정)
        var footer = el('div', 'p-4 border-t border-outline-variant dark:border-outline flex flex-col gap-1');
        var settingsLink = buildNavLink(SETTINGS_ITEM, SET_ACTIVE, SET_IDLE);
        footer.appendChild(settingsLink);
        aside.appendChild(footer);

        // 외부 도구 바로가기(설비박사 챗봇·ReverseAI PLCtoAASX) — 데모 관리자 게이트가 활성일 때만 노출.
        //   /api/nav 의 externalShortcuts 배열(데모 전환 활성 + 개별 노출 체크한 항목)로 아래 콜백에서 렌더한다.
        //   게이트 비활성이면 아예 DOM 에 넣지 않아 사이드바에 흔적이 없다('바로가기' 라벨 포함 미렌더).
        //   '설정' 링크 앞에 '바로가기' 라벨 + 링크들을 삽입한다.
        function renderExternalShortcuts(items) {
            if (!items || !items.length) return;
            if (footer.querySelector('[data-dsp-ext-shortcut]')) return;   // 중복 렌더 방지
            var label = el('div', 'text-[10px] uppercase font-bold tracking-wider text-outline px-4 mb-1', '바로가기');
            label.setAttribute('data-dsp-ext-shortcut', '');
            footer.insertBefore(label, settingsLink);
            items.forEach(function (s) {
                // external:true → buildNavLink 이 새 탭(target=_blank) + open_in_new 아이콘 부여.
                var item = { label: s.label, href: s.href, icon: s.icon || 'open_in_new', external: true };
                var link = buildNavLink(item, SET_ACTIVE, SET_IDLE);
                link.setAttribute('data-dsp-ext-shortcut', '');
                footer.insertBefore(link, settingsLink);
            });
        }

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
        // 우측 위젯(실시간 배지)은 축소 금지 — 좌측 제목이 먼저 말줄임되도록.
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
        var agScan = agentRow();
        var agData = agentRow();
        agHub.text.textContent = 'PROMAKER HUB: —';
        agPlc.text.textContent = 'PLC 어댑터: —';
        // 수집 방식 — Promaker 업로드 시 선택(런타임 세팅 "PLC 읽기 방식"). 상태가 아닌 구성 정보라
        // 점 색은 중립 파랑 고정, session.json 이 없으면(업로드 이력 없음) 행 자체를 숨긴다.
        agScan.text.textContent = 'PLC 수집: —';
        agScan.row.style.display = 'none';
        agScan.row.title = 'Promaker 업로드 시 선택한 PLC 읽기 방식';
        agData.text.textContent = 'PLC 데이터 수신중';
        agData.row.style.display = 'none';  // 실제 수신 확인 전까지 숨김 — 수신되면 applySummary 가 켠다.

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
        agPopCard.appendChild(agScan.row);
        agPopCard.appendChild(agData.row);
        agPopover.appendChild(agPopCard);
        document.body.appendChild(agPopover);

        // ── PLC 어댑터 상세(IP) 렌더 — applySummary 가 채우는 최신 어댑터/출처를 사용. ──
        var _agAdapters = [];   // [{name, ip, port, connected, error, vendor}]
        var _agPlcSource = '';  // 'agent' | 'ping' | 'none'
        var _agAddr = null;     // {expected, seen, missing[]} — 모델 주소 수신 커버리지(진단 표시 전용)
        var _agAddrSystems = []; // [{system, expected, seen, missing[]}] — 멀티 PLC 시스템별 분해
        var _plcDetailOpen = false;
        function renderPlcDetail() {
            agPlcDetail.innerHTML = '';
            // 모델 주소 수신 커버리지 — "연결은 정상인데 그 주소만 0 건"(주소 오타/영역 불일치)은 어댑터
            // 상태로는 안 보인다. 주소 정리 작업 중 즉시 확인할 수 있게 상세 패널 맨 위에 노출.
            // 전부 수신 중이면 표시하지 않는다(정상 상태에 노이즈 금지).
            // 멀티 PLC(시스템 2개 이상)면 "어느 PLC 구간이 안 오는가"가 핵심이라 시스템별 행으로 분해해 보여준다.
            var addrPartial = _agAddr && _agAddr.expected > 0 && _agAddr.seen < _agAddr.expected;
            if (addrPartial && _agAddrSystems.length > 1) {
                _agAddrSystems.forEach(function (s) {
                    if (!s || !s.expected) return;
                    var full = s.seen >= s.expected;
                    var row = el('div', null, s.system + ' · 주소 ' + s.seen + '/' + s.expected + ' 수신'
                        + (full ? '' : ' — ' + (s.expected - s.seen) + '개 미수신'));
                    row.style.cssText = 'font-weight:700;color:'
                        + (full ? AG_DOT.green : (s.seen === 0 ? AG_DOT.red : AG_DOT.orange)) + ';';
                    if (!full && s.missing && s.missing.length)
                        row.title = '미수신 주소: ' + s.missing.join(', ')
                            + (s.expected - s.seen > s.missing.length ? ' …' : '');
                    agPlcDetail.appendChild(row);
                });
                var gap = el('div'); gap.style.cssText = 'height:6px;';
                agPlcDetail.appendChild(gap);
            } else if (addrPartial) {
                var warn = el('div', null, '모델 주소 ' + _agAddr.expected + '개 중 '
                    + _agAddr.seen + '개 수신 · ' + (_agAddr.expected - _agAddr.seen) + '개 미수신 — 주소 확인 필요');
                warn.style.cssText = 'margin-bottom:6px;font-weight:700;color:' + AG_DOT.orange + ';';
                if (_agAddr.missing && _agAddr.missing.length)
                    warn.title = '미수신 주소: ' + _agAddr.missing.join(', ')
                        + (_agAddr.expected - _agAddr.seen > _agAddr.missing.length ? ' …' : '');
                agPlcDetail.appendChild(warn);
            }
            if (!_agAdapters.length) {
                var none = el('div', null, _agPlcSource === 'none'
                    ? '대상 PLC 가 설정되어 있지 않습니다.'
                    : 'PLC 정보 없음');
                none.style.opacity = '0.7';
                agPlcDetail.appendChild(none);
                return;
            }
            _agAdapters.forEach(function (a) {
                var line = el('div');
                line.style.cssText = 'display:flex;align-items:center;gap:6px;flex-wrap:wrap;';
                var d = el('span');
                d.style.cssText = 'flex:0 0 auto;width:7px;height:7px;border-radius:50%;background:'
                    + (a.connected ? AG_DOT.green : AG_DOT.red) + ';';
                var label = (a.name || 'PLC') + ' · ' + (a.ip || '?') + ':' + (a.port || 0);
                var nm = el('span', null, label);
                nm.style.cssText = 'font-variant-numeric:tabular-nums;';
                line.appendChild(d);
                line.appendChild(nm);
                // 매칭 시스템 — 서버가 모델 AID(ip:port)와 대조한 결과.
                //   system=이름: 그 시스템 소속 / system="": 모델에 없음(구 모델 잔존/설정 불일치 후보)
                //   / system=null(구 서버·모델 미로드): 표기 생략.
                if (a.system != null) {
                    var sysChip = el('span', null, a.system === '' ? '모델 미매칭' : a.system);
                    sysChip.style.cssText = 'flex:0 0 auto;font-size:10px;line-height:1;padding:2px 6px;'
                        + 'border-radius:999px;font-weight:700;'
                        + (a.system === ''
                            ? 'background:rgba(251,146,60,0.16);color:#c2610c;'
                            : 'background:rgba(33,112,228,0.12);color:#2170e4;');
                    sysChip.title = a.system === ''
                        ? '현재 모델(AASX)의 PLC 접속정보에 이 IP:Port 가 없습니다 — 이전 모델의 잔존 상태이거나 접속정보 불일치일 수 있습니다.'
                        : '현재 모델에서 이 PLC 와 매칭된 시스템: ' + a.system;
                    line.appendChild(sysChip);
                }
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

        // ── 페이지별 액션 슬롯 (Excel 다운로드 등) — x-teleport 대상 ──
        var pageActionsSlot = document.createElement('div');
        pageActionsSlot.id = 'dsp-header-actions';
        pageActionsSlot.style.cssText = 'display:flex;align-items:center;gap:6px;';
        headRight.appendChild(pageActionsSlot);

        headRight.appendChild(liveBadge);

        // ── 콘텐츠 폭 토글(기본 1400px ↔ 넓게=전체 폭) — 1.1 의 pref 를 명시값으로 박제(auto 종료). 제외 페이지는 숨김. ──
        var widthBtn = el('button', 'dsp-width-toggle');
        widthBtn.type = 'button';
        var widthIc = icon('width_full');
        widthBtn.appendChild(widthIc);
        function renderWidthBtn() {
            widthBtn.style.display = widePageCapable() ? '' : 'none';
            var w = wideEffective();
            widthIc.textContent = w ? 'width_normal' : 'width_full';
            widthBtn.setAttribute('aria-pressed', w ? 'true' : 'false');
            var pref = widthPref();
            widthBtn.title = (w ? '화면 폭: 넓게(전체 폭)' : '화면 폭: 기본(최대 1400px)')
                + (pref === 'auto' ? ' · 자동' : '')
                + ' — 클릭: ' + (w ? '기본 폭(1400px)으로' : '넓게(전체 폭)로') + ' 전환';
            widthBtn.setAttribute('aria-label', widthBtn.title);
        }
        widthBtn.addEventListener('click', function () {
            var next = wideEffective() ? 'fixed' : 'wide';
            try { localStorage.setItem(WIDTH_KEY, next); } catch (e) { /* ignore */ }
            applyWide();
            renderWidthBtn();
        });
        renderWidthBtn();
        headRight.appendChild(widthBtn);
        // auto 모드는 창 폭에 따르므로 리사이즈 시 재평가(모니터 이동/창 축소).
        window.addEventListener('resize', function () { applyWide(); renderWidthBtn(); });
        window.addEventListener('storage', function (e) {
            if (e.key === WIDTH_KEY) { applyWide(); renderWidthBtn(); }
        });

        var _agPopOpen = false;
        function closeAgPopover(e) {
            // 팝오버 내부 클릭('상세' 토글 등)은 닫지 않는다 — 바깥 클릭에서만 닫힘.
            if (e && agPopover.contains(e.target)) return;
            // 배지 자체 클릭도 여기서 닫지 않는다. 이 리스너는 document 캡처 단계라 배지의 click 핸들러보다
            // 먼저 실행되므로, 여기서 닫아버리면 뒤이어 도는 배지 핸들러가 _agPopOpen=false 를 보고 다시 열어
            // 두 번째 클릭이 영원히 접히지 않는다. 배지 클릭의 열기/닫기는 배지 핸들러의 토글에만 맡긴다.
            if (e && liveBadge.contains(e.target)) return;
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

        // ── 더티 가드: 페이지 내 모든 <a> 링크 클릭 인터셉트 ──
        document.addEventListener('click', function (e) {
            var a = e.target.closest('a[href]');
            if (!a) return;
            var href = a.getAttribute('href');
            if (!href || href.charAt(0) === '#' || href.indexOf('javascript:') === 0) return;
            // 새 탭 링크(외부 바로가기)는 현재 페이지를 떠나지 않으므로 더티 가드 제외 — 그대로 새 탭에서 열림.
            if (a.target === '_blank') return;
            if (!_isDirty()) return;
            e.preventDefault();
            navigateTo(href);
        });

        // ── 더티 가드: 브라우저 뒤로가기·새로고침·닫기 ──
        window.addEventListener('beforeunload', function (e) {
            if (_isDirty()) { e.preventDefault(); e.returnValue = ''; return ''; }
        });

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
                    navMenu.appendChild(buildNavLink(PLC_DEBUG_ITEM, LINK_ACTIVE, LINK_IDLE));
                }
                // 외부 바로가기 — 서버가 내려준 목록(데모 전환 활성 + 개별 노출 체크)만 푸터에 삽입.
                renderExternalShortcuts(data.externalShortcuts);
                buildScopeTrees(data.systems);
            })
            .catch(function () { /* ignore */ });

        // ── 9) /api/nav/summary: 이상발생 배지 + 연결 배지 + Agent 상태 (주기 폴링) ──
        var HUB_LIVE = {
            connected:    ['is-live', '실시간'],
            connecting:   ['is-poll', '연결 중'],
            reconnecting: ['is-poll', '재연결 중'],
            disconnected: ['is-poll', '연결 끊김']
        };

        function applySummary(data) {
            var count = data.anomalyActiveCount || 0;
            // 이상·알람 페이지를 보고 있는 동안은 화면에 이미 알림이 보이므로 배지를 0 으로 두고,
            // 서버 시각을 ack 로 갱신해 다른 페이지로 나가도 지금까지의 Error 는 다시 안 뜨게 한다.
            if (onAlarmPage) {
                count = 0;
                if (data.serverTimeUtc) {
                    try { localStorage.setItem(ANOMALY_ACK_KEY, data.serverTimeUtc); } catch (e) { /* ignore */ }
                }
            }
            // 이상·알람 최상위 링크 배지 갱신(배열 = 호환 유지, 현재 1개) — 전역 Error 카운트.
            anomalyBadges.forEach(function (b) {
                b.textContent = count;
                b.style.display = count > 0 ? '' : 'none';
            });

            var agent = data.agent || {};

            // ── Agent 상태 블록 ──
            var hub = agent.hub || 'disconnected';
            var plcTotal = agent.plcTotal || 0;
            var plcDown = agent.plcDisconnected || 0;
            var plcSource = agent.plcSource || '';
            var hasData = !!data.receivingData;

            var hubLabel = hub === 'connected' ? '정상'
                : (hub === 'connecting' ? '연결 중'
                : (hub === 'reconnecting' ? '재연결 중' : '끊김'));
            agHub.text.textContent = 'PROMAKER HUB: ' + hubLabel;
            agHub.dot.style.background = hub === 'connected' ? AG_DOT.green
                : ((hub === 'connecting' || hub === 'reconnecting') ? AG_DOT.orange : AG_DOT.gray);

            // PLC 어댑터: agent=에이전트 보고 / ping=DSPilot 직접 핑 폴백 / none=대상 미설정.
            // 멀티 PLC(2대 이상)는 몇 대가 죽었는지가 정보라 개수를 병기한다.
            var plcLabel, plcColor;
            if (plcSource === 'ping') {
                if (plcTotal === 0) { plcLabel = 'PLC 어댑터: 대상 미설정'; plcColor = AG_DOT.gray; }
                else if (plcDown > 0) {
                    plcLabel = 'PLC 어댑터: ' + (plcTotal > 1
                        ? plcDown + '/' + plcTotal + '대 응답 없음 (직접확인)'
                        : '응답 없음 (직접확인)');
                    plcColor = AG_DOT.red;
                }
                else {
                    plcLabel = 'PLC 어댑터: ' + (plcTotal > 1
                        ? plcTotal + '대 연결 (직접확인)'
                        : '연결됨 (직접확인)');
                    plcColor = AG_DOT.green;
                }
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

            // 수집 방식(Promaker 업로드 시 선택) — direct=Agent 직접 / delegated=Edge 단말 위임.
            // null(구 서버·업로드 이력 없음)이면 행을 숨겨 노이즈를 만들지 않는다.
            var scanMode = agent.plcScanMode || null;
            if (scanMode) {
                agScan.text.textContent = 'PLC 수집: ' + (scanMode === 'delegated'
                    ? 'Edge 단말 위임' : 'Agent 직접');
                agScan.dot.style.background = '#60a5fa';
                agScan.row.style.display = '';
            } else {
                agScan.row.style.display = 'none';
            }

            // 상세(IP) 패널 데이터 갱신 — 열려 있으면 즉시 다시 렌더.
            _agAdapters = agent.adapters || [];
            _agAddr = { expected: agent.addrExpected || 0, seen: agent.addrSeen || 0, missing: agent.addrMissing || [] };
            _agAddrSystems = agent.addrSystems || [];
            _agPlcSource = plcSource;
            if (_plcDetailOpen) renderPlcDetail();

            // 실제 유입이 있을 때만 "수신중" 행을 노출, 그 전(대기/끊김)에는 행 자체를 숨긴다.
            agData.row.style.display = hasData ? '' : 'none';
            agData.text.textContent = 'PLC 데이터 수신중';
            agData.dot.style.background = AG_DOT.green;

            // 유입 공백 경과 → " 4분 18초" 꼴 접미사. 15초 미만/미제공은 빈 문자열(배지 문구 무변).
            function formatGap(sec) {
                if (typeof sec !== 'number' || !isFinite(sec) || sec < 15) return '';
                var s = Math.floor(sec);
                if (s < 60) return ' ' + s + '초';
                var m = Math.floor(s / 60), r = s % 60;
                if (m < 60) return ' ' + m + '분' + (r ? ' ' + r + '초' : '');
                return ' ' + Math.floor(m / 60) + '시간 ' + (m % 60) + '분';
            }

            // ── 접힌 배지: 3행(Hub·PLC·데이터) 최악 상태 반영 ──
            var badgeClass, badgeText;
            if (hub !== 'connected') {
                var hl = HUB_LIVE[hub] || HUB_LIVE.disconnected;
                badgeClass = hl[0]; badgeText = hl[1];
            } else if (plcColor === AG_DOT.red) {
                badgeClass = 'is-warn'; badgeText = 'PLC 끊김';
            } else if (plcColor === AG_DOT.green && !hasData) {
                // 공백 길이를 병기 — 15초 순간 공백(사이클 사이 정상 대기)과 수 분짜리 수집 장애를
                // 사용자가 구분할 수 있게 한다. 경과 미제공(구 서버)이면 종전 문구 그대로.
                badgeClass = 'is-warn'; badgeText = '데이터 대기' + formatGap(data.inboundGapSeconds);
            } else if (plcColor === AG_DOT.green) {
                badgeClass = 'is-live'; badgeText = '실시간';
            } else {
                badgeClass = 'is-poll'; badgeText = 'PLC 미설정';
            }
            liveBadge.className = 'dash-live ' + badgeClass;
            liveText.textContent = badgeText;
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
        // 숨긴 탭은 폴링 정지(방치 탭의 상시 유입 차단), 다시 보이면 즉시 1회 재조회로 따라잡기.
        setInterval(function () { if (!document.hidden) pollSummary(); }, 4000);
        document.addEventListener('visibilitychange', function () { if (!document.hidden) pollSummary(); });

        // ── 10) 전역 토스트 ──
        function showShellToast(msg, type) {
            var bg = type === 'warning' ? '#d97706' : type === 'error' ? '#dc2626' : '#16a34a';
            var t = el('div', null, msg);
            t.style.cssText = 'position:fixed;bottom:24px;left:50%;transform:translateX(-50%);z-index:3000;'
                + 'max-width:90vw;padding:12px 18px;border-radius:8px;background:' + bg + ';color:#fff;'
                + 'font-size:0.86rem;font-weight:600;box-shadow:0 6px 20px rgba(0,0,0,0.25);';
            document.body.appendChild(t);
            setTimeout(function () {
                t.style.transition = 'opacity 0.4s';
                t.style.opacity = '0';
                setTimeout(function () { t.remove(); }, 400);
            }, 6000);
        }
        // 전역 노출 — 페이지 스크립트(기간 클램프 안내 등)가 셸 토스트를 재사용.
        window.dspToast = showShellToast;

        // ── 커스텀 기간 상한(2개월) 공용 클램프 ──
        //   초과 시 편집 기준(anchor: 'start'|'end', 기본 'end')을 고정하고 반대편을 당긴다.
        //   호출측은 clamped=true 면 dspToast 로 안내하고 되써진 값을 입력에 반영한다.
        //   62일 = "2개월" 의 SSOT — 서버 인메모리 미러 창(63일)보다 항상 작게 유지할 것.
        window.DSP_MAX_RANGE_DAYS = 62;
        window.dspClampRange = function (start, end, anchor) {
            var maxMs = window.DSP_MAX_RANGE_DAYS * 864e5;
            if (!(start instanceof Date) || !(end instanceof Date) || isNaN(start) || isNaN(end))
                return { start: start, end: end, clamped: false };
            if (end.getTime() - start.getTime() <= maxMs)
                return { start: start, end: end, clamped: false };
            if (anchor === 'start') return { start: start, end: new Date(start.getTime() + maxMs), clamped: true };
            return { start: new Date(end.getTime() - maxMs), end: end, clamped: true };
        };
        window.dspRangeClampMsg = '기간은 최대 2개월까지 선택할 수 있어 자동으로 조정했습니다.';
    } catch (err) {
        try { console.error('[shell.js] failed to build app-shell', err); } catch (e) { /* ignore */ }
    }
})();
