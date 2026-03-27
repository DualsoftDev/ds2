// Call History Chart - 실행 회차별 GoingTime + 이상 구간 하이라이트
let callHistoryChartInstance = null;

// 이동평균 계산
function calcMovingAverage(data, windowSize) {
    const result = [];
    for (let i = 0; i < data.length; i++) {
        const start = Math.max(0, i - windowSize + 1);
        const win = data.slice(start, i + 1);
        result.push(win.reduce((s, v) => s + v, 0) / win.length);
    }
    return result;
}

// 이동 표준편차 계산
function calcMovingStdDev(data, movingAvg, windowSize) {
    const result = [];
    for (let i = 0; i < data.length; i++) {
        const start = Math.max(0, i - windowSize + 1);
        const win = data.slice(start, i + 1);
        const mean = movingAvg[i];
        const variance = win.reduce((s, v) => s + (v - mean) ** 2, 0) / win.length;
        result.push(Math.sqrt(variance));
    }
    return result;
}

// 연속 이상 구간 검출 (±2σ 초과)
function detectAnomalyRegions(data, movingAvg, movingStd) {
    const flags = data.map((val, i) => {
        const upper = movingAvg[i] + 2 * movingStd[i];
        const lower = movingAvg[i] - 2 * movingStd[i];
        return val > upper || val < lower;
    });

    const regions = [];
    let start = null;
    for (let i = 0; i < flags.length; i++) {
        if (flags[i] && start === null) {
            start = i;
        } else if (!flags[i] && start !== null) {
            regions.push({ start, end: i - 1 });
            start = null;
        }
    }
    if (start !== null) {
        regions.push({ start, end: flags.length - 1 });
    }
    return regions;
}

// 이상 구간 배경을 그리는 커스텀 플러그인
const anomalyHighlightPlugin = {
    id: 'anomalyHighlight',
    beforeDraw(chart) {
        const regions = chart.options.plugins.anomalyHighlight?.regions;
        if (!regions || regions.length === 0) return;

        const { ctx, chartArea, scales } = chart;
        const xScale = scales.x;
        const yTop = chartArea.top;
        const yBottom = chartArea.bottom;

        ctx.save();
        regions.forEach(region => {
            const xStart = xScale.getPixelForValue(region.start - 0.5);
            const xEnd = xScale.getPixelForValue(region.end + 0.5);
            const clampedStart = Math.max(xStart, chartArea.left);
            const clampedEnd = Math.min(xEnd, chartArea.right);

            // 빨간 반투명 배경
            ctx.fillStyle = 'rgba(239, 68, 68, 0.12)';
            ctx.fillRect(clampedStart, yTop, clampedEnd - clampedStart, yBottom - yTop);

            // 좌우 경계선
            ctx.strokeStyle = 'rgba(239, 68, 68, 0.4)';
            ctx.lineWidth = 1.5;
            ctx.setLineDash([4, 3]);
            ctx.beginPath();
            ctx.moveTo(clampedStart, yTop);
            ctx.lineTo(clampedStart, yBottom);
            ctx.moveTo(clampedEnd, yTop);
            ctx.lineTo(clampedEnd, yBottom);
            ctx.stroke();
            ctx.setLineDash([]);
        });
        ctx.restore();
    }
};

window.renderCallHistoryChart = function (canvasId, executionData, averageMs, stdDevMs) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) {
        console.error('Canvas element not found:', canvasId);
        return;
    }

    const ctx = canvas.getContext('2d');

    if (callHistoryChartInstance) {
        callHistoryChartInstance.destroy();
    }

    const labels = executionData.map(d => d.executionNumber);
    const goingTimes = executionData.map(d => d.goingTimeMs);
    const timestamps = executionData.map(d => d.timestamp);

    // 이동평균/표준편차 (이상구간 검출용, 차트에는 표시하지 않음)
    const windowSize = Math.max(3, Math.min(10, Math.floor(goingTimes.length / 5)));
    const movingAvg = calcMovingAverage(goingTimes, windowSize);
    const movingStd = calcMovingStdDev(goingTimes, movingAvg, windowSize);

    // 이상 구간 검출
    const anomalyRegions = detectAnomalyRegions(goingTimes, movingAvg, movingStd);

    const datasets = [
        // GoingTime 실측 라인
        {
            label: 'GoingTime (ms)',
            data: goingTimes,
            borderColor: 'rgb(59, 130, 246)',
            backgroundColor: 'rgba(59, 130, 246, 0.05)',
            borderWidth: 2,
            fill: false,
            tension: 0.2,
            pointRadius: goingTimes.length > 50 ? 1 : 3,
            pointHitRadius: 8,
            pointHoverRadius: 5,
            pointBackgroundColor: 'rgb(59, 130, 246)',
            pointBorderColor: 'rgb(59, 130, 246)'
        },
        // 전체 평균선
        {
            label: '\ud3c9\uade0 (' + Math.round(averageMs) + ' ms)',
            data: labels.map(() => averageMs),
            borderColor: 'rgba(239, 68, 68, 0.6)',
            borderWidth: 1.5,
            borderDash: [6, 4],
            pointRadius: 0,
            pointHitRadius: 0,
            fill: false
        }
    ];

    callHistoryChartInstance = new Chart(ctx, {
        type: 'line',
        data: { labels, datasets },
        plugins: [anomalyHighlightPlugin],
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: 'index',
                intersect: false
            },
            plugins: {
                anomalyHighlight: {
                    regions: anomalyRegions
                },
                legend: {
                    display: true,
                    position: 'top',
                    labels: {
                        usePointStyle: true,
                        padding: 10,
                        font: { size: 10, weight: '600' }
                    }
                },
                tooltip: {
                    enabled: true,
                    backgroundColor: 'rgba(0, 0, 0, 0.85)',
                    titleFont: { size: 12, weight: 'bold' },
                    bodyFont: { size: 11 },
                    padding: 10,
                    cornerRadius: 6,
                    callbacks: {
                        title: function (items) {
                            const idx = items[0].dataIndex;
                            return '\uc2e4\ud589 #' + labels[idx] + '  |  ' + timestamps[idx];
                        },
                        label: function (context) {
                            const idx = context.dataIndex;
                            const dsIdx = context.datasetIndex;

                            if (dsIdx === 0) {
                                const val = context.parsed.y;
                                const inAnomaly = anomalyRegions.some(r => idx >= r.start && idx <= r.end);
                                let text = 'GoingTime: ' + val + ' ms';
                                if (inAnomaly) text += '  \u26a0 \uc774\uc0c1 \uad6c\uac04';
                                return text;
                            }
                            if (dsIdx === 1) return '\ud3c9\uade0: ' + Math.round(context.parsed.y) + ' ms';
                            return null;
                        }
                    }
                }
            },
            scales: {
                x: {
                    display: true,
                    title: {
                        display: true,
                        text: '\uc2e4\ud589 \ud68c\ucc28',
                        font: { size: 11, weight: '600' }
                    },
                    grid: { display: false },
                    ticks: {
                        maxTicksLimit: 20,
                        font: { size: 10 }
                    }
                },
                y: {
                    display: true,
                    title: {
                        display: true,
                        text: 'GoingTime (ms)',
                        font: { size: 11, weight: '600' }
                    },
                    beginAtZero: true,
                    grid: { color: 'rgba(0, 0, 0, 0.05)' },
                    ticks: { font: { size: 10 } }
                }
            }
        }
    });
};

window.destroyCallHistoryChart = function () {
    if (callHistoryChartInstance) {
        callHistoryChartInstance.destroy();
        callHistoryChartInstance = null;
    }
};
