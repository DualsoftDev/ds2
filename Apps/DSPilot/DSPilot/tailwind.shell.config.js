/*
 * shell 빌드 — shell.js 가 ds.css 페이지(heatmap/uptime/cycle/flow/cctv/settings/plc-debug/dashboard)에
 * 주입하는 stitch 셸(사이드바+헤더)용.  입력: tailwind/shell.input.css → 출력: wwwroot/css/stitch-shell.css
 *   · content = shell.js (클래스 문자열 리터럴을 스캔 → 셸이 쓰는 유틸리티만 생성).
 *   · preflight 끔 — 본문 ds.css 페이지의 전역 리셋 충돌 방지(셸 리셋은 shell.input.css 의 .dsp-shell 스코프).
 *   · 플러그인 없음 — 셸은 forms/container-queries 불필요(페이지 입력폼 간섭 방지).
 */
const theme = require('./tailwind.theme');
module.exports = {
  darkMode: 'class',
  content: ['./wwwroot/app/shell.js'],
  corePlugins: { preflight: false },
  theme: { extend: theme },
  plugins: [],
};
