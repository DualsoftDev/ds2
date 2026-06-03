# Ds2.Reverse.Studio — WPF 설계

> 사용자가 직접 GUI 로 (1) 랜덤 DS2 모델 생성 → (2) 가시화 → (3) 시뮬레이션 + 로그 차트 → (4) 역변환 알고리즘 적용 → (5) 정확도 리포트 확인 까지 한 화면에서 수행.

---

## 1. 핵심 사용 시나리오

```
[Generate]              [Simulate]                [Reverse]              [Verify]
랜덤 DS2 모델 생성   →   시뮬레이션 (events)   →   알고리즘 검증 실행   →   정답 vs 검출 비교
(Case A/B 선택)         (timeline 차트 표시)      (Ds2.Reverse.Core)      (P/R/F1 표시)
```

한 사이클에 모든 단계 자동 실행되거나, 사용자가 단계별 [Step] 버튼으로 진행.

---

## 2. 모델 케이스

### Case A — 인라인 설비 (Inline Line)

**구조**:
- Active System 1 (Main)
- Active Flow 1 (Line)
- Multiple Works W1 → W2 → ... → Wn (StartReset 으로 연결)
- 각 Work 안 1~2 calls (예: ADV / RET 페어)

**Arrows**:
- arrowWorks: W1 → W2 → ... → Wn (모두 StartReset, ArrowType=3)
- arrowCalls: 각 Work 안 단순 ADV → RET (Start) 1개

**생성 파라미터**:
- 스테이지 수 `nStages` (3 ~ 20)
- 캐파 `capacity` (1 ~ 10)
- avg lag (200 ~ 1000ms)
- jitter (10 ~ 50ms)

**랜덤성**:
- 스테이지 수 무작위
- 일부 스테이지에 GroupReset variant (10% 확률) — Group arrow 추가
- 일부 스테이지에 fan-out (5% 확률, 2nd 작업) 추가

---

### Case B — 단독 Work + Internal DAG

**구조**:
- Active System 1 (Main)
- Active Flow 1 (Flow1)
- Work 1 (WorkA) — 한 work 안에 다수 calls
- N calls (call0, call1, ..., callN-1)
- 무작위 DAG (intra-work arrowCalls)

**Arrows**:
- arrowCalls: 무작위 DAG. 모든 (i, j) 쌍 (i < j) 에서 edge probability `density` 로 선택.
- 일부 edges 는 Group (5% 확률).

**생성 파라미터**:
- call 수 `nCalls` (5 ~ 30)
- density (0.1 ~ 0.5) — DAG 간선 밀도
- 사슬 깊이 vs 분기 정도 (depth/branch ratio)
- group probability (0 ~ 0.2)

**랜덤성**:
- DAG topological order 따라 timing 자동 생성 (parent.finish + stage_lag = child.start)
- branch + join 패턴 다양

---

## 3. 솔루션 구조

```
Solutions/Reverse/Apps/Ds2.Reverse.Studio/
├── Ds2.Reverse.Studio.csproj                    WPF .NET 9 (C#)
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── ViewModels/
│   ├── MainViewModel.cs                          전체 상태 + 명령 dispatch
│   ├── GeneratorViewModel.cs                     Case A/B + 파라미터
│   ├── ModelViewerViewModel.cs                   생성된 모델 가시화 데이터
│   ├── SimulationViewModel.cs                    시뮬 events 차트 데이터
│   └── ReverseReportViewModel.cs                 역변환 결과 + metrics
├── Models/
│   ├── GeneratorOptions.cs                       파라미터 record
│   ├── GeneratedModel.cs                         { DsStore, GroundTruth }
│   ├── InlineLineGenerator.cs                    Case A 생성기
│   ├── StandaloneDagGenerator.cs                 Case B 생성기
│   └── ReverseResult.cs                          { Detected, Metrics, Report }
├── Services/
│   ├── SimulationService.cs                      모델 → CapturedEvent 시퀀스
│   ├── ReverseService.cs                         Ds2.Reverse.Core.ReverseEngine 래퍼
│   └── ExportService.cs                          sdf 저장 + 결과 export
├── Views/
│   ├── ModelGraphView.xaml                       모델 그래프 시각화 (SkiaSharp / 직접 Canvas)
│   ├── TimelineChartView.xaml                    LiveCharts2 — 이벤트 timeline (lane chart)
│   ├── MetricsBoardView.xaml                     P/R/F1 + 분포 차트
│   └── ReportDiffView.xaml                       원본 vs 검출 arrows 비교 테이블
├── Converters/
│   ├── BoolToVisibilityConverter.cs
│   └── ArrowTypeBrushConverter.cs
├── Styles/
│   └── Theme.xaml                                Material-ish 컬러
└── Assets/
    └── icon.png
```

### Project References

```xml
<ProjectReference Include="..\..\Ds2.Reverse.Core\Ds2.Reverse.Core.fsproj" />
<ProjectReference Include="..\..\Ds2.Reverse.Bench\Ds2.Reverse.Bench.fsproj" />
<ProjectReference Include="..\..\..\Core\Ds2.Core\Ds2.Core.fsproj" />
```

### Package References

```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
<PackageReference Include="LiveChartsCore.SkiaSharpView.WPF" Version="2.*" />
<PackageReference Include="MahApps.Metro" Version="2.*" />
```

---

## 4. MainWindow 레이아웃

```
┌─────────────────────────────────────────────────────────────────────────┐
│ [≡] Ds2.Reverse.Studio                                          [_][□][×]│
├─────────────────────────────────────────────────────────────────────────┤
│  📁 New  │  ⚙ Generate  │  ▶ Simulate  │  🔍 Reverse  │  💾 Export  │  ❓ │
├──────────────┬──────────────────────────────────────────────────────────┤
│ Generator    │ ┌─Model────────────────────────────────────────────────┐ │
│ ─────────────│ │                                                       │ │
│ Case:        │ │      [Model Graph — nodes/edges 시각화]               │ │
│ ◉ A Inline   │ │                                                       │ │
│ ○ B DAG      │ │                                                       │ │
│              │ │                                                       │ │
│ Params:      │ └───────────────────────────────────────────────────────┘ │
│ Stages: ▢ 8  │ ┌─Simulation Log───────────────────────────────────────┐ │
│ Capacity: ▢ 3│ │                                                       │ │
│ Lag avg ms:  │ │      [Timeline lanes per call]                        │ │
│ ▢ 500        │ │                                                       │ │
│ Jitter: ▢ 20 │ │                                                       │ │
│ Seed: ▢ 42   │ └───────────────────────────────────────────────────────┘ │
│              │ ┌─Reverse Report───────────────────────────────────────┐ │
│ [Generate]   │ │ P=1.000 R=0.973 F1=0.986                              │ │
│ [Simulate]   │ │ TP=37 FP=0 FN=1                                       │ │
│ [Reverse]    │ │ Missing: F1.D2.RET → F1.D1.RET (Group, lag too noisy) │ │
│              │ │                                                       │ │
│              │ │ [arrows diff table]                                   │ │
│ Auto run ✓   │ └───────────────────────────────────────────────────────┘ │
├──────────────┴──────────────────────────────────────────────────────────┤
│ Status: Ready  •  60 cycles simulated  •  4980 events  •  F1=0.986        │
└─────────────────────────────────────────────────────────────────────────┘
```

**3분할**:
- 좌측 사이드바 (300px): Generator 패널
- 우측 메인 영역 (수직 3분할):
  - 상단 1/3: Model Graph (그래프 시각화)
  - 중간 1/3: Simulation Log (timeline chart)
  - 하단 1/3: Reverse Report (metrics + diff)

각 영역은 사용자가 splitter 로 크기 조정 가능.

---

## 5. 데이터 흐름

```
GeneratorVM ──[GenerateCommand]──► GenerateModel()
                                       │
                                       ▼
                                  GeneratedModel
                                  ├── DsStore          (정답 모델)
                                  ├── GroundTruth      (arrowCalls + arrowWorks)
                                  └── Metadata         (seed, params)
                                       │
                                       ▼
                                  MainVM.CurrentModel
                                       │
        ┌──────────────────────────────┼──────────────────────────────┐
        ▼                              ▼                              ▼
ModelViewerVM                  SimulationVM                  ReverseReportVM
(graph render)                 [SimulateCommand]              [ReverseCommand]
                                  │                                   │
                                  ▼                                   ▼
                              CapturedEvent[]                 ReverseResult
                                  │                                   │
                                  ▼                                   ▼
                              TimelineChartView              MetricsBoard
                                                              + DiffTable
```

---

## 6. 알고리즘 인터페이스

### IModelGenerator (C#)

```csharp
public interface IModelGenerator
{
    GeneratedModel Generate(GeneratorOptions options);
}

public record GeneratorOptions(
    int? Seed,
    int Param1, int Param2, ...);

public record GeneratedModel(
    DsStore Store,
    IReadOnlyList<ArrowSpec> GroundTruth,
    string CaseName,
    GeneratorOptions Options);

public record ArrowSpec(string Src, string Tgt, ArrowKind Kind);
public enum ArrowKind { Start, Group, Reset, StartReset, ResetReset }
```

### Case A 생성 알고리즘

```csharp
public GeneratedModel Generate(GeneratorOptions opts)
{
    var rng = new Random(opts.Seed ?? Random.Shared.Next());
    var store = ModelBuilder.emptyStore("InlineLine", "Main");
    var flowId = ModelBuilder.addFlow(store, mainSys, "Line");
    var works = new List<Guid>();
    for (int i = 1; i <= opts.NStages; i++)
    {
        var wid = ModelBuilder.addWork(store, flowId, "Line", $"W{i}");
        works.Add(wid);
        // 각 Work 안 ADV/RET calls
        var advId = ModelBuilder.addCallWithApi(store, wid, flowId, $"S{i}.ADV", "");
        var retId = ModelBuilder.addCallWithApi(store, wid, flowId, $"S{i}.RET", "");
        ModelBuilder.addArrowCall(store, wid, advId, retId, ArrowType.Start);
    }
    // Works 간 StartReset chain
    var arrows = new List<ArrowSpec>();
    for (int i = 0; i < works.Count - 1; i++)
    {
        ModelBuilder.addArrowWork(store, mainSys, works[i], works[i + 1], ArrowType.StartReset);
        arrows.Add(new ArrowSpec($"Line.W{i+1}", $"Line.W{i+2}", ArrowKind.StartReset));
    }
    return new GeneratedModel(store, arrows, "InlineLine", opts);
}
```

### Case B 생성 알고리즘 (Random DAG)

```csharp
public GeneratedModel Generate(GeneratorOptions opts)
{
    var rng = new Random(opts.Seed ?? Random.Shared.Next());
    var store = ModelBuilder.emptyStore("DagWork", "Main");
    var flowId = ModelBuilder.addFlow(store, mainSys, "Flow1");
    var workId = ModelBuilder.addWork(store, flowId, "Flow1", "WorkA");

    var calls = new List<Guid>();
    for (int i = 0; i < opts.NCalls; i++)
    {
        var cid = ModelBuilder.addCallWithApi(store, workId, flowId, $"N{i}.S", "");
        calls.Add(cid);
    }

    // Random DAG: 각 (i, j) with i < j 에서 prob 으로 edge
    var arrows = new List<ArrowSpec>();
    for (int i = 0; i < calls.Count; i++)
    {
        for (int j = i + 1; j < calls.Count; j++)
        {
            if (rng.NextDouble() < opts.Density)
            {
                bool isGroup = (j - i == 1) && rng.NextDouble() < opts.GroupProb;
                ModelBuilder.addArrowCall(
                    store, workId, calls[i], calls[j],
                    isGroup ? ArrowType.Group : ArrowType.Start);
                arrows.Add(new ArrowSpec(
                    $"N{i}.S", $"N{j}.S",
                    isGroup ? ArrowKind.Group : ArrowKind.Start));
            }
        }
    }
    // DAG 보장 — i < j 이라 cycle 없음
    return new GeneratedModel(store, arrows, "DagWork", opts);
}
```

---

## 7. 시뮬레이션 엔진

### 입력: GeneratedModel
### 출력: List<CapturedEvent>

```csharp
public class SimulationService
{
    public List<CapturedEvent> Simulate(
        GeneratedModel model, int nCycles, long cycleMs, int seed)
    {
        // 1. Topological order of all calls (intra + inter work)
        // 2. 각 call 의 발화 시간 = parents.maxFinish + stageLag + jitter
        // 3. cycle 마다 timing 반복 (cycle * cycleMs offset)
        // 4. Group 페어는 동시 발화 (lag ~0)
        var events = new List<CapturedEvent>();
        for (int c = 0; c < nCycles; c++)
        {
            long t0 = c * cycleMs;
            var fireTime = new Dictionary<Guid, long>();
            foreach (var call in TopoOrder(model.Store))
            {
                long parentMax = ParentFinishes(model.Store, call)
                                  .DefaultIfEmpty(0).Max();
                long jitter = rng.Next(-20, 21);
                long t = parentMax + StageLag + jitter;
                fireTime[call] = t;
                events.Add(new CapturedEvent { T = t0 + t, Name = CallName(call) });
            }
        }
        return events;
    }
}
```

---

## 8. 차트 시각화

### Timeline Chart (LiveCharts2 RowSeries)

- X 축: 시간 (ms)
- Y 축: call 이름 (각 lane 별 row)
- 각 발화 = small marker (point)
- 색상: Start = blue, Group = orange

```csharp
public class TimelineChartViewModel
{
    public ObservableCollection<ISeries> Series { get; set; } =
        new()
        {
            new ScatterSeries<TimelinePoint>
            {
                Values = points,
                GeometrySize = 8,
                Fill = new SolidColorPaint(SKColors.SteelBlue)
            }
        };
}
public record TimelinePoint(double T, int Lane, string Name);
```

### Model Graph (직접 Canvas / DiagramLayout)

- 각 Work = rectangle node
- 각 Call = inner small node
- Arrow = line with arrowhead
- ArrowType 별 색상: Start (blue), Group (orange), Reset (red), StartReset (purple)
- 자동 레이아웃: Sugiyama / layered

WPF 자체 Canvas 또는 외부 라이브러리 (예: `Microsoft.Msagl`).

---

## 9. 역변환 + 리포트

### ReverseService

```csharp
public class ReverseService
{
    public ReverseResult Run(GeneratedModel model, List<CapturedEvent> events)
    {
        // 1. Build candidates from model.GroundTruth + spurious noise
        var candidates = model.GroundTruth
            .Select(a => new ArrowCandidate(a.Src, a.Tgt, a.Kind.ToString()))
            .ToList();
        // (optional) add some spurious 후보 to test robustness

        // 2. Run Ds2.Reverse.Core.ReverseEngine
        var input = ReverseEngine.mkInput(...);
        var (detectedStore, report) = ReverseEngine.run(input);

        // 3. Compare detected arrows vs model.GroundTruth
        var metrics = Compare(model.GroundTruth, detectedStore);
        return new ReverseResult(detectedStore, report, metrics);
    }
}

public record ReverseResult(
    DsStore DetectedStore,
    DetectionReport Report,
    DetectionMetrics Metrics);

public record DetectionMetrics(
    int TruthCount, int DetectedCount, int TP, int FP, int FN,
    double Precision, double Recall, double F1,
    IReadOnlyList<ArrowDiff> FalsePositives,
    IReadOnlyList<ArrowDiff> FalseNegatives);
```

### MetricsBoard

- 큰 텍스트: P/R/F1
- 작은 차트: confusion matrix / by-type breakdown
- 색상 코드: F1 ≥ 0.95 (녹색), 0.85~0.95 (노랑), < 0.85 (빨강)

### Diff Table

| Status | Src | Tgt | Type | Score |
|--------|-----|-----|------|-------|
| ✓ TP   | A   | B   | Start | suff=1.0 necc=1.0 |
| ✗ FP   | X   | B   | Start | suff=0.9 necc=0.7 |
| – FN   | C   | D   | Group | (not detected) |

---

## 10. 사용자 워크플로우

### 기본 흐름
1. **Case 선택** (A or B) — Sidebar 라디오
2. **파라미터 조정** — slider/numeric input
3. **Generate** 버튼 클릭 → Model Graph 즉시 표시
4. **Simulate** 버튼 → Timeline chart 즉시 채워짐
5. **Reverse** 버튼 → Metrics + Diff table 표시
6. **Export** → sdf / json / png 저장

### Auto-run 모드
- "Auto run" 체크 시 Generate → Simulate → Reverse 자동 연쇄
- 슬라이더 변경 시 즉시 reflow (debounce 300ms)

### Batch mode
- "Run 100 random models" 옵션
- 100 시드 × 자동 생성/시뮬/검증 → aggregate F1 분포 차트

---

## 11. 핵심 디자인 결정 (Decisions)

| 결정 | 채택 | 근거 |
|------|------|------|
| Language | **C# WPF + F# 라이브러리** | Promaker 와 동일 스타일. F# Ds2.Reverse.Core 직접 참조 |
| Framework | .NET 9 | Ds2 표준 |
| MVVM | **CommunityToolkit.Mvvm** | Promaker 동일 |
| Chart | **LiveCharts2 (SkiaSharp)** | WPF 호환, scatter/line 풍부 |
| Graph viz | **Canvas + 자체 layout** (Sugiyama) | 외부 의존성 최소 |
| Theme | MahApps.Metro (옵션) | Promaker 와 일관성 |
| File format | .sdf (Promaker 호환) | Ds2.Serialization.JsonConverter |

---

## 12. 구현 단계 (Milestones)

### M1 — 기본 골격 (1일)
- csproj + App.xaml + MainWindow.xaml
- 3-pane 레이아웃 (Generator / Model / Log+Report)
- DummyData 로 UI 검증

### M2 — Generator (1일)
- InlineLineGenerator (Case A)
- StandaloneDagGenerator (Case B)
- GeneratorViewModel + parameter binding

### M3 — Model Viewer (1일)
- Sugiyama layout
- Canvas rendering (works/calls/arrows)
- ArrowType 별 색상

### M4 — Simulator + Timeline (1일)
- SimulationService
- LiveCharts2 scatter timeline
- 색 lane 분리

### M5 — Reverse + Metrics (1일)
- ReverseService 래퍼
- MetricsBoardView
- ArrowDiffView

### M6 — Polish + Batch mode (1일)
- Auto-run mode
- Export (sdf/json/png)
- Batch random 100 시드 차트

**총 약 6일 작업** (단순 estimation).

---

## 13. 검증 기준

| 항목 | 기준 |
|------|------|
| 모델 생성 | Case A/B 모두 .sdf 저장 → Promaker 열림 |
| 시뮬레이션 | 60 cycle, 충분한 events 생성 (≥ 500개) |
| 역변환 | F1 ≥ 0.90 (랜덤 100 모델 평균) |
| UI 응답성 | Generate / Simulate / Reverse 각각 1초 이내 |
| 메모리 | 100 cycle / 30 calls 모델에서 50MB 이하 |

---

## 14. 향후 확장

- 실 데이터 (DEMO / ***REDACTED***EVO) 로드 → 같은 도구로 검증
- LogicGraph 모드 — PLC 래더 로직 시각화
- 알고리즘 파라미터 튜닝 panel (CV / suff / necc 슬라이더)
- Multi-flow 모델 케이스 (Case C)
- 3D 시각화 (Three.js / Helix-toolkit)

---

## 15. 시각 mock-up 예시

### Model Graph (Case A — 5 stage inline)
```
  ┌────┐  StartReset  ┌────┐  StartReset  ┌────┐  StartReset  ┌────┐  StartReset  ┌────┐
  │ W1 │─────────────►│ W2 │─────────────►│ W3 │─────────────►│ W4 │─────────────►│ W5 │
  └────┘              └────┘              └────┘              └────┘              └────┘
   │ADV                │ADV                │ADV                │ADV                │ADV
   ▼Start              ▼                   ▼                   ▼                   ▼
   RET                 RET                 RET                 RET                 RET
```

### Model Graph (Case B — random DAG, 10 calls)
```
        N0 ────► N1 ────► N3 ────► N7
         │        │         │       │
         ▼        ▼         ▼       ▼
        N2 ────► N4 ────► N6 ────► N9
                  │         │
                  ▼         ▼
                 N5 ────► N8
```

### Timeline Chart
```
  Lane (call)
  ┃
S5.RET│        ●            ●            ●            ●
S5.ADV│       ●            ●            ●            ●
S4.RET│      ●           ●           ●           ●
S4.ADV│     ●           ●           ●           ●
...
S1.ADV│  ●        ●        ●        ●
      └─────────────────────────────────────────────► t (ms)
        0      2k     4k     6k     8k    10k
```

### Metrics Board
```
┌───────────────────────────────────────┐
│   Precision     Recall      F1        │
│   ┌─────┐      ┌─────┐    ┌─────┐    │
│   │1.000│      │0.973│    │0.986│    │
│   └─────┘      └─────┘    └─────┘    │
│                                       │
│   TP=37   FP=0   FN=1                 │
│   Tier-1=15   Tier-2=22                │
└───────────────────────────────────────┘
```

---

## 끝

위 설계에 ✓ 받으면 M1 (기본 골격) 부터 단계별 구현 진행.
