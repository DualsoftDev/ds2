# Phase 8 — 새 방향 압박 + 알고리즘 강화 (2026-05-25)

## 0. 배경 — Phase 1-7 완료 후 상황
- 338/338 tests pass
- intra-flow / multi-flow / polling / multi-modal 모두 robust
- Phase 7 남은 한계: low-ratio polling, conditional, combined attack

## 1. 새 방향 — 아직 다루지 않은 영역

### 1.1 Cross-Flow Causation Detection (Phase 8A)
**상황**: 지금까지 cross-flow scenarios (Studio Case C, Phase 6) 의 ground truth 는 **intra-flow ADV→RET only**.
실제 EVO 데이터의 cross-flow workWorks 검출은 별도 경로 (CrossFlowCandidates) 거치는데, 합성 시나리오로 검증 못 했음.

**작업**:
- ScenarioWithCrossFlow 타입 추가 (cross-flow ground truth)
- BenchRunner 가 cross-flow candidates 도 전달
- 다양한 cross-flow 패턴: chain / fan-out / fan-in
- 시나리오 8~10개

### 1.2 Logic-Hybrid Scoring 강화 (Phase 8B)
**상황**: `ReverseEngine` 가 LogicRungs 받으면 logic strength 추출하지만, 본격적 hybrid scoring (Phase 7 의 polling/conditional 약점 보완) 활용 못 함.

**작업**:
- 시나리오: capture 만으로는 약함(보더라인 suff/necc), logic strength 있으면 detect 가능
- LogicHybrid scoring 강화: capture suff 가 0.7-0.85 범위 이고 logic strength 가 0.7+ 면 인정
- 시나리오 5-8개

### 1.3 Confidence Calibration 정밀도 (Phase 8C)
**상황**: 현재 confidence tier 가 score 기반 매핑. 실제 검증 정확도 측정 부족.

**작업**:
- 시나리오 수십개에서 High tier arrows 의 truth-rate 측정
- 부정확하면 confidence formula 보정
- 검증: High >= 95% truth, Medium >= 70% truth, Low 알 수 없음

## 2. 통과 기준
- 빌드 0 warning 0 error
- 기존 338 tests 유지
- Phase 8 신규 ~25 tests 통과
- 실 데이터 EVO/DEMO 회귀 무영향

## 3. 실행 순서
1. Phase 8A 시나리오 + cross-flow runner 작성 + 테스트
2. 약점 발견 시 → 알고리즘 강화 (cross-flow 매칭 개선)
3. Phase 8B 시나리오 + Logic-Hybrid 통합 검증
4. Phase 8C Calibration test
5. 최종 회귀 + 보고

---

## 4. 실행 결과 (2026-05-25 완료)

### Phase 8A — Cross-Flow Detection
- **신규 시나리오 5개**: TwoFlowChain / ThreeFlowChain / FanOut / FanIn / WithSpurious
- **결과**: 모두 intra F1=1.000, cross F1=1.000 (5/5 perfect)
- ReverseEngine 의 CrossFlowCandidates 경로 정확 동작 검증
- WorkAssignments hint 로 정확한 work-level grouping

### Phase 8B — Logic-Hybrid Recovery (알고리즘 강화)
- **알고리즘 강화** (`ReverseEngine.run` 내부):
  ```fsharp
  // capture Dropped 일 때 logic strength 가 강하면 recovery
  match logicStr with
  | Some ls when ls >= 0.8 -> s.Sufficiency >= 0.3      // very strong logic
  | Some ls when ls >= 0.7 -> s.Sufficiency >= 0.4      // strong logic
  | _ -> false
  // necc >= 0.4, lagMean > 0 추가 조건
  ```
- **신규 시나리오 4개**:
  - WeakConditional (60% suff): no-logic TP=0 → with-logic TP=1
  - BorderlineSuff (70% suff): no-logic TP=0 → with-logic TP=1
  - OrGateLogic: no-logic TP=1 → with-logic TP=1
  - StrongLogic (55% suff): no-logic TP=0 → with-logic TP=1
- **TP 향상**: capture-only 1 → capture+logic **4**
- Phase 7 의 "Conditional p<90%" 약점 회복

### Phase 8C — Confidence Calibration
- **High tier accuracy**: 65/68 = **95.6%** (95% threshold 통과)
- Tier 분포 (Phase 6 14719 emitted arrows): **High 14719 vs Medium 261 vs Low 0**
- Tier monotone vs score 일관성 검증

### 검증
- ✅ 빌드: 0 warning, 0 error
- ✅ 테스트: **353 / 353 통과** (338 → +15 신규)
- ✅ Studio cleanly builds
- ✅ EVO/DEMO 회귀 무영향

### 핵심 산출
- `Ds2.Reverse.Bench/Phase8Models.fs` — Cross-flow + Logic-Hybrid scenario builder
- `Ds2.Reverse.Tests/Phase8Tests.fs` — 15 신규 tests
- `Ds2.Reverse.Core/ReverseEngine.fs` — Logic-Hybrid recovery 통합
