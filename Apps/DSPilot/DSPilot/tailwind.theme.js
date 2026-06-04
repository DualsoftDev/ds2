/*
 * 공유 stitch 테마 — dashboard2.html 인라인 tailwind.config 와 동일한 토큰.
 * full 빌드(tailwind.config.js)와 shell 빌드(tailwind.shell.config.js)가 함께 require 한다.
 * 디자인 토큰을 바꿀 때는 이 파일 한 곳만 수정 → `npm run build:css` 재생성.
 */
module.exports = {
  colors: {
    'secondary': '#0058be', 'on-primary-fixed-variant': '#3c475a', 'tertiary-fixed': '#fadfb8',
    'background': '#f7f9fb', 'on-surface-variant': '#45474c', 'inverse-primary': '#bcc7de',
    'on-secondary-container': '#fefcff', 'primary-fixed-dim': '#bcc7de', 'on-secondary-fixed': '#001a42',
    'primary': '#091426', 'surface-container-lowest': '#ffffff', 'tertiary': '#1e1200',
    'outline-variant': '#c5c6cd', 'on-error-container': '#93000a', 'surface-bright': '#f7f9fb',
    'error': '#ba1a1a', 'surface-container-low': '#f2f4f6', 'on-tertiary-container': '#a38c6a',
    'tertiary-fixed-dim': '#ddc39d', 'surface-container-high': '#e6e8ea', 'inverse-on-surface': '#eff1f3',
    'on-background': '#191c1e', 'secondary-container': '#2170e4', 'on-primary-container': '#8590a6',
    'inverse-surface': '#2d3133', 'on-primary-fixed': '#111c2d', 'surface': '#f7f9fb',
    'surface-dim': '#d8dadc', 'secondary-fixed': '#d8e2ff', 'on-tertiary-fixed': '#271902',
    'secondary-fixed-dim': '#adc6ff', 'primary-fixed': '#d8e3fb', 'surface-variant': '#e0e3e5',
    'on-tertiary': '#ffffff', 'on-surface': '#191c1e', 'on-tertiary-fixed-variant': '#564427',
    'surface-tint': '#545f73', 'on-error': '#ffffff', 'surface-container': '#eceef0',
    'outline': '#75777d', 'on-primary': '#ffffff', 'error-container': '#ffdad6',
    'on-secondary': '#ffffff', 'primary-container': '#1e293b', 'surface-container-highest': '#e0e3e5',
    'on-secondary-fixed-variant': '#004395', 'tertiary-container': '#35260c'
  },
  borderRadius: { 'DEFAULT': '0.125rem', 'lg': '0.25rem', 'xl': '0.5rem', 'full': '0.75rem' },
  spacing: { 'gutter': '16px', 'stack-sm': '8px', 'stack-md': '16px', 'card-padding': '20px', 'container-margin': '24px' },
  // 모든 토큰에 한글 폴백(Noto Sans KR)+sans-serif 추가 — Inter 는 라틴 전용이라
  // 폴백이 없으면 한글이 OS 기본폰트로 떨어져 본문(Noto)과 글꼴이 달라진다(제각각). Inter 우선=라틴/숫자, 한글=Noto.
  fontFamily: {
    'display-metrics': ['Inter', 'Noto Sans KR', 'sans-serif'], 'headline-lg': ['Inter', 'Noto Sans KR', 'sans-serif'], 'headline-md': ['Inter', 'Noto Sans KR', 'sans-serif'],
    'body-lg': ['Inter', 'Noto Sans KR', 'sans-serif'], 'label-sm': ['Inter', 'Noto Sans KR', 'sans-serif'], 'body-md': ['Inter', 'Noto Sans KR', 'sans-serif'], 'mono-data': ['Inter', 'Noto Sans KR', 'sans-serif']
  },
  fontSize: {
    'display-metrics': ['32px', { lineHeight: '40px', letterSpacing: '-0.02em', fontWeight: '700' }],
    'headline-lg': ['24px', { lineHeight: '32px', fontWeight: '600' }],
    'headline-md': ['18px', { lineHeight: '24px', fontWeight: '600' }],
    'body-lg': ['16px', { lineHeight: '24px', fontWeight: '400' }],
    'label-sm': ['12px', { lineHeight: '16px', letterSpacing: '0.02em', fontWeight: '500' }],
    'body-md': ['14px', { lineHeight: '20px', fontWeight: '400' }],
    'mono-data': ['14px', { lineHeight: '20px', fontWeight: '600' }]
  }
};
