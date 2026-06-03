# Ds2.Reverse 재구현 / 테스트 확장 계획 (2026-05-24)

## 0. 백업

- **원본**: `Solutions/Reverse.bak.20260524/` (716K, source only, bin/obj/.vs 제외)
- 빌드 산출물은 제외하여 정리된 source-only snapshot.

---

## 1. 목표

> **"test 확장을 다양하고 완벽하게"** — 코어 알고리즘은 검증됨 (110 tests),
> 이번 작업은 **테스트 커버리지와 robustness 를 한 단계 더 끌어올림**.

### 1.1 비-목표 (out of scope)
- 알고리즘 재설계 (현재 14 모듈은 검증됨)
- WPF Studio UI 재작성 (8 case 검증됨)
- 실 데이터 (DEMO/EVO) 호환성 변경

### 1.2 핵심 목표
1. **단위 테스트 확장** — 각 module 의 모든 public API 에 unit test
2. **경계 (boundary) 테스트** — threshold 정확히 통과/실패 케이스
3. **edge case 테스트** — empty / single / extreme N / boundary lag
4. **negative 테스트** — spurious 가 정확히 drop 되는지 검증
5. **stress 테스트** — 100+ 시나리오 동시 실행, 1000+ events
6. **property 테스트 확장** — symmetry / commutativity / idempotence
7. **calibration 테스트** — confidence tier 의 통계적 정확도
8. **회귀 baseline** — 변경 시 영향 분석용

---

## 2. 테스트 카테고리 매트릭스

| # | 카테고리 | 신규 tests | 목적 |
|---|----------|-----------|------|
| **U** | Unit (per module) | ~40 | 각 함수의 isolated 검증 |
| **B** | Boundary | ~15 | threshold 정확 통과/실패 |
| **E** | Edge cases | ~15 | empty / single / extreme |
| **N** | Negative | ~10 | spurious 정확 drop |
| **P** | Property+ | ~10 | symmetry / commutativity |
| **C** | Calibration | ~5 | confidence accuracy |
| **S** | Stress | ~5 | 1000+ scenarios |
| **R** | Regression baseline | ~5 | 변경 감지 |
| | **소계** | **~105** | (현재 110 + 신규 = **~215**) |

---

## 3. 구체 테스트 항목

### U. Unit Tests (per module)

#### U-CausationDetection (~15 tests)
- `score`: 단일 페어 정상, 페어 없음, 한쪽 비어있음, 모두 같은 시각, parallel boundary
- `gate`: 5가지 kind (group/reset/trigger_reset/mutex/trigger) 각각
- `mutexScore`: 정확 mutex / partial overlap / not mutex / single side empty
- `confidence`: tier 경계 (0.89/0.9, 0.69/0.7, 0.49/0.5), N 단계별
- `bayesianAggregate`: empty / single / many / clamped extremes
- `estimateNoiseLevel`: clean / mixed / very noisy / boundary

#### U-OnlineDetection (~5 tests)
- `OnlineScore.AddA / AddB`: 순서 입력, 동시 입력, 빈 입력
- `Snapshot`: empty / partial / converged
- `analyzeDrift`: monotone-increase / decrease / plateau / oscillation / too-short

#### U-AnomalyDetection (~5 tests)
- `learn`: 짧은 학습 / 정상 패턴 / 잡음 패턴
- `scoreCycle`: 정확 match / 누락 / 추가 / shifted
- `analyzeAllCycles`: threshold 미만/초과

#### U-LogicGraph (~5 tests)
- `extractCandidates`: 단순 LOAD, AND/OR mix, NOT, multi-level recursion
- Strength 계산 정확성

#### U-DagEnforcement (~5 tests)
- `topoBreakCycle`: DAG 그대로 / 단순 cycle / 다중 cycle / 자기 자신
- `transitiveReduction`: 정확히 transitive edge 만 제거

#### U-ModelBuilder (~5 tests)
- emptyStore / addFlow / addWork / addCallWithApi / addArrowCall / normalizeFullName

---

### B. Boundary Tests (~15 tests)

- Suff exactly 0.85 (passes) vs 0.849 (drop)
- Necc exactly 0.85 vs 0.849
- LagCv exactly 0.30 vs 0.301
- LagStd at 150 (smallLagFallback boundary)
- LagMean at 150 (smallLagFallback boundary)
- Parallel lag at exact 50ms
- Effective window at cycle*0.7 boundary
- MinFires at exact 5
- Confidence at 0.9 / 0.7 / 0.5 boundaries
- Mode count threshold (3-mode가 정확히 됨 vs 안 됨)
- Outlier filter Q1/Q3 boundary

---

### E. Edge Cases (~15 tests)

- Empty events list
- Single event
- All events same timestamp
- Two events same name
- Cycle hint = 0
- Window = 0
- All same lag (zero variance)
- Negative lag (impossible but check)
- Very long lag (cross window)
- Empty arrows / 1 arrow / 1000 arrows
- 1 cycle / 1000 cycles
- All calls in 1 work / each call in own work

---

### N. Negative Tests (~10 tests)

- 무작위 random events → 인과 0 detected
- 강한 confounded (외부 timer) → drop
- 완전 무관 두 chain → cross arrows 0 detected
- 패턴 없는 사이클 → 모두 drop
- noise burst → spurious 인식 안 함
- 모든 z 시나리오의 spurious arrows MUST be dropped
- 모든 q 시나리오의 spurious MUST be dropped (deep bottleneck 등)

---

### P. Property Tests (~10 tests)

- **Determinism**: same input → same output (모든 phase 시나리오, 5 seeds)
- **Idempotence**: run twice → same result
- **Symmetry**: rename calls → arrows renamed correspondingly
- **Translation**: shift all event times by T → same arrows
- **Scaling**: multiply lags by 2 + cycle by 2 → same arrows
- **Monotone N**: more cycles → F1 안 떨어짐
- **Confidence monotone NA**: NA 증가 → confidence 증가
- **Bayesian symmetry**: aggregate(a, b) == aggregate(b, a)

---

### C. Calibration Tests (~5 tests)

- High tier (≥0.9) arrows 의 실제 정확도 ≥ 95% (synthetic 사용)
- Medium tier (≥0.7) 의 실제 정확도 ≥ 75%
- Low tier (≥0.5) 의 실제 정확도 ≥ 50%
- Reject (<0.5) 의 실제 정확도 ≤ 30%
- Calibration plot data 생성

---

### S. Stress Tests (~5 tests)

- 200 시나리오 일괄 실행 (m0-100 + 모든 phase) < 30s
- 1000 cycles single scenario
- 100-node chain
- Memory < 500MB
- Concurrent: 동시에 10 시나리오 (no state leak)

---

### R. Regression Baseline (~5 tests)

- m0-100: F1 = 1.000 (exact match)
- DEMO arrowCalls ≥ 9
- EVO default arrowCalls ≥ 90 (95 baseline)
- All Phase 1-5 aggregate F1 ≥ 0.90
- Multi-seed std < 0.05

---

## 4. 구조 / 파일

### 4.1 새 테스트 파일
- `Tests/Unit/CausationDetectionUnitTests.fs`
- `Tests/Unit/OnlineDetectionUnitTests.fs`
- `Tests/Unit/AnomalyDetectionUnitTests.fs`
- `Tests/Unit/LogicGraphUnitTests.fs`
- `Tests/Unit/DagEnforcementUnitTests.fs`
- `Tests/Unit/ModelBuilderUnitTests.fs`
- `Tests/BoundaryTests.fs`
- `Tests/EdgeCaseTests.fs`
- `Tests/NegativeTests.fs`
- `Tests/CalibrationTests.fs`
- `Tests/StressTests2.fs` (이미 StressTests.fs 있어서 2)
- `Tests/RegressionBaselineTests.fs`

### 4.2 기존 파일 유지
- 기존 110 tests 모두 유지 (regression baseline 역할)
- 알고리즘 core (Ds2.Reverse.Core/*.fs) 변경 없음
- 시나리오 (Ds2.Reverse.Bench/*.fs) 변경 없음
- Studio 변경 없음

---

## 5. 실행 순서

1. ✅ 백업 (`Reverse.bak.20260524/`)
2. ✅ 본 계획 문서 (REBUILD-PLAN.md)
3. **Unit tests** 카테고리별 작성 (CausationDetection → OnlineDetection → ...)
4. **Boundary tests** 작성
5. **Edge case tests** 작성
6. **Negative tests** 작성
7. **Property+ tests** 작성
8. **Calibration tests** 작성
9. **Stress tests** 작성
10. **Regression baseline** 작성
11. fsproj 에 모두 등록
12. Build + Test → 215+ 통과 확인
13. 최종 보고서 업데이트

---

## 6. 통과 기준

- 빌드: 경고 0, 오류 0
- 테스트: **210+ 통과, 0 실패**
- 기존 110 tests 모두 유지 (regression)
- 신규 100+ tests 모두 통과
- 실 데이터 (DEMO/EVO) 회귀 무영향
- 실행 시간: 전체 테스트 < 60s

---

## 7. 실행 결과 (2026-05-24 완료)

### 빌드
- ✅ 전체 솔루션 빌드 (Core + Bench + Tests + Studio)
- ✅ 경고 0개, 오류 0개

### 테스트
- ✅ **261 / 261 통과** (기존 110 + 신규 151)
- ✅ 실행 시간 ~6초 (목표 60s 안)

### 신규 테스트 파일 (13개, 약 1,500줄)
1. `Unit/CausationDetectionUnitTests.fs` (32 tests) — score, gate, mutexScore, confidence, bayesianAggregate, estimateNoiseLevel
2. `Unit/OnlineDetectionUnitTests.fs` (12 tests) — OnlineScore + analyzeDrift
3. `Unit/AnomalyDetectionUnitTests.fs` (9 tests) — pattern learn + scoreCycle + analyzeAllCycles
4. `Unit/LogicGraphUnitTests.fs` (9 tests) — extractCandidates + AND/OR/NOT + recursion + cycle
5. `Unit/DagEnforcementUnitTests.fs` (7 tests) — topoBreakCycle + transitiveReduction
6. `Unit/ModelBuilderUnitTests.fs` (10 tests) — sanitize + normalize + addFlow/Work/Call/Arrow
7. `BoundaryTests.fs` (15 tests) — suff/necc/cv/std/parallel/window/MinFires/confidence/outlier 경계
8. `EdgeCaseTests.fs` (15 tests) — empty/single/same-time/extreme N/long/short cycle
9. `NegativeTests.fs` (10 tests) — random/confounded/spurious 거부
10. `PropertyPlusTests.fs` (10 tests) — Idempotence/Determinism/Translation/Scaling/Symmetry/Monotonicity
11. `CalibrationTests.fs` (5 tests) — tier 정확도 + 분포 + score range
12. `StressTests2.fs` (5 tests) — 200 시나리오 / 1000 cycle / 100-node / parallel / memory
13. `RegressionBaselineTests.fs` (7 tests) — m0-100 perfect + Phase 1-5 F1 + config 유지

### 백업
- `Solutions/Reverse.bak.20260524/` (716K source-only snapshot)

### 검증 통과
- 빌드 cleanly (0/0)
- 261/261 tests pass
- 실 데이터 (DEMO/EVO) 회귀 무영향
- 실행 시간 ~6초 (목표 60s 내)
