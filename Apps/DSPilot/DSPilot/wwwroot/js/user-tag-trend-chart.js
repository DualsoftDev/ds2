// UserTag 추이 차트 (Chart.js stacked bar + Top N 가로 bar + 레벨 도넛).
// /user-tags 페이지가 .NET interop 으로 호출.

const charts = {};

const LEVEL_COLORS = {
    'Error':   { fill: 'rgba(239, 68, 68, 0.85)',  border: 'rgb(239, 68, 68)'  },
    'Warning': { fill: 'rgba(245, 158, 11, 0.85)', border: 'rgb(245, 158, 11)' },
    'Info':    { fill: 'rgba(59, 130, 246, 0.7)',  border: 'rgb(59, 130, 246)' },
};

function destroyIfExists(id) {
    if (charts[id]) {
        try { charts[id].destroy(); } catch (e) { /* ignore */ }
        delete charts[id];
    }
}

// timeBuckets: [{ bucketStartIso, level, count }]
// levels: ["Info","Warning","Error"]
export function renderTrendChart(chartId, timeBuckets, granularity) {
    destroyIfExists(chartId);
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    // 버킷 시작 시각 기준으로 unique label 추출 (정렬 유지)
    const seen = new Map();
    for (const b of timeBuckets) {
        if (!seen.has(b.bucketStartIso)) seen.set(b.bucketStartIso, true);
    }
    const labels = Array.from(seen.keys());
    const labelToIdx = new Map(labels.map((l, i) => [l, i]));

    const levels = ['Info', 'Warning', 'Error'];
    const datasets = levels.map(lvl => {
        const data = new Array(labels.length).fill(0);
        for (const b of timeBuckets) {
            if (b.level === lvl) {
                const idx = labelToIdx.get(b.bucketStartIso);
                if (idx !== undefined) data[idx] = b.count;
            }
        }
        const color = LEVEL_COLORS[lvl] || LEVEL_COLORS.Info;
        return {
            label: lvl,
            data,
            backgroundColor: color.fill,
            borderColor: color.border,
            borderWidth: 1,
            stack: 'level',
        };
    });

    const timeUnit = ({
        'hour':  'hour',
        'day':   'day',
        'week':  'week',
        'month': 'month',
    })[granularity] || 'day';

    charts[chartId] = new Chart(canvas.getContext('2d'), {
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
                    grid: { color: 'rgba(127, 127, 127, 0.08)' },
                },
                y: {
                    stacked: true,
                    beginAtZero: true,
                    ticks: { precision: 0 },
                    grid: { color: 'rgba(127, 127, 127, 0.12)' },
                },
            },
            plugins: {
                legend: { position: 'top' },
                tooltip: { enabled: true },
            },
        },
    });
}

// topRows: [{ name, level, count }]
export function renderTopChart(chartId, topRows) {
    destroyIfExists(chartId);
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const labels = topRows.map(r => r.name);
    const counts = topRows.map(r => r.count);
    const colors = topRows.map(r => (LEVEL_COLORS[r.level] || LEVEL_COLORS.Info).fill);

    charts[chartId] = new Chart(canvas.getContext('2d'), {
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
                x: { beginAtZero: true, ticks: { precision: 0 } },
                y: { ticks: { autoSkip: false } },
            },
            plugins: {
                legend: { display: false },
                tooltip: { enabled: true },
            },
        },
    });
}

// levelCounts: { Info:N, Warning:N, Error:N }
export function renderLevelDoughnut(chartId, levelCounts) {
    destroyIfExists(chartId);
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const labels = ['Info', 'Warning', 'Error'];
    const data = labels.map(l => levelCounts[l] || 0);
    const colors = labels.map(l => (LEVEL_COLORS[l] || LEVEL_COLORS.Info).fill);

    charts[chartId] = new Chart(canvas.getContext('2d'), {
        type: 'doughnut',
        data: {
            labels,
            datasets: [{
                data,
                backgroundColor: colors,
                borderColor: colors.map(c => c.replace(/0\.\d+/, '1')),
                borderWidth: 1,
            }],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { position: 'bottom' } },
        },
    });
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
