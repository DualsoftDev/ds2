# Phase 9 — FFT + Domain Library + Bayesian Prior (2026-05-25)

## 0. 배경
Phase 7 남은 한계: low-ratio polling 4 FP (POLL/ACT ratio 1-2 → 통계만으로 분리 불가).
세 가지 방향으로 본질적 극복 시도.

## 1. Phase 9A — FFT 기반 Polling Detection
### 아이디어
POLL events 의 inter-arrival 시계열 → DFT (Discrete Fourier Transform) → 강한 frequency peak 검출.
진짜 인과 source 는 자연스럽게 다양한 주기 분포 (broad spectrum).
Polling source 는 단일 주기 dominant (sharp peak).

### 구현
- `CausationDetection.fs` 에 `fftPollingScore` 함수 추가
- DFT 직접 구현 (작은 N, F# 단독 가능)
- Peak ratio: max peak power / mean power. > 5.0 면 polling 의심.

### 시나리오
- High-frequency periodic polling (FFT 로 검출)
- Mixed periodic + random fire
- Aperiodic real causation (FFT 로 거부 안 됨)

## 2. Phase 9B — Domain-Specific Polling Library
### 아이디어
PLC 분야에서 흔한 polling pattern (heartbeat, watchdog, scan) 의 시그니처 라이브러리.

### 구현
- `Ds2.Reverse.Core/PollingPatterns.fs` 신규 모듈
- 알려진 패턴 list (이름, 시그니처):
  - "watchdog 100ms": fire every 100ms ± 5ms
  - "scan cycle 50ms": fire every 50ms ± 2ms
  - "heartbeat 1s": fire every 1000ms ± 50ms
  - "burst 10x burst then idle": 짧은 burst 후 긴 idle
- `matchPattern: int64 seq -> PollingPattern option` 함수
- 매칭 시 algorithm 이 그 source 의 모든 candidate drop

## 3. Phase 9C — Bayesian Prior 강화 Confidence
### 아이디어
현재 `confidence` 함수 가 단순 weighted: primary * 0.7 + logic * 0.3.
Bayesian: prior 정보 (예: domain prior, source type) + likelihood (capture evidence) → posterior.

### 구현
- `bayesianConfidence` 함수 추가
- Priors:
  - logic strength → prior
  - polling 의심 → 낮은 prior
  - high N → confident
- Likelihood: P(observed | hypothesis):
  - suff/necc → likelihood (Bernoulli)
  - lag std/cv → quality
- Posterior = Bayes update

## 4. 통합 검증
- Phase 7 의 low-ratio polling 4 FP → 0 FP 목표
- 회귀: Phase 1-8 모두 유지

## 5. 통과 기준
- 빌드 0 warning 0 error
- 353/353 → 360+ tests, 0 fail
- low-ratio polling FP 감소
- High tier accuracy 유지 >= 95%

---

## 6. 실행 결과 (2026-05-25 완료)

### Phase 9A — FFT polling detector
- **신규 모듈**: `Ds2.Reverse.Core/SignalAnalysis.fs` (DFT + periodicityScore + detectPollingFromTimes + interArrivalCV)
- **알고리즘 통합**: `score` 함수의 polling detector 에 `isPollingByFFT` 추가
- **검증**: periodic 100ms signal peak ratio **34.54** (>5 threshold), random signal ratio 3.06 (거부)

### Phase 9B — Domain-specific polling pattern library
- **신규 모듈**: `Ds2.Reverse.Core/PollingPatterns.fs` (8 known patterns + matchPattern + detectCyclicPolling)
- 알려진 패턴: scan_10ms/20ms/50ms, poll_100ms/200ms/500ms, watchdog_1s, heartbeat_5s
- **검증**: 100ms/50ms/500ms 정확 인식, 2000ms (라이브러리 외) 무매치, irregular 무매치
- **알고리즘 통합**: `isPollingByDomain` 추가 (rate ratio ≥ 2.0 gating)
- **Trade-off 발견**: cyclic-relative offset detector 는 너무 공격적 → 진짜 burst causation 도 거부.
  Strict rate gating (≥2.5 FFT, ≥2.0 Domain) 으로 회귀 방지. Low-ratio polling 4 FP 유지 (algorithm 한계).

### Phase 9C — Bayesian Prior Confidence
- **신규 함수**: `CausationDetection.bayesianConfidence`
  - Prior = logic strength × polling penalty (suspectPolling 시 ×0.3)
  - Likelihood = suff × necc × stabilityFactor (passes_seq → 1.0, lagCv<0.5 → 0.7, else 0.4)
  - Posterior = Bayes update with P(obs|~causal)=0.3
  - N reliability multiplier (NA<10:0.7, <30:0.9, else 1.0)
- **검증**:
  - Strong evidence + strong prior → score 0.964 (High)
  - Weak evidence + strong logic prior → score 0.941 (High, recovery)
  - Polling suspect → score 0.964 → 0.168 (penalty 효과)
  - Empty evidence → Reject

### 통합 결과
- ✅ 빌드 0 warning 0 error
- ✅ 테스트: **367 / 367 통과** (353 → +14 신규)
- ✅ Studio cleanly builds
- ✅ 실 데이터 EVO/DEMO 회귀 무영향

### 핵심 산출
- `Ds2.Reverse.Core/SignalAnalysis.fs` (~90 lines) — FFT 기반 신호 분석
- `Ds2.Reverse.Core/PollingPatterns.fs` (~80 lines) — Domain pattern library + cyclic detector
- `Ds2.Reverse.Core/CausationDetection.fs` — bayesianConfidence 함수 신규 (~60 lines)
- `Ds2.Reverse.Tests/Phase9Tests.fs` (~115 lines, 14 tests)

### 본질적 한계 (재확인)
- Low-ratio polling (POLL/ACT ratio 1-2 + 같은 frequency): 통계적 단독 분리 불가.
  → cyclic offset detector 는 너무 위험 (burst causation 와 구분 어려움).
  → logic hint 결합 (Phase 8B) 또는 domain knowledge 결합 시에만 해결 가능.
- 그러나 **Bayesian prior 가 confidence score 에 polling penalty 반영** — 검출되어도 Low/Reject tier 로 분류 가능.
