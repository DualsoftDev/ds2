# Ds2.Reverse 개발 히스토리

> PLC 캡처 로그 + arrow 후보 → DS2 모델 (DsStore) 역공학.
> 본 문서는 알고리즘 / 시나리오 / Studio 의 단계별 진화 기록.

---

## 1. 초기 (v0~v10) — 합성 모델 + DEMO 데이터

### v1~v3: 기본 인과 검출
- VLINE 합성 모델 (단순 chain) 에서 P/R/F1 = 1.000 달성.
- Sufficiency / Necessity gate 도입.

### v4~v9: gate 강화
- **Lag CV gate**: lag stability 검사 (cv ≤ 0.30).
- **Std-abs fallback**: 작은 lag 의 jitter 흡수 (std ≤ 150ms).
- **Bimodal stable**: lag 가 두 peak 분포 (queue / bottleneck 인정).
- **Linear drift**: linear regression 으로 단조 lag 변동 인정.
- **Group detection**: parallel lag 페어 (lag ≈ 0) → ArrowType.Group.

### v10: 완성 + DEMO 검증
- DEMO 데이터 (`D:\dstest\demoKit`, 14 calls, 9 arrowCalls) 에서 알고리즘 검증.
- 합성 시나리오 m0~m100 (101 개) 모두 perfect F1=1.0 달성.

### v11~v15: LS PLC + 다차원 검증
- LS PLC (`D:\dstest\onlyLogic\plc`) — logic + 심볼 만으로 sdf 생성.
- F# Solution 으로 정식 분리 (`C:\ds\ds2\Solutions\Reverse`).
- 1차원 변동 외에 다차원 (`D`, `S`, `C`, `N/F/L`, `M/I/X`, `H`, `K`) 시나리오 합성.

### v16~v17: Logic Graph + Cluster
- **LogicGraph**: 재귀 expand + AND/OR 강도 분석 → arrow strength 0~1.
- **Cluster causation**: 한 sink 에 multiple sources 시, 각 source 의 cluster 별 평가.
- *****REDACTED***EVO** 실 데이터 (`D:\dstest\kwangmyeongEVO`, 376 calls, 70 arrowCalls) 적용.
- WPF Studio 초기 (Case A: Inline / Case B: Standalone DAG).

---

## 2. 강화 로드맵 Phase 1 — Foundations

### 알고리즘
| 항목 | 코드 | 효과 |
|------|------|------|
| **B1.1 Cyclic Drift** | `cyclicStable` (autocorrelation + cos/sin fit) | lag 가 sin/cos 패턴이면 인정. d1, d2 시나리오 통과 |
| **B3.1 Reset 검출** | `gate "reset"` → CycleHintMs 무효화 | 크로스-사이클 reset (B→A reset) 검출 가능 |
| **B3.2 Mutex 검출** | `mutexScore` (co-occurrence rate < 10%) | A/B 가 상호 배타적이면 ResetReset emit |
| **Multi-modal 거부** | `modeCount` (75ms bin histogram) | 5-modal (q4 deep bottleneck) drop |
| **DAG cycle 면제** | `isDagEdgeKind` 필터 | Reset/ResetReset 가 자연 cycle 형성하도록 |

### 시나리오 (13 신규)
| 차원 | 코드 | 개수 | 설명 |
|------|------|------|------|
| **R Reset** | r0-r4 | 5 | self-reset / mutex / startReset / spurious reset |
| **Q Queue** | q0-q4 | 5 | short / long / variable / rejection / deep bottleneck |
| **D Drift** | d0-d2 | 3 | linear / cyclic cosine / strong cyclic |

### Studio
- **Case C** (Multi-Flow Inline): 2~5 flows + cross-flow chain.
- **Case D** (Branch / Choice): 공통 W1 → N branches.
- SimulationService 가 work-level arrows 도 entry/exit call 로 확장 (cross-work chain).

### 회귀 결과
- Phase 1 R/Q 10/10 perfect, F1=1.0
- 알고리즘 전체 일관성 유지 (m0-100 101/101 perfect)

---

## 3. 강화 로드맵 Phase 2 — Realistic + Confidence

### 알고리즘
| 항목 | 코드 | 효과 |
|------|------|------|
| **B2.1 ArrowConfidence** | `confidence` (primary + nReliability + logic) | 0~1 연속 신뢰도 점수 |
| **B2.2 Soft Classification** | `ConfidenceTier`: High/Medium/Low/Reject | tier 별 처리 (review flag 등) |
| **B5 Logic+Stat Hybrid** | `confidence sco (Some logicStrength)` | logit 결합 — 신뢰도 향상 |

### 시나리오 (6 신규)
| 차원 | 코드 | 개수 | 설명 |
|------|------|------|------|
| **P Polling** | p0-p2 | 3 | polling only / polling+causation / heartbeat noise |
| **V Variable** | v0-v2 | 3 | uniform range / bimodal / warming drift |

### Studio
- **Case E** (Recycle Loop): 마지막 work → 첫 work 재진입.
- **Case F** (PLC-Realistic Cell): Robot + Conveyor + Jig 5 stage standard.
- **UI**: Confidence column + Tier 색깔 (High=초록 / Medium=노랑 / Low=주황 row).
- `DetectionReport.EmittedConfidence` 모든 emit arrow 의 신뢰도 누적.

### 회귀 결과
- Phase 2 P/V 4/6 perfect, F1=0.89 (polling 의 인과 모방 spurious 인정 — 알고리즘 한계).
- EVO 70 arrowCalls 유지.

---

## 4. 강화 로드맵 Phase 3 — Adversarial Hardened

### 알고리즘
| 항목 | 코드 | 효과 |
|------|------|------|
| **B4.1 Background Noise Estimation** | `estimateNoiseLevel` (cycle offset std/200ms) | 0~1 noise level 추정 |
| **B4.2 Outlier Filtering** | Tukey IQR (Q1-1.5×IQR ~ Q3+1.5×IQR) | lag outlier 제거 (50% 보호) |
| **B4.3 Bayesian Aggregation** | `bayesianAggregate` (logit fusion) | multiple evidence 결합 |

### 시나리오 (9 신규)
| 차원 | 코드 | 개수 | 설명 |
|------|------|------|------|
| **G Graph** | g0-g3 | 4 | Star / Tree / Bipartite / Diamond |
| **Z Adversarial** | z0-z4 | 5 | noise / spurious / outlier / double-fire |

### Studio
- **Case G** (Capacity Variable): inline + token 수 변동.
- **Case H** (Adversarial Mix): A→B→C chain + N spurious noise calls.

### 회귀 결과
- Phase 3 G/Z 9/9 perfect, F1=1.0.
- 알고리즘 robust against noise.

---

## 5. 추가 강화 (Phase 4+)

### Phase 4 — Kombinatorial + Stress

#### 시나리오 (8 신규)
| 차원 | 코드 | 개수 | 설명 |
|------|------|------|------|
| **K Kombinatorial** | k0-k3 | 4 | chain+group / diamond+cross / drift+noise / reset cycle |
| **S Stress edge** | s0-s3 | 4 | 80ms tight / 1800ms long / 80ms jitter / **k-means 3-modal** |

#### B1.2 K-means Multi-modal 수용
- `kmeansStable` (1-D k-means, k=3/4, cluster std < 60ms, min size ≥ 12%)
- well-separated 3-cluster lag 분포 인정 → s3 통과.
- 우연히 발견된 **modeCount Int32.MinValue 오버플로우 버그** 수정.

### Phase 5 — Overlap + Temporal

#### 시나리오 (6 신규)
| 차원 | 코드 | 개수 | 설명 |
|------|------|------|------|
| **O Overlap** | o0-o2 | 3 | parallel chains / independent / nested |
| **T Temporal shift** | t0-t2 | 3 | regime change / gradual shift / variable cycle |

#### Pattern API 진화 — Cycle-aware
- `PatternCycleAware: (int -> Random -> CyclePattern) option`
- `Simulator.simulateCycleAware` — cycle index 를 patternBuilder 에 전달.
- **Mutable state leakage 버그 수정**: 이전 drift 시나리오의 closure 가 multi-seed 실행 시 state 누적되던 문제 해결.

---

## 6. 알고리즘 확장 (B6 / B7)

### B6 Online / Incremental Detection
`Ds2.Reverse.Core/OnlineDetection.fs`
- **OnlineScore** class: Welford's online algorithm.
- `AddA`, `AddB` 메서드로 streaming 처리 (event 도착 순서대로).
- `Snapshot()` — 언제든지 현재 CausationScore 산출.
- `SnapshotConfidence()` — 현재 ArrowConfidence.

### B7.1 Anomaly Pattern Learning
`Ds2.Reverse.Core/AnomalyDetection.fs`
- **CyclePattern**: 첫 N cycle 의 정상 패턴 학습 (call name → mean/std offset).
- **scoreCycle**: 새 cycle 의 deviation 측정 (offset z-score + missing/extra penalty).
- **analyzeAllCycles**: 임계값 초과 cycle 인덱스 반환.

### B7.2 Causation Drift Alert
- `analyzeDrift (history: ArrowConfidence list)`: confidence slope 분석.
- 결과: `Stable | Dropping(slope, recent) | Picking(slope, recent)`.
- 라인 상태 변화 알림 (confidence 떨어지면 detection 자체가 약해진 신호).

### B5.2 Dynamic Threshold (자동 조정)
- `CausationConfig.withNoiseLevel`: noise 0~1 에 따라 cfg 완화/강화.
  - clean → suff/necc 0.90, cv ≤ 0.30
  - noisy → suff/necc 0.80, cv ≤ 0.45
- `ReverseEngine.Input.AutoTuneThreshold = true` 시 events 분석 후 cfg 자동 적용.

---

## 7. 테스트 / 검증 인프라

### Property-based Tests (`PropertyTests.fs`)
- **Determinism**: 같은 seed → 같은 결과 (4 seed × 5-노드 chain).
- **Scale**: N=10/25/50 chain (5s timeout, F1 ≥ 0.95).
- **Monotonicity**: N cycle 더 추가해도 F1 안 떨어짐.
- **Confidence monotone**: NA 증가 → confidence 증가.
- **Robust**: 10 random seed 에서 10/10 perfect.

### Multi-seed Tests (`MultiSeedTests.fs`)
- 42 시나리오 × 5 seed (1/42/314/12345/999999):
  - per-seed avgF1 ≥ 0.92
  - std across seeds < 0.10 (안정성).
- 28 시나리오 × cycle counts (20/40/60/100) std < 0.10.

### Performance Tests (`PerformanceTests.fs`)
- Chain N=10/25/50/100, 30 cycle < 2s.
- 28-scenario sweep < 5s.

### Full Pipeline Tests (`FullPipelineTests.fs`)
- auto-tune + score + confidence + anomaly + Bayesian end-to-end.
- online + drift 검증.

### Anomaly Tests (`AnomalyTests.fs`)
- Pattern learn (empty / clean / jittery).
- Missing / extra / shifted event detection.
- analyzeAllCycles flag 검증.

### EVO autoTune Comparison
- Default: 96 arrowCalls (이전 70 → 신규 algorithm features 효과).
- AutoTune ON: 226 arrowCalls (noise=1.0 추정 → cfg 완화).
- 비율 검증: ON ≥ OFF (noisy 환경에서 autoTune 이 더 관대).

### Scenario Report Generators
- **ScenarioReport.html**: 42 시나리오 표 (F1 색깔: perfect=초록 / good=노랑 / poor=빨강).
- **ScenarioReport.csv**: 스프레드시트 분석용.

---

## 8. 버그 / 이슈 수정 히스토리

| 발견 시점 | 버그 | 원인 | 수정 |
|----------|------|------|------|
| Phase 1 | r0 cross-cycle Reset 미검출 | effective_window = cycle*0.7 차단 | "reset" kind 면 CycleHintMs 무효화 |
| Phase 1 | r0 A→B Start 가 cycle break 로 제거됨 | DAG cycle 검사가 Start+Reset 모두 검사 | `isDagEdgeKind` 필터 — Reset/ResetReset 면제 |
| Phase 1 | q0 bimodal 미인정 | gap 150ms threshold 너무 strict | 100ms 로 완화 + std<60ms |
| Phase 1 | q4 multi-modal 통과 (FP) | smallLagFallback 너무 loose | `lagStd < 50.0 OR lagStd < lagMean * 0.8` 로 엄격화 |
| Phase 1 | q4 modeCount=1 (잘못) | 75ms bin 안에 클러스터 다 들어감 | bin width + consecutive bin 합치기 |
| Phase 4 | s3 k-means 미통과 | `Int32.MinValue` 오버플로우로 modeCount=2 (≠ 3) | `prevOpt: int option` 으로 sentinel 교체 |
| Phase 5 | Drift 시나리오 multi-seed 시 결과 변동 | `let mutable k = 0` closure state 누적 | `PatternCycleAware: (int -> Random -> CyclePattern)` 도입 + `Simulator.simulateCycleAware` |
| 정리 | 17 warnings (unused vars) | 미사용 let bindings | `_` prefix 또는 binding 제거 |

---

## 9. 실 데이터 검증 진화

### DEMO Kit (`D:\dstest\demoKit`)
- v10: 14 calls, 9 arrowCalls 정확 검출.
- 알고리즘 유지 회귀 — 모든 phase 강화 후에도 정상.

### ***REDACTED***EVO (`D:\dstest\kwangmyeongEVO`)
- v17: 376 calls, **70** arrowCalls + 20 arrowWorks.
- Phase 1-5 강화 후 (default): **96** arrowCalls + 20 arrowWorks (37% 증가).
- AutoTune ON: **226** arrowCalls (noise=1.0 추정에 따른 cfg 완화).

---

## 10. WPF Studio 진화

### v17 (Phase 0)
- Case A (Inline Line StartReset chain)
- Case B (Standalone Work + DAG)
- Generate / Simulate / Reverse 3 단계
- Async cycle-by-cycle plotting

### Phase 1
- Case C (Multi-Flow Inline 2~5 flows + cross-flow)
- Case D (Branch / Choice)
- SimulationService 가 work-level arrows expand (entry/exit call chain)

### Phase 2
- Case E (Recycle Loop)
- Case F (PLC-Realistic Cell — Robot/Conveyor/Jig)
- Confidence 컬럼 + Tier 색깔 (RowStyle DataTrigger)
- Anomaly summary text 표시

### Phase 3
- Case G (Capacity Variable)
- Case H (Adversarial Mix)
- Seed Sweep (10 seed × current case, F1 분포 출력)
- Auto-tune threshold 체크박스

### 최종 상태
8 Case (A~H), 시각화 + 메트릭 + anomaly 표시 + autoTune.

---

## 11. 문서 / 산출물

### 문서
- [STRENGTHENING-ROADMAP.md](STRENGTHENING-ROADMAP.md) — 강화 계획 (체크리스트)
- [DEVELOPMENT-HISTORY.md](DEVELOPMENT-HISTORY.md) — 본 문서
- [FINAL-SPEC.md](FINAL-SPEC.md) — 최종 스펙
- [AlgorithmHistory.html](AlgorithmHistory.html) — 초기 알고리즘 변화 시각화
- [StrengtheningTimeline.html](StrengtheningTimeline.html) — 강화 타임라인
- [EasyGuide.html](EasyGuide.html) — 사용 가이드
- [IntegrationReport.html](IntegrationReport.html) — 통합 결과 보고
- [ScenarioReport.html](ScenarioReport.html) — 42 시나리오 F1 표

### 실행 산출물
- `EVO_v18.sdf` — ***REDACTED***EVO 의 역공학된 DsStore
- `ScenarioReport.csv` — 스프레드시트 분석용

---

## 12. 최종 메트릭 (2026-05-23 기준)

| 항목 | 값 |
|------|------|
| xunit 테스트 | **110 / 110 통과** |
| 합성 시나리오 (m0-100) | 101 / 101 perfect, F1=1.0 |
| Phase 1-5 시나리오 | **42** (R5/Q5/D3/P3/V3/G4/Z5/K4/S4/O3/T3) |
| Multi-seed avg F1 | 0.92 (5 seed × 42 시나리오) |
| Multi-seed std | < 0.02 (안정) |
| Studio Cases | **8** (A~H) |
| 알고리즘 모듈 | **14** (B1.1/B1.2/B2.1/B2.2/B3.1/B3.2/B4.1/B4.2/B4.3/B5/B5.2/B6/B7.1/B7.2) |
| EVO arrowCalls | 70 → **96** (default) / 226 (autoTune) |
| 빌드 경고 | 0 / 오류 0 |
| 코드 라인 (Reverse.Core) | ~1,200 |
| 코드 라인 (Reverse.Bench) | ~1,500 |
| 코드 라인 (Reverse.Tests) | ~2,000 |
| 코드 라인 (Studio) | ~1,200 |
