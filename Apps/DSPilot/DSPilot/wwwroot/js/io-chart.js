// Chart.js wrapper for IO data visualization
const charts = {};

export function renderChart(chartId, data) {
    const canvas = document.getElementById(chartId);
    if (!canvas) {
        console.error(`Canvas with id ${chartId} not found`);
        return;
    }

    const ctx = canvas.getContext('2d');

    // Destroy existing chart if any
    if (charts[chartId]) {
        charts[chartId].destroy();
    }

    // Create new chart
    charts[chartId] = new Chart(ctx, {
        type: 'line',
        data: data,
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: 'index',
                intersect: false,
            },
            scales: {
                x: {
                    type: 'time',
                    time: {
                        parser: 'YYYY-MM-DDTHH:mm:ss',
                        tooltipFormat: 'HH:mm:ss.SSS',
                        displayFormats: {
                            second: 'HH:mm:ss'
                        }
                    },
                    title: {
                        display: true,
                        text: 'Time'
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.05)'
                    }
                },
                y: {
                    beginAtZero: true,
                    max: 1.2,
                    ticks: {
                        stepSize: 1,
                        callback: function(value) {
                            return value === 1 ? 'ON' : value === 0 ? 'OFF' : '';
                        }
                    },
                    title: {
                        display: true,
                        text: 'Signal State'
                    },
                    grid: {
                        color: 'rgba(0, 0, 0, 0.1)'
                    }
                }
            },
            plugins: {
                legend: {
                    display: true,
                    position: 'top',
                },
                tooltip: {
                    enabled: true,
                    callbacks: {
                        label: function(context) {
                            let label = context.dataset.label || '';
                            if (label) {
                                label += ': ';
                            }
                            label += context.parsed.y === 1 ? 'ON' : 'OFF';
                            return label;
                        }
                    }
                },
                zoom: {
                    pan: {
                        enabled: true,
                        mode: 'x'
                    },
                    zoom: {
                        wheel: {
                            enabled: true
                        },
                        pinch: {
                            enabled: true
                        },
                        mode: 'x'
                    }
                }
            },
            elements: {
                point: {
                    radius: 0,
                    hitRadius: 10
                },
                line: {
                    tension: 0,
                    borderWidth: 2
                }
            }
        }
    });
}

export function destroyChart(chartId) {
    if (charts[chartId]) {
        charts[chartId].destroy();
        delete charts[chartId];
    }
}

export function updateChart(chartId, data) {
    if (charts[chartId]) {
        charts[chartId].data = data;
        charts[chartId].update();
    } else {
        renderChart(chartId, data);
    }
}
