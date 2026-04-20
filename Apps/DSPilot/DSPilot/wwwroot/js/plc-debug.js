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

        canvas.style.height = `${options.chartHeight || 720}px`;
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
                            text: '시간'
                        },
                        ticks: {
                            autoSkip: true,
                            maxTicksLimit: 14
                        }
                    },
                    y: {
                        min: -0.2,
                        max: yMax,
                        afterBuildTicks: function (axis) {
                            axis.ticks = lanes.map(lane => ({ value: Number(lane.value) }));
                        },
                        afterFit: function (axis) {
                            axis.width = Math.max(axis.width, 260);
                        },
                        title: {
                            display: true,
                            text: '태그'
                        },
                        ticks: {
                            autoSkip: false,
                            font: {
                                size: 11
                            },
                            callback: function (value) {
                                return labelLookup.get(Number(value)) || '';
                            }
                        },
                        grid: {
                            color: 'rgba(10, 30, 90, 0.08)'
                        }
                    }
                }
            }
        });

        return true;
    }
};
