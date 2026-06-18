window.plcDebug = {
    chart: null,

    formatTooltipSecondTenth: function (value) {
        const date = value instanceof Date ? value : new Date(value);
        if (Number.isNaN(date.getTime())) {
            return '';
        }

        const seconds = String(date.getSeconds()).padStart(2, '0');
        const tenth = Math.floor(date.getMilliseconds() / 100);
        return `${seconds}.${tenth}`;
    },

    destroyChart: function () {
        if (this.chart) {
            this.chart.destroy();
            this.chart = null;
        }
    },

    // 차트 텍스트/격자/툴팁을 Control Room 테마 토큰에서 읽음 (shell.js 가 <html> 에 .dark-theme 설정 → :root 읽기 정확)
    themeColor: function (token, fallback) {
        try {
            const v = getComputedStyle(document.documentElement).getPropertyValue(token).trim();
            return v || fallback;
        } catch (e) {
            return fallback;
        }
    },

    renderChart: function (datasets, options) {
        const canvas = document.getElementById('plcDebugChart');
        if (!canvas) {
            console.error('Chart canvas not found');
            return false;
        }

        options = options || {};
        const lanes = Array.isArray(options.lanes) ? options.lanes : [];
        const ctx = canvas.getContext('2d');
        const labelLookup = new Map(lanes.map(lane => [Number(lane.value), lane.label]));

        this.destroyChart();

        if (typeof Chart === 'undefined') {
            console.error('Chart.js is not loaded');
            return false;
        }

        // Control Room 테마 토큰 (라이트/다크 자동) — 축 텍스트/격자/툴팁이 다크 캔버스에서도 읽히도록
        const gridColor = this.themeColor('--color-lines', 'rgba(14,27,42,0.10)');
        const textSecondary = this.themeColor('--color-text-secondary', '#5A6B7E');
        const textPrimary = this.themeColor('--color-text-primary', '#0E1B2A');
        const surfaceColor = this.themeColor('--color-surface', '#FFFFFF');
        const isDark = document.documentElement.classList.contains('dark-theme');
        const tooltipBg = isDark ? '#0E1722' : textPrimary;
        const tooltipText = isDark ? '#DCE6F0' : surfaceColor;

        // 모바일(≤480px): 서버 산출 높이(레인 수 기반)가 과도할 수 있어 ~400px 로 캡
        const isPhone = (typeof window !== 'undefined') && window.innerWidth <= 480;
        const requestedHeight = options.chartHeight || 720;
        const effectiveHeight = isPhone ? Math.min(requestedHeight, 400) : requestedHeight;
        canvas.style.height = `${effectiveHeight}px`;
        canvas.style.width = '100%';

        let minTime = null;
        let maxTime = null;

        datasets.forEach(dataset => {
            dataset.data.forEach(point => {
                const time = new Date(point.x).getTime();
                if (minTime === null || time < minTime) minTime = time;
                if (maxTime === null || time > maxTime) maxTime = time;
            });
        });

        const rangeStart = options.rangeStart ? new Date(options.rangeStart).getTime() : minTime;
        const rangeEnd = options.rangeEnd ? new Date(options.rangeEnd).getTime() : maxTime;
        const laneHeight = 2;
        const yMax = Math.max(lanes.length * laneHeight, 2);

        this.chart = new Chart(ctx, {
            type: 'line',
            data: {
                datasets: datasets
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                normalized: true,
                interaction: {
                    mode: 'nearest',
                    axis: 'xy',
                    intersect: false,
                },
                plugins: {
                    decimation: {
                        enabled: false,
                        algorithm: 'lttb'
                    },
                    legend: {
                        display: datasets.length <= 20,
                        position: 'top',
                        labels: {
                            usePointStyle: true,
                            padding: 12,
                            color: textSecondary,
                            font: {
                                size: 11
                            }
                        }
                    },
                    title: {
                        display: false
                    },
                    tooltip: {
                        mode: 'nearest',
                        intersect: false,
                        position: 'nearest',
                        backgroundColor: tooltipBg,
                        titleColor: tooltipText,
                        bodyColor: tooltipText,
                        borderColor: gridColor,
                        borderWidth: 1,
                        callbacks: {
                            title: function (items) {
                                if (!items || items.length === 0) {
                                    return '';
                                }

                                const rawX = items[0].raw && items[0].raw.x ? items[0].raw.x : null;
                                if (!rawX) {
                                    return items[0].label || '';
                                }

                                return window.plcDebug.formatTooltipSecondTenth(rawX);
                            },
                            label: function (context) {
                                const label = context.dataset.label || '';
                                const rawValue = context.raw && typeof context.raw.rawValue !== 'undefined'
                                    ? context.raw.rawValue
                                    : context.parsed.y;
                                return `${label}: ${rawValue}`;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        type: 'time',
                        time: {
                            tooltipFormat: 'yyyy-MM-dd HH:mm:ss',
                            displayFormats: {
                                millisecond: 'HH:mm:ss.SSS',
                                second: 'HH:mm:ss',
                                minute: 'HH:mm',
                                hour: 'HH:mm',
                                day: 'MM-dd',
                                month: 'yyyy-MM'
                            }
                        },
                        min: rangeStart,
                        max: rangeEnd,
                        title: {
                            display: true,
                            text: '시간',
                            color: textSecondary
                        },
                        ticks: {
                            autoSkip: true,
                            maxTicksLimit: 14,
                            color: textSecondary
                        },
                        grid: {
                            color: gridColor
                        }
                    },
                    y: {
                        min: -0.2,
                        max: yMax,
                        afterBuildTicks: function (axis) {
                            axis.ticks = lanes.map(lane => ({ value: Number(lane.value) }));
                        },
                        afterFit: function (axis) {
                            // 모바일(≤480px): 260px 고정폭이 화면 대부분을 차지 → ~100px 로 축소
                            var minLaneWidth = (typeof window !== 'undefined' && window.innerWidth <= 480) ? 100 : 260;
                            axis.width = Math.max(axis.width, minLaneWidth);
                        },
                        title: {
                            display: true,
                            text: '태그',
                            color: textSecondary
                        },
                        ticks: {
                            autoSkip: false,
                            color: textSecondary,
                            font: {
                                size: 11
                            },
                            callback: function (value) {
                                return labelLookup.get(Number(value)) || '';
                            }
                        },
                        grid: {
                            color: gridColor
                        }
                    }
                }
            }
        });

        return true;
    }
};
