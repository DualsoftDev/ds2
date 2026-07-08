# 22. OEE 계산 스펙 — 사이클 단위 (CT/MT/WT) 모델 (P5 v4)

> 상태: **설계(결정 확정)**. `P5_UPTIME_SUMMARY.html`(v4) 의 사이클 단위 OEE 모델을 `/app/uptime.html`
> OEE 종합 탭 + `OeeController`/`OeeMath` 로 구현하기 위한 **계산 스펙**. 데이터 모델(테이블/수집/리포지토리)은
> [doc/21](21_OEE_DOWNTIME_DESIGN.md) 가 정본이며, 본 문서는 **산출 공식과 사이클 모델만** 다룬다.
>
> ⚠️ 본 스펙은 doc/21 §12.4 의 세 결정을 **의도적으로 반전**한다 — §9 의 반전표 참조. (가용성 분모 = 시간기반
> 폴백체인 → **사이클기반**, CT이상치 = p10 → **14일 평균**, MTTR = 제거 → **부활**.) 반전의 근거·대가는 §6 정직성
> 경계에 명시한다. 가짜로 보이게 만들지 않는 것(doc/21 §10)은 그대로 계승한다.

## 0. 모델 전환 한 줄
**시간기반 OEE**(벽시계 계획시간 ÷ 정지 이벤트) → **사이클기반 OEE**(CT = MT + WT, 사이클별 비가동 판정).
가용성의 분모가 "계획시간(달력/시프트)" 에서 "**관측된 사이클 CT 의 합**" 으로 바뀌는 것이 핵심이다.

---

## 1. 사이클 데이터 모델 — CT = MT + WT (신규 수집 불필요)

P5 모델의 입력은 **이미 `dspFlowHistory` 에 적재**되어 있다 (doc/21 §2.4 데이터, [DspRepositoryAdapter.cs](../DSPilot/Adapters/DspRepositoryAdapter.cs) `mt`/`wt`/`ct`/`IsIdle` 컬럼). 추가 PLC 태그·신규 테이블 없이 산출 가능.

분해 정의는 [CycleDerivation.cs](../DSPilot/Services/CycleDerivation.cs) 단일 소스 (라이브 엔진·화면·재계산 공유):

| 기호 | 의미 | 정의 |
|---|---|---|
| **start** | 시작 경계 | Head OutTag↑ |
| **complete** | 완료 마커 | 해당 사이클 구간 내 **첫** Tail InTag↑ |
| **MT** (ActiveMs) | 동작시간 | `complete − start` |
| **CT** (PeriodMs) | 가동시간(주기) | `다음 start − start` (마지막 열린 사이클은 null) |
| **WT** | 대기시간 | `CT − MT = 다음 start − complete` |

> ⚠️ **정지(stuck) 사이클의 MT 거동** — Tail InTag↑ 가 다음 start 전에 발화하면 `complete` 가 늦게 잡혀 **MT 가 큼**
> (정지를 머금음 → 아래 §3 판정 성립). 그러나 끝내 완료 마커가 안 뜨면 `complete=null` → **MT=null, CT 만 폭증**한다.
> 따라서 §3 판정은 MT 단독이 아니라 **CT 폭주·무사이클까지 함께** 정의해야 한다(P5 문서의 "MT > CT이상치" 표현만으론 불완전).

---

## 2. CT이상치 (표준 CT) = 14일 평균 — 판정·성능 공용

### 2.1 정의
- **CT이상치 = 최근 14일 클린사이클(IsIdle=0, ct>0) CT 의 평균** (flow별, ms).
- **두 용도 공용**: ① 비가동 판정 임계(§3), ② 성능 P 의 표준치(§4).
- **RAM 산출, DB 영구기입 금지** (doc/21 §12 CT이상치 정책 계승 — 값 드리프트로 추세가 흔들리지 않게).

### 2.2 산출 — `OeeCtStatsService` 윈도우 변경
- 현재 [OeeCtStatsService.ComputeAsync](../DSPilot/Services/OeeCtStatsService.cs#L42) 는 **"flow별 최근 sampleLimit(기본 2000) 사이클"** 을 잡고
  `Recommended=p10` 을 표준CT로 쓴다(`Avg` 도 이미 계산하지만 미사용).
- **변경**: 신규 산출 경로는 `WHERE recordedAt >= now − 14d` **시간 윈도우** + `Avg`(평균) 사용.
  - 옵션 A(권장): `ComputeAsync` 에 `windowDays` 파라미터 추가 → 14일 윈도우 모드. 기존 호출부(추천 테이블/자동기입)는
    그대로 두고, OEE 산출만 14일 평균을 읽음.
  - 옵션 B: 신규 메서드 `ComputeCtThresholdAsync(windowDays=14)` 분리.
- **샘플 부족 폴백**: 14일 표본 < `MinCleanCycles`(doc/21 §12.4 D, 기본 5/30) → CT이상치 `null` → A·P·OEE 는 산출 불가
  (`—` + 사유, 가짜 % 금지 — doc/21 §10).

### 2.3 doc/21 §12.4 p10 결정의 반전 (대가 명시)
doc/21 §3·§12 은 "평균/중앙을 표준으로 쓰면 성능이 자기 자신과 비교돼 **순환정의**" 라며 p10(최속 반복가능)을 채택했다.
본 스펙은 P5 통일을 위해 **14일 평균으로 되돌린다**. 그 결과 §6 ① 의 대가(정상상태에서 P≈100%)를 수용하되, 성능의
**해석을 "최속 대비 손실" → "14일 추세 대비 당기 저하"** 로 재정의한다(저하 감지기). 카드 노트 문구도 그에 맞춘다.

---

## 3. 비가동 판정 — 사이클별 + 무사이클 합산 (dedup 필수)

한 사이클이 아래 중 하나면 **그 사이클 CT 전체가 비가동 CT** (P5 §①: 인식지연 + 고장 + going후 정상회복 모두 포함).

1. **MT 과주행**: `MT > CT이상치` — 동작이 표준 사이클 전체보다 길게 늘어진 정지(완료가 늦게 발화).
2. **CT 폭주(완료 실패)**: `complete = null AND CT > CT이상치` — 끝내 완료 못 한 정지 사이클(§1 의 MT=null 케이스 보강).
3. **무사이클 정지 합산** (사용자 결정): 사이클이 **아예 안 찍힌** 구간 = 라인 완전정지. `oeeDowntimeEvent`
   (`detectSource='nocycle'`, doc/21 §5.1) 가 이미 잡고 있으므로, 이 정지를 비가동 CT 에 가산.

### 3.1 이중계상(dedup) — 본 스펙 최대 난점
②/③ 은 시간이 겹친다: 중간에 멈췄다 복구된 정지는 **긴 CT(②)** 로도 잡히고 **무사이클 이벤트(③)** 로도 잡혀 같은
벽시계 시간을 두 번 셀 수 있다(doc/21 §11.1 정신 — dedup 부재 = 이중계상).

**해결**: 무사이클 정지(③)는 **어떤 사이클 CT 구간과도 겹치지 않는 부분만** 가산한다.
- `Σ비가동CT = Σ(①②사이클 CT) + (무사이클 정지 구간 − 모든 사이클 CT 구간의 합집합)`
- 구간 차집합은 [OeeController](../DSPilot/Controllers/OeeController.cs) 의 기존 `Intervals` 헬퍼(합집합/교집합/차집합) 재사용.
- 효과: "끝까지 꺼져있던(복구 사이클 없는)" 구간만 보충되고, 복구된 정지는 ② 로 1회만 계상.

### 3.2 IsIdle 와의 관계
기존 `IsIdle`(CT 범위 `> Max || < Min`, [CycleDerivation.Averages](../DSPilot/Services/CycleDerivation.cs#L78))는 **이상치(아웃라이어) 제거용 큰 캡**이며
CT이상치(14일 평균)보다 훨씬 크다. **본 판정과 별개**다 — IsIdle 은 통계 정제용, §3 판정은 OEE 비가동 분류용.
혼동 금지: §4 의 Σ실측CT 는 "정상 사이클(§3 비가동 아님)" 의 CT 합이며, IsIdle 아웃라이어는 통계(CT이상치) 산출에서만 제외.

### 3.3 비생산 자동 분류 — 10×CT 장시간 무변화 정지 (2026-06-23 추가, 5일 시각대 추정 대체)

> **2026-07-08 갱신 — 당일 판정 복귀 + 사용자 오버라이드.** 2026-07-06 벽시계 전환 때 KPI 경로에서 껐던
> 당일 10×CT 판정(`applyLongStop`)을 **자동 모드에서 재활성**한다(14일 학습창의 KPI 적용은 폐기 — 사용자
> 생산 패턴을 학습창이 다 못 덮고, 당일 실측 파티션이 TEEP 와 정합되기 때문). 대신 자동 판정의 오심은
> **정지 이벤트 로그의 '비생산↔비가동 보내기'**(`POST /api/oee/downtime/reclassify`, `classifySource='manual'`,
> `reasonCode='non_production'`)로 행 단위 확정한다 — 수동 확정은 자동보다 우선(양방향), 기존 고장/유지보수
> 체크(수동 분류)도 '비가동 확정' 오버라이드로 작동해 호환된다. 확정·승격된 비생산은 벽시계 A 분모에서도
> 빠진다(집계 2-패스: 분류 확정 후 생산가능 창 산출). 미계측(§3.4) 카빙이 선행되므로 수신 공백은 비생산으로
> 흡수되지 않는다. 부수: '생산 요일(휴무)' 기능 제거 — 쉬는 날은 사이클이 없어 이 규칙이 자동 흡수.
> 정본 경위 = doc/OEE_DOWNTIME_CONSISTENCY_DIAGNOSIS_2026-07-06.md §9.
§3 의 비가동(①②③) 중 **"변화 없음" 정지가 14일 평균 CT 의 10배 이상**이면, 그건 짧은 고장·잼이 아니라 **애초에
생산하던 시간이 아니다**(무오더·교대·주말·조퇴 등) — 그 시간을 **비생산**으로 보고 가용성(A) **분모에서 제외**한다(`PlannedDownMs`).
이로써 시간기반 "계획시간 분모"를 따로 추정하지 않고도, 비생산 시간을 데이터에서 자동으로 떼어낼 수 있다.

- **3단 분류**: CT ≤ 1×평균 = 정상 / 1× < (무변화 정지) < 10×평균 = 비가동(A 깎임) / ≥ 10×평균 = **비생산**(분모 밖).
- **대상**: "변화 없음" 정지만 — 무사이클 갭(③)·미완료 멈춤(② `MT=null AND CT≥10×`). **완료된 느린 사이클(① `MT>thr` 이나 완료됨=움직였음)은 제외** — 다운타임 유지.
- **판정 = 순수 CT**: 고장비트(usertag)·이상감지(abnormal)·분류(equipment_fault) **신호와 완전 독립**(가드 없음, 사용자 결정 2026-06-23).
  진짜 장시간 고장도 ≥10× 면 비생산으로 빠진다 — 가용성은 "CT 흐름 연속성" 지표이고, 고장은 다운타임 목록·이상감지·MTBF 가 별도 레이어로 잡는다(doc/21 §4).
- **임계**: 무변화 갭은 flow별 `10 × 14일평균CT`(라인=대표 평균). [OeeMath.IsLongStopNonProduction](../DSPilot/Services/OeeMath.cs) 단일 판정, 배수 `NonProductionCtMultiplier=10`.
- **자동/수동 토글** (`OeeManualSettings.PlannedStopsAuto`):
  - **자동(기본)**: 위 10× 규칙. 시각대 윈도 없음(지속시간만으로 판정). "2일차부터"는 14일 평균 baseline 이 있어야 작동하므로 자연 충족.
  - **수동**: 사용자가 24시간 연표로 직접 그린 비생산 시각대(`PlannedStops`)만 적용(시작 시각이 윈도에 든 사이클 전부 제외). **수동 적용 시 자동 OFF**, '자동 계산' 재선택 시 ON.
  - 결정: [OeeController.ResolvePlannedWindows](../DSPilot/Controllers/OeeController.cs) → `ComputeCycleAggregateAsync(applyLongStop)`. API `GET/PUT /api/oee/planned-stops`, `POST /api/oee/planned-stops/auto`.
- **dedup**: 비생산으로 뺀 무변화 구간도 §3.1 차집합 대상(이중계상 방지)은 동일. 비생산은 onset/repair(MTBF/MTTR)에 미반영.

### 3.4 미계측(수신 공백) 분리 — 통신 헬스 심박 (2026-07-04 추가)
**문제**: plcTagLog 는 태그 *변화* 시에만 기록되는 change-log 라, "기계가 멈춤"과 "수신이 끊김"(VPN/현장망 단절,
에이전트 다운, DSPilot 다운)이 사후에 구분되지 않는다. 수신 공백을 §3.3 규칙이 무변화 정지로 읽으면 통째로
비생산이 되어 **가용성이 거짓으로 좋아진다**(17시간 단절 → A=100% 사례, 2026-07-04). 반대로 비가동으로 읽으면
가짜 고장이 폭증한다. **모르는 시간은 어느 쪽도 아니다.**

- **소스(SSOT)**: [OeeCommHealthService](../DSPilot/Services/OeeCommHealthService.cs) 가 60초 심박으로 PLC 연결
  상태(에이전트 보고 `PlcConnectionStatusTracker` ▸ 직접 TCP 핑 `PlcPingService` 폴백)를 oee.db
  `oeeCommHealthLog(sampledAt, plcOk)` 에 영속. oee.db 인 이유 = plc.db 전체초기화에도 계측 이력 보존.
- **판정**: 심박 1개(plcOk=1)가 `[t, t+150s)` 를 계측으로 보증. 범위 중 보증되지 않은 잔여(심박 없음=앱 다운,
  또는 plcOk=0=PLC 미연결)가 **미계측**. 3분 미만 조각은 보고 안 함(재시작/지터 허용 — 보수).
- **소급 금지**: 심박 최초 행(epoch) 이전 기간에는 미계측을 주장하지 않는다 — 판정 근거가 없으므로 기존 동작 유지.
  판정 방향은 전부 보수적(불확실 = 미계측 아님): 핑 대상 미설정(시뮬레이션)=정상 취급.
- **집계 반영**(`ComputeCycleAggregateAsync`): 미계측 구간을 무사이클 갭·미완료 멈춤 CT 에서 **차집합 후** §3/§3.3
  판정 — 가동/비가동/비생산 어디에도 안 들어가고 `UnmeasuredMs` 로만 보고(A 분자·분모 밖). 10× 판정도 계측된
  잔여 길이 기준(모르는 시간이 임계를 채워 정지를 비생산으로 승격시키지 않게). 전 구간 미계측인 행은 onset/repair 미계상.
- **표시 정책(사용자 결정 2026-07-04)**: 화면에서는 미계측을 **비생산에 합쳐** 보여준다 — 추이 남색(합산, 기본 표시로
  전환), 타임라인/날짜별 패턴/TEEP 막대/배지 모두 단일 '비생산' 카테고리. **단 내부는 분리 유지**: DTO 별도 필드
  (`UnmeasuredMs`/`UnmeasuredWindows`/`CurrentlyUnmeasured` — 진단·후속 소비자용 보존), KPI 카빙(⓪ 미계측 →
  ① 비자동 정지 → ② 비생산 → ③ 자동 잔여, BuildDailySlot), **§3.5 학습 차집합(14일 이동평균에 절대 미유입)**.
- **한계(정직, Phase 1 과제)**:
  - teep/matrix 셀 단위는 Phase 0 미반영(TEEP 합계·시간분해 막대는 반영).
  - 심박은 **라인 단일 비트**(하나라도 끊김=down) — 멀티 PLC 부분 단절 시 건강한 flow 의 시간까지 미계측 처리되고,
    그 flow 가 그 동안 기록한 정지는 미계측에 흡수되어 A 가 상방 왜곡될 수 있다(정상 사이클은 카빙하지 않으므로
    가동은 유지 — 비대칭). 단일 PLC 사이트(현 배포)는 해당 없음. → Phase 1: 어댑터→flow 매핑 per-flow 심박.
  - 정상 사이클(가동 ΣCT·runFloor)은 미계측과 차집합하지 않는다 — 미계측 창 가장자리에 걸친 사이클은
    '가동+미계측' 양쪽에 계상될 수 있다(사이클 1개 길이 이하, TEEP 잔여 클램프로 흡수).
  - PlcConnectionStatusTracker 의 어댑터 엔트리는 TTL 이 없어, 제거/개명된 어댑터의 stale disconnected 보고가
    재시작 전까지 plcOk=0 을 유발할 수 있다(수정 경로: Hub Closed 시 ClearAll 뿐). → Phase 1: 신선도 게이트.
  - 심박 시작(단절 후 최초 5.5분 = 커버 150s+보고하한 180s)까지는 기존 로직(비가동)으로 잡히다가 다음 조회에서
    미계측으로 자가 수정된다(보수 — 짧은 단절을 미계측으로 과잉 주장하지 않는 대가).

### 3.5 비생산 시간대 학습기 — 일별 샘플 투표제 (2026-07-04 추가, Phase 1 참고 표시 전용)
**목표 모델(오너 사양)**: "어제~14일 전, 하루마다 그날의 24시간 비생산 영역 샘플 1장 → 이동평균으로 오늘의
예상 비생산 시간대를 연산". 슬롯별 값 = 비생산이었던 활동일의 비율(0~1), 컷 이상만 창으로 승격.
**현 단계는 섀도(참고 표시 전용)** — KPI 판정은 여전히 §3.3 의 10× 지속시간 규칙. 창 품질이 검증되면
Phase 2 에서 이 창이 판정자(§3.3 은 폴백)로 승격된다.

- **구모델 폐기 사유**: "14일 통틀어 1건이라도 있으면 창"(union) + 시작 hour 1칸만 마킹 + 라인 union —
  단발 정지 하나가 시간대를 영구 오염(6/30 하루가 08-12시 창을 만든 실사례). 이동평균이 아니었음.
- **알고리즘**([OeeNonProdPatternService](../DSPilot/Services/OeeNonProdPatternService.cs) +
  `OeeMath.SlotVotesFromMinuteWindows`/`BuildNonProdPatternWindows` 순수함수):
  ① 기간 = 어제 로컬 자정까지 LookbackDays(14, 당일 제외).
  ② 학습 재료 = 장시간(≥10×평균CT) 정지 구간 — dspFlowHistory 미완료 유휴 사이클(flow별 임계) ∪
    oeeDowntimeEvent nocycle 이벤트(라인 대표 임계) — 라이브 §3.3 과 동일 문턱. **미계측(§3.4) 구간은 차집합**
    (블랙아웃이 패턴을 학습시키지 않게 — 순환 오염 차단).
  ③ 활동일(사이클 ≥1 인 로컬 날짜)마다 그날 구간을 30분 슬롯에 페인팅(슬롯 절반 이상 커버만 투표) = 하루 1표.
  ④ 승격 = 투표 ≥ PromoteRatio(0.6) × 활동일 수. 활동일 < MinActiveDays(3) 면 창 미성립(가짜 창 금지).
- **파라미터**(appsettings): `Oee:NonProdPattern:{SlotMinutes=30, PromoteRatio=0.6, MinActiveDays=3, LookbackDays=14}`.
- **표시**: uptime-oee 비생산 타임라인에 "예상 비생산 (14일 학습 · 미적용)" 청록 점선 윤곽(실제 제외분 진한 블록과
  겹쳐 비교 — 섀도 검증), 대시보드 '비생산 시간 중' 배지(loadNonprod)도 이 창 소비. API = GET planned-stops/auto-pattern
  (ActiveDays/PromoteRatio 필드 추가), 라인 레벨 24h 캐시(AutoPatternCache, 자동 전환 시 즉시 재계산).
- **한계(정직)**: 요일 무구분(주말 무가동 날은 활동일에서 제외 — 주말 자체를 창으로 배우지 않음, Phase 2 정책 결정 필요).
  무가동 날의 nocycle 이벤트는 학습 재료엔 들어가나 그날이 비활동일이면 표가 없음(창은 활동일 시간표만 반영).

---

## 4. 지표 공식 — `OeeMath` 순수함수 단일 소스

표기: `N` = 기간 내 정상(비가동 아님) 사이클 수, `Σ실측CT` = 정상 사이클 CT 합, `Σ비가동CT` = §3 비가동 CT 합(dedup 후).

| 지표 | 공식 | 신규 `OeeMath` 함수 |
|---|---|---|
| **가용성 A** | `Σ실측CT / (Σ실측CT + Σ비가동CT)` | `ComputeCycleAvailability(normalCtSum, idleCtSum)` |
| **성능 P** | `(N × CT이상치) / Σ실측CT`, min 1.0 캡 | `ComputeCyclePerformance(n, ctThreshold, normalCtSum)` |
| **품질 Q** | `양품수 / 총생산수` (기본 100% 가정) | 기존 `ResolveQuality` 재사용 (doc/21 §12.2·§12.4) |
| **OEE** | `A × P × Q` | 기존 `ComputeOee` 재사용 |
| **MTTR** | `mean(고장 onset → Call going 변화)` | `ComputeMttr(onsets[], recoveries[])` |
| **MTBF** | `mean(연속 고장 onset 간격)` | `ComputeMtbf2(onsets[])` (정의 변경 — §5) |

**검산 (P5 §⑥ 예시, STN3)**: CT이상치=30s, N=90, Σ실측CT=2970s, Σ비가동CT=1200s
- A = 2970/(2970+1200) = **71.2%**
- P = (90×30)/2970 = 2700/2970 = **90.9%**
- Q = **100%** (가정)
- OEE = 0.712 × 0.909 × 1.0 = **64.7%** ✓

---

## 5. 신뢰성 동반지표 — MTTR · MTBF (OEE 곱셈식과 독립)

P5 §④: 두 지표는 **고장 onset 시각**에서 파생한다. onset = **사이클 시작 + CT이상치** (사이클 시작점 ✗ — 표준 시간만큼
경과해 초과한 인식 시점).

- **MTTR (평균 수리시간)**: `고장 onset → 해당 flow Call 의 going 변화 시점` 구간들의 평균.
  - going 회복 신호 = 정지에서 빠져나오는 첫 going 전환(메모리 flow 상태 모델의 going-any OR-스캔 정신과 일치 —
    [doc/21] 와 별개로 `StateReconcile`/모니터링 going 소스 확인은 구현 시 잔여 결정).
- **MTBF (평균 고장간격)**: `고장 onset → 다음 비가동 CT 의 고장 onset` 갭들의 평균.
  - ⚠️ doc/21 §3·§12.4 의 `MTBF = Σruntime / 고장건수` 를 **onset 간격 평균으로 대체**한다(P5 정의). DTO 는 같은 필드 재사용.
- **무고장 배지 유지**: 고장 onset 0건 → `max(n,1)` 류 가짜 수치 금지, "🟢 무고장" 배지(doc/21 §12.4 B 계승).
- **MTTR 부활**: doc/21 §12.4 B 가 UI 에서 제거했던 MTTR 카드를 KPI 6개로 복원(`Mttr/MttrNote` DTO 잔존분 재활성).

---

## 6. 정직성 경계 — 반전의 대가를 숨기지 않는다

1. **성능 P 의 의미 변화 (14일 평균의 대가)** — 정상상태에서 `N×평균 ≈ Σ실측CT` → **P ≈ 100% 에 수렴**. 이는 "최속 대비
   손실" 이 아니라 "**14일 추세 대비 당기 저하**" 다(저하 감지기). 카드 노트를 이 해석으로 명시 — 90%대 P 를 "거의 완벽"
   으로 오독하지 않게. (doc/21 의 p10 "순환정의" 경고를 인지하고 내린 결정.)
2. **이중계상(dedup)** — §3.1 의 구간 차집합을 반드시 적용. 누락 시 무사이클 정지와 긴 CT 가 겹쳐 가용성 분모가 부풀고
   A 가 낮아진다.
3. **MT=null 정지 사각** — Tail 미발화 정지는 MT 단독 판정(P5 문구)으로 안 잡힘 → §3 ②(CT 폭주) 보강이 필수.
4. **완전정지 사각** — 순수 사이클기반은 사이클이 안 찍히면 invisible. §3 ③(무사이클 합산)으로 보완하기로 결정.
5. **산출 불가 = 정직 표기** — CT이상치 표본 부족/사이클 0 → `—` + 사유. 가짜 % 금지(doc/21 §10 계승).

---

## 7. DTO · API 변경

- **`OeeSummaryDto`** ([OeeModels.cs](../DSPilot/Models/Oee/OeeModels.cs)) 신규 필드:
  `NormalCtMs`(Σ실측CT), `IdleCtMs`(Σ비가동CT, dedup 후), `NormalCycleCount`(N), `CtThresholdMs`(14일 평균),
  `Mttr`/`MttrNote`(재활성), `AvailabilitySource` 값에 `'cycle'` 추가.
- **`GET /api/oee/summary`** — `availabilitySource='cycle'` 가 1차. **계획시간 폴백 체인(doc/21 §12.4 A)은 보존**
  (사용자 결정 — 미폐기). 비교/전환용으로 병기하거나 설정으로 선택.
- **정지 이벤트 시스템 존치** — `oeeDowntimeEvent`/로그/분류/단서(doc/21 §2.1·§5·§12.4 C)는 ① 무사이클 정지 합산(§3 ③)의
  소스, ② 비가동 CT 의 **원인 라벨링** 레이어로 계속 사용. 삭제하지 않음.

---

## 8. 단계별 구현
- **Phase 1 (백엔드 산출 경로)**: `OeeCtStatsService` 14일 윈도우/평균 + `OeeMath.{ComputeCycleAvailability,
  ComputeCyclePerformance}` + `BuildSummaryAsync` 에 `availabilitySource='cycle'` 추가(기존 경로 보존). DTO 필드.
  순수함수 테스트 추가(P5 §⑥ 검산 포함).
- **Phase 2 (비가동 판정 + dedup)**: §3 ①②③ 판정 + `Intervals` 차집합 dedup. `OeeMath.ClassifyCycle`.
- **Phase 3 (신뢰성)**: onset(사이클 시작 + CT이상치) 도출 + `ComputeMttr`/`ComputeMtbf2`(going 회복 소스 확정).
- **Phase 4 (UI)**: OEE 종합 탭 — 6 KPI, "계획시간 분모" 섹션 → **사이클 분해 시각화**(P5 §⑤ SVG: 정상/비가동 CT
  타임라인 + onset·going·MTTR/MTBF 마커), 표준CT 라벨 재서술(14일 평균·판정 겸용), 계획시간 체인은 접이식 강등.

---

## 9. doc/21 §12.4 와의 관계 — 반전표

| 항목 | doc/21 §12.4 (시간기반) | 본 스펙 §x (사이클기반) | 처리 |
|---|---|---|---|
| 가용성 분모 | 계획시간 폴백체인(시프트▸자동▸달력) | Σ실측CT / Σ전체CT (+무사이클 합산) | **1차 전환**, 폴백체인 보존(병기) |
| CT이상치 | p10 최속(평균=순환정의로 폐기) | 14일 평균 (판정·성능 공용) | **반전** (§2.3 대가 명시) |
| MTBF 정의 | Σruntime / 고장건수 | 연속 onset 간격 평균 | **대체** |
| MTTR | UI 제거(DTO 잔존) | 부활 (KPI 6개) | **복원** |
| 정지 이벤트/분류/단서 | 감지·분류·단서 3계층 | 동일 — 무사이클 합산 소스 + 원인 라벨링 | **존치** |
| 무고장 배지 / Q 가정 정책 | 채택 | 동일 | **계승** |

### 관련 파일
`Services/OeeCtStatsService.cs`(CT이상치 14일 윈도우 — 변경 핵심), `Services/CycleDerivation.cs`(MT/WT/CT 분해 정의),
`Services/OeeMath.cs`(신규 순수함수 + 테스트), `Controllers/OeeController.cs`(`BuildSummaryAsync`/`Intervals` dedup/
`ResolveAvailabilityAsync` cycle 경로), `Models/Oee/OeeModels.cs`(DTO 필드), `Adapters/DspRepositoryAdapter.cs`
(`dspFlowHistory` mt/wt/ct), `wwwroot/app/uptime.html`(OEE 종합 탭 UI), `P5_UPTIME_SUMMARY.html`(설계 원본 v4).
