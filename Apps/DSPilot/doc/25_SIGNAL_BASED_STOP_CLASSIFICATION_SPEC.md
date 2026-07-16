# 25. 신호 기반 정지 분류 + flow별 생산가능 분모 스펙

> 상태: **구현 완료(2026-07-16, P0~P2 일괄)** — 빌드·단위테스트(OeeMathTests ClassifyStopWindow 7종 포함)
> 통과, 로컬 스모크(summary/downtime/daily/actual/teep) 통과. **kit_test 실서버 §5 회귀는 배포 후 확인 대기.**
> 구현 위치: 분류 SSOT = [OeeMath.ClassifyStopWindow](../DSPilot/Services/OeeMath.cs), 집계 =
> [OeeControllerBase.ComputeCycleAggregateCoreAsync](../DSPilot/Controllers/OeeControllerBase.cs),
> 자가치유 = InvalidateStaleNonProdDetectionsAsync + [NonProdWriteQueueService.Batch](../DSPilot/Services/NonProdWriteQueueService.cs),
> 전이 로그 = OeeDowntimeController.LogClassifyTransitions, 토글 = OeeManualSettings.SignalClassifyEnabled.
>
> 2026-07-16 kit_test 실증 테스트(의도적 고장 3회)에서 확인된
> 분류 불일치 4건을 근본 수정하기 위한 스펙. 산출 공식의 정본은 [doc/22](22_OEE_CALCULATION_SPEC.md),
> 무사이클 갭 감지는 [doc/23](23_GAP_BASED_DOWNTIME_SPEC.md) 이 정본이며, 본 문서는 **정지의
> 분류(고장/대기/비생산/공백)와 가용성 분모의 스코프만** 다룬다.
>
> ⚠️ 본 스펙은 doc/22 §3.3 의 "판정 = 순수 CT, 신호와 완전 독립(2026-06-23)" 결정을 **의도적으로
> 반전**한다 — §8 반전표 참조. 반전 근거는 §0 실증, 대가와 방어는 §6 정직성 경계에 명시한다.

## 0. 배경 — 2026-07-16 kit_test 실증에서 확인된 문제 4건

시나리오: 6-flow 라인(평균 CT ≈ 42~48s, 비가동 배수 2.5×, 비생산 배수 10× ≈ 8분)에서
`1st_usb.RET`(이송 소속 디바이스)를 의도적으로 고장 — 5분·5.5분·41분 정지 3회.

| # | 관찰 | 원인(코드 확정) |
|---|---|---|
| ① | 41분 정지: 이송이 **비생산**으로 분류. abnormal(동작 지연, 09:10:51 이송 귀속)·usertag(해지시간초과이상 09:11:00)가 있었는데도 | §3.3 판정이 신호 독립(순수 CT). 걸린 작업은 abandon(Going 고정 해제)으로 완료 기록 없이 소멸 → mt 증거도 부재([이송 재가동 첫 사이클 ct=42.6s 실측](../DSPilot/Controllers/DashboardController.cs#L217)) |
| ② | 5분 정지 2회: 형제 5개 flow 가 **고장 12건**으로 계상(MTBF 왜곡) | 무신호 무사이클 갭은 전부 비가동·고장 기본 |
| ③ | flow별 화면과 전체 화면의 분류 불일치(전체에선 비생산 소멸) | 라인 집계가 전 flow 감지정지 사이클을 flow 구분 없이 갭에서 차감([OeeControllerBase.cs:1104](../DSPilot/Controllers/OeeControllerBase.cs#L1104)) + 생산가능 창이 라인 공통 단일 창 × N([:1224](../DSPilot/Controllers/OeeControllerBase.cs#L1224)) |
| ④ | 판정 뒤집힘 후 '비생산 시간대' 카드에 stale 자동 비생산 잔존 | `oeeNonProdDetectionLog` 가 onset 키 UPSERT 영구 보존 — 자가치유 없음([:1174](../DSPilot/Controllers/OeeControllerBase.cs#L1174), 청소는 수동 재분류 시에만 [OeeDowntimeController.cs:123](../DSPilot/Controllers/OeeDowntimeController.cs#L123)) |

부수 확인(조치 불요): 엔진 ct 는 정지 갭을 포함하지 않음(재가동 첫 사이클 ct 정상 실측) —
"장사이클의 정상 편입으로 P 오염" 우려는 이 엔진에선 발생하지 않는다.

## 1. 결정 요약 — 분류표 (SSOT)

정지 창(무사이클 갭 이벤트 또는 감지정지 사이클) 하나에 대해, **flow 별로** 아래 표를 적용한다.
"신호" 의 정의·매칭은 §2, "기준" = 비생산 판정 경계(`CT이상치 × 비생산배수`, flow별).

| 조건 | 지속 | 분류 | A 반영 | 고장 건수 | 표기 |
|---|---|---|---|---|---|
| **자기 flow 귀속 신호 있음** (유발자) | 무관 | **고장** | 손실 | **1** | 근거 신호 표기 |
| 자기 신호 없음 + **같은 창에 라인 내 유발 flow 존재** (형제) | < 기준 | **공백** | 손실 | 0 | '대기(고장 여파)' |
| 〃 | ≥ 기준 | **비생산** | 분모 밖 | 0 | '대기(고장 여파)' 라벨 |
| **라인 전체 무신호** (현행 유지) | 비가동배수×~기준 | 비가동(고장 기본) | 손실 | 1 | — |
| 〃 | ≥ 기준 | 비생산 | 분모 밖 | 0 | — |

우선순위(위가 이김):
1. **수동 재분류**(`classifySource='manual'`) — 모든 자동 판정에 우선(현행 유지, 양방향).
2. **완료된 MT 과주행 사이클**(doc/22 §3 ①, 움직인 증거) — 고장 유지(비생산 승격 제외 현행 유지).
   mt 는 그 자체가 "자기 flow 신호"와 동급의 고장 증거다.
3. 위 분류표.

판정 안정성: 신호는 발생 순간부터 **영구 사실**이므로 유발 flow 의 '고장'은 신호 발생 즉시 확정·불변.
형제 flow 는 공백 → (기준 통과 시) 비생산·대기 의 **단방향 1회 전이**만 가진다. 2026-07-16 에 관찰된
"비생산↔비가동 왕복"의 구조적 원인(시간 경과·데이터 도착에 따른 재판정 요동)이 제거된다.

## 2. 신호 판별 — 정의 · 매칭 · 게이트

### 2.1 신호 소스 (`userTagAlertLog`, [AttachCluesAsync](../DSPilot/Controllers/OeeControllerBase.cs#L94) 와 동일 규약)
- **abnormal** (flow 스코프): `valueType='Abnormal' AND matchOp='AbnormalDetect'`.
  flow = `tagAddress` 첫 " / " 세그먼트. **유발 flow 특정의 유일한 소스.**
- **usertag** (라인 스코프): `logLevel='Error'` 일반 행. flowName 컬럼이 없어 flow 특정 불가 —
  "라인 내 고장 신호 존재" 판정에만 사용한다.
- 단선/오감지 등 메모리 전용 이상은 이력이 없으므로 판별 소스가 아니다(영속 kind 만).

### 2.2 매칭 창
정지 `[start, end]` 에 대해 신호 발생 시각이 `[start − lookback, end]` 에 들면 매칭.
`lookback = max(그 flow 의 비가동 판정 경계(thr×비가동배수), 60s)` — 무사이클 갭 이벤트가
정지 시작보다 늦게 열리는 만큼(doc/23 감지 체인), 정지 직전·직후에 찍힌 신호를 흡수한다.
신호는 점 이벤트(clear 미기록)이므로 구간 겹침이 아닌 시각 포함으로 판정한다.

### 2.3 유발 flow 특정 규칙
- 같은 창의 abnormal 이 귀속된 flow = 유발자(복수 가능).
- **usertag 만 있고 abnormal 이 없으면 유발자 특정 불가** → 그 창의 정지 flow 전부 고장 유지
  (대기 강등 없음 — 보수적. 형제 강등은 유발자가 특정된 경우에만 허용).

### 2.4 커버리지 게이트 (필수 안전장치)
조회 기간 ∪ 직전 14일에 `userTagAlertLog` 행(abnormal+usertag 합계)이 **0건이면 본 스펙의
신호 규칙 전체를 비활성**하고 §3.3 순수 CT 규칙으로 폴백한다 — 감지 인프라가 없거나 죽은
사이트에서 "신호 없음 = 비생산" 이 가용성을 부풀리는 것을 차단(§6 ①). 설정 토글
`OeeManualSettings.SignalClassifyEnabled`(기본 true)와 AND 조건.

## 3. flow별 생산가능 분모 — "단일 창 × N" 반전

### 3.1 현행 → 신규
- 현행: `AvailableWallMs = (기간 − 비생산(라인 공통) − 미계측) × flow수`
  ([:1224](../DSPilot/Controllers/OeeControllerBase.cs#L1224) `availableSingleMs * thrCount`).
- **신규**: `AvailableWallMs = Σ_flow (기간 − 미계측 − 비생산_flow)`.
  `비생산_flow = 지정 시각대 창(라인 공통 유지) ∪ 그 flow 의 승격 비생산(일반 + 대기)`.
  미계측은 통신 단위이므로 **라인 공통 유지**.
- 가동/유지보수/고장 벽시계는 이미 flow별 합산(패스 2, [:1193](../DSPilot/Controllers/OeeControllerBase.cs#L1193)) —
  비생산만 라인 공통이던 비대칭을 해소한다. 전체 화면 = flow별 분류의 합으로 표시(문제 ③ 해소).

### 3.2 무사이클 갭 차감의 flow 스코프화 (문제 ③ 근본 수정)
라인 조회에서 `Intervals.Subtract(nocycle, cycleIdleIntervals)` 의 `cycleIdleIntervals` 를
flow별 dict 로 분리 — **그 flow 의 갭은 그 flow 의 감지정지 사이클만** 차감한다. 타 flow 의
사이클이 형제 flow 의 갭(비생산 후보)을 지우는 현상 제거. doc/22 §3.1 dedup 의 취지(같은
flow 의 이중계상 방지)는 유지된다.

### 3.3 표시
- 정산 바/도넛: 비생산 세그먼트를 **일반 비생산 / 대기(고장 여파)** 로 분화(색·라벨 분리).
  공백 툴팁에 대기 성분(기준 미만 형제 정지) 표기 — 미세 슬랙 진단 지표의 희석 방어.
- 정지 이벤트 로그: 구분 값에 '대기' 추가(비가동/비생산 탭 유지, 대기는 비생산 탭에 라벨).
  각 행에 판정 근거(매칭된 신호명) 표기 — 단서 조인 재사용, 표시 전용에서 판정 근거로 승격.
- **같은 정지 이중 표시 흡수(2026-07-16 추가, 사용자 확인)**: 하나의 정지가 무가동 이벤트(DB)와
  ct 폭주 사이클(합성 이상치초과)에 동시에 잡히면 목록엔 **무가동 DB 행 하나만**(같은 flow, 겹침 ≥60%
  흡수) — 감지 칩을 '무가동+이상치초과' 로 병기해 이중 감지 사실은 보존. KPI 는 집계 차감(§22 3.1)으로
  원래 1회 계상이므로 표시 전용 정리(로그 행 수 ↔ 도넛 건수 정합 개선).
- **합성 행 고장 체크 해제(2026-07-16 추가)**: 이상치초과 합성 행도 고장↔유지보수 체크 가능 —
  set-fault API 가 reclassify 와 동일하게 실제 이벤트 행으로 materialize 후 manual 분류(의도된
  정지가 이상치로 잡힌 경우의 교정 경로).

## 4. 파생 정합

### 4.1 감지 로그 자가치유 (문제 ④)
`oeeNonProdDetectionLog` 에 `invalidatedAt` 컬럼 추가. 재계산에서 어떤 구간이 비생산이
아닌 것으로 확정되면(고장/대기-공백 재분류) 겹치는 자동 감지 행을 **invalidate 마킹**
(삭제 대신 — doc/22 §3.3 감사·재현성 규약과 절충: 행은 보존하되 표시에서 제외).
[GetNonProdIntervalsFromLogAsync](../DSPilot/Repositories/OeeRepositoryAdapter.cs#L782) 는
invalidated 행 제외. 쓰기는 기존 [NonProdWriteQueueService](../DSPilot/Services/NonProdWriteQueueService.cs)
에 invalidate 작업 추가(읽기 경로 비차단 유지).

### 4.2 자동 패턴 학습 오염 방지
'대기' 라벨 구간은 '비생산 시간대' 카드(planned-stops/actual)의 표시·일별 접기에서 **제외**
(집계 구간은 WaitScoped 차집합, 감지 로그는 `detectionReason='wait-starve'` SQL 필터) — 고장 여파가
"그 시각대는 원래 비생산" 으로 보이지 않게 한다. ⚠한계: 섀도 학습기
[OeeNonProdPatternService](../DSPilot/Services/OeeNonProdPatternService.cs) 는 dspFlowHistory 를 직접
읽는 순수 CT 판정이라 신호 미반영 — 참고 표시 전용(KPI 미적용)이므로 수용, 승격 시 재설계.

### 4.3 판정 전이 로그 (원인 추적 계측)
정지 이벤트/합성 행의 구분이 직전 계산과 달라질 때 `ILogger` 정보 로그 1줄:
`[OEE-CLASSIFY] flow=X ev=Id 구분 A→B 근거=신호명|기준통과|수동`. 스키마 추가 없음(필요 시
테이블 승격). 2026-07-16 유형의 "언제 왜 바뀌었나" 를 다음 테스트에서 즉시 확정하기 위함.

## 5. 검증 계획 — kit_test 2026-07-16 회귀 기준표

구현 후 같은 날 데이터 재조회 시 기대값(디버그 58494, Playwright 캡처 + API 대조).
실측 신호: 08:17:09 abnormal(이송 RET) / 08:27:46 은 **usertag 만**(2nd_usb.RET_센서단선이상, abnormal 없음)
/ 09:10:51 abnormal(이송 RET) + 09:11:00 usertag ×2.

| 정지 | 현행 | 기대(본 스펙) |
|---|---|---|
| 08:16 (~5분, abnormal=이송 08:17:09) | 고장 6건 | **이송 고장 1건**, 형제 5건 → 공백·대기(건수 0) |
| 08:27 (~5.5분, usertag 만 — 유발 특정 불가 §2.3) | 고장 6건 | 고장 6건 **유지**(보수적 — 형제 강등은 유발자 특정 시에만) |
| 09:10 (41분, abnormal=이송 09:10:51) | 비생산 6건·고장 0 | **이송 고장 1건(41분)**, 형제 5건 → 비생산·대기(41분) |
| 전체 A | 96~97.8% (요동) | 이송 몫 손실만 반영 + 대기 세그먼트 가시화, 판정 불변(왕복 없음) |
| '비생산 시간대' 카드 | stale 40분 잔존 | 재분류 시 자가치유(invalidate)로 소거, 대기는 애초 미표시(§4.2) |

부수 확인: failureCount(라인)가 병합 세그먼트 수 → **flow별 이벤트 수(Σ)** 로 바뀐다(§3.2) — 정지 로그
행 수와 정합되는 방향이나, 무신호 라인 정지에서는 건수가 flow 수만큼 커진다(릴리스 노트 명시).

추가 검증: flow별 화면 = 전체 화면 분류 합 일치(문제 ③), 브리핑 메일·Excel·TEEP·사전계산
push(`X-Dsp-Precomputed-Age-Ms`)가 동일 분모를 쓰는지 소비처 전수 대조. `OeeMath` 신규
순수함수는 [DSPilot.Tests/OeeMathTests](../DSPilot.Tests/OeeMathTests.cs) 에 단위 테스트 추가.

## 6. 정직성 경계 (doc/21 §10 계승)

1. **부정 판정 리스크** — "신호 없음 = 비생산·대기" 는 감지가 살아있을 때만 참. 무장 사망
   이력(캘리브 게이트 stale, 블랙아웃 미발화)이 있으므로 §2.4 커버리지 게이트를 **필수**로
   하고, 수동 재분류 우선을 유지한다. 잔여 리스크(기간 내 신호는 있으나 특정 flow 무장만
   죽은 경우)는 수용 — 카드 노트에 명시.
2. **A 소급 상승** — 판정은 조회 시 재계산이므로 과거 수치가 배포 즉시 달라진다(사용자 결정:
   소급 통일). 릴리스 노트 + '정지·비생산 판정 기준' 카드에 규칙 변경 표기.
3. **라인 정지 과소반영** — 형제 시간이 분모 밖으로 가면 전체 A 에서 라인 정지가 유발 flow
   몫(1/N)만 보인다. '대기' 세그먼트 가시화(§3.3)로 시야 유지 — 숫자는 공정하게, 사건은
   보이게.

## 7. 반전표

| 결정 | 종전(정본) | 본 스펙 | 근거 |
|---|---|---|---|
| 비생산 판정 ↔ 신호 | 완전 독립, 가드 없음 (doc/22 §3.3, 2026-06-23) | 신호 우선(유발=고장 확정) | §0 ① — 고장이 비생산으로 유출, mt 증거는 abandon 으로 소실 가능 |
| 생산가능 창 | 라인 공통 단일 창 × N | flow별 창 합산 | §0 ③ — flow/전체 분류 불일치 |
| 형제 flow 무신호 정지 | 고장(건수 포함) | 대기: 공백/비생산(건수 제외) | §0 ② — MTBF 왜곡(1사고=N건) |
| 감지 로그 행 | 영구 스냅샷(감사) | invalidate 마킹 자가치유 | §0 ④ — 표시 소스로 쓰이는 한 정합 우선, 행 보존으로 감사 절충 |

## 8. 구현 단계

- **P0** (독립·즉효): §4.3 판정 전이 로그 + §4.1 감지 로그 자가치유.
- **P1** (집계 내부): §2 신호 판별 + §1 분류표(형제 대기 강등 포함). `OeeMath` 순수함수 + 테스트.
- **P2** (구조·광역): §3 flow별 분모 + 무사이클 flow 스코프 차감 + UI 세그먼트/로그 구분 —
  소비처 전수 재검증(§5) 포함. P0/P1 은 P2 없이도 배포 가능(형제 강등은 flow별 창이 없어도
  기존 라인 창에 합산 가능하나, 전체 화면 분화 표시는 P2 에서 완성).
