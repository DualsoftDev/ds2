# Phase 3: Dashboard 위젯 개선

**우선순위**: 중  
**예상 기간**: 2-3일  
**목표**: Dashboard에 실시간 통계 및 상태 위젯 추가

---

## 📋 작업 개요

기존 Dashboard 페이지에 Direction별 Call 상태, 실시간 통계 차트, 병목 감지 알림 위젯을 추가하여 한눈에 시스템 상태를 파악할 수 있도록 개선합니다.

---

## 🎯 작업 항목

### 1. Direction별 Call 상태 위젯

**위치**: Dashboard.razor에 추가

```razor
@* Direction별 Call 상태 요약 위젯 *@
<section class="card dashboard-direction-widget">
    <div class="card-header">
        <span class="material-icons mr-2">category</span>
        <h6 class="typo-h6">Direction별 Call 상태</h6>
    </div>
    <div class="card-content">
        <div class="direction-grid">
            @* InOut *@
            <div class="direction-card">
                <div class="direction-header">
                    <span class="direction-icon" style="background:#4CAF50;">IO</span>
                    <span class="direction-name">InOut</span>
                </div>
                <div class="direction-stats">
                    <div class="stat-row">
                        <span class="stat-label">Ready</span>
                        <span class="stat-value">@_directionStats.InOut.ReadyCount</span>
                    </div>
                    <div class="stat-row">
                        <span class="stat-label">Going</span>
                        <span class="stat-value state-going">@_directionStats.InOut.GoingCount</span>
                    </div>
                    <div class="stat-row">
                        <span class="stat-label">Finish</span>
                        <span class="stat-value state-finish">@_directionStats.InOut.FinishCount</span>
                    </div>
                </div>
            </div>

            @* InOnly *@
            <div class="direction-card">
                <div class="direction-header">
                    <span class="direction-icon" style="background:#2196F3;">I</span>
                    <span class="direction-name">InOnly</span>
                </div>
                <div class="direction-stats">
                    <div class="stat-row">
                        <span class="stat-label">Ready</span>
                        <span class="stat-value">@_directionStats.InOnly.ReadyCount</span>
                    </div>
                    <div class="stat-row">
                        <span class="stat-label">Finish</span>
                        <span class="stat-value state-finish">@_directionStats.InOnly.FinishCount</span>
                    </div>
                </div>
            </div>

            @* OutOnly *@
            <div class="direction-card">
                <div class="direction-header">
                    <span class="direction-icon" style="background:#FF9800;">O</span>
                    <span class="direction-name">OutOnly</span>
                </div>
                <div class="direction-stats">
                    <div class="stat-row">
                        <span class="stat-label">Ready</span>
                        <span class="stat-value">@_directionStats.OutOnly.ReadyCount</span>
                    </div>
                    <div class="stat-row">
                        <span class="stat-label">Going</span>
                        <span class="stat-value state-going">@_directionStats.OutOnly.GoingCount</span>
                    </div>
                </div>
            </div>
        </div>
    </div>
</section>

@code {
    private DirectionStatsModel _directionStats = new();

    private class DirectionStatsModel
    {
        public DirectionStat InOut { get; set; } = new();
        public DirectionStat InOnly { get; set; } = new();
        public DirectionStat OutOnly { get; set; } = new();
    }

    private class DirectionStat
    {
        public int ReadyCount { get; set; }
        public int GoingCount { get; set; }
        public int FinishCount { get; set; }
    }

    private async Task LoadDirectionStats()
    {
        var allCalls = await DbService.GetAllCallsAsync();
        
        var callsByDirection = allCalls
            .GroupBy(c => c.Direction)
            .ToDictionary(g => g.Key, g => g.ToList());

        // InOut
        if (callsByDirection.TryGetValue("InOut", out var inOutCalls))
        {
            _directionStats.InOut = new DirectionStat
            {
                ReadyCount = inOutCalls.Count(c => c.State == "Ready"),
                GoingCount = inOutCalls.Count(c => c.State == "Going"),
                FinishCount = inOutCalls.Count(c => c.State == "Finish")
            };
        }

        // InOnly
        if (callsByDirection.TryGetValue("InOnly", out var inOnlyCalls))
        {
            _directionStats.InOnly = new DirectionStat
            {
                ReadyCount = inOnlyCalls.Count(c => c.State == "Ready"),
                FinishCount = inOnlyCalls.Count(c => c.State == "Finish")
            };
        }

        // OutOnly
        if (callsByDirection.TryGetValue("OutOnly", out var outOnlyCalls))
        {
            _directionStats.OutOnly = new DirectionStat
            {
                ReadyCount = outOnlyCalls.Count(c => c.State == "Ready"),
                GoingCount = outOnlyCalls.Count(c => c.State == "Going")
            };
        }
    }
}
```

### 2. 실시간 통계 차트 위젯

```razor
@* 실시간 MT/WT/CT 차트 *@
<section class="card dashboard-stats-chart">
    <div class="card-header">
        <span class="material-icons mr-2">bar_chart</span>
        <h6 class="typo-h6">실시간 통계 (최근 30초)</h6>
    </div>
    <div class="card-content">
        <canvas id="statsChart" style="max-height: 300px;"></canvas>
    </div>
</section>

@code {
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await InitializeChart();
        }
    }

    private async Task InitializeChart()
    {
        await JS.InvokeVoidAsync("initStatsChart", "statsChart");
    }
}
```

**JavaScript 파일**: `wwwroot/js/dashboard-charts.js`

```javascript
window.initStatsChart = function(canvasId) {
    const ctx = document.getElementById(canvasId).getContext('2d');
    window.statsChart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                {
                    label: 'MT (Moving Time)',
                    data: [],
                    borderColor: '#4CAF50',
                    tension: 0.4
                },
                {
                    label: 'WT (Waiting Time)',
                    data: [],
                    borderColor: '#FF9800',
                    tension: 0.4
                },
                {
                    label: 'CT (Cycle Time)',
                    data: [],
                    borderColor: '#2196F3',
                    tension: 0.4
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                y: {
                    beginAtZero: true,
                    title: {
                        display: true,
                        text: 'Time (ms)'
                    }
                }
            }
        }
    });
};

window.updateStatsChart = function(timestamp, mt, wt, ct) {
    if (!window.statsChart) return;
    
    const chart = window.statsChart;
    chart.data.labels.push(timestamp);
    chart.data.datasets[0].data.push(mt);
    chart.data.datasets[1].data.push(wt);
    chart.data.datasets[2].data.push(ct);
    
    // 최근 30개만 유지
    if (chart.data.labels.length > 30) {
        chart.data.labels.shift();
        chart.data.datasets.forEach(dataset => dataset.data.shift());
    }
    
    chart.update();
};
```

### 3. 병목 감지 알림 위젯

```razor
@* 병목 감지 알림 위젯 *@
<section class="card dashboard-bottleneck-alerts">
    <div class="card-header">
        <span class="material-icons mr-2">warning</span>
        <h6 class="typo-h6">병목 감지 알림</h6>
    </div>
    <div class="card-content">
        @if (_bottleneckAlerts.Any())
        {
            <div class="alert-list">
                @foreach (var alert in _bottleneckAlerts.Take(5))
                {
                    <div class="alert-item @GetAlertClass(alert.Severity)">
                        <span class="material-icons alert-icon">@GetAlertIcon(alert.Severity)</span>
                        <div class="alert-content">
                            <div class="alert-title">@alert.CallName</div>
                            <div class="alert-message">@alert.Message</div>
                            <div class="alert-time">@alert.Timestamp.ToString("HH:mm:ss")</div>
                        </div>
                    </div>
                }
            </div>
        }
        else
        {
            <div class="empty-state">
                <span class="material-icons">check_circle</span>
                <span>현재 병목 현상이 감지되지 않았습니다</span>
            </div>
        }
    </div>
</section>

@code {
    private List<BottleneckAlert> _bottleneckAlerts = new();

    private class BottleneckAlert
    {
        public string CallName { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public AlertSeverity Severity { get; set; }
    }

    private enum AlertSeverity
    {
        Info,
        Warning,
        Error
    }

    private string GetAlertClass(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Error => "alert-error",
        AlertSeverity.Warning => "alert-warning",
        _ => "alert-info"
    };

    private string GetAlertIcon(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Error => "error",
        AlertSeverity.Warning => "warning",
        _ => "info"
    };

    private async Task DetectBottlenecks()
    {
        // F# BottleneckDetection 모듈 사용
        var store = ProjectService.GetStore();
        var allFlows = ProjectService.GetAllFlows();
        
        foreach (var flow in allFlows)
        {
            var bottlenecks = DSPilot.Engine.Analysis.BottleneckDetection.detectBottlenecks(flow, store);
            
            // 병목 감지 시 알림 추가
            foreach (var bottleneck in bottlenecks)
            {
                _bottleneckAlerts.Add(new BottleneckAlert
                {
                    CallName = bottleneck.CallName,
                    Message = $"평균 대기 시간: {bottleneck.AverageWaitTime:F2}ms (임계값 초과)",
                    Timestamp = DateTime.Now,
                    Severity = bottleneck.AverageWaitTime > 1000 ? AlertSeverity.Error : AlertSeverity.Warning
                });
            }
        }

        // 최대 100개까지만 유지
        if (_bottleneckAlerts.Count > 100)
        {
            _bottleneckAlerts = _bottleneckAlerts.TakeLast(100).ToList();
        }
    }
}
```

### 4. Flow 상태 히트맵 위젯

```razor
@* Flow 상태 히트맵 *@
<section class="card dashboard-flow-heatmap">
    <div class="card-header">
        <span class="material-icons mr-2">grid_on</span>
        <h6 class="typo-h6">Flow 상태 히트맵</h6>
    </div>
    <div class="card-content">
        <div class="flow-heatmap-grid">
            @foreach (var flow in _flowHeatmapData)
            {
                <div class="flow-heatmap-cell" style="background:@GetHeatmapColor(flow.UtilizationRate)">
                    <div class="flow-heatmap-name">@flow.FlowName</div>
                    <div class="flow-heatmap-value">@(flow.UtilizationRate.ToString("P0"))</div>
                </div>
            }
        </div>
    </div>
</section>

@code {
    private List<FlowHeatmapData> _flowHeatmapData = new();

    private class FlowHeatmapData
    {
        public string FlowName { get; set; } = "";
        public double UtilizationRate { get; set; }
    }

    private string GetHeatmapColor(double utilization)
    {
        if (utilization >= 0.8) return "#388E3C"; // 진한 녹색 (80% 이상)
        if (utilization >= 0.6) return "#66BB6A"; // 연한 녹색 (60-80%)
        if (utilization >= 0.4) return "#FFA726"; // 주황색 (40-60%)
        if (utilization >= 0.2) return "#FF7043"; // 진한 주황색 (20-40%)
        return "#F44336"; // 빨간색 (20% 미만)
    }

    private async Task LoadFlowHeatmapData()
    {
        var allFlows = await DbService.GetAllFlowsAsync();
        
        _flowHeatmapData = allFlows.Select(flow =>
        {
            // ActiveCallCount / TotalCallCount로 가동률 계산
            var totalCalls = ProjectService.GetAllCalls().Count(c => c.ParentId == flow.Id);
            var utilization = totalCalls > 0 ? (double)flow.ActiveCallCount / totalCalls : 0;
            
            return new FlowHeatmapData
            {
                FlowName = flow.FlowName,
                UtilizationRate = utilization
            };
        }).ToList();
    }
}
```

---

## 📝 CSS 스타일

**파일**: `wwwroot/css/dashboard-widgets.css` (새 파일)

```css
/* Direction 위젯 */
.dashboard-direction-widget .direction-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 16px;
}

.direction-card {
    background: var(--color-background);
    border-radius: 12px;
    padding: 16px;
}

.direction-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 12px;
}

.direction-icon {
    width: 32px;
    height: 32px;
    border-radius: 8px;
    display: flex;
    align-items: center;
    justify-content: center;
    color: white;
    font-weight: 700;
    font-size: 14px;
}

.direction-name {
    font-weight: 600;
    font-size: 14px;
}

.direction-stats {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.stat-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
}

.stat-label {
    font-size: 12px;
    color: var(--color-text-secondary);
}

.stat-value {
    font-weight: 600;
    font-size: 16px;
}

.stat-value.state-going {
    color: #FF9800;
}

.stat-value.state-finish {
    color: #4CAF50;
}

/* 병목 알림 위젯 */
.alert-list {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.alert-item {
    display: flex;
    gap: 12px;
    padding: 12px;
    border-radius: 8px;
    border-left: 4px solid;
}

.alert-item.alert-error {
    background: #FFEBEE;
    border-color: #F44336;
}

.alert-item.alert-warning {
    background: #FFF3E0;
    border-color: #FF9800;
}

.alert-item.alert-info {
    background: #E3F2FD;
    border-color: #2196F3;
}

.alert-icon {
    flex-shrink: 0;
}

.alert-content {
    flex: 1;
}

.alert-title {
    font-weight: 600;
    margin-bottom: 4px;
}

.alert-message {
    font-size: 13px;
    color: var(--color-text-secondary);
}

.alert-time {
    font-size: 11px;
    color: var(--color-text-tertiary);
    margin-top: 4px;
}

/* Flow 히트맵 */
.flow-heatmap-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
    gap: 8px;
}

.flow-heatmap-cell {
    aspect-ratio: 1;
    border-radius: 8px;
    padding: 12px;
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    color: white;
    font-weight: 600;
    transition: transform 0.2s;
}

.flow-heatmap-cell:hover {
    transform: scale(1.05);
    cursor: pointer;
}

.flow-heatmap-name {
    font-size: 11px;
    text-align: center;
    margin-bottom: 8px;
}

.flow-heatmap-value {
    font-size: 18px;
}
```

---

## 📝 구현 체크리스트

- [ ] Direction별 Call 상태 위젯 구현
- [ ] 실시간 통계 차트 구현
- [ ] Chart.js 라이브러리 추가
- [ ] 병목 감지 알림 위젯 구현
- [ ] Flow 상태 히트맵 위젯 구현
- [ ] CSS 스타일 추가
- [ ] SignalR 연동 (실시간 업데이트)
- [ ] 빌드 및 테스트

---

## 🚀 다음 단계

Phase 3 완료 후:
- `PHASE4_UX_UI_IMPROVEMENT.md` 진행
- 다크모드 및 반응형 디자인 구현
