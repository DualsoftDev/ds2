// Flow History Chart using Chart.js
let flowHistoryChartInstance = null;

window.renderFlowHistoryChart = function (canvasId, chartData) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) {
        console.error('Canvas element not found:', canvasId);
        return;
    }

    const ctx = canvas.getContext('2d');

    // Destroy existing chart instance
    if (flowHistoryChartInstance) {
        flowHistoryChartInstance.destroy();
    }

    // Create new chart
    flowHistoryChartInstance = new Chart(ctx, {
        type: 'line',
        data: chartData,
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: 'index',
                intersect: false,
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
                            weight: '600'
                        }
                    }
                },
                tooltip: {
                    enabled: true,
                    mode: 'index',
                    intersect: false,
                    backgroundColor: 'rgba(0, 0, 0, 0.8)',
                    titleFont: {
                        size: 13,
                        weight: 'bold'
                    },
                    bodyFont: {
                        size: 12
                    },
                    padding: 12,
                    cornerRadius: 6,
                    callbacks: {
                        label: function (context) {
                            let label = context.dataset.label || '';
                            if (label) {
                                label += ': ';
                            }
                            if (context.parsed.y !== null) {
                                label += context.parsed.y + ' ms';
                            }
                            return label;
                        }
                    }
                }
            },
            scales: {
                x: {
                    display: true,
                    title: {
                        display: true,
                        text: 'Cycle Number',
                        font: {
                            size: 12,
                            weight: '600'
                        }
                    },
                    grid: {
                        display: false
                    },
                    ticks: {
                        maxTicksLimit: 20,
                        font: {
                            size: 10
                        }
                    }
                },
                y: {
                    display: true,
                    title: {
                        display: true,
                        text: 'Time (ms)',
                        font: {
                            size: 12,
                            weight: '600'
                        }
                    },
                    beginAtZero: true,
                    grid: {
                        color: 'rgba(0, 0, 0, 0.05)'
                    },
                    ticks: {
                        font: {
                            size: 10
                        }
                    }
                }
            },
            elements: {
                point: {
                    radius: 2,
                    hitRadius: 8,
                    hoverRadius: 5
                },
                line: {
                    tension: 0.2
                }
            }
        }
    });
};

// Cleanup function when modal is closed
window.destroyFlowHistoryChart = function () {
    if (flowHistoryChartInstance) {
        flowHistoryChartInstance.destroy();
        flowHistoryChartInstance = null;
    }
};
