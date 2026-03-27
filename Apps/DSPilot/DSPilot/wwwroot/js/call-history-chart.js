// Call History Chart - 실행 회차별 GoingTime 라인 차트
let callHistoryChartInstance = null;

// ms → 사람이 읽기 쉬운 시간 문자열 변환
function formatMs(ms) {
    if (ms <= 0) return '0s';
    if (ms < 1000) return ms + 'ms';
    if (ms < 60000) return (ms / 1000).toFixed(1) + 's';
    if (ms < 3600000) return Math.floor(ms / 60000) + 'm ' + Math.floor((ms % 60000) / 1000) + 's';
    return Math.floor(ms / 3600000) + 'h ' + Math.floor((ms % 3600000) / 60000) + 'm';
}

// 마우스 휠 줌 플러그인 - 커서 기준 확대/축소, 더블클릭 초기화
const wheelZoomPlugin = {
    id: 'wheelZoom',
    afterInit(chart) {
        const canvas = chart.canvas;

        canvas.addEventListener('wheel', function (e) {
            if (!chart.chartArea) return;
            const { left, right, top, bottom } = chart.chartArea;
            if (e.offsetX < left || e.offsetX > right || e.offsetY < top || e.offsetY > bottom) return;
            e.preventDefault();

            const zoomFactor = e.deltaY > 0 ? 1.05 : 0.95;
            const xScale = chart.scales.x;
            const yScale = chart.scales.y;

            // X축: 커서 위치 비율 기준 확대/축소
            const xRatio = (e.offsetX - left) / (right - left);
            const xMin = xScale.min;
            const xMax = xScale.max;
            const xRange = xMax - xMin;
            const newXRange = xRange * zoomFactor;
            const newXMin = Math.max(0, Math.round(xMin + (xRange - newXRange) * xRatio));
            const newXMax = Math.min(chart.data.labels.length - 1, Math.round(newXMin + newXRange));

            if (newXMax - newXMin < 5) return; // 최소 5개 포인트

            // 전체 범위로 돌아오면 초기 상태로 리셋
            const isFullRange = newXMin === 0 && newXMax >= chart.data.labels.length - 1;
            if (isFullRange) {
                xScale.options.min = undefined;
                xScale.options.max = undefined;
                yScale.options.min = undefined;
                yScale.options.max = undefined;
            } else {
                xScale.options.min = chart.data.labels[newXMin];
                xScale.options.max = chart.data.labels[newXMax];

                // Y축: 보이는 X 범위 내 데이터의 min/max에 맞춤
                const visibleData = chart.data.datasets[0].data.slice(newXMin, newXMax + 1);
                const dataMin = Math.min(...visibleData);
                const dataMax = Math.max(...visibleData);
                const yPad = (dataMax - dataMin) * 0.1 || dataMax * 0.1;
                yScale.options.min = Math.max(0, Math.floor(dataMin - yPad));
                yScale.options.max = Math.ceil(dataMax + yPad);
            }

            chart.update('none');
        }, { passive: false });

        // 드래그 패닝: 확대 상태에서 좌우 드래그로 이동
        let dragStartX = null;
        let dragStartMin = null;
        let dragStartMax = null;

        canvas.addEventListener('mousedown', function (e) {
            if (!chart.chartArea) return;
            const { left, right, top, bottom } = chart.chartArea;
            if (e.offsetX < left || e.offsetX > right || e.offsetY < top || e.offsetY > bottom) return;
            // 확대 상태가 아니면 패닝 불필요
            const xScale = chart.scales.x;
            if (xScale.min === 0 && xScale.max >= chart.data.labels.length - 1) return;
            dragStartX = e.offsetX;
            dragStartMin = xScale.min;
            dragStartMax = xScale.max;
            canvas.style.cursor = 'grabbing';
        });

        canvas.addEventListener('mousemove', function (e) {
            if (dragStartX == null) return;
            const { left, right } = chart.chartArea;
            const xScale = chart.scales.x;
            const pixelRange = right - left;
            const dataRange = dragStartMax - dragStartMin;
            const dx = e.offsetX - dragStartX;
            const dataShift = Math.round(-dx / pixelRange * dataRange);

            let newMin = dragStartMin + dataShift;
            let newMax = dragStartMax + dataShift;

            // 경계 클램프
            if (newMin < 0) { newMax -= newMin; newMin = 0; }
            const maxIdx = chart.data.labels.length - 1;
            if (newMax > maxIdx) { newMin -= (newMax - maxIdx); newMax = maxIdx; newMin = Math.max(0, newMin); }

            xScale.options.min = chart.data.labels[newMin];
            xScale.options.max = chart.data.labels[newMax];

            // Y축: 보이는 범위에 맞춤
            const yScale = chart.scales.y;
            const visibleData = chart.data.datasets[0].data.slice(newMin, newMax + 1);
            const dataMin = Math.min(...visibleData);
            const dataMax = Math.max(...visibleData);
            const yPad = (dataMax - dataMin) * 0.1 || dataMax * 0.1;
            yScale.options.min = Math.max(0, Math.floor(dataMin - yPad));
            yScale.options.max = Math.ceil(dataMax + yPad);

            chart.update('none');
        });

        function endDrag() {
            if (dragStartX != null) {
                dragStartX = null;
                canvas.style.cursor = '';
            }
        }
        canvas.addEventListener('mouseup', endDrag);
        canvas.addEventListener('mouseleave', endDrag);

        canvas.addEventListener('dblclick', function () {
            chart.scales.x.options.min = undefined;
            chart.scales.x.options.max = undefined;
            chart.scales.y.options.min = undefined;
            chart.scales.y.options.max = undefined;
            chart.update('none');
        });
    }
};

// 수직 크로스헤어 플러그인 - 마우스 위치에 세로선 표시
const crosshairPlugin = {
    id: 'crosshair',
    afterDraw(chart) {
        if (chart._crosshairX == null) return;
        const { ctx, chartArea: { top, bottom } } = chart;
        ctx.save();
        ctx.beginPath();
        ctx.moveTo(chart._crosshairX, top);
        ctx.lineTo(chart._crosshairX, bottom);
        ctx.lineWidth = 1;
        ctx.strokeStyle = 'rgba(100, 100, 100, 0.4)';
        ctx.setLineDash([4, 3]);
        ctx.stroke();
        ctx.restore();
    },
    afterEvent(chart, args) {
        const event = args.event;
        if (event.type === 'mousemove' && chart.chartArea) {
            const { left, right } = chart.chartArea;
            if (event.x >= left && event.x <= right) {
                chart._crosshairX = event.x;
            } else {
                chart._crosshairX = null;
            }
        } else if (event.type === 'mouseout') {
            chart._crosshairX = null;
        }
        chart.draw();
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

    // 차트 데이터 기준 평균/표준편차 계산 (서버 누적 통계 대신 현재 표시 데이터 사용)
    const n = goingTimes.length;
    const chartAvg = goingTimes.reduce((s, v) => s + v, 0) / n;
    const chartStdDev = Math.sqrt(goingTimes.reduce((s, v) => s + (v - chartAvg) ** 2, 0) / n);

    // 이상치 포인트를 크게 표시: 평균 ± 2*표준편차 밖이면 강조
    const threshold = 2 * chartStdDev;
    const baseRadius = goingTimes.length > 50 ? 1 : 3;
    const outlierRadius = 3;
    const pointRadii = goingTimes.map(v =>
        Math.abs(v - chartAvg) > threshold ? outlierRadius : baseRadius
    );
    const pointColors = goingTimes.map(v =>
        Math.abs(v - chartAvg) > threshold ? 'rgb(239, 68, 68)' : 'rgb(59, 130, 246)'
    );

    callHistoryChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            labels: labels,
            datasets: [
                {
                    label: 'GoingTime',
                    data: goingTimes,
                    borderColor: 'rgb(59, 130, 246)',
                    backgroundColor: 'rgba(59, 130, 246, 0.1)',
                    borderWidth: 2,
                    fill: true,
                    tension: 0.2,
                    pointRadius: pointRadii,
                    pointHitRadius: 8,
                    pointHoverRadius: 5,
                    pointBackgroundColor: pointColors,
                    pointBorderColor: pointColors
                },
                {
                    label: '\ud3c9\uade0 (' + formatMs(Math.round(chartAvg)) + ')',
                    data: labels.map(() => chartAvg),
                    borderColor: 'rgba(239, 68, 68, 0.7)',
                    borderWidth: 2,
                    borderDash: [6, 4],
                    pointRadius: 0,
                    pointHitRadius: 0,
                    fill: false
                },
                {
                    label: '\ud3c9\uade0+2\u03c3',
                    data: labels.map(() => chartAvg + threshold),
                    borderColor: 'rgba(239, 68, 68, 0.15)',
                    borderWidth: 1,
                    borderDash: [3, 3],
                    pointRadius: 0,
                    pointHitRadius: 0,
                    fill: '+1',
                    backgroundColor: 'rgba(239, 68, 68, 0.04)'
                },
                {
                    label: '\ud3c9\uade0-2\u03c3',
                    data: labels.map(() => Math.max(0, chartAvg - threshold)),
                    borderColor: 'rgba(239, 68, 68, 0.15)',
                    borderWidth: 1,
                    borderDash: [3, 3],
                    pointRadius: 0,
                    pointHitRadius: 0,
                    fill: false
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'nearest', intersect: false },
            plugins: {
                crosshair: true,
                legend: {
                    display: true,
                    position: 'top',
                    labels: {
                        usePointStyle: true,
                        padding: 12,
                        font: { size: 11, weight: '600' },
                        filter: function (item) {
                            return item.datasetIndex <= 1;
                        }
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
                            if (context.datasetIndex === 0) return 'GoingTime: ' + formatMs(context.parsed.y);
                            if (context.datasetIndex === 1) return '\ud3c9\uade0: ' + formatMs(Math.round(context.parsed.y));
                        },
                        filter: function (item) {
                            return item.datasetIndex <= 1;
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
                    title: { display: true, text: 'GoingTime', font: { size: 11, weight: '600' } },
                    beginAtZero: true,
                    grid: { color: 'rgba(0, 0, 0, 0.05)' },
                    ticks: {
                        font: { size: 10 },
                        callback: function (value) { return formatMs(value); }
                    }
                }
            }
        },
        plugins: [crosshairPlugin, wheelZoomPlugin]
    });
};

window.destroyCallHistoryChart = function () {
    if (callHistoryChartInstance) {
        callHistoryChartInstance.destroy();
        callHistoryChartInstance = null;
    }
};
