# Ds2.Reverse 최종 스펙

> 본 문서는 알고리즘 / API / 시나리오 / 테스트 / Studio 의 최종 동작 정의.
> 변경 히스토리는 [DEVELOPMENT-HISTORY.md](DEVELOPMENT-HISTORY.md) 참조.

---

## 1. 개요

### 1.1 목적
PLC 캡처 로그 (rising-edge events) + arrow 후보 → 인과적으로 검증된 DS2 모델 (`DsStore`) 재구성.

### 1.2 입력
- **Events**: `CapturedEvent list` — `{ T: int64; Name: string }`
- **Candidates**: `ArrowCandidate list` — `{ Src; Tgt; DeclaredKind }`
- **FlowCalls**: `Map<flowName, (callName, address) list>`
- **Config**: `CausationConfig` — gate thresholds + cycle hint
- **Optional**: LogicRungs (PLC 래더 → strength), CrossFlowCandidates, WorkAssignments, AutoTuneThreshold

### 1.3 출력
- **DsStore**: Flows / Works / Calls / ApiDefs / ArrowCalls / ArrowWorks 완성된 모델
- **DetectionReport**: 통계 + dropped detail + emitted confidence + noise level + anomalous cycles

---

## 2. 솔루션 구조

```
Solutions/Reverse/
├── Ds2.Reverse.Core/           (F# 알고리즘 library)
│   ├── Types.fs                — 핵심 타입 (CausationScore, ArrowConfidence, DetectionReport, …)
│   ├── LogicGraph.fs           — 래더 boolean expression + 재귀 expand + AND/OR strength
│   ├── CausationDetection.fs   — score, gate, confidence, mutexScore, bayesianAggregate, …
│   ├── OnlineDetection.fs      — Welford streaming OnlineScore + analyzeDrift
│   ├── AnomalyDetection.fs     — CyclePattern learn + scoreCycle + analyzeAllCycles
│   ├── DagEnforcement.fs       — topoBreakCycle + transitiveReduction
│   ├── ModelBuilder.fs         — DsStore mutation helpers
│   └── ReverseEngine.fs        — 메인 파이프라인 (run: Input → DsStore × DetectionReport)
│
├── Ds2.Reverse.Bench/          (F# 벤치마크 + 합성 시나리오)
│   ├── VLine.fs                — VLINE 기본 시나리오
│   ├── Simulator.fs            — Cycle 시뮬레이션 (CyclePattern + simulate + simulateCycleAware)
│   ├── Scenario.fs             — Scenario 타입 + Primitives (chain, fanOut, fanIn, groupPair, confounded)
│   ├── Evaluation.fs           — Precision / Recall / F1 계산
│   ├── Models.fs               — 기본 m0-100 (101 시나리오)
│   ├── MoreModels.fs           — D1-D5 시나리오
│   ├── LogicModels.fs          — Logic strength 검증 시나리오
│   ├── CapacityModels.fs       — Capacity 가변 시나리오
│   ├── AdvancedModels.fs       — Pair-wise + Drift advanced
│   ├── HybridModels.fs         — Logic+capture hybrid
│   ├── ClusterModels.fs        — Multi-source cluster
│   ├── StressModels.fs         — Stress edge cases
│   ├── Phase1Models.fs         — R (Reset) + Q (Queue) + D (Drift)
│   ├── Phase2Models.fs         — P (Polling) + V (Variable)
│   ├── Phase3Models.fs         — G (Graph) + Z (Adversarial)
│   ├── Phase4Models.fs         — K (Kombinatorial) + S (Stress)
│   ├── Phase5Models.fs         — O (Overlap) + T (Temporal)
│   └── BenchRunner.fs          — runOne, runAll, formatSummary
│
├── Ds2.Reverse.Tests/          (xunit 110 tests)
│   ├── 기본:                     CausationTests, DagTests, EndToEndTests, BenchTests
│   ├── Phase 별:                 Phase{1-5}Tests + AllPhaseSweepTests
│   ├── 알고리즘 단위:             OnlineTests, AnomalyTests, AdvancedConfigTests
│   ├── 회귀 + 통합:              FullPipelineTests, MultiSeedTests, PerformanceTests
│   ├── Property:                 PropertyTests (deterministic + scale + monotone)
│   └── 출력 생성:                ScenarioReportTests (HTML + CSV)
│
└── Apps/Ds2.Reverse.Studio/    (WPF GUI)
    ├── Models/
    │   ├── Records.cs          — ModelCase enum + GeneratorOptions + ArrowDiff + …
    │   └── Generators.cs       — 8 case Generator (InlineLine, StandaloneDag, MultiFlow, Branch, RecycleLoop, PlcCell, CapacityVar, AdversarialMix)
    ├── Services/
    │   ├── SimulationService.cs — events 생성 + work-level arrow expand
    │   └── ReverseService.cs    — Ds2.Reverse.Core 호출 + Confidence 추출
    ├── ViewModels/
    │   └── MainViewModel.cs    — 모든 commands (Generate / Simulate / Reverse / SeedSweep)
    └── MainWindow.xaml         — 8 case 라디오 + 파라미터 + Confidence DataGrid (tier 색깔)
```

---

## 3. 알고리즘 — 인과 검출 파이프라인

```
입력 events + cfg + candidates
     │
     ├── (autoTune?) estimateNoiseLevel → withNoiseLevel cfg
     │
     ├── 각 (src, tgt) 페어:
     │   ├── score(cfg, aTimes, bTimes) → CausationScore
     │   │   ├── effective_win = min(WindowMs, CycleHintMs * 0.7)
     │   │   ├── sufficiency: P[A 직후 [-parallelLag, win] 안 B]
     │   │   ├── necessity:   P[B 직전 [-parallelLag, win] 안 A]
     │   │   ├── Outlier filter (Tukey IQR)
     │   │   ├── lag mean / std / cv 계산
     │   │   ├── modeCount (75ms bin histogram)
     │   │   └── stable = lagCv ≤ cfg.LagCvMax
     │   │             OR smallLagFallback (mean<150 AND std적절)
     │   │             OR bimodalStable (gap≥100, std<60, 25%/75% balanced)
     │   │             OR driftStable (linear regression, residual<15% mean)
     │   │             OR cyclicStable (autocorrelation, cos/sin fit)
     │   │             OR kmeansStable (k=3/4, std<60, min size ≥12%)
     │   │       (multi-modal modeCount>2 일 때 kmeansStable 만 통과 가능)
     │   │
     │   ├── gate(declaredKind, score) → GatingDecision:
     │   │   • "group"    → passes_grp 검사 → EmitGroup
     │   │   • "reset"    → cycleHint 무효화 후 재계산 → EmitSequential(2, …)
     │   │   • "mutex"    → mutexScore (co-occurrence < 10%) → EmitSequential(4, …)
     │   │   • "trigger_reset" → EmitSequential(3, …)
     │   │   • "trigger"  → passes_seq 검사 → EmitSequential(1, …)
     │   │
     │   ├── (Dropped 인 경우) Mutex / Cluster fallback 시도
     │   │
     │   └── confidence(score, logicStrength?) → ArrowConfidence:
     │       Score = primary(0.7) + logic(0.3) — logic 없으면 capture-only
     │       nReliability = NA<10 → 0.5 / NA<30 → 0.8 / else 1.0
     │       Tier = High(≥0.9) / Medium(≥0.7) / Low(≥0.5) / Reject
     │
     ├── DAG enforcement (Start + StartReset edges 만):
     │   topoBreakCycle (Kahn, weakness = suff + necc - cv)
     │   transitiveReduction (A→C 가 A→B→C 경로 있으면 제거)
     │
     ├── Reset / ResetReset edges 직접 emit (DAG check 면제 — 자연 cycle 허용)
     │
     ├── Group edges emit
     │
     ├── Cross-flow → ArrowWorks emit (work name 매칭)
     │
     └── (활성 시) Anomaly Detection: learn 20 cycles → flag deviating cycles
```

---

## 4. 핵심 타입 (Ds2.Reverse.Core)

### 4.1 CausationScore
```fsharp
type CausationScore = {
    NA: int
    NB: int
    Sufficiency: float          // 0~1
    Necessity: float            // 0~1
    LagMean: float              // ms
    LagStd: float
    LagCv: float
    AbsLagMean: float
    IsParallel: bool
    PassesSeq: bool
    PassesGrp: bool
    Reason: string option       // 실패 사유
}
```

### 4.2 ConfidenceTier + ArrowConfidence
```fsharp
type ConfidenceTier = High | Medium | Low | Reject

type ArrowConfidence = {
    Score: float                // 0~1
    Tier: ConfidenceTier
    Evidence: string list       // ["passes_seq"; "high_suff"; "low_cv"; "n=60"]
    NReliability: float         // 0.5 ~ 1.0
}
```

### 4.3 GatingDecision
```fsharp
type GatingDecision =
    | EmitSequential of arrowTypeCode: int * score: CausationScore
        // code: 1=Start, 2=Reset, 3=StartReset, 4=ResetReset
    | EmitGroup of CausationScore
    | Dropped of reason: string * score: CausationScore
```

### 4.4 CausationConfig
```fsharp
type CausationConfig = {
    WindowMs: int64                  // default 3000
    SufficiencyMin: float            // default 0.85
    NecessityMin: float              // default 0.85
    LagCvMax: float                  // default 0.30
    LagStdAbsMs: float               // default 150
    MinFires: int                    // default 5
    ParallelLagMs: float             // default 50
    CycleHintMs: int64 option        // None = WindowMs
}

module CausationConfig =
    let defaults: CausationConfig
    let withCycleHint (cycleMs: int64) (cfg: CausationConfig)
    let withNoiseLevel (noise: float) (cfg: CausationConfig)   // B5.2
```

### 4.5 DetectionReport
```fsharp
type DetectionReport = {
    mutable TotalCandidates: int
    mutable PassedSeq: int
    mutable PassedGrp: int
    mutable DroppedCausation: int
    mutable RemovedCycle: int
    mutable RemovedTransitive: int
    mutable RemovedGroupDup: int
    mutable FinalArrowCount: int
    mutable NoiseLevel: float                                          // B4.1
    DroppedDetail: ResizeArray<string * string * CausationScore * string>
    CycleWarn: ResizeArray<string * string>
    TransitiveLog: ResizeArray<string * string>
    GroupEmitted: ResizeArray<string * string>
    EmittedConfidence: ResizeArray<string * string * ArrowConfidence>  // B2.1
    AnomalousCycles: ResizeArray<int * float>                          // B7.1
}
```

---

## 5. 주요 API

### 5.1 CausationDetection
```fsharp
module CausationDetection =
    val score: CausationConfig -> int64 seq -> int64 seq -> CausationScore
    val gate: declaredKind: string -> CausationScore -> GatingDecision
    val mutexScore: CausationConfig -> int64 seq -> int64 seq -> bool * float * int * int
    val confidence: CausationScore -> logicStrength: float option -> ArrowConfidence
    val bayesianAggregate: float list -> float          // B4.3
    val estimateNoiseLevel: CapturedEvent list -> int64 -> float    // B4.1
    val clusterScore: CausationConfig -> (string * int64 seq) list -> int64 seq -> Map<string, ClusterScore>
```

### 5.2 OnlineDetection (B6)
```fsharp
type OnlineScore() =
    member SetWindow: int64 -> unit
    member SetParallelLag: int64 -> unit
    member AddA: int64 -> unit
    member AddB: int64 -> unit
    member Snapshot: unit -> CausationScore
    member SnapshotConfidence: unit -> ArrowConfidence

type DriftAlert = Stable | Dropping of float * float | Picking of float * float

val analyzeDrift: ArrowConfidence list -> DriftAlert   // B7.2
```

### 5.3 AnomalyDetection (B7.1)
```fsharp
type CyclePattern = {
    NCyclesLearned: int
    Offsets: Map<string, float * float>      // name → (meanOffset, stdOffset)
    EventsPerCycle: float
}

val learn: (int64 * string) list -> int64 -> int -> CyclePattern
val scoreCycle: CyclePattern -> (int64 * string) seq -> int64 -> float
val analyzeAllCycles:
    CyclePattern -> (int64 * string) list -> int64 -> float -> (int * float) list * int list
```

### 5.4 ReverseEngine
```fsharp
type Input = {
    ProjectName: string
    ActiveSystemName: string
    FlowCalls: Map<string, (string * string) list>
    Candidates: ArrowCandidate list
    Events: CapturedEvent list
    Config: CausationConfig
    LogicRungs: LogicRung list option
    LogicMaxDepth: int
    LogicStrengthThreshold: float
    CrossFlowCandidates: ArrowCandidate list
    WorkAssignments: Map<string, string * string>
    AutoTuneThreshold: bool                                  // B5.2
}

val mkInput: projectName -> activeSystemName -> flowCalls -> candidates -> events -> cfg -> Input
val run: Input -> DsStore * DetectionReport
```

---

## 6. 시나리오 차원 (Ds2.Reverse.Bench)

### 6.1 기본 (Models.fs) — m0-100, 101 시나리오
| 카테고리 | 코드 | 개수 |
|---------|------|------|
| chain | m0-9 | 10 |
| fanOut | m10-19 | 10 |
| fanIn | m20-29 | 10 |
| groupMix | m30-39 | 10 |
| confounded | m40-49 | 10 |
| spurious | m50-59 | 10 |
| longChain | m60-69 | 10 |
| multiBackEdge | m70-79 | 10 |
| edge | m80-89 | 10 |
| composite | m90-100 | 11 |

### 6.2 Phase 별 신규 시나리오 (42 시나리오)
| 차원 | 코드 | 개수 | 패턴 |
|------|------|------|------|
| **R Reset** | r0-r4 | 5 | self-reset / mutex / startReset |
| **Q Queue** | q0-q4 | 5 | short / long / variable / rejection / deep bottleneck |
| **D Drift** | d0-d2 | 3 | linear / cyclic cosine / strong cyclic |
| **P Polling** | p0-p2 | 3 | polling only / + causation / heartbeat noise |
| **V Variable** | v0-v2 | 3 | uniform / bimodal / warming drift |
| **G Graph** | g0-g3 | 4 | Star / Tree / Bipartite / Diamond |
| **Z Adversarial** | z0-z4 | 5 | noise / spurious / outlier / double-fire |
| **K Kombinatorial** | k0-k3 | 4 | chain+group / diamond+cross / drift+noise / reset cycle |
| **S Stress** | s0-s3 | 4 | tight 80ms / long 1800ms / large jitter / **k-means 3-modal** |
| **O Overlap** | o0-o2 | 3 | parallel chains / independent / nested |
| **T Temporal** | t0-t2 | 3 | regime change / gradual shift / variable cycle |

### 6.3 Scenario 타입
```fsharp
type Scenario = {
    Name: string
    Flow: string
    GroundTruth: VLine.GroundTruthArrow list
    Spurious: VLine.GroundTruthArrow list
    AllCalls: string list
    Pattern: Random -> Simulator.CyclePattern
    PatternCycleAware: (int -> Random -> Simulator.CyclePattern) option   // drift state-safe
    CycleMs: int64
}
```

---

## 7. WPF Studio — Case 명세

| Case | 클래스 | 패턴 | 주요 파라미터 |
|------|--------|------|---------------|
| **A** | `InlineLineGenerator` | Inline chain W1 → ... → Wn (StartReset) | NStages, Capacity |
| **B** | `StandaloneDagGenerator` | 한 Work 안 random DAG | NCalls, Density, GroupProb |
| **C** | `MultiFlowGenerator` | 2~5 flows + cross-flow chain | NFlows, StagesPerFlow |
| **D** | `BranchGenerator` | W1 → N branches | NBranches, BranchEntropy |
| **E** | `RecycleLoopGenerator` | Inline + 마지막→첫 재진입 | RecycleStages, RecycleProbability |
| **F** | `PlcCellGenerator` | Robot+Conveyor+Jig 표준 sequence | PlcUseRobot, PlcUseConveyor, PlcUseJig |
| **G** | `CapacityVarGenerator` | 4-stage + token 수 변동 | CapMinTokens, CapMaxTokens |
| **H** | `AdversarialMixGenerator` | A→B→C chain + N spurious | AdvSpuriousCount, AdvNoiseLevel |

### Studio 공통 기능
- **Generate**: 선택된 case 의 random 모델 생성 (RandomSeed checkbox 면 매번 새 seed)
- **Simulate**: cycle-by-cycle async plotting (SimStepDelayMs 로 속도 조절)
- **Reverse**: F# 알고리즘 호출 → DetectionMetrics (P/R/F1, TP/FP/FN, Diffs)
- **Seed Sweep**: 10 seed 로 같은 case 반복 → F1 분포 출력
- **Auto-tune Threshold**: events 의 noise level 추정 → cfg 자동 조정
- **UI**: Confidence column + Tier 색깔 (High=초록 / Medium=노랑 / Low=주황)
- **Anomaly summary**: top 5 anomalous cycles 표시

---

## 8. 테스트 카테고리 (110 tests)

| 카테고리 | 파일 | tests | 검증 |
|---------|------|-------|------|
| 기본 검증 | CausationTests, DagTests | ~5 | score 함수 / DAG enforcement |
| 합성 모델 | BenchTests | 5 | m0-100 perfect rate 5 seed 반복 |
| End-to-End | EndToEndTests | 2 | VLINE 정확 검출 + serialize 회귀 |
| 실 데이터 | RealDataTests, EvoDataTests | 3 | DEMO / EVO (auto-tune 포함) |
| 강화 모델 | MoreModelsTests, LogicGraphTests, CapacityTests, AdvancedTests, HybridTests, ClusterTests, StressTests | ~10 | 각 차원 aggregate F1 |
| **Phase 1-5** | Phase{1-5}Tests | 14 | 각 phase F1 threshold |
| **All Phase** | AllPhaseSweepTests | 4 | 통합 F1 / distribution / cumulative |
| **Property** | PropertyTests | 11 | deterministic + scale + monotone + 10 seed |
| **Online** | OnlineTests | 7 | streaming + anytime + drift alert |
| **Anomaly** | AnomalyTests | 6 | pattern learn + missing/extra/shifted |
| **Multi-seed** | MultiSeedTests | 3 | 5 seed × 42 시나리오 + cycle count stability |
| **Performance** | PerformanceTests | 6 | chain N=10-100 + sweep < 5s |
| **Pipeline** | FullPipelineTests | 2 | 모든 컴포넌트 통합 (auto-tune+score+conf+anomaly) |
| **Advanced Config** | AdvancedConfigTests | 10 | noise estimation + dynamic threshold + bayesian |
| 출력 생성 | ScenarioReportTests | 2 | HTML + CSV 생성 |
| 디버그 | DebugBenchTests | 1 | failing 시나리오 상세 출력 |

---

## 9. 검증 통과 기준

| 기준 | 값 | 현재 상태 |
|------|------|----------|
| 기존 183 시나리오 회귀 | F1 = 1.000 | ✓ 101/101 perfect |
| 신규 42 시나리오 | F1 ≥ 0.90 | ✓ 0.92 (40/42 perfect) |
| 실 데이터 DEMO | arrowCalls ≥ 9 | ✓ |
| 실 데이터 EVO | arrowCalls ≥ 70 | ✓ 96 (default), 226 (autoTune) |
| Performance | 50 cycles × 100 calls < 1s | ✓ chain N=100 < 600ms |
| Multi-seed std | < 0.10 | ✓ < 0.02 |
| 빌드 | 0 warning / 0 error | ✓ |

---

## 10. 본질적 한계 (Out-of-Scope)

| 한계 | 이유 |
|------|------|
| Multi-modal 3+ 인과 (uniform spread) | bin 별 인구가 평탄해서 cluster 구조 없음. k-means 도 std 보장 안 됨. |
| Polling vs causation 분리 | POLL 이 항상 ACT 직전 발화 → 통계적으로 분리 불가 (logic hint 필요) |
| Hidden confounding | 외부 timer 등 측정 불가 변수 |
| Real-time weight 자가 조정 | 강화학습 영역 (B6 online 은 read-only) |
| Independent multi-source 의 spurious 자동 제거 (o1 case) | competing-hypothesis 필터가 fan-in 정상 패턴을 false-drop 위험 |

---

## 11. 빠른 시작

### 알고리즘 호출
```fsharp
open Ds2.Reverse.Core

let events = [ { T = 0L; Name = "A" }; { T = 300L; Name = "B" }; ... ]
let candidates = [ { Src = "A"; Tgt = "B"; DeclaredKind = "trigger" } ]
let flowCalls = Map.ofList [ "Main", [ "A", ""; "B", "" ] ]
let cfg = CausationConfig.defaults |> CausationConfig.withCycleHint 2000L

let input = ReverseEngine.mkInput "MyProject" "Main" flowCalls candidates events cfg
let store, report = ReverseEngine.run input

printfn "Detected %d arrows, %d dropped"
    report.FinalArrowCount report.DroppedCausation
```

### 시나리오 벤치 실행
```fsharp
open Ds2.Reverse.Bench

let summary, _ =
    BenchRunner.runAll Phase1Models.all CausationConfig.defaults 42 60
printfn "%s" (BenchRunner.formatSummary summary)
```

### Studio 실행
1. Visual Studio 또는 `dotnet run --project Apps/Ds2.Reverse.Studio`
2. Case 선택 (A-H) → Generate → Simulate → Reverse
3. (선택) Seed Sweep / Auto-tune 활성화

### 시나리오 보고서 생성
```bash
dotnet test --filter "FullyQualifiedName~ScenarioReport"
# 결과: ScenarioReport.html + ScenarioReport.csv
```

---

## 12-B. 무한 반복 테스팅 강화 (2026-05-24 #2)

### 신규 인프라 (Bench)
- `RandomScenarioGen.fs` — Topology × Lag 무작위 사양 (TopologyKind/LagKind DU)
- `InfiniteTestRunner.fs` — `runBounded` / `runUntilStop` / `formatStats`
- `FailureRecorder.fs` — TSV append/load, fail seed 저장

### 신규 테스트 (6 파일, 30+ tests)
- `Fuzz/InfiniteFuzzTests.fs` — bounded fuzz, crash 검증
- `Fuzz/RandomChainTests.fs` — chain N=2~20 + 100 random
- `Fuzz/RandomTopologyTests.fs` — Star/Tree/DAG/Bipartite
- `Fuzz/RandomLagTests.fs` — 5 lag kinds + 50 random
- `Fuzz/FuzzPipelineTests.fs` — full pipeline 100 random
- `Fuzz/RegressionSeedTests.fs` — 알려진 good seeds

### 메트릭 (이번 버전)
- xunit: 261 → **292 tests** (+31)
- 각 fuzz test 가 100~수백 random scenarios 실행 → **누적 5,000+ scenarios per run**
- 실행 시간: 38초 (fuzz 30초 stress 포함)
- Crash 0건 — 모든 random input 안정

---

## 12-A. 재구현 / 테스트 확장 (2026-05-24)

### 백업
- `Solutions/Reverse.bak.20260524/` (716K, source-only)

### 신규 테스트 파일 (13개)
- `Unit/` — 6개 모듈별 단위 테스트 (CausationDetection / Online / Anomaly / LogicGraph / Dag / ModelBuilder)
- `BoundaryTests.fs` — threshold 경계값 검증
- `EdgeCaseTests.fs` — empty / single / extreme 처리
- `NegativeTests.fs` — spurious 정확 거부
- `PropertyPlusTests.fs` — Idempotence / Translation / Scaling / Symmetry
- `CalibrationTests.fs` — tier 통계 정확도
- `StressTests2.fs` — 200 시나리오 / 1000 cycle / parallel / memory
- `RegressionBaselineTests.fs` — 변경 감지 기준선

### 메트릭 (이번 버전)
- xunit: 110 → **261 tests** (+151, 137% 증가)
- 빌드: 경고 0, 오류 0
- 테스트 실행: ~6초

---

## 12. 변경 사항 (2026-05-23)

### 알고리즘
- B1.1 / B1.2 / B2.1 / B2.2 / B3.1 / B3.2 / B4.1 / B4.2 / B4.3 / B5 / B5.2 / B6 / B7.1 / B7.2 — 14 모듈 모두 구현 완료
- ReverseEngine 에 autoTune + anomaly 통합

### 시나리오
- 42 신규 phase 시나리오 (R/Q/D/P/V/G/Z/K/S/O/T)
- modeCount Int32.MinValue 오버플로우 버그 수정 (multi-modal 정확화)
- Pattern stateful 누적 버그 수정 (PatternCycleAware 도입)

### 테스트
- 42 → 110 tests (+68)
- Property + Online + Anomaly + Multi-seed + Performance + Pipeline + AdvancedConfig 카테고리 신규

### Studio
- 2 → 8 cases (C/D/E/F/G/H 추가)
- Confidence Tier UI + Anomaly summary + Seed Sweep + Auto-tune

### 실 데이터
- EVO ***REDACTED*** 70 → 96 arrowCalls (default), 226 (autoTune)

### 코드 품질
- 빌드 경고 17 → 0 (unused vars 정리)
- 빌드 오류 0 유지
