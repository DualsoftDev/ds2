// Call History Chart - 실행 회차별 GoingTime 라인 차트
let callHistoryChartInstance = null;

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

    callHistoryChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            labels,
            datasets: [
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
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            plugins: {
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
                            if (context.datasetIndex === 0) return 'GoingTime: ' + context.parsed.y + ' ms';
                            if (context.datasetIndex === 1) return '\ud3c9\uade0: ' + Math.round(context.parsed.y) + ' ms';
                            return null;
                        }
                    }
                }
            },
            scales: {
                x: {
                    display: true,
                    title: { display: true, text: '\uc2e4\ud589 \ud68c\ucc28', font: { size: 11, weight: '600' } },
                    grid: { display: false },
                    ticks: { maxTicksLimit: 20, font: { size: 10 } }
                },
                y: {
                    display: true,
                    title: { display: true, text: 'GoingTime (ms)', font: { size: 11, weight: '600' } },
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
