# Ds2.Reverse 알고리즘 강화 로드맵

> **두 방향 병행 강화**:
> A. **모델 다양성** — 더 풍부하고 다양한 합성/실 데이터로 알고리즘 stress
> B. **알고리즘 확장** — 새 인과 패턴 인식 + 신뢰도 + adversarial robustness

---

## 🟢 진행 상태 (2026-05-22 업데이트)

| Phase | 항목 | 상태 |
|-------|------|------|
| **Phase 1** | R / Q 차원 시나리오 (10) | ✅ F1=1.0 |
| Phase 1 | D 차원 (Drift, 3 시나리오) | ✅ |
| Phase 1 | B1.1 Cyclic Drift detection (autocorrelation) | ✅ |
| Phase 1 | B3.1 Reset 검출 (cycle hint 비활성화) | ✅ |
| Phase 1 | B3.2 Mutex 검출 (co-occurrence) | ✅ |
| Phase 1 | Multi-modal 거부 (histogram + smallLag tighten) | ✅ |
| Phase 1 | Reset/ResetReset DAG cycle 면제 | ✅ |
| Phase 1 | Studio Case C (Multi-Flow) | ✅ |
| Phase 1 | Studio Case D (Branch) | ✅ |
| **Phase 2** | P / V 차원 시나리오 (6) | ✅ F1=0.89 |
| Phase 2 | B2.1 ArrowConfidence (0~1 점수) | ✅ |
| Phase 2 | B2.2 Soft classification (High/Med/Low/Reject) | ✅ |
| Phase 2 | B5 Logic+Stat Hybrid scoring | ✅ |
| Phase 2 | Studio Case E (Recycle Loop) | ✅ |
| Phase 2 | Studio Case F (PLC-Realistic Cell) | ✅ |
| Phase 2 | Studio UI confidence column + tier 색깔 | ✅ |
| **Phase 3** | G / Z 차원 시나리오 (9) | ✅ F1=1.0 |
| Phase 3 | B4.2 Outlier filtering (Tukey IQR) | ✅ |
| Phase 3 | Studio Case G (Capacity Variable) | ✅ |
| Phase 3 | Studio Case H (Adversarial Mix) | ✅ |

**검증**: xunit **109/109 통과**, EVO/DEMO 회귀 무영향. 신규 시나리오 **42 개** (R/Q/D/P/V/G/Z/K/S/O/T). EVO arrowCalls 70 → **96** 향상. Multi-seed (5 seeds × 42 시나리오) avg F1 ≥ 0.92, std < 0.02. 시나리오 보고서 HTML + CSV 생성.

| 추가 | 항목 | 상태 |
|------|------|------|
| 추가 | Property-style tests (FsCheck 대안, 11 tests) | ✅ |
| 추가 | B6 Online / Incremental detection (Welford streaming) | ✅ |
| 추가 | B7.2 Causation Drift Alert (slope-based) | ✅ |
| 추가 | B4.1 Background Noise Estimation (avg std/200ms) | ✅ |
| 추가 | B5.2 Dynamic Threshold (cfg.withNoiseLevel + ReverseEngine 자동 적용) | ✅ |
| 추가 | All-phase sweep test (28 시나리오 F1≥0.85) | ✅ |
| 추가 | Studio Seed Sweep (10 seed F1 분포 측정) | ✅ |
| 추가 | Studio Auto-tune 체크박스 | ✅ |
| 추가 | B7.1 Anomaly Pattern Learning (cycle deviation) | ✅ |
| 추가 | 최종 통합 HTML 리포트 | ✅ IntegrationReport.html |
| 추가 | B4.3 Bayesian aggregation (logit fusion, 5 tests) | ✅ |
| 추가 | Performance regression tests (chain N=10-100, scenario sweep) | ✅ |
| 추가 | Full pipeline integration test (auto-tune + score + confidence + anomaly + Bayesian) | ✅ |
| 추가 | Phase 4: K (Kombinatorial 4) + S (Stress 3) 시나리오 | ✅ F1=1.0 |
| 추가 | B1.2 Multi-modal k-means (well-separated 3-cluster 인정) | ✅ |
| 추가 | Phase 5: O (Overlap 3) + T (Temporal 3) 시나리오 | ✅ F1≈0.92 |
| 추가 | EVO autoTune 비교 test (Default 96 vs AutoTune 226 arrowCalls) | ✅ |
| 추가 | Studio Anomaly summary 표시 (top 5 anomalous cycles) | ✅ |
| 추가 | DetectionReport.NoiseLevel + AnomalousCycles 노출 | ✅ |
| 추가 | modeCount Int32.MinValue 오버플로우 버그 수정 | ✅ |

**잔여 (저우선)**: 추가 알고리즘 강화 (다른 시나리오 합성 필요 시).

---

## 0. 현재 상태 (Baseline)

| 항목 | 값 |
|------|------|
| 합성 시나리오 | 183 (m × D × S × C × N/F/L × M/I/X × H × K) |
| 실 데이터 | DEMO (14 calls, 9 arrowCalls) + ***REDACTED***EVO (376 calls, 70 arrowCalls) |
| 알고리즘 게이트 | 6단계: Sufficiency / Necessity / CV / Std-abs / Bimodal / Drift / Cluster |
| xunit 테스트 | 40/40 통과 |
| WPF Studio | Case A (Inline) + Case B (DAG) 생성기 |

**본질적 한계** (현재 미해결):
- Cyclic drift (cosine pattern) — linear drift 만 인식
- Multi-modal (3+) lag — bimodal 만 지원
- Cross-flow only flow (CARTYPE 같은) — in-active 없이 cross-flow 만으로 인식
- 다중 cause B 의 같은-cycle 발화 (k4 같은 case)
- Conditional / Stateful 인과

---

## A. 모델 다양성 확장 — 6 개 신규 Case + 6 개 새 차원

### A1. WPF Studio 신규 Case (실 PLC 패턴 모방)

#### Case C — Multi-Flow Inline (2~5 flows + cross-flow)
- 여러 flow (F1, F2, F3) 가 cross-flow arrows 로 동기화
- ***REDACTED***EVO 의 station chain (S141 → S142 → ...) 패턴 모방
- 파라미터: # flows (2~5), # stages per flow (3~8), sync 빈도

```
[F1: W1 → W2 → W3] ─→ [F2: W1 → W2] ─→ [F3: W1]
       │                    ↑                  │
       └────── group ──────┘                  │
             (cross-flow)                     │
                       ┌──────────────────────┘
                       └─→ token return ─→ F1
```

#### Case D — Branch / Choice
- 조건부 분기: `A → B` 또는 `A → C` (확률적 선택)
- 차종 변경, 모드 전환 등 모방
- 파라미터: branch 수 (2~4), 각 branch 확률

```
      [B chain]
     ↗
[A] ─ (random 70/30) ─ [C chain]
     ↘
      [D chain]
```

#### Case E — Recycle Loop (Token Re-entry)
- Token 이 라인 끝에서 라인 시작으로 재진입
- Re-work, repair, buffer 패턴 모방
- 파라미터: 재진입 확률, 재진입 횟수 max

```
[W1] → [W2] → [W3] → [W4] → [W5]
  ↑                            │
  └────── recycle (15% prob) ──┘
```

#### Case F — PLC-Realistic (Cell with Robot + Conveyor + Jig)
- 실제 station 구성: Robot, Conveyor, Jig, Sensors, Cylinders
- 각 device 별 다른 timing 패턴 (Robot 1~3초, Conveyor 0.5초, Sensor 즉시)
- 표준 sequence (LOAD → CLAMP → PROCESS → UNCLAMP → UNLOAD)

```
Conveyor.IN → Jig.CLAMP → Robot.WELD → Jig.UNCLAMP → Conveyor.OUT
              (200ms)      (1500ms)     (200ms)        (300ms)
```

#### Case G — Capacity Variable
- Cycle 마다 다른 token 수 (1~5 사이 무작위)
- 라인 idle 구간 / burst 구간 / 평상 구간 혼합
- 파라미터: idle 확률, burst 확률

#### Case H — Adversarial Mix
- 의도된 spurious arrows + noise + confounded + drift 모두 섞음
- Algorithm 의 false-positive resistance 검증
- 파라미터: spurious 비율, noise level, confounded 강도

### A2. 합성 시나리오 새 차원 (6 차원 추가)

| 차원 | 코드 | 패턴 | 시나리오 수 |
|------|------|------|----------|
| **R (Reset)** | r0~r4 | RST 격리 — A 발화 후 N cycle 뒤 reset (자기소멸) | 5 |
| **Q (Queue)** | q0~q4 | Bottleneck variants — 다양한 queue 길이 / 처리 시간 | 5 |
| **P (Polling)** | p0~p4 | 주기적 polling + 간헐 actual fire — 인과 vs 폴링 구분 | 5 |
| **V (Variable)** | v0~v4 | Cycle 마다 다른 device duration (drift 외 패턴) | 5 |
| **G (Graph)** | g0~g4 | Tree / Mesh / Star / Bipartite topology | 5 |
| **Z (Adversarial)** | z0~z9 | 알고리즘 약점 노출 (multi-modal, partial+noise mix 등) | 10 |

**총 35 신규 시나리오** (183 → 218)

### A3. Property-Based Testing (FsCheck 도입)

- Random graph generator (DAG with N nodes, M edges)
- Invariants:
  - **확장성**: # nodes 100~1000 에서 알고리즘이 timeout 안 남
  - **일관성**: 같은 seed 면 같은 결과 (deterministic)
  - **단조성**: capture 데이터 더 추가해도 정확도 ↓ 안 함
  - **Symmetry**: 모델 순서 바꿔도 같은 결과
- 100 random scenarios per invariant

---

## B. 알고리즘 확장 — 7개 신규 모듈

### B1. 새 인과 패턴 검출

#### B1.1 Cyclic Drift (cosine / periodic)
**현재**: linear drift 만 (residual std 검사)
**확장**: lag 의 cyclic pattern 인식 — Fourier / autocorrelation

```fsharp
let cyclicStable (lags: int64[]) =
    // 1. Linear detrend
    let detrended = lags - linearFit(lags)
    // 2. Autocorrelation → 강한 주기성 발견
    let acf = autocorrelation detrended
    let maxLag = argmax acf[1..]    // 0 제외
    if acf[maxLag] > 0.7 then
        // 주기 발견 → 주기 모델로 추가 residual 계산
        let cyclicFit = ...
        let residual = detrended - cyclicFit
        residualStd < lagMean * 0.10
    else false
```

#### B1.2 Multi-Modal (3+ peaks)
**현재**: bimodal (2 peaks) 만 인식
**확장**: k-means 기반 클러스터링 → k 자동 결정 (3~5)

```fsharp
let multiModalStable (lags: int64[]) =
    // Try k = 2, 3, 4 — pick best fit (silhouette score)
    let bestK, clusters = bestClustering lags maxK=4
    // 모든 cluster 의 std < threshold AND 가장 작은 cluster ≥ 15%
    clusters |> List.forall (fun c -> std c.lags < 50)
            && (smallestCluster clusters).size / total > 0.15
```

#### B1.3 Conditional Causation
**개념**: 어떤 변수 X 의 상태에 따라 A → B 인지 아닌지 달라짐
**예**: X=true 일 때 A → B, X=false 일 때 A → C
**구현**: events 의 context (다른 변수 상태) 와 causation 의 결합

#### B1.4 Hierarchical / Nested Causation
- A → B → C 의 chain 인지 A → C 의 직접 인과인지 구분
- Mediator analysis: A → M → B (M 이 매개)

### B2. 신뢰도 (Confidence) + Soft Classification

#### B2.1 Per-Arrow Confidence Score
**현재**: 이진 (passes / drops)
**확장**: 0~1 의 연속 confidence score

```fsharp
type ArrowConfidence = {
    Score: float          // 0~1
    Tier: ConfidenceTier  // High | Medium | Low | Reject
    Evidence: string list // ["passes_seq"; "drift_stable"; ...]
    Uncertainty: float    // bootstrap variance
}

let confidence (sco: CausationScore) (logicStrength: float option) : ArrowConfidence =
    // Weighted combination:
    //   suff/necc/stability scores → primary
    //   logic strength (if exists) → bonus
    //   sample size → reliability multiplier
    let primary = (sco.Sufficiency + sco.Necessity) / 2.0 * stabilityWeight
    let logicBonus = logicStrength |> Option.defaultValue 0.5
    let nReliability = if sco.NA < 10 then 0.5
                       elif sco.NA < 30 then 0.8
                       else 1.0
    let raw = primary * 0.7 + logicBonus * 0.3
    let scaled = raw * nReliability
    let tier = if scaled >= 0.9 then High
              elif scaled >= 0.7 then Medium
              elif scaled >= 0.5 then Low
              else Reject
    { Score = scaled; Tier = tier; ... }
```

#### B2.2 Soft Classification (3-tier emit)
- **High** (≥ 0.9): emit + green
- **Medium** (0.7~0.9): emit + yellow + "review"
- **Low** (0.5~0.7): drop + flag "uncertain — manual check"
- **Reject** (< 0.5): drop silent

Studio UI 에서 medium/low 를 노란/주황 marker 로 표시 → 사용자 검토.

### B3. 새 검출 타입

#### B3.1 Reset (RST) 인과
- 패턴: A 발화 → N cycle 뒤 A 자동 reset (소멸)
- 검출: A 의 발화-소멸 cycle 의 일관성

#### B3.2 Mutex (ResetReset)
- 패턴: A 발화 ↔ B 발화 = 상호배타 (한 번에 하나만)
- 검출: A 와 B 의 발화 시각 겹침 없음 + 둘 다 발화 빈도 ≥ threshold

#### B3.3 Counter / Accumulator
- 패턴: A 가 N 번 발화 후 B 발화 (count-based trigger)
- 검출: A 발화 횟수 분포 + B 발화 시각의 correlation

#### B3.4 Timer (TON / TOF)
- 패턴: A on 후 시간 경과하면 B on
- 검출: A 의 on duration 과 B 의 fire 시각 매칭

### B4. Adversarial Robustness

#### B4.1 Background Noise Estimation
- Capture 의 전체 noise level 측정
- Noise 위에 인과 신호가 얼마나 강한지 SNR (Signal-to-Noise Ratio) 계산
- 약한 신호는 자동 reject

#### B4.2 Outlier Filtering
- lag 분포의 outlier 제거 후 다시 평가
- Tukey IQR / MAD (Median Absolute Deviation) 사용

#### B4.3 Confidence-Weighted Aggregation
- 여러 candidates 가 다른 신뢰도일 때 가중 결합
- Bayesian update — prior (logic strength) → posterior (with capture evidence)

### B5. Logic + Stat Hybrid Scoring

#### B5.1 Combined Score Function
```fsharp
let hybridScore
    (statScore: CausationScore)
    (logicStrength: float)   // 0~1 from LogicGraph
    (capturePresent: bool) =
    if not capturePresent then
        // Logic-only mode
        if logicStrength >= 0.5 then Emit
        else Drop
    elif statScore.PassesSeq || statScore.PassesGrp then
        // Capture confirmed
        Emit { confidence = logicStrength * 0.3 + statScore.Avg * 0.7 }
    elif logicStrength >= 0.7 && statScore.Sufficiency >= 0.5 then
        // Strong logic + weak capture → tentative emit
        Emit { confidence = 0.6 }
    else
        Drop
```

#### B5.2 Dynamic Threshold
- 시나리오의 noise level 에 따라 threshold 자동 조정
- Noisy data → 더 관대 / Clean data → 더 strict

### B6. Online / Incremental Detection

#### B6.1 Streaming Mode
- Events 가 stream 으로 들어옴 (cycle 마다 추가)
- 매 cycle 후 score 업데이트 (Welford's online algorithm)
- 결과 confidence 가 시간 따라 수렴

```fsharp
type OnlineScore = {
    mutable n: int
    mutable suffHits: int
    mutable necHits: int
    mutable lagSum: int64
    mutable lagSqSum: int64
}
let update (s: OnlineScore) (newEvent: CapturedEvent) = ...
let snapshot (s: OnlineScore) : CausationScore = ...
```

#### B6.2 Anytime Algorithm
- 언제든지 stop 하고 현재 결과 받을 수 있음
- 더 많은 데이터 = 더 정확

### B7. Anomaly Detection

#### B7.1 Pattern Learning
- 정상 cycle 의 events pattern 학습
- 새 cycle 의 deviation 측정

#### B7.2 Causation Drift Alert
- 알고리즘이 검출한 arrows 의 confidence 가 시간 따라 변화
- Drop / pickup 패턴 → 라인 상태 변화 알림

---

## C. 통합 로드맵 (3 Phase, 약 3주)

### Phase 1 — Foundations (Week 1)
**모델**:
- ✅ Studio Case C (Multi-Flow Inline)
- ✅ Studio Case D (Branch)
- ✅ R 차원 (Reset) 5 시나리오
- ✅ Q 차원 (Queue variants) 5 시나리오

**알고리즘**:
- ✅ B1.1 Cyclic Drift detection
- ✅ B3.1 Reset (RST) 인과 검출
- ✅ B3.2 Mutex (ResetReset) 검출

**검증**: 회귀 +10 시나리오 perfect, EVO/DEMO 영향 없음

### Phase 2 — Realistic + Confidence (Week 2)
**모델**:
- ✅ Studio Case E (Recycle Loop)
- ✅ Studio Case F (PLC-Realistic Cell)
- ✅ P 차원 (Polling) 5 시나리오
- ✅ V 차원 (Variable duration) 5 시나리오

**알고리즘**:
- ✅ B2.1 Per-Arrow Confidence Score (0~1)
- ✅ B2.2 Soft Classification (High/Med/Low/Reject)
- ✅ B5 Logic + Stat Hybrid scoring
- ✅ Studio UI: Medium/Low arrows 색깔 구분

**검증**: 실 데이터 (DEMO/EVO) F1 측정 + Confidence 분포 분석

### Phase 3 — Adversarial + Advanced (Week 3)
**모델**:
- ✅ Studio Case G (Capacity Variable)
- ✅ Studio Case H (Adversarial Mix)
- ✅ G 차원 (Graph topology) 5 시나리오
- ✅ Z 차원 (Adversarial) 10 시나리오

**알고리즘**:
- ✅ B1.2 Multi-Modal (3+) stability
- ✅ B4 Adversarial Robustness (noise/outlier/Bayesian)
- ✅ B6 Online / Incremental detection
- ✅ FsCheck Property-based testing

**검증**: Property tests 통과 + Adversarial F1 ≥ 0.85

---

## D. 검증 기준

### D1. 회귀 (Regression)
| 카테고리 | 기준 |
|---------|------|
| 기존 183 시나리오 | F1 = 1.000 유지 |
| 신규 35 시나리오 | F1 ≥ 0.90 |
| 실 데이터 (DEMO) | arrowCalls ≥ 9, F1 ≥ 0.95 |
| 실 데이터 (EVO) | arrowCalls ≥ 70, F1 ≥ 0.85 |

### D2. 신뢰도 (Confidence Calibration)
- High tier arrows 의 실제 정확도 ≥ 95%
- Medium tier arrows 의 실제 정확도 ≥ 75%
- Low tier 는 review 대상 (정확도 측정 X)
- Calibration plot 분석

### D3. Adversarial
- Z 차원 시나리오 (의도된 spurious + noise) 에서 false-positive rate ≤ 10%
- 실 데이터의 가짜 인과 차단율 ≥ 90%

### D4. Performance
- 50 cycles × 100 calls 모델 검증 < 1초
- 1000 시나리오 회귀 < 5분
- Memory < 200MB

---

## E. 마일스톤 + Deliverables

### M1 (Phase 1 끝): Foundation Strong
**산출**:
- 합성 시나리오 218 (이전 183 + R/Q + 신규)
- Cyclic drift / Reset / Mutex 검출
- 회귀 통과 보고

**검증 통과 기준**: 218/218 perfect 또는 ≥ 0.95 평균 F1

### M2 (Phase 2 끝): Production-Ready Confidence
**산출**:
- Case E/F 신규 generator
- ArrowConfidence + Hybrid scoring
- Studio UI 의 confidence 표시
- 실 데이터 재검증 결과 (F1 + confidence 분포)

**검증 통과 기준**: 실 데이터 F1 향상 + Calibration 정상

### M3 (Phase 3 끝): Adversarial Hardened
**산출**:
- 모든 차원 시나리오 합쳐 ~250 시나리오
- Multi-modal stability
- FsCheck property tests
- 광범위한 adversarial 회귀 통과
- 최종 알고리즘 문서 (HTML)

**검증 통과 기준**: Adversarial F1 ≥ 0.85 + Property tests 통과

---

## F. 의사 결정 트리 (Decision Framework)

### F1. 어떤 케이스부터?
**우선순위**: 실 데이터에 가까운 케이스부터
1. Case F (PLC-Realistic) — 실 데이터와 가장 가까움, 즉시 가치
2. Case C (Multi-Flow) — ***REDACTED***EVO 같은 라인
3. Case D (Branch) — 차종 다양화
4. Case E (Recycle) — 재작업 패턴
5. Case G/H (variable + adversarial) — robustness

### F2. 어떤 알고리즘부터?
**우선순위**: 본질적 한계부터 + 실 데이터 영향 큰 것
1. B2 Confidence + Soft classification — 즉시 가치 (사용자 검토 가능)
2. B5 Logic+Stat Hybrid — EVO 의 부족점 해결
3. B1.1 Cyclic Drift — 알고리즘 한계 해결
4. B3.1/3.2 Reset/Mutex — 새 검출 타입
5. B4 Adversarial — 후순위 (real data 에 noise 적음)

### F3. 실패 시 행동
- 시나리오 추가했는데 회귀 fail → 알고리즘 강화 우선
- 알고리즘 강화로 다른 시나리오 fail → trade-off 분석, 시나리오 재조정
- Real data 영향 → 즉시 fix priority 상승

---

## G. 본질적 한계 (Out of Scope)

| 한계 | 이유 | 향후 |
|------|------|------|
| Causation discovery from logs alone (no logic hints) | 통계만으로 spurious 구분 어려움 | 사용자 hint 받기 |
| Hidden confounding variables | 외부 timer 등 측정 불가 | 알 수 없음 |
| Real-time learning | Online detection 후 weight 조정은 강화학습 영역 | 향후 |
| Dynamic graph (변하는 인과) | 정상 vs 비정상 학습 필요 | B7 Anomaly 로 부분 해결 |

---

## H. 우선순위 권장 (실용 관점)

만약 **1주 안 끝내야** 한다면:
1. Studio Case F (PLC-Realistic) — 사용자가 즉시 가치 체감
2. B2 Confidence Score + Soft class — UI 가치 큼
3. B5 Hybrid Scoring — 실 데이터 향상

만약 **2주 가능**:
1. 위 + Case C (Multi-Flow) + B3.1 Reset + B1.1 Cyclic drift

만약 **3주 가능**: 전체 로드맵 진행
