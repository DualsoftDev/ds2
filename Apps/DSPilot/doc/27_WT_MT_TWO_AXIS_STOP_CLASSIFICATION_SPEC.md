# 27. 정지·비생산·고장 판정의 WT/MT 2축 전환 (2026-09-09)

> **2026-09-11 doc/28 로 대체** — 정지(비가동) 밴드·정지 배수·신호 판별·초과분 적립이 폐기되고 두 규칙(고장=MT·비생산=WT)·사이클 행 단위 모델로 바뀌었다. 아래는 이력.
> 상태: **구현 완료(2026-09-09)** — 빌드·단위테스트(OeeMathTests 353건) 통과, Playwright 정적 모의 렌더 확인.
> 정본: 판정 순수함수 = [OeeMath](../DSPilot/Services/OeeMath.cs)(`ResolveWtStopBoundaryMs` / `ResolveWtNonProdBoundaryMs` /
> `ResolveWaitMs` / `ClassifyCycle` / `ResolveDowntimeAccrualMs` / `ClassifyStopWindow`), 집계 SQL = [OeeControllerBase.DtCondSql](../DSPilot/Controllers/OeeControllerBase.cs),
> 기준선 = [OeeCtStatsService.ComputeWtBaselineAsync](../DSPilot/Services/OeeCtStatsService.cs), 설정 = `OeeManualSettings.IdleWtMultiplier / NonProdWtMultiplier / FaultMtMultiplier`,
> UI = [uptime-oee.html](../DSPilot/wwwroot/app/uptime-oee.html) `#ct-mult-section`.
> 산출 공식(doc/22)·신호 분류(doc/25)·행 집합 모델(doc/26)은 그대로이며, 본 문서는 **정지/비생산/고장을 가르는 축과 경계**만 바꾼다.

## 0. 배경

종전 판정은 CT 축(사이클 길이 = 14일 평균 CT × 배수)이 정지와 비생산을, MT 축(중앙 MT × 배수)이 고장 유발자를 맡았다.
CT = MT + WT 라서 CT 축은 "설비가 늘어진 것"과 "사이클 사이에 서 있던 것"을 한 숫자로 봤고, 그 결과

- 정지 후 재개 사이클(mt 정상·wt 폭주)을 잡기 위해 2026-08-19 ct 절을 덧붙였고, MT 만 초과한 행의 적립을 "초과분만"으로 따로 예외 처리해야 했다(2026-08-24).
- 사용자가 "비가동 5×, 비생산 24.5×" 를 설정할 때 그 배수가 무엇의 배수인지(동작인지 대기인지) 화면에서 설명되지 않았다.
- 평균 CT 는 정지를 머금은 사이클에 끌려간다(kit 라인 실측: 평균 CT 1,000초 vs 중앙 CT 6.5초).

## 1. 결정 — 축마다 하나의 의미

사이클 = **동작(MT)** + **대기(WT)**. 판정 순서(위가 이김):

| 순서 | 조건 | 결과 | 비고 |
|---|---|---|---|
| ① | `mt > 중앙MT × 고장배수`(절대 하한 1s) 또는 mt NULL(tail 미완료) + 정지 경계 초과 | **비가동(고장)**, 유발자 | 길이 무관. **비생산으로 승격되지 않는다.** 같은 창의 다른 flow 는 여파(대기) |
| ② | 그 외 `COALESCE(wt, ct) > WT 정지 경계` | **비가동(정지)** 후보 → doc/25 신호 분류(자기 신호=고장 / 형제=대기 / 무신호=대기) | |
| ③ | ②에서 대기 ≥ WT 비생산 경계 | **비생산**(분모 밖). 형제면 '대기·비생산' | 비교값 = min(대기, 미계측 카빙 후 잔여) |
| ④ | 둘 다 미만 | 정상 | 느린 사이클은 성능 P 가 흡수 |

수동 재분류(비생산↔비가동 보내기)는 종전대로 모든 자동 판정에 우선한다.

### 1.1 경계 (SSOT = OeeMath)

```
WT 정지 경계   = max(중앙WT × 정지배수,   중앙CT × 1)                 ResolveWtStopBoundaryMs
WT 비생산 경계 = max(중앙WT × 비생산배수, 중앙CT × 10, 정지 경계)      ResolveWtNonProdBoundaryMs
MT 고장 경계   = max(중앙MT × 고장배수,   1,000ms)                    ResolveMtFaultBoundaryMs (기존)
대기(비교값)   = wt ?? ct                                            ResolveWaitMs  (SQL: COALESCE(wt, ct))
```

- **기준선은 중앙값**이다(MT 와 같은 이유 — 정지가 WT 에 그대로 실려 평균이 오염된다).
- **하한(사이클 수)**: 중앙 WT 가 극소인 flow(항상 소재가 대기 — kit Turn Zone 중앙 WT 0.6초)에서 배수만 곱하면 3초 대기가 정지, 18초 대기가 비생산이 된다. "한 사이클도 못 채운 대기는 정지가 아니다 / 10사이클 미만은 비생산이 아니다"를 하한으로 둔다. 현장 3000(#121·셔틀)에선 배수 경계가 항상 하한보다 커서 발동하지 않는다.
- 중앙 WT 0 은 정상 기준선이다(경계는 하한이 맡는다). 기준선 미보유 = 중앙 CT 를 낼 수 없는 flow.

### 1.2 폴백 — WT 기준선이 없는 flow

tail 이 정의되지 않은 flow 는 mt·wt 가 항상 NULL 이라 WT 기준선을 만들 수 없다. 이 flow 만 종전처럼 **평균 CT × 배수**를 경계로 쓴다.
비교값도 `COALESCE(wt, ct) = ct` 라 종전 규칙과 동일하다. 화면 칩은 이 flow 를 점선 테두리 + "CT n분 기준"으로 정직하게 표시한다.

### 1.3 적립 범위

- WT 초과 행 → **사이클 전체** 적립(종전 CT 초과와 동일 취급). 대기 성분만 적립하는 안은 A 분자·타임라인 구멍 처리가 커져 이번엔 채택하지 않았다(§5).
- MT 만 초과 행 → 평소 대비 **초과분만**(2026-08-24 규칙 유지).

### 1.4 '진행 중' 표시 기준

열린 사이클의 경과 = 동작 + 대기인데 어디까지가 동작인지 모른다. 평소 동작(중앙 MT)을 빼고 남은 만큼을 대기로 보아,
**표시 기준 = 중앙 MT + WT 정지 경계**. 정지 로그 문구에 두 성분을 그대로 적는다.

## 2. 기본값과 설정

| 설정 | 키 | 기본 | 범위 | 비고 |
|---|---|---|---|---|
| 정지(비가동) 배수 | `OeeManual.IdleWtMultiplier` | **5×WT** | 1.5~20 | 종전 2.5×CT 를 실측 WT 비중으로 환산하면 #121 5.8× / 셔틀 2.8× / kit 3.2× |
| 비생산 배수 | `OeeManual.NonProdWtMultiplier` | **30×WT** | 5~300 | 종전 15×CT → 17~35× 에 흩어짐. 단일 등가값 없음 |
| 고장 유발 배수 | `OeeManual.FaultMtMultiplier` | 2.5×MT | 1~10 | 변경 없음 |

- 구 키 `IdleCtMultiplier` / `NonProdCtMultiplier` 는 **이관하지 않는다** — 5×CT 와 5×WT 는 뜻이 달라 그대로 읽으면 경계가 옮겨간다. 새 키 미보유 = 기본값. 구 키는 ExtensionData 로 무해 보존.
  ⚠ 현장 3000 은 5×/24.5× 를 쓰고 있었으므로 배포 후 화면에서 재설정이 필요하다(칩의 flow별 환산 분·시간을 보고 맞춘다).
- API 경로 `GET/PUT /api/oee/ct-multipliers` 는 호환 유지. 응답 필드 `idleWtMultiplier / nonProdWtMultiplier / faultMtMultiplier` + flow별 `avgCtMs / medianMtMs / medianWtMs / medianCtMs` + 하한 상수(`stopFloorCtMultiples / nonProdFloorCtMultiples / faultFloorMs`).
  화면 환산식(uptime-workspace.js `cmBound`)은 서버 경계 함수와 같은 식이어야 한다.
- `planned-stops` 응답 `nonProdWtMultiplier / idleWtMultiplier`(구 `ctMultiplier / idleCtMultiplier`).

## 3. 소비처 전수 (CT×배수를 쓰던 곳 → WT 경계)

| 위치 | 변경 |
|---|---|
| `DtCondSql` | `ct > @Thr` → `COALESCE(wt, ct) > @WtThr`. MT 미보유 폴백 `@MtThr = @WtThr` |
| 집계 행 루프 | 적립 `ResolveDowntimeAccrualMs(ct, mt, wt, wtStop, mtB, mtMed)`, 분류 `ClassifyStopWindow(..., min(measured, wait), wtNonProd)` |
| 유발자 사전수집 | tail 미완료(mt NULL) 행 경계 = WT 정지 경계 |
| 신호 매칭 lookback | `max(60s, WT 정지 경계)` |
| 정지 로그 노트 | 축별 문구(동작 초과 → 고장 유발 · 대기 초과 · 미완료 정지 · 폴백은 "평균 CT" 라고 명시). `DowntimeCycles` 튜플에 `MtMs/WtMs/MtOverrun` 추가 |
| '진행 중' 합성 행 | §1.4 |
| 계측 품질 | `@WtThr` 바인딩, `ThresholdMs` = 정지 경계 |
| 비생산 패턴 학습기(참고 표시) | 문턱 = WT 비생산 경계, 비교값 `COALESCE(wt, ct)`(구 `mt IS NULL` 제약 제거) |
| 감지 로그 `oeeNonProdDetectionLog.ctThresholdMs` | 값 = 중앙 WT(폴백 평균 CT). 컬럼명은 호환 유지 |
| 집계 캐시 키 | v32 + WT 기준선 서명 |
| 삭제 | `OeeMath.ClassifyGap / GapClass / DowntimeGapMultiplier`(doc/26 이후 호출처 없음) |

## 4. UI (설비효율 ▸ 판정 기준 카드)

처음 보는 사용자가 "무엇의 몇 배인가"를 바로 읽을 수 있게 4단 구성:

1. **사이클 해부도** — `한 사이클 = [동작 MT] + [대기 WT]`, "동작이 길어지면 → 고장 · 대기가 길어지면 → 정지 → 더 길면 비생산".
2. **판정 순서 4단계** — ①동작 초과=고장(비생산 전환 없음) ②대기 초과=정지 ③더 길면 비생산 ④원인이 남이면 대기. 숫자는 슬라이더와 실시간 동기.
3. **축별 2레인** — 동작(MT) 레인: 정상 | 고장(유발자) + "비생산 전환 없음" 배지, 슬라이더 1개. 대기(WT) 레인: 정상 | 정지 | 비생산, 슬라이더 2개. 스케일이 다른 두 축을 한 막대에 그리지 않는다. 레인 머리의 ⓘ 에 기준선·하한 설명.
4. **flow 칩** — `#121 · 동작 2.3분 · 대기 1.7분 | 고장 > 5.6분 · 정지 > 8.6분 · 비생산 ≥ 51.5분`. 기준선 없는 flow 는 점선 + "CT 4.0분 기준".

저장 전 재분류 미리보기·기본값·적용(dirty `*`)은 종전 규약 유지([[project_dspilot_save_button_dirty_state]]).

## 5. 남은 결정 · 정직성 경계

- **소급 변경**: 판정은 조회 시 재계산이라 배포 즉시 과거 A·정지 건수가 바뀐다(doc/25 §6 ② 와 같은 정책). 릴리스 노트에 명시.
- **WT 초과 행의 적립 범위**: 사이클 전체(현행). 대기 성분만 적립 + 동작 성분을 가동으로 되돌리는 안은 A 분자 변화·패스 2 잔여 처리와 함께 별도 검토.
- **비생산 절대 하한**(예: 5분)은 두지 않았다 — 사이클 수 하한(10사이클)으로 극소 WT flow 만 방어. 초고속 라인에서 65초 비생산 승격이 실제 문제가 되면 재검토.
- **WT 기준선의 취약성**: 항상 소재가 대기하는 설비는 중앙 WT≈0 이라 배수가 의미를 잃고 하한(1사이클)이 실질 경계가 된다. 칩에 "대기 0.6초" 가 보이면 이 상태다.
