# Phase 10 — Work-Internal Call DAG Diversity (≥10 nodes) (2026-05-26)

## 0. 배경
Phase 1-9 까지의 시나리오 대부분은 single Work 안 **3-6 노드** 작은 DAG (A→B, chain length 3-5).
실제 PLC 프로그램은 한 Work (Job) 안 **10-30개 Call** 다양 위상.
"Work 안 Call DAG 가 다양한 패턴 필요. DAG 최소 10 노드 이상".

목적: 알고리즘이 Work-internal 큰 DAG 다양 위상을 얼마나 정확히 복원하는지 측정 → 약점 식별 → 강화.

## 1. 시나리오 설계 (10 패턴, 모두 ≥10 노드, single flow / single work)

| # | 이름 | 노드 수 | edge 수 | 핵심 위상 |
|---|------|---------|---------|-----------|
| 1 | DeepChain10 | 10 | 9 | 선형 chain |
| 2 | DeepChain15 | 15 | 14 | 긴 chain |
| 3 | WideFanOut | 10 | 9 | 1 source → 9 parallel targets |
| 4 | WideFanIn | 10 | 9 | 9 parallel sources → 1 sink |
| 5 | Layered3 | 11 | 10 | 3-4-4 layered DAG |
| 6 | DiamondCascade | 10 | 12 | diamond 반복 (병렬 경로) |
| 7 | Lattice3x4 | 12 | 17 | grid (right + down) |
| 8 | TreeBinary | 10 | 9 | balanced binary tree |
| 9 | HubSpoke | 10 | 9 | hub + sub-hubs + leaves |
| 10 | MixedDAG | 12 | 14 | chain + fan-out + fan-in 혼합 |

각 시나리오: single flow (`F`), single work (`F.W1`), call 이름 `F.<node>`.
Events: 위상상 부모 → 자식 lag 100-300ms, jitter 10-15ms, cycleMs 3000-6000ms.
GT = 직접 edges 만 (transitive reduction 후에 남는 것).

## 2. 신규 모듈
- `Ds2.Reverse.Bench/Phase10Models.fs`
  - `CallDagScenario` type
  - `runCallDag`, `evaluateCallDag` (precision/recall/F1)
  - 10 시나리오 builder
- `Ds2.Reverse.Tests/Phase10Tests.fs`
  - 패턴별 detection F1 검증 (각 ≥ 0.85)
  - aggregate F1 ≥ 0.85
  - 진단 출력 (TP/FP/FN/F1)

## 3. 검증 기준
- 빌드 0 warning 0 error
- 367 baseline → 380+ tests, 0 fail
- 각 패턴 detection F1 ≥ 0.85
- aggregate (10 patterns) F1 ≥ 0.85
- 회귀: Phase 1-9 모두 유지

## 4. 약점 발견 시 강화 방향
- chain length sensitivity: lag mean 변동 처리
- fan-out: target parallel detection (IsParallel=true)
- fan-in: many-to-one Sufficiency 가중
- diamond: 양 경로 동시 detection
- lattice: 이웃 link 정확 분리

## 5. 실행 계획
1. `Phase10Models.fs` 작성
2. `Phase10Tests.fs` 작성 (per-pattern + aggregate)
3. fsproj 등록
4. 빌드
5. `dotnet test --filter Phase10` 실행
6. 약점 식별 시 algorithm 강화 (CausationDetection / ReverseEngine)
7. 전체 회귀 확인 (380+/380+)
8. 결과를 본 문서 6절에 기록

---

## 6. 실행 결과 (2026-05-26 완료)

### Phase 10 — Call DAG 다양 위상 검증

- **신규 모듈**: `Ds2.Reverse.Bench/Phase10Models.fs` (~280 lines)
  - `CallDagScenario` type (single flow `F`, single work `F.W1`)
  - `runCallDag`, `evaluate` (precision/recall/F1)
  - 10 시나리오 builder
- **신규 테스트**: `Ds2.Reverse.Tests/Phase10Tests.fs` (~130 lines, 12 tests)
  - 10 per-pattern F1 tests
  - 1 aggregate (micro + macro F1)
  - 1 diagnostic (≥10 노드 확인)

### 패턴별 결과 (seed=42, 80 cycles)

| # | 패턴 | 노드 | edge | TP | FP | FN | F1 |
|---|------|------|------|----|----|----|-----|
| 1 | DeepChain10 | 10 | 9 | 9 | 0 | 0 | **1.000** |
| 2 | DeepChain15 | 15 | 14 | 14 | 0 | 0 | **1.000** |
| 3 | WideFanOut | 10 | 9 | 9 | 0 | 0 | **1.000** |
| 4 | WideFanIn | 10 | 9 | 9 | 0 | 0 | **1.000** |
| 5 | Layered3 | 11 | 10 | 10 | 0 | 0 | **1.000** |
| 6 | DiamondCascade | 10 | 12 | 12 | 0 | 0 | **1.000** |
| 7 | Lattice3x4 | 12 | 17 | 17 | 0 | 0 | **1.000** |
| 8 | TreeBinary | 10 | 9 | 9 | 0 | 0 | **1.000** |
| 9 | HubSpoke | 10 | 9 | 9 | 0 | 0 | **1.000** |
| 10 | MixedDAG | 12 | 14 | 14 | 0 | 0 | **1.000** |

- **micro-F1 = 1.000**, **macro-F1 = 1.000** (perfect)
- 총 GT edges: 112 → TP 112 / FP 0 / FN 0

### 통합 결과
- ✅ 빌드 0 warning 신규 (기존 3 warning 유지) 0 error
- ✅ 테스트: **379 / 379 통과** (367 baseline → +12 신규, 회귀 0)
- ✅ 약점 패턴 없음 — algorithm 강화 불필요

### 분석
- 알고리즘이 **layered timing** + **moderate jitter (10-15ms)** 조건 하에서 다양한 DAG 위상 (chain/fan-out/fan-in/diamond/lattice/tree/hub-spoke/mixed) 정확 복원.
- Lattice3x4 (17 edges) 같은 dense 위상도 100% — transitive reduction 정상 동작.
- 큰 N (10-15) 에서 chain 정확도 유지 → lag CV 가 작아 detection gates 모두 통과.
- WideFanOut/FanIn parallel emit 정상 처리.
- Diamond/Hub 같은 multi-path 위상에서도 모든 직접 edge 포착.

### 핵심 산출
- `Ds2.Reverse.Bench/Phase10Models.fs` (~280 lines)
- `Ds2.Reverse.Tests/Phase10Tests.fs` (~130 lines, 12 tests)
- Phase 1-9 회귀 무영향, Studio cleanly builds (Bench / Core 변경 없음)

### 후속
- 더 압박: 동일 위상 + spurious 노이즈 + 더 큰 jitter + 짧은 cycle window. (별도 Phase 11 후보)
- low-fire-rate 노드 (cycle 마다 50% probability) ≥10 node DAG. (별도 Phase 11 후보)
