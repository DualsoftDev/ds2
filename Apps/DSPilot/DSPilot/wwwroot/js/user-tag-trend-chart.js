// UserTag 추이 차트 (Chart.js stacked bar + Top N 가로 bar + 레벨 도넛).
// /user-tags 페이지가 .NET interop 으로 호출.

const charts = {};

// 차트 색은 paint 시점에 테마 토큰을 읽어 산출한다 (다크/라이트 토글 대응).
// Error=red / Warning=amber 는 심각도 시맨틱이라 고정. Info=브랜드 azure accent.
function cssVar(name, fallback) {
    const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return v || fallback;
}

function levelColors() {
    const azure = cssVar('--color-primary', '#0E7CCB');
    return {
        'Error':   { fill: 'rgba(239, 68, 68, 0.85)',  border: 'rgb(239, 68, 68)'  },
        'Warning': { fill: 'rgba(245, 158, 11, 0.85)', border: 'rgb(245, 158, 11)' },
        'Info':    { fill: `color-mix(in srgb, ${azure} 70%, transparent)`, border: azure },
    };
}

// 구분(ABNORMAL/USERTAG) 색 — 이상알람TAG 로즈레드로 통일.
function categoryColors() {
    return {
        'ABNORMAL': { fill: 'rgba(190, 18, 60, 0.85)', border: 'rgb(190, 18, 60)' },
        'USERTAG':  { fill: 'rgba(190, 18, 60, 0.85)', border: 'rgb(190, 18, 60)' },
    };
}

// 범례 표시용 한글 라벨(데이터 키는 서버가 주는 ABNORMAL/USERTAG 코드 유지).
const CATEGORY_LABELS = { 'ABNORMAL': '자동감지', 'USERTAG': '이상알람TAG' };

// 축/범례/툴팁 텍스트·격자선을 테마 가변으로 (다크 캔버스에서 가독성 확보).
function themeChartColors() {
    return {
        grid: cssVar('--color-lines', 'rgba(127, 127, 127, 0.12)'),
        gridSoft: cssVar('--color-lines', 'rgba(127, 127, 127, 0.08)'),
        text: cssVar('--color-text-secondary', '#5b6b7d'),
        textStrong: cssVar('--color-text-primary', '#0E1B2A'),
        surface: cssVar('--color-surface', '#ffffff'),
    };
}

function destroyIfExists(id) {
    if (charts[id]) {
        try { charts[id].destroy(); } catch (e) { /* ignore */ }
        delete charts[id];
    }
}

// 다크/라이트 토글 시에만 차트를 재생성(색 재계산)하기 위한 테마 시그니처.
function isDark() {
    return document.documentElement.classList.contains('dark-theme');
}

// timeBuckets: [{ bucketStartIso, level, count }] — level 슬롯은 이제 구분(ABNORMAL/USERTAG)을 담는다.
// cats: 표시할 구분 목록(기본 둘 다). 설비별 보기는 ['ABNORMAL'] 만 넘겨 자동감지 단일로 그린다
//   (설비별은 서버가 자동감지만 주고 FillBucketGaps 가 USERTAG 0-채움 버킷을 남기므로, 여기서 명시적으로 배제).
// onBarClick: 막대 클릭 드릴다운 콜백 — ({ iso, total }) 로 그 버킷 한 칸을 알려준다(구분은 스택 합산:
//   interaction.mode='index' 라 클릭 지점이 어느 스택 조각인지 모호하고, 화면의 구분 필터가 이미 적용돼 있다).
//   in-place 갱신 경로에서도 최신 콜백이 쓰이도록 옵션 클로저가 아닌 chart._onBarClick 을 통해 호출한다.
export function renderTrendChart(chartId, timeBuckets, granularity, cats, onBarClick) {
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    // 버킷 시작 시각 기준으로 unique label 추출 (정렬 유지)
    const seen = new Map();
    for (const b of timeBuckets) {
        if (!seen.has(b.bucketStartIso)) seen.set(b.bucketStartIso, true);
    }
    const labels = Array.from(seen.keys());
    const labelToIdx = new Map(labels.map((l, i) => [l, i]));

    const CAT_COLORS = categoryColors();
    const tc = themeChartColors();
    const catList = (cats && cats.length) ? cats : ['ABNORMAL', 'USERTAG'];
    const datasets = catList.map(cat => {
        const data = new Array(labels.length).fill(0);
        for (const b of timeBuckets) {
            if (b.level === cat) {
                const idx = labelToIdx.get(b.bucketStartIso);
                if (idx !== undefined) data[idx] = b.count;
            }
        }
        const color = CAT_COLORS[cat] || CAT_COLORS.USERTAG;
        return {
            label: CATEGORY_LABELS[cat] || cat,
            data,
            backgroundColor: color.fill,
            borderColor: color.border,
            borderWidth: 1,
            stack: 'category',
        };
    });

    const timeUnit = ({
        'hour':  'hour',
        'day':   'day',
        'week':  'week',
        'month': 'month',
    })[granularity] || 'day';

    // 같은 canvas·테마면 destroy+new Chart 대신 in-place 갱신 — 차트 재생성 canvas/GPU churn 방지(dashboard2 와 동일 정책).
    const existing = charts[chartId];
    if (existing && existing.canvas === canvas && existing._dark === isDark()) {
        existing.data.labels = labels;
        existing.data.datasets = datasets;
        existing.options.scales.x.time.unit = timeUnit;
        existing._onBarClick = onBarClick;
        existing.update('none');
        return;
    }

    destroyIfExists(chartId);
    const chart = new Chart(canvas.getContext('2d'), {
        type: 'bar',
        data: { labels, datasets },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            // 막대 클릭 = 그 버킷 드릴다운(어떤 태그가 몇 시에 떴는지). 0건 칸은 열지 않는다.
            onClick(evt, els, chart) {
                const el = (els && els.length) ? els[0] : null;
                if (!el) return;
                const iso = chart.data.labels[el.index];
                const total = chart.data.datasets.reduce((a, ds) => a + (Number(ds.data[el.index]) || 0), 0);
                if (!iso || total <= 0) return;
                if (typeof chart._onBarClick === 'function') chart._onBarClick({ iso, total });
            },
            onHover(evt, els, chart) {
                const hit = !!(els && els.length) && chart.data.datasets.some(ds => (Number(ds.data[els[0].index]) || 0) > 0);
                const target = evt?.native?.target;
                if (target) target.style.cursor = (hit && typeof chart._onBarClick === 'function') ? 'pointer' : 'default';
            },
            scales: {
                x: {
                    type: 'time',
                    // 툴팁·축 라벨 시각 표기 — ISO 숫자형(현장 표준, 앱 전역 통일). day=07-04 / hour=09:00 / month=2026-07.
                    time: {
                        unit: timeUnit,
                        tooltipFormat: 'yyyy-MM-dd HH:mm',
                        displayFormats: { hour: 'HH:mm', day: 'MM-dd', week: 'MM-dd', month: 'yyyy-MM' },
                    },
                    stacked: true,
                    grid: { color: tc.gridSoft },
                    ticks: { color: tc.text },
                },
                y: {
                    stacked: true,
                    beginAtZero: true,
                    ticks: { precision: 0, color: tc.text },
                    grid: { color: tc.grid },
                },
            },
            plugins: {
                legend: { position: 'top', labels: { color: tc.text } },
                tooltip: { enabled: true, backgroundColor: tc.surface, titleColor: tc.textStrong, bodyColor: tc.text, borderColor: tc.grid, borderWidth: 1 },
            },
        },
    });
    chart._dark = isDark();
    chart._onBarClick = onBarClick;
    charts[chartId] = chart;
}

// altName(콤마 구분 이름 목록) → 축 두 번째 줄. 한 주소에 이름이 여럿 붙을 수 있어(자동감지 4유형 등)
// 앞 2개만 적고 나머지는 "외 N" 으로 접는다(전체 목록은 툴팁에 그대로 표시).
const TOP_ALT_INLINE = 2;
function altNames(alt) {
    return String(alt || '').split(',').map(s => s.trim()).filter(Boolean);
}
function altLabel(alt) {
    const names = altNames(alt);
    if (names.length === 0) return '';
    if (names.length <= TOP_ALT_INLINE) return names.join(', ');
    return names.slice(0, TOP_ALT_INLINE).join(', ') + ` 외 ${names.length - TOP_ALT_INLINE}`;
}

// 상태 스택 색 — 해소(호박)·미해소(로즈)·자동감지(보라). 색각 이상 3종 분리와 표면 대비를
// 검증한 조합(dataviz validate_palette, light/dark 통과). 상태색이라 시계열의 구분색(로즈 단색)과는 다르다.
// '복구 완료'(azure) 조각은 2026-10-01 뺐다 — 그 판정은 /api/user-tags/reliability 를 매 폴링 돌려야 나왔고,
// 디바이스별 판정은 Excel 시트가 정본이 됐다(doc/31 §8). 해소 ⊇ 복구이므로 해소 조각이 그 몫을 품는다.
function topStateColors() {
    return {
        cleared:   { fill: 'rgba(217, 119, 6, 0.85)',  label: '해소',
                     tip: '조건이 풀렸습니다 — 재가동 여부는 Excel 디바이스별 시트에서' },
        open:      { fill: 'rgba(190, 18, 60, 0.85)',  label: '미해소',
                     tip: '조건이 아직 걸려 있습니다' },
        abnormal:  { fill: 'rgba(124, 58, 237, 0.85)', label: '자동감지',
                     tip: '점 이벤트라 해소 개념이 없습니다' },
    };
}
const TOP_STATE_KEYS = ['cleared', 'open', 'abnormal'];

// topRows: [{ name, level, count, altName, clearedCount }] — level 슬롯은 구분(ABNORMAL/USERTAG).
// 막대 하나 = 경로(주소) 하나의 발생 수를 상태로 쌓은 것 — 해소 / 미해소(이상알람TAG), 발생(자동감지).
//   해소 = 서버 집계 clearedCount. 스냅샷 한 응답으로 다 그려진다(추가 조회 없음).
// 축 라벨은 2줄 — 1줄=그룹키(경로 기준이면 태그 주소), 2줄=반대편 이름(altName). 주소만으로는 어떤
// 이상알람TAG/자동감지인지 알 수 없어 둘을 함께 보여준다.
export function renderTopChart(chartId, topRows) {
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const C = topStateColors();
    const tc = themeChartColors();
    const labels = topRows.map(r => {
        const key = r.name || '(주소 없음)';
        const alt = altLabel(r.altName);
        return alt ? [key, alt] : [key];
    });
    const cats = topRows.map(r => r.level);
    const alts = topRows.map(r => altNames(r.altName));
    const totals = topRows.map(r => r.count || 0);
    const seg = topRows.map(r => {
        const total = r.count || 0;
        if (r.level === 'ABNORMAL') return { cleared: 0, open: 0, abnormal: total };
        const cleared = Math.min(total, r.clearedCount || 0);
        return { cleared, open: total - cleared, abnormal: 0 };
    });
    const series = TOP_STATE_KEYS.map(k => seg.map(s => s[k]));

    // 같은 canvas·테마면 in-place 갱신(차트 재생성 churn 방지). 데이터셋 3개는 고정이라 순서로 맞춘다.
    const existing = charts[chartId];
    if (existing && existing.canvas === canvas && existing._dark === isDark()) {
        existing.data.labels = labels;
        TOP_STATE_KEYS.forEach((k, i) => { existing.data.datasets[i].data = series[i]; });
        existing._rowCats = cats;
        existing._rowAlts = alts;
        existing._rowTotals = totals;
        existing.update('none');
        return;
    }

    destroyIfExists(chartId);
    const chart = new Chart(canvas.getContext('2d'), {
        type: 'bar',
        data: {
            labels,
            datasets: TOP_STATE_KEYS.map((k, i) => ({
                label: C[k].label,
                data: series[i],
                backgroundColor: C[k].fill,
                // 조각 사이 1px 표면색 경계 — 같은 막대 안에서 상태 경계가 보이게.
                borderColor: tc.surface,
                borderWidth: 1,
                stack: 'state',
            })),
        },
        options: {
            indexAxis: 'y',
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: { stacked: true, beginAtZero: true, ticks: { precision: 0, color: tc.text }, grid: { color: tc.gridSoft } },
                y: { stacked: true, ticks: { autoSkip: false, color: tc.text }, grid: { color: tc.grid } },
            },
            plugins: {
                legend: {
                    display: true, position: 'bottom',
                    labels: {
                        color: tc.text, boxWidth: 10, boxHeight: 10,
                        // 이 화면에 없는 상태(예: 자동감지만 볼 때의 복구/해소/미해소)는 범례에서 지운다.
                        filter: (item, data) => (data.datasets[item.datasetIndex]?.data || []).some(v => v > 0),
                    },
                },
                tooltip: {
                    enabled: true, backgroundColor: tc.surface, titleColor: tc.textStrong, bodyColor: tc.text, footerColor: tc.text, borderColor: tc.grid, borderWidth: 1,
                    callbacks: {
                        // 축은 "외 N" 으로 접히므로 툴팁엔 주소 + 이름 전체를 펼쳐 보여준다.
                        title(items) {
                            const i = items[0]?.dataIndex ?? 0;
                            const chart = items[0]?.chart;
                            const raw = chart?.data?.labels?.[i];
                            const key = Array.isArray(raw) ? raw[0] : raw;
                            const names = chart?._rowAlts?.[i] || [];
                            return names.length ? [String(key), ...names.map(n => '· ' + n)] : [String(key)];
                        },
                        label(ctx) {
                            const k = TOP_STATE_KEYS[ctx.datasetIndex];
                            return `${ctx.dataset.label}: ${ctx.parsed.x}` + (C[k]?.tip ? ` — ${C[k].tip}` : '');
                        },
                        // 조각 하나만 짚어도 막대 전체(발생 수)와 구분을 함께 읽게.
                        footer(items) {
                            const i = items[0]?.dataIndex ?? 0;
                            const chart = items[0]?.chart;
                            const cat = CATEGORY_LABELS[chart?._rowCats?.[i]] || '';
                            return `발생 ${chart?._rowTotals?.[i] ?? 0}` + (cat ? ` · ${cat}` : '');
                        },
                    },
                },
            },
        },
    });
    chart._dark = isDark();
    chart._rowCats = cats;
    chart._rowAlts = alts;
    chart._rowTotals = totals;
    charts[chartId] = chart;
}

// levelCounts: { Info:N, Warning:N, Error:N }
export function renderLevelDoughnut(chartId, levelCounts) {
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const LEVEL_COLORS = levelColors();
    const tc = themeChartColors();
    const labels = ['Info', 'Warning', 'Error'];
    const data = labels.map(l => levelCounts[l] || 0);
    const colors = labels.map(l => (LEVEL_COLORS[l] || LEVEL_COLORS.Info).fill);
    const borders = labels.map(l => (LEVEL_COLORS[l] || LEVEL_COLORS.Info).border);

    // 같은 canvas·테마면 in-place 갱신(차트 재생성 churn 방지).
    const existing = charts[chartId];
    if (existing && existing.canvas === canvas && existing._dark === isDark()) {
        const ds = existing.data.datasets[0];
        ds.data = data; ds.backgroundColor = colors; ds.borderColor = borders;
        existing.update('none');
        return;
    }

    destroyIfExists(chartId);
    const chart = new Chart(canvas.getContext('2d'), {
        type: 'doughnut',
        data: {
            labels,
            datasets: [{
                data,
                backgroundColor: colors,
                borderColor: borders,
                borderWidth: 1,
            }],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: { position: 'bottom', labels: { color: tc.text } },
                tooltip: { backgroundColor: tc.surface, titleColor: tc.textStrong, bodyColor: tc.text, borderColor: tc.grid, borderWidth: 1 },
            },
        },
    });
    chart._dark = isDark();
    charts[chartId] = chart;
}
