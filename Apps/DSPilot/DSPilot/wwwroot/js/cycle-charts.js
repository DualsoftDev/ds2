// Cycle Time Analysis Charts
let cycleTimeChartInstance = null;
let histogramChartInstance = null;
let signalTimelineChartInstance = null;

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
