# 23. Gap 기반 비가동 판정 스펙 (고정 120초 무사이클 대체)

> 상태: **Phase 1 구현 완료(2026-07-03), Phase 2 보류.** doc/21(시간기반)·doc/22(사이클기반) 위에 얹는 개정.
> 배경 대화: 2026-07-03. 무사이클 고정 임계(120초)의 위험성 → per-flow gap 판정으로 대체.
> Phase 1 구현: `OeeMath.ClassifyGap`/`ResolveNoCycleThresholdMs`/`DowntimeGapMultiplier`(§5·§7),
> `OeeCtStatsService.ComputeGapMedianAsync`(§4), `OeeDowntimeStateMachine` per-flow 임계+5분 TTL 캐시(§6 ①②③).
> 테스트 12건 추가(OeeMathTests), 전체 147/147 통과.

## 1. 문제 (현행)

비가동(다운타임) 자동 판정은 현재 두 축:

1. **사이클 분류** (`OeeMath.ClassifyCycle` / `ComputeCycleAggregateAsync` 인라인 SQL `dtCond`):
   완료 사이클 `MT > 14일평균CT` → 비가동, 미완료 `CT > 14일평균CT` → 비가동.
2. **무사이클(nocycle)** (`OeeDowntimeStateMachine`): 마지막 사이클 후 **고정 120초**(`Oee:NoCycleSeconds`) 무가동 → 정지 이벤트(`detectSource='nocycle'`), oee.db 에 기록 → `ComputeCycleAggregateAsync` 가 `GetNocycleIntervalsMsAsync` 로 읽어 dedup 합산.

### 1.1 왜 ①만으론 부족한가 (무사이클이 load-bearing)
`MT > 평균CT` 는 **모션(MT)** 으로 판정한다. 그런데 라인 정지의 대부분은 "모션이 과주행"이 아니라 **"모션 끝난 뒤 다음 사이클이 안 시작"**(자재/작업자/상류 대기)이다. 이 경우 재개 사이클의 모션은 정상 → `MT>평균CT` 미발화, 비가동 집합에 안 들어가 10×CT 비생산 규칙도 볼 대상이 없다. **"안 도는 상태"를 감지하는 건 무사이클뿐** → 제거 불가.

### 1.2 왜 고정 120초가 위험한가
무사이클 임계는 **전역 고정 120초**인데 flow 마다 주기(CT)가 다르다.
- 주기 > 120초인 flow(느린/배치성)는 **정상 가동 중에도** 사이클 간격이 120초를 넘겨 **매 사이클 거짓 onset** → 정상 시간이 비가동으로 계상, A 급락.
- 사실상 "모든 flow 주기 < 12초"라는 숨은 가정. doc/21 §Phase3 에 "per-flow 연동(ResolveEffectiveCycleRangeMs 재사용)" 미결로 남아 있던 지점.

## 2. 핵심 아이디어

"정지 여부"를 고정 초로 탐지하지 말고, **각 flow 의 gap(=다음 가동까지 간격)을 그 flow 자신의 학습된 기준(gap')과 비교**한다. gap 은 이미 데이터에 있다:

- **CT = MT + WT**, 그리고 **WT = CT − MT = 완료→다음시작 = gap**. (`CycleDerivation`: `PeriodMs=starts[i+1]−starts[i]`, `WT=Period−Active=starts[i+1]−complete[i]`.)

즉 닫힌 사이클의 gap 은 **row 의 WT** 그 자체다. 고정 임계도, 별도 상태머신도 (A 계산 목적으론) 필요 없다.

## 3. gap 정의

| 종류 | 정의 | 출처 |
|---|---|---|
| **닫힌 gap** (다음 사이클 있음) | `WT = CT − MT` | dspFlowHistory row (mt·ct) |
| **열린 gap** (지금 멈춤, 다음 시작 없음) | `min(now, toUtc) − lastComplete` | window 끝에서 합성 |

열린 gap 이 옛 무사이클이 실시간 폴링으로 잡던 유일한 케이스다. **OEE A 산출은 조회 시점에 합성 가능** → 실시간 상태머신 불필요(대시보드 "현재 정지중" 배지 용도로만 선택적 유지).

## 4. gap 기준값 gap' (학습)

`OeeCtStatsService` 에 신규 `ComputeGapMedianAsync` (CT 임계와 동일 파이프라인, 단순화):

- **중앙값(median)** of `WT (=ct−mt)` — 평균 아님(이상치에 강하고 단순).
- **클린 사이클만**: `COALESCE(IsIdle,0)=0 AND ct>0 AND mt IS NOT NULL`. IsIdle 은 CT(주기) 이상치를 이미 제외하므로 **정지를 머금은 사이클은 자동 배제** → gap' 이 정지에 오염되지 않음(threshold creep 방지의 실제 주체).
- **최근 14일**, **오늘 제외**(`recordedAt < today 00:00`).
- **가중 없음.** (반대가중은 성능 P 의 자기참조 방지용 장치일 뿐, 비가동 "분류"엔 불필요 — 정지 gap 은 정상 gap 과 자릿수가 달라 신호가 압도적.)
- 신규 flow(오늘만 데이터) 폴백: CT 임계와 동일하게 오늘 포함 잠정값 `TryAdd`, 클린샘플 < `ConfidentMinCleanCycles`(5) 면 "샘플 부족" 표시.

## 5. 판정 규칙 (순수 함수, 테스트 대상)

`OeeMath` 신규:

```csharp
public enum GapClass { Normal, Downtime, NonProduction }

/// gap(ms)을 정상/비가동/비생산으로 분류.
///   gap ≥ NonProductionCtMultiplier(10) × ctThresholdMs → 비생산 (기존 10×CT 규칙 재사용)
///   gap > DowntimeGapMultiplier(k) × gapMedianMs        → 비가동
///   그 외                                                → 정상
/// gapMedianMs ≤ 0(표본부족) 이면 비가동 판정 불가 → 비생산 경계만 적용(상위 게이트).
public static GapClass ClassifyGap(double gapMs, double gapMedianMs, double ctThresholdMs)
```

- `DowntimeGapMultiplier` **k = 3** (기본). `gap > gap'` (×1)은 중앙값의 절반이 초과하므로 오탐 → 마진 필수. k=2 는 최소선, 작고 변동 큰 gap 에서 튐 → **k=3 권장**(튜닝 노브).
- 비생산은 기존 `IsLongStopNonProduction`(10×CT) 유지 — 순서상 비생산 먼저 검사(항상 비가동 경계보다 큼: `3×WT < 10×(MT+WT)`).

## 6. 통합 (단계별)

### Phase 1 — 최소·저위험 (고정 120초 제거)
`OeeDowntimeStateMachine.TickAsync` 의 전역 `thresholdMs = NoCycleSeconds×1000` 을 **flow별 임계 폴백 체인**으로 교체:

```
threshold(flow) =
    ① max(k × gapMedian(flow), floor)   // 최근 14일 클린 WT 표본 충분 → per-flow (성숙)
    ② else k × 14일평균CT(flow)          // gap' 없지만 CT 임계는 학습됨 → 여전히 per-flow
    ③ else NoCycleSeconds(120s)          // 학습 전무 = 콜드스타트(시설 막 가동/신규 flow/표본 0) → 부트스트랩
```

- `floor`(예: 30초): gap' 이 아주 짧은 초고속 flow 에서 잡음성 미세정지까지 onset 되는 것 방지.
- **핵심**: 고정 120초는 **제거하지 않고 ③ 부트스트랩으로 격하**. 기존 문제(상시·전역 적용→느린 flow 상시 오탐)는 해소되고, Day 0 완전무지 상태의 첫날 정지 감지는 유지. flow 가 clean gap'/CT 를 학습하면(보통 Day 1+) 자동으로 ①/②로 승격, 120초는 더 이상 쓰이지 않음. 잠정 구간은 "샘플 부족" 표시(doc/21 §10 정직성).
- 나머지 로직(onset/clear/dedup/집계)·`detectSource='nocycle'` 그대로 → downtime 목록/추이/랭킹 소스 무변경.
- 효과: 느린/배치성 flow 거짓 onset 제거. **위험 지점만 정확히 해소.**

### Phase 2 — 정리 (선택, 상태머신 OEE 역할 은퇴)
`ComputeCycleAggregateAsync` 에서 gap 을 조회 시점에 직접 도출:
- 닫힌 gap: 완료 사이클 loop 에서 `WT = ct−mt` 를 `ClassifyGap` → idle/nonprod/normal 분해(이중계상 없이 사이클 단위 일관).
- 열린 gap: window 마지막 사이클 뒤 `min(now,to)−lastComplete` 합성 → `ClassifyGap`.
- `GetNocycleIntervalsMsAsync` + oee.db nocycle 의존 제거(A 한정). 상태머신은 대시보드 실시간 배지 필요 시에만 경량 유지.
- ⚠️ downtime 목록/일별추이/랭킹이 nocycle 이벤트를 소비하므로, Phase 2 시 그 소스도 gap 도출로 이전 필요 → 범위 큼. **Phase 1 안정화 후 착수.**

## 7. 상수·노브

| 상수 | 값(기본) | 위치 | 의미 |
|---|---|---|---|
| `NonProductionCtMultiplier` | 10 | OeeMath (기존) | 비생산 = ≥10×CT |
| `DowntimeGapMultiplier` (신규) | 3 | OeeMath | 비가동 = >3×gap' |
| gap floor (신규) | 30_000ms | 상태머신/집계 | 초고속 flow 잡음 하한 |
| window / 오늘제외 | 14d / today | OeeCtStatsService | gap' 산출 창 |

`Oee:NoCycleSeconds`(120s) 는 **제거하지 않고 폴백 체인 ③(콜드스타트 부트스트랩)으로 격하**. 주 규칙이 아니라 학습 전 안전망.

## 8. 테스트 (OeeMathTests 추가)

- `ClassifyGap`: 정상(gap ≤ 3×gap'), 비가동(3×gap' < gap < 10×CT), 비생산(gap ≥ 10×CT), 경계, gap'≤0(표본부족) 폴백.
- gap' median 산출: IsIdle 제외 확인(정지 사이클이 gap' 을 안 올리는지), 오늘 제외.
- 회귀: 기존 `ClassifyCycle`(MT>thr)·`IsLongStopNonProduction` 불변 유지(과주행 모션 경로는 그대로).

## 9. 리스크·마이그레이션

- **양립성**: Phase 1 은 임계값만 교체 → 스키마/데이터 마이그레이션 없음. 기존 설치는 재시작 시 즉시 per-flow 로 전환.
- **초기 거동**(Day 0/3/15): gap' 도 CT 임계와 동일하게 Day 0 은 오늘-폴백(자기참조·샘플부족 표시), Day 1+ 어제까지 기준, Day 15+ 롤링. floor 가 Day 0 잡음 하한 역할.
- **정직성(doc/21 §10)**: gap' 표본 부족이면 비가동 판정 보류(가짜 정지 금지), "샘플 부족" 표시로 대체.
