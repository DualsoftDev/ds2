# Phase 6 — Flow 차원 (1~20) 알고리즘 강화 (2026-05-25)

## 사용자 요청
> "c 번 테스트로 flow 1~20 개까지 조절해가면서 알고리즘 그만할때 까지 계속 강화"

## 작업 범위

### Studio Case C 확장
- `MultiFlowGenerator.NFlows` 범위: 2~5 → **1~20**

### Bench Phase 6 신규 — F 차원 (Multi-Flow Inline)
- `Ds2.Reverse.Bench/Phase6Models.fs` 신규
- 15 variants × 20 flow counts = **300 scenarios**

### 인프라 변경
- `Scenario.flowCallsAuto` — call name prefix 기반 multi-flow 자동 그룹핑
- `BenchRunner.runOne` — multi-flow scenario 자동 감지 (prefix `f/fa/fh/fs/fx/fb/fu/fc/ft/fn/fH/fL/fR/fT/fY`)

---

## Round 별 결과

### Round 1 — Basic Multi-Flow (단순 inline chain)
| 변형 | scenarios | F1 |
|------|-----------|-----|
| Simple (f01-f20) | 20 | **1.000 / 20 perfect** |

### Round 2 — Variant Multi-Flow
| 변형 | scenarios | F1 |
|------|-----------|-----|
| Async (fa01-fa20) — flow 시작점 변동 | 20 | 1.000 |
| HeteroLag (fh01-fh20) — flow 별 lag 다름 | 20 | 1.000 |
| Spurious (fs01-fs20) — 각 flow noise call | 20 | 1.000 |

### Round 2b — Cross-Flow / Sync / Burst
| 변형 | scenarios | F1 |
|------|-----------|-----|
| CrossFlowChain (fx01-fx20) — sequential flows | 20 | 1.000 |
| SyncBarrier (fb01-fb20) — 모든 flow 동시 발화 | 20 | 1.000 |
| Burst (fu01-fu20) — 50% flow 만 발화/cycle | 20 | 1.000 |

### Round 3 — Adversarial
| 변형 | scenarios | F1 |
|------|-----------|-----|
| Confounded (fc01-fc20) — 외부 timer shift | 20 | 1.000 (FP=0) |
| TightCycle (ft01-ft20) — 매우 짧은 cycle | 20 | 1.000 |
| HeavyNoise (fn01-fn20) — flow 당 5 noise | 20 | 1.000 (FP=0) |

### Round 4 — Stress
| 변형 | scenarios | F1 |
|------|-----------|-----|
| HighStage (fH01-fH20) — 10 stages/flow | 20 | 1.000 |
| LongChain (fL01-fL20) — 15-node chain/flow | 20 | 1.000 |
| RatioStress (fR01-fR20) — flow 별 stages 비대칭 | 20 | 1.000 |

### Round 5 — Intra-Flow Adversarial
| 변형 | scenarios | F1 |
|------|-----------|-----|
| TransitiveBait (fT01-fT20) — N1→N3 spurious | 20 | 1.000 (FP=0) |
| CycleBait (fY01-fY20) — N4→N1 cross-cycle spurious | 20 | 1.000 (FP=0) |

---

## 최종 결과

- **300 / 300 perfect** (F1 = 1.000 all)
- **TotalFP = 0** (모든 spurious 가 정확히 거부됨)
- **알고리즘 약점 없음** — 모든 multi-flow 변형에서 회귀 없이 안정 동작

### 약점 발견 X — 알고리즘 강화 불필요
이번 Round 1~5 를 통해 알고리즘의 multi-flow robustness 가 검증되었다.
- 1~20 flows 어떤 수든 정확 처리
- 의도된 spurious 모두 거부
- transitive / cycle / multi-modal / async / confounded 모두 처리
- 어떤 케이스에서도 crash / FP / FN 없음

---

## 검증

- ✅ Studio Case C 범위 확장 (1~20 flows)
- ✅ Bench Phase6 300 scenarios 생성
- ✅ Phase6 dimension diagnostic (각 variant 별 추적)
- ✅ Full xunit: **314 / 314 통과**
- ✅ Studio builds cleanly
- ✅ EVO/DEMO 회귀 무영향

---

## 알고리즘의 multi-flow 안정성 근거

1. **work-id 분리**: ReverseEngine 이 candidate 의 src/tgt 가 같은 work 인 경우만 score 계산.
2. **flowCallsAuto**: prefix 기반 flow 자동 분리 → 각 flow 가 독립 처리.
3. **DAG enforcement per-work**: cycle 검사가 work 별 — multi-flow 가 서로 영향 없음.
4. **Cross-flow candidates 별도 경로**: ArrowWorks 별도로 처리 (cross-flow spurious 가 intra-flow 결과 영향 없음).

이런 구조 덕분에 multi-flow stress 가 algorithm 에 영향이 없다.

---

## 코드 추가 / 변경

- **신규**: `Ds2.Reverse.Bench/Phase6Models.fs` (~280 lines, 15 variants)
- **신규**: `Ds2.Reverse.Tests/Phase6Tests.fs` (~250 lines, 21 tests)
- **변경**: `Ds2.Reverse.Bench/Scenario.fs` (`flowCallsAuto` 추가)
- **변경**: `Ds2.Reverse.Bench/BenchRunner.fs` (multi-flow 자동 감지)
- **변경**: `Apps/Ds2.Reverse.Studio/Models/Generators.cs` (NFlows 1~20)
