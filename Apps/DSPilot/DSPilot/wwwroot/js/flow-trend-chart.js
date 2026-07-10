// Flow 추이 차트 (Chart.js)
// /flow?name= 페이지가 .NET interop 으로 호출.
// - renderTrendChart : 가동시간/동작시간/대기시간 시계열 라인
// - renderCycleCountChart : 버킷별 사이클 수 + 비가동 사이클 stacked bar
// - renderTimeDistribution : 동작시간 vs 대기시간 비중 도넛

const charts = {};

const COLORS = {
    CT: { fill: 'rgba(99, 102, 241, 0.18)',  border: 'rgb(99, 102, 241)'  },
    MT: { fill: 'rgba(16, 185, 129, 0.18)',  border: 'rgb(16, 185, 129)'  },
    WT: { fill: 'rgba(245, 158, 11, 0.18)',  border: 'rgb(245, 158, 11)'  },
    Active: { fill: 'rgba(59, 130, 246, 0.75)', border: 'rgb(59, 130, 246)' },
    Idle:   { fill: 'rgba(156, 163, 175, 0.75)', border: 'rgb(156, 163, 175)' },
};

function destroyIfExists(id) {
    if (charts[id]) {
        try { charts[id].destroy(); } catch (e) { /* ignore */ }
        delete charts[id];
    }
}

function timeUnitOf(granularity) {
    return ({
        'hour': 'hour',
        'day':  'day',
        'week': 'week',
        'month':'month',
    })[granularity] || 'day';
}

// buckets: [{ bucketStartIso, avgCT, avgMT, avgWT, count, idleCount }]
export function renderTrendChart(chartId, buckets, granularity) {
    destroyIfExists(chartId);
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const labels = buckets.map(b => b.bucketStartIso);
    const ct = buckets.map(b => b.avgCT);
    const mt = buckets.map(b => b.avgMT);
    const wt = buckets.map(b => b.avgWT);

    const ds = (label, data, color) => ({
        label,
        data,
        backgroundColor: color.fill,
        borderColor: color.border,
        borderWidth: 2,
        fill: false,
        tension: 0.25,
        spanGaps: true,
        pointRadius: 2,
        pointHoverRadius: 5,
    });

    charts[chartId] = new Chart(canvas.getContext('2d'), {
        type: 'line',
        data: {
            labels,
            datasets: [
                ds('가동시간',        ct, COLORS.CT),
                ds('동작시간',        mt, COLORS.MT),
                ds('대기시간',        wt, COLORS.WT),
            ],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            scales: {
                x: {
                    type: 'time',
                    time: { unit: timeUnitOf(granularity), tooltipFormat: 'yyyy-MM-dd HH:mm', displayFormats: { hour: 'HH:mm', day: 'MM-dd', week: 'MM-dd', month: 'yyyy-MM' } },
                    grid: { color: 'rgba(127, 127, 127, 0.08)' },
                },
                y: {
                    beginAtZero: true,
                    title: { display: true, text: '시간 (초)' },
                    grid: { color: 'rgba(127, 127, 127, 0.12)' },
                },
            },
            plugins: {
                legend: { position: 'top' },
                tooltip: {
                    callbacks: {
                        label: function (ctx) {
                            const v = ctx.parsed.y;
                            return `${ctx.dataset.label}: ${v == null ? '-' : v.toFixed(2)} sec`;
                        },
                    },
                },
            },
        },
    });
}

export function renderCycleCountChart(chartId, buckets, granularity) {
    destroyIfExists(chartId);
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const labels = buckets.map(b => b.bucketStartIso);
    const active = buckets.map(b => Math.max(0, (b.count || 0) - (b.idleCount || 0)));
    const idle   = buckets.map(b => b.idleCount || 0);

    charts[chartId] = new Chart(canvas.getContext('2d'), {
        type: 'bar',
        data: {
            labels,
            datasets: [
                {
                    label: '정상 가동횟수',
                    data: active,
                    backgroundColor: COLORS.Active.fill,
                    borderColor: COLORS.Active.border,
                    borderWidth: 1,
                    stack: 'cycles',
                },
                {
                    label: '비가동',
                    data: idle,
                    backgroundColor: COLORS.Idle.fill,
                    borderColor: COLORS.Idle.border,
                    borderWidth: 1,
                    stack: 'cycles',
                },
            ],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            scales: {
                x: {
                    type: 'time',
                    time: { unit: timeUnitOf(granularity), tooltipFormat: 'yyyy-MM-dd HH:mm', displayFormats: { hour: 'HH:mm', day: 'MM-dd', week: 'MM-dd', month: 'yyyy-MM' } },
                    stacked: true,
                    grid: { color: 'rgba(127, 127, 127, 0.08)' },
                },
                y: {
                    stacked: true,
                    beginAtZero: true,
                    ticks: { precision: 0 },
                    title: { display: true, text: '가동횟수' },
                    grid: { color: 'rgba(127, 127, 127, 0.12)' },
                },
            },
            plugins: { legend: { position: 'top' } },
        },
    });
}

// shares: { mt, wt }  (총 ms)
export function renderTimeDistribution(chartId, shares) {
    destroyIfExists(chartId);
    const canvas = document.getElementById(chartId);
    if (!canvas) return;

    const data = [shares.mt || 0, shares.wt || 0];
    charts[chartId] = new Chart(canvas.getContext('2d'), {
        type: 'doughnut',
        data: {
            labels: ['동작시간', '대기시간'],
            datasets: [{
                data,
                backgroundColor: [COLORS.MT.border, COLORS.WT.border],
                borderColor: [COLORS.MT.border, COLORS.WT.border],
                borderWidth: 1,
            }],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: { position: 'bottom' },
                tooltip: {
                    callbacks: {
                        label: function (ctx) {
                            const total = ctx.dataset.data.reduce((s, v) => s + (v || 0), 0);
                            const v = ctx.parsed || 0;
                            const pct = total > 0 ? (v / total * 100).toFixed(1) : '0.0';
                            return `${ctx.label}: ${(v / 1000).toFixed(1)} sec (${pct}%)`;
                        },
                    },
                },
            },
        },
    });
}

export function disposeAll(ids) {
    for (const id of ids) destroyIfExists(id);
}
