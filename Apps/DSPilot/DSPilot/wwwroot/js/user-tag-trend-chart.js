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

// 구분(ABNORMAL/USERTAG) 색 — 레벨이 Error 단일로 통일된 뒤 시계열/버킷은 구분으로 스택한다.
// 둘 다 Error 알람이라 빨강 계열로 통일하되, 스택/도넛에서 구분되도록 톤을 다르게 둔다.
// ABNORMAL(자동감지)=밝은 빨강, USERTAG(사용자지정)=진한 로즈레드.
function categoryColors() {
    return {
        'ABNORMAL': { fill: 'rgba(239, 68, 68, 0.85)', border: 'rgb(239, 68, 68)' },
        'USERTAG':  { fill: 'rgba(190, 18, 60, 0.85)',  border: 'rgb(190, 18, 60)' },
    };
}

// 범례 표시용 한글 라벨(데이터 키는 서버가 주는 ABNORMAL/USERTAG 코드 유지).
const CATEGORY_LABELS = { 'ABNORMAL': '자동감지', 'USERTAG': '사용자지정' };

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
export function renderTrendChart(chartId, timeBuckets, granularity) {
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
    const cats = ['ABNORMAL', 'USERTAG'];
    const datasets = cats.map(cat => {
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
            scales: {
                x: {
                    type: 'time',
                    time: { unit: timeUnit, tooltipFormat: 'yyyy-MM-dd HH:mm' },
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
    charts[chartId] = chart;
}

// topRows: [{ name, level, count }]
export function renderTopChart(chartId, topRows) {
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const LEVEL_COLORS = levelColors();
    const tc = themeChartColors();
    const labels = topRows.map(r => r.name);
    const counts = topRows.map(r => r.count);
    const colors = topRows.map(r => (LEVEL_COLORS[r.level] || LEVEL_COLORS.Info).fill);

    // 같은 canvas·테마면 in-place 갱신(차트 재생성 churn 방지).
    const existing = charts[chartId];
    if (existing && existing.canvas === canvas && existing._dark === isDark()) {
        existing.data.labels = labels;
        const ds = existing.data.datasets[0];
        ds.data = counts; ds.backgroundColor = colors; ds.borderColor = colors;
        existing.update('none');
        return;
    }

    destroyIfExists(chartId);
    const chart = new Chart(canvas.getContext('2d'), {
        type: 'bar',
        data: {
            labels,
            datasets: [{
                label: '알림 수',
                data: counts,
                backgroundColor: colors,
                borderColor: colors,
                borderWidth: 1,
            }],
        },
        options: {
            indexAxis: 'y',
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: { beginAtZero: true, ticks: { precision: 0, color: tc.text }, grid: { color: tc.gridSoft } },
                y: { ticks: { autoSkip: false, color: tc.text }, grid: { color: tc.grid } },
            },
            plugins: {
                legend: { display: false },
                tooltip: { enabled: true, backgroundColor: tc.surface, titleColor: tc.textStrong, bodyColor: tc.text, borderColor: tc.grid, borderWidth: 1 },
            },
        },
    });
    chart._dark = isDark();
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

export function downloadCsv(filename, csvContent) {
    const blob = new Blob([new Uint8Array([0xEF, 0xBB, 0xBF]), csvContent], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}
