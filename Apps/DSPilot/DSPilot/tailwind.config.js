/*
 * full stitch 빌드 — dashboard2.html 및 향후 단독 stitch 페이지(ds.css 미로드)용.
 *   입력: tailwind/stitch.input.css  →  출력: wwwroot/css/stitch.css
 * preflight(기본 base) 포함 — 단독 페이지라 전역 리셋이 안전/필요.
 */
const theme = require('./tailwind.theme');
module.exports = {
  darkMode: 'class',
  content: ['./wwwroot/app/**/*.html'],
  theme: { extend: theme },
  plugins: [require('@tailwindcss/forms'), require('@tailwindcss/container-queries')],
};
