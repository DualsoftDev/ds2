// 사이클 분석 Gantt Chart

window.cycleAnalysis = {
    chart: null,
    chartData: null,
    selectedBarIndex: null,

    renderGanttChart: function (canvasId, datasets, cycleStartTime) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) {
            console.error('Canvas not found:', canvasId);
            return false;
        }

        // 기존 차트 완전 파괴
        if (this.chart) {
            try {
                this.chart.destroy();
            } catch (e) {
                console.warn('Error destroying chart:', e);
            }
            this.chart = null;
        }

        // Gap 오버레이 제거
        this.removeGapAnnotations();
        this.selectedBarIndex = null;

        // 데이터 저장
        this.chartData = datasets;
        const baseTime = new Date(cycleStartTime);

        // 디버깅: 데이터 확인
        console.log('Cycle Start Time:', cycleStartTime, 'Parsed:', baseTime);
        console.log('Datasets count:', datasets.length);
        if (datasets.length > 0) {
            console.log('First dataset:', datasets[0]);
            console.log('First data point x:', datasets[0].data[0].x);
            console.log('First data point y:', datasets[0].data[0].y);
        }

        // Canvas 초기화 (중요!)
        const ctx = canvas.getContext('2d');
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        // 데이터 검증 및 변환
        const chartData = datasets.map(d => {
            const xData = d.data[0].x;
            // x가 배열인지 확인하고, 각 값이 유효한 timestamp인지 검증
            if (Array.isArray(xData) && xData.length === 2) {
                const start = Number(xData[0]);
                const end = Number(xData[1]);
                if (start > 0 && end > start) {
                    return {
                        x: [start, end],
                        y: d.data[0].y
                    };
                }
            }
            console.error('Invalid data:', d);
            return null;
        }).filter(d => d !== null);

        if (chartData.length === 0) {
            console.error('No valid data to display');
            return false;
        }

        console.log('Chart data after validation:', chartData);

        // 실제 시간 범위 계산
        let minTime = Infinity;
        let maxTime = -Infinity;

        chartData.forEach(d => {
            if (d.x[0] < minTime) minTime = d.x[0];
            if (d.x[1] > maxTime) maxTime = d.x[1];
        });

        // 약간의 여백 추가 (전체 범위의 2%)
        const timeRange = maxTime - minTime;
        const padding = timeRange * 0.02;
        minTime = minTime - padding;
        maxTime = maxTime + padding;

        console.log('Time range:', new Date(minTime), 'to', new Date(maxTime));

        this.chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: datasets.map(d => d.label),
                datasets: [{
                    label: 'Call Duration',
                    data: chartData,
                    backgroundColor: datasets.map((d, idx) =>
                        this.selectedBarIndex === idx ? 'rgba(10, 30, 90, 0.9)' : d.backgroundColor),
                    borderWidth: datasets.map((d, idx) =>
                        this.selectedBarIndex === idx ? 2 : 0),
                    borderColor: datasets.map((d, idx) =>
                        this.selectedBarIndex === idx ? 'rgba(10, 30, 90, 1)' : 'transparent')
                }]
            },
            options: {
                indexAxis: 'y',
                responsive: true,
                maintainAspectRatio: false,
                onClick: (event, elements) => {
                    if (elements.length > 0) {
                        const index = elements[0].index;
                        this.handleBarDoubleClick(index);
                    }
                },
                plugins: {
                    legend: {
                        display: false
                    },
                    tooltip: {
                        callbacks: {
                            label: (context) => {
                                const dataIndex = context.dataIndex;
                                const dataset = this.chartData[dataIndex];
                                const startTime = new Date(baseTime.getTime() + dataset.startOffsetMs);
                                const endTime = new Date(baseTime.getTime() + dataset.endOffsetMs);
                                const duration = (dataset.endOffsetMs - dataset.startOffsetMs) / 1000;

                                const lines = [
                                    `Call: ${dataset.label}`,
                                    `Device: ${dataset.deviceName}`,
                                    `시작: ${this.formatTime(startTime)}`,
                                    `종료: ${this.formatTime(endTime)}`,
                                    `소요: ${duration.toFixed(2)}s`
                                ];

                                if (dataset.gapFromPreviousMs > 0) {
                                    lines.push(`Gap: ${(dataset.gapFromPreviousMs / 1000).toFixed(2)}s`);
                                }

                                return lines;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        type: 'time',
                        min: minTime,
                        max: maxTime,
                        time: {
                            displayFormats: {
                                millisecond: 'HH:mm:ss.SSS',
                                second: 'HH:mm:ss',
                                minute: 'HH:mm',
                                hour: 'HH:mm'
                            },
                            tooltipFormat: 'yyyy-MM-dd HH:mm:ss.SSS'
                        },
                        ticks: {
                            source: 'auto',
                            autoSkip: true,
                            maxTicksLimit: 15
                        },
                        title: {
                            display: true,
                            text: '절대 시간'
                        }
                    },
                    y: {
                        title: {
                            display: true,
                            text: 'Call 순서'
                        }
                    }
                }
            }
        });

        return true;
    },

    handleBarDoubleClick: function(index) {
        const dataset = this.chartData[index];

        // 이전 선택 해제
        if (this.selectedBarIndex !== null && this.selectedBarIndex !== index) {
            this.removeGapAnnotations();
        }

        // 새 선택
        if (this.selectedBarIndex === index) {
            // 이미 선택된 경우 토글 (선택 해제)
            this.selectedBarIndex = null;
            this.removeGapAnnotations();
        } else {
            // 새로 선택
            this.selectedBarIndex = index;
            this.showGapAnnotations(index);
        }

        // 차트 업데이트 (색상 변경)
        this.updateChartColors();
    },

    updateChartColors: function() {
        if (!this.chart) return;

        this.chart.data.datasets[0].backgroundColor = this.chartData.map((d, idx) =>
            this.selectedBarIndex === idx ? 'rgba(10, 30, 90, 0.9)' : d.backgroundColor);
        this.chart.data.datasets[0].borderWidth = this.chartData.map((d, idx) =>
            this.selectedBarIndex === idx ? 2 : 0);
        this.chart.data.datasets[0].borderColor = this.chartData.map((d, idx) =>
            this.selectedBarIndex === idx ? 'rgba(10, 30, 90, 1)' : 'transparent');

        this.chart.update();
    },

    showGapAnnotations: function(index) {
        const current = this.chartData[index];
        const previous = index > 0 ? this.chartData[index - 1] : null;
        const next = index < this.chartData.length - 1 ? this.chartData[index + 1] : null;

        const canvas = document.getElementById('cycleGanttChart');
        const container = canvas.parentElement;

        // Gap 정보 표시용 오버레이 생성
        let overlay = document.getElementById('gap-overlay');
        if (!overlay) {
            overlay = document.createElement('div');
            overlay.id = 'gap-overlay';
            overlay.style.position = 'absolute';
            overlay.style.top = '0';
            overlay.style.left = '0';
            overlay.style.right = '0';
            overlay.style.bottom = '0';
            overlay.style.pointerEvents = 'none';
            overlay.style.zIndex = '10';
            container.style.position = 'relative';
            container.appendChild(overlay);
        }

        let html = '<div style="padding: 16px; background: rgba(255,255,255,0.95); border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.15); margin-bottom: 12px;">';
        html += `<div style="font-weight: 600; margin-bottom: 8px; color: #0A1E5A;">📊 ${current.label}</div>`;

        if (previous) {
            const gap = (current.startOffsetMs - previous.endOffsetMs) / 1000;
            html += `<div style="margin: 4px 0; padding: 8px; background: #fff3cd; border-left: 3px solid #ffc107; border-radius: 4px;">`;
            html += `<span style="color: #856404;">⬅️ 이전 Gap:</span> <strong>${gap.toFixed(2)}s</strong>`;
            html += `<div style="font-size: 0.85em; color: #666; margin-top: 4px;">← ${previous.label}</div>`;
            html += `</div>`;
        }

        if (next) {
            const gap = (next.startOffsetMs - current.endOffsetMs) / 1000;
            html += `<div style="margin: 4px 0; padding: 8px; background: #d1ecf1; border-left: 3px solid #17a2b8; border-radius: 4px;">`;
            html += `<span style="color: #0c5460;">➡️ 다음 Gap:</span> <strong>${gap.toFixed(2)}s</strong>`;
            html += `<div style="font-size: 0.85em; color: #666; margin-top: 4px;">${next.label} →</div>`;
            html += `</div>`;
        }

        html += '</div>';
        overlay.innerHTML = html;
    },

    removeGapAnnotations: function() {
        const overlay = document.getElementById('gap-overlay');
        if (overlay) {
            overlay.innerHTML = '';
        }
    },

    formatTime: function(date) {
        const hh = String(date.getHours()).padStart(2, '0');
        const mm = String(date.getMinutes()).padStart(2, '0');
        const ss = String(date.getSeconds()).padStart(2, '0');
        const ms = String(date.getMilliseconds()).padStart(3, '0');
        return `${hh}:${mm}:${ss}.${ms}`;
    },

    destroyChart: function () {
        if (this.chart) {
            this.chart.destroy();
            this.chart = null;
        }
        this.removeGapAnnotations();
        this.selectedBarIndex = null;
        this.chartData = null;
    }
};
