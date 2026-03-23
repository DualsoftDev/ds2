// Cycle Time Analysis Charts
let cycleTimeChartInstance = null;
let histogramChartInstance = null;
let signalTimelineChartInstance = null;
let ioGanttChartInstance = null;

window.renderCycleTimeChart = function (inLabel, outLabel, inTimestamps, inValues, outTimestamps, outValues) {
    const ctx = document.getElementById('cycleTimeChart');
    if (!ctx) {
        console.error('Canvas element not found: cycleTimeChart');
        return;
    }

    // Destroy existing chart
    if (cycleTimeChartInstance) {
        cycleTimeChartInstance.destroy();
    }

    // Prepare datasets
    const datasets = [];

    if (inTimestamps && inTimestamps.length > 0) {
        datasets.push({
            label: inLabel,
            data: inTimestamps.map((t, i) => ({ x: t, y: inValues[i] })),
            borderColor: 'rgba(54, 162, 235, 1)',
            backgroundColor: 'rgba(54, 162, 235, 0.1)',
            borderWidth: 2,
            stepped: true,
            fill: false,
            pointRadius: 0,
            tension: 0
        });
    }

    if (outTimestamps && outTimestamps.length > 0) {
        datasets.push({
            label: outLabel,
            data: outTimestamps.map((t, i) => ({ x: t, y: outValues[i] })),
            borderColor: 'rgba(255, 99, 132, 1)',
            backgroundColor: 'rgba(255, 99, 132, 0.1)',
            borderWidth: 2,
            stepped: true,
            fill: false,
            pointRadius: 0,
            tension: 0
        });
    }

    cycleTimeChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            datasets: datasets
        },
        options: {
            responsive: true,
            maintainAspectRatio: true,
            interaction: {
                mode: 'index',
                intersect: false
            },
            plugins: {
                legend: {
                    display: true,
                    position: 'top',
                    labels: {
                        usePointStyle: true,
                        padding: 15,
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    }
                },
                tooltip: {
                    backgroundColor: 'rgba(0, 0, 0, 0.8)',
                    padding: 12,
                    titleFont: {
                        size: 14,
                        weight: 'bold'
                    },
                    bodyFont: {
                        size: 13
                    },
                    callbacks: {
                        label: function (context) {
                            const value = context.parsed.y;
                            const state = (value === 1.0 || value === 2.0) ? 'ON' : 'OFF';
                            return ` ${context.dataset.label}: ${state}`;
                        }
                    }
                }
            },
            scales: {
                x: {
                    type: 'category',
                    title: {
                        display: true,
                        text: '시간',
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    },
                    ticks: {
                        maxRotation: 45,
                        minRotation: 45,
                        autoSkip: true,
                        maxTicksLimit: 20
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.05)'
                    }
                },
                y: {
                    min: -0.2,
                    max: 2.5,
                    title: {
                        display: true,
                        text: '신호 상태',
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    },
                    ticks: {
                        stepSize: 1,
                        callback: function (value) {
                            if (value === 0) return 'OFF';
                            if (value === 1) return 'ON (In)';
                            if (value === 2) return 'ON (Out)';
                            return '';
                        }
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.1)'
                    }
                }
            }
        }
    });
};

window.renderHistogramChart = function (labels, counts) {
    const ctx = document.getElementById('histogramChart');
    if (!ctx) {
        console.error('Canvas element not found: histogramChart');
        return;
    }

    // Destroy existing chart
    if (histogramChartInstance) {
        histogramChartInstance.destroy();
    }

    // Create gradient
    const gradient = ctx.getContext('2d').createLinearGradient(0, 0, 0, 300);
    gradient.addColorStop(0, 'rgba(255, 99, 132, 0.8)');
    gradient.addColorStop(1, 'rgba(255, 159, 64, 0.8)');

    histogramChartInstance = new Chart(ctx, {
        type: 'bar',
        data: {
            labels: labels,
            datasets: [{
                label: '빈도',
                data: counts,
                backgroundColor: gradient,
                borderColor: 'rgba(255, 99, 132, 1)',
                borderWidth: 2,
                borderRadius: 6,
                borderSkipped: false
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: true,
            plugins: {
                legend: {
                    display: false
                },
                tooltip: {
                    backgroundColor: 'rgba(0, 0, 0, 0.8)',
                    padding: 12,
                    titleFont: {
                        size: 14,
                        weight: 'bold'
                    },
                    bodyFont: {
                        size: 13
                    },
                    callbacks: {
                        title: function (context) {
                            return `범위: ${context[0].label} sec`;
                        },
                        label: function (context) {
                            return ` 빈도: ${context.parsed.y}`;
                        }
                    }
                }
            },
            scales: {
                y: {
                    beginAtZero: true,
                    ticks: {
                        stepSize: 1,
                        callback: function (value) {
                            return Math.floor(value);
                        }
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.05)'
                    }
                },
                x: {
                    grid: {
                        display: false
                    },
                    ticks: {
                        maxRotation: 45,
                        minRotation: 45
                    }
                }
            }
        }
    });
};

// IO Gantt Chart - Y축이 IO 태그, X축이 시간, ON 구간을 bar로 표시
window.renderIOGanttChart = function (ioTags, ioData) {
    const ctx = document.getElementById('cycleTimeChart');
    if (!ctx) {
        console.error('Canvas element not found: cycleTimeChart');
        return;
    }

    // Destroy existing chart
    if (ioGanttChartInstance) {
        ioGanttChartInstance.destroy();
    }

    // ioData 구조:
    // {
    //   tagName: "InTag",
    //   segments: [
    //     { start: timestamp, end: timestamp, state: 'ON' },
    //     ...
    //   ]
    // }

    const datasets = [];
    const colors = [
        'rgba(54, 162, 235, 0.8)',   // Blue
        'rgba(255, 99, 132, 0.8)',   // Red
        'rgba(75, 192, 192, 0.8)',   // Green
        'rgba(255, 159, 64, 0.8)',   // Orange
        'rgba(153, 102, 255, 0.8)',  // Purple
        'rgba(255, 206, 86, 0.8)',   // Yellow
    ];

    // 실제 데이터의 최소/최대 시간 계산
    let minTime = Infinity;
    let maxTime = -Infinity;

    ioData.forEach((io, index) => {
        const color = colors[index % colors.length];

        // ON 구간을 bar로 표시
        io.segments.forEach((segment, segIdx) => {
            // ISO 8601 문자열을 Date 객체로 변환
            const startTime = new Date(segment.start).getTime();
            const endTime = new Date(segment.end).getTime();

            // 최소/최대 시간 업데이트
            minTime = Math.min(minTime, startTime);
            maxTime = Math.max(maxTime, endTime);

            datasets.push({
                label: segIdx === 0 ? io.tagName : '', // 첫 번째 세그먼트만 레이블 표시
                data: [{
                    x: [startTime, endTime],
                    y: io.tagName
                }],
                backgroundColor: color,
                borderColor: color.replace('0.8', '1'),
                borderWidth: 1,
                barThickness: 20,
                categoryPercentage: 0.8,
                barPercentage: 0.9
            });
        });
    });

    ioGanttChartInstance = new Chart(ctx, {
        type: 'bar',
        data: {
            labels: ioTags,
            datasets: datasets
        },
        options: {
            indexAxis: 'y',  // Horizontal bar
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    display: true,
                    position: 'top',
                    labels: {
                        filter: function(item) {
                            return item.text !== '';  // 빈 레이블 제거
                        },
                        usePointStyle: true,
                        padding: 15,
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    }
                },
                tooltip: {
                    backgroundColor: 'rgba(0, 0, 0, 0.8)',
                    padding: 12,
                    titleFont: {
                        size: 14,
                        weight: 'bold'
                    },
                    bodyFont: {
                        size: 13
                    },
                    callbacks: {
                        title: function(context) {
                            return context[0].label;
                        },
                        label: function(context) {
                            const data = context.raw;
                            if (data && data.x && Array.isArray(data.x)) {
                                const start = new Date(data.x[0]).toLocaleTimeString('ko-KR');
                                const end = new Date(data.x[1]).toLocaleTimeString('ko-KR');
                                const duration = ((data.x[1] - data.x[0]) / 1000).toFixed(2);
                                return [
                                    `시작: ${start}`,
                                    `종료: ${end}`,
                                    `지속: ${duration}s`
                                ];
                            }
                            return 'ON';
                        }
                    }
                }
            },
            scales: {
                x: {
                    type: 'time',
                    min: minTime !== Infinity ? minTime : undefined,
                    max: maxTime !== -Infinity ? maxTime : undefined,
                    time: {
                        displayFormats: {
                            millisecond: 'HH:mm:ss.SSS',
                            second: 'HH:mm:ss',
                            minute: 'HH:mm',
                            hour: 'HH:mm'
                        }
                    },
                    title: {
                        display: true,
                        text: '시간',
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.05)'
                    }
                },
                y: {
                    title: {
                        display: true,
                        text: 'IO Tags',
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.1)'
                    }
                }
            }
        }
    });
};

window.renderSignalTimelineChart = function (inLabel, outLabel, inTimestamps, inValues, outTimestamps, outValues) {
    const ctx = document.getElementById('signalTimelineChart');
    if (!ctx) {
        console.error('Canvas element not found: signalTimelineChart');
        return;
    }

    // Destroy existing chart
    if (signalTimelineChartInstance) {
        signalTimelineChartInstance.destroy();
    }

    // Prepare datasets
    const datasets = [];

    if (inTimestamps && inTimestamps.length > 0) {
        datasets.push({
            label: inLabel,
            data: inTimestamps.map((t, i) => ({ x: t, y: inValues[i] })),
            borderColor: 'rgba(54, 162, 235, 1)',
            backgroundColor: 'rgba(54, 162, 235, 0.1)',
            borderWidth: 2,
            stepped: true,
            fill: false,
            pointRadius: 0,
            tension: 0
        });
    }

    if (outTimestamps && outTimestamps.length > 0) {
        datasets.push({
            label: outLabel,
            data: outTimestamps.map((t, i) => ({ x: t, y: outValues[i] })),
            borderColor: 'rgba(255, 99, 132, 1)',
            backgroundColor: 'rgba(255, 99, 132, 0.1)',
            borderWidth: 2,
            stepped: true,
            fill: false,
            pointRadius: 0,
            tension: 0
        });
    }

    signalTimelineChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            datasets: datasets
        },
        options: {
            responsive: true,
            maintainAspectRatio: true,
            interaction: {
                mode: 'index',
                intersect: false
            },
            plugins: {
                legend: {
                    display: true,
                    position: 'top',
                    labels: {
                        usePointStyle: true,
                        padding: 15,
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    }
                },
                tooltip: {
                    backgroundColor: 'rgba(0, 0, 0, 0.8)',
                    padding: 12,
                    titleFont: {
                        size: 14,
                        weight: 'bold'
                    },
                    bodyFont: {
                        size: 13
                    },
                    callbacks: {
                        label: function (context) {
                            const value = context.parsed.y;
                            const state = (value === 1.0 || value === 2.0) ? 'ON' : 'OFF';
                            return ` ${context.dataset.label}: ${state}`;
                        }
                    }
                }
            },
            scales: {
                x: {
                    type: 'category',
                    title: {
                        display: true,
                        text: '시간',
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    },
                    ticks: {
                        maxRotation: 45,
                        minRotation: 45,
                        autoSkip: true,
                        maxTicksLimit: 20
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.05)'
                    }
                },
                y: {
                    min: -0.2,
                    max: 2.5,
                    title: {
                        display: true,
                        text: '신호 상태',
                        font: {
                            size: 12,
                            weight: 'bold'
                        }
                    },
                    ticks: {
                        stepSize: 1,
                        callback: function (value) {
                            if (value === 0) return 'OFF';
                            if (value === 1) return 'ON (In)';
                            if (value === 2) return 'ON (Out)';
                            return '';
                        }
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.1)'
                    }
                }
            }
        }
    });
};
