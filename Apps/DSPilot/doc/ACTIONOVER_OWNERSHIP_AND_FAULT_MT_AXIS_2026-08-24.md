# ActionOver 판정 소유권 이전 + 고장 판정 MT 축 전환 (2026-08-24)

> 인수인계 문서. 신고 → 규명 → 적용 변경 → **미완료 작업** 순.
> 현장: 우진 라인 6 flow(투입·이송·가공·조립·검사·배출), 검증 인스턴스 dev 58494.

---

## 0. 한 줄 요약

"자동감지 abnormal이 이상·알람 페이지에 안 뜬다"는 신고에서 출발해, **ActionOver 발행이 사실상 전면 정지 상태**였음을 규명하고 판정 주체를 DSPilot으로 옮겼다. 그 과정에서 **OEE 고장 판정이 CT 축이라 유발자를 못 가린다**는 별개 결함이 드러나 판정 축을 MT로 전환했다.
기동 직후 오탐(§1-5)과 정상 운전 중 오탐(§1-9)은 엔진 워치독 차단으로 처리했다(§2-F) — **실기 재검증 필요**.

---

## 1. 규명한 문제

### 1-1. ActionOver 게이트가 현재 모델 device work 전량에서 닫혀 있었음

`/api/settings/calibration-status` → **total 54, staleCount 26**. 26개 = Conveyor1~6, 1IN/1OUT_CYL ADV·RET, 1st~4th stp/usb ADV·RET = 현재 라인 device work 전부.

```
2nd_stp_Flow.RET   사이드카 6024ms  vs  모델 1070ms
Conveyor3_Flow.MOVE 사이드카 8113ms vs  모델 3285ms
```

`CalibrationState.IsMaxMeasured`는 사이드카 `maxMs`와 모델 `MaxDuration`의 **int 완전일치**를 요구한다. 불일치 → 게이트 닫힘 → 엔진 워치독·어댑터 양쪽 다 **예외도 로그도 없이 미발행**.

**인과 사슬**

| 시각 | 사건 |
|---|---|
| 7월 | DSPilot 실측 보정이 `max(중앙값×1.6, 클린최대) + 5000ms`로 AASX 기록 + 사이드카 도장 |
| 2026-08-19 `622c5f0d` | 자동 stale-repair 폐기 → 수동 버튼 전용 |
| 2026-08-24 08:56 | project.aasx 재발행으로 Min/Max 교체 → 26건 stale |
| 이후 | ActionOver 0건 (조용히) |

AASX를 덮어쓴 주체는 [Runner.cs:288-341](../../Promaker/Promaker/ViewModels/Simulation/Runner/Runner.cs#L288-L341) — Promaker 모니터링 정지 시 "학습된 duration을 AASX에 반영?" → 예. **사이드카는 갱신하지 않는다.**

### 1-2. 같은 필드(MaxDuration)에 의미가 다른 두 값이 들어옴

| 쓴 주체 | 값의 의미 | 예시 |
|---|---|---|
| DSPilot 실측 보정 버튼 | ActionOver 임계 (**+5초 포함**) | 6070ms |
| Promaker 학습 반영 | 정상 동작 밴드 상한 (여유 없음) | 713ms |

실측 예: `2nd_usb.RET` 실측 평균 535ms / max 738ms인데 적용 AASX Max가 **713ms** — 관측 최대보다 작다. 이 값으로 판정하면 정상 사이클이 매번 ActionOver가 된다. **게이트가 닫혀 있던 게 오히려 오탐을 막고 있었다.**

### 1-3. DSPilot 엔진이 봉인 2겹으로 판정 불가였음

DSPilot도 Monitoring 엔진을 돌리며 태그를 먹고 device Work를 Going으로 전이시키고 overdue 워치독까지 스케줄한다. 그런데:

1. `SetMaxMeasured`를 아무도 호출하지 않아 `engineIsMaxMeasured`가 기본 `fun _ -> false`
2. `engine.AbnormalDetected`를 구독하지 않아 발행돼도 소비처 없음

즉 **완전히 동작하는 감지기가 두 겹으로 봉인**된 채 순수 소비자로만 동작하고 있었다.

**판정 주체 전수** (`EventDrivenEngine` 생성 지점, Tutorial 제외)

| 호스트 | 게이트 주입 | ActionOver |
|---|---|---|
| Promaker.Agent `MonitoringSupervisor.cs:750` | ○ | ○ |
| Promaker WPF self engine `Runner.Start.cs:146` | ○ | ○ |
| DSPilot `SimulationEngineService.cs:213` | **✗** | **✗** |

외부 수집기(Pi5 등)는 Agent의 SignalHub `WriteTags`로 값을 밀어넣고 **판정은 Agent 엔진이 한다** — 수집 위임은 판정 위임이 아니다. 위험한 건 DSPilot이 Agent 아닌 Hub에 붙는 구성뿐이었고, 그 경우 ActionOver가 0건이었다.

### 1-4. 이송 미발화 — 실패 신호의 모양이 다름

이 라인은 **OUT을 IN 도달까지 유지하다 IN이 오면 내린다**(모든 콜에서 `IN↑`과 `OUT↓`이 같은 ms). 실패하면 PLC가 1~2초 만에 OUT을 그냥 회수한다:

```
Conveyor2.MOVE
  10:50:38.663  OUT↑
  10:50:40.154  OUT↓    ← 1.49초 만에 회수, IN 없음
       ...      4분 5초 정지 ...
  10:54:45.998  OUT↑    재명령
  10:54:47.600  IN ↑    완료
```

기존 두 경로가 모두 못 본다:

| 경로 | 조건 | 이 케이스 |
|---|---|---|
| 엔진 device-watchdog | Max+250ms 시점에 Call이 아직 Going인가 | OUT 회수로 Going 이탈 → 건너뜀 |
| 어댑터 OUT-falling | OUT 하강 시점 경과 > Max | 1,491ms < 8,100ms → 미발행 |

→ **명령 회수와 무관하게 IN 도달까지 재는 시계**가 필요.

### 1-5. 기동 직후 오탐 (★ 미해결)

서비스 재기동 11:18:18 → 20초, 23초 뒤 ActionOver 발화. 발행값이 전부 **정확히 `MaxMs + 1`** = 엔진 device-watchdog 서명:

| 시각 | 콜 | elapsed | 모델 Max |
|---|---|---|---|
| 11:18:38 | 이송/1st_stp.ADV | 5334 | **5333** |
| 11:18:41 | 투입/1IN_CYL.ADV | 8451 | 8450 |
| 11:19:32 | 조립/Conveyor4.MOVE | 10682 | 10681 |

**원인**: 재접속 시 Agent가 보내는 resync 베이스라인이 `InjectIOValueByAddress`로 엔진에 주입되면서 device Work가 **관측된 OUT 상승 없이** Going으로 올라간다 → 워치독이 사이클 시작으로 오인.

어댑터(Agent)에는 방어가 있다(`everOutRisingSeen`). 소스 주석:
> prevActive 는 resync baseline 주입으로도 채워지므로 "관측했다"의 증거가 못 된다 — baseline 을 신뢰하면 합류 직후의 정상 완료 In 이 사이클마다 오판된다(실기)

**엔진 내부 워치독은 상태만 보이고 출처는 안 보여서 이 구분이 구조적으로 불가능하다.** DSPilot은 `HubSource.Resync`가 별도 분기라 구분할 수 있다.

### 1-6. 오탐 1건이 OEE 고장 건수를 3배로 부풀림

같은 정지에서 6개 flow 중 3개가 고장으로 찍혔다. [OeeMath.ClassifyStopWindow](../DSPilot/Services/OeeMath.cs) 규칙은 `hasOwnSignal → Fault`가 **최우선**이라, 자기 flow에 abnormal이 있으면 무조건 고장이다.

```
고장 3건 = 조립(실제) + 이송·투입(기동 오탐)
정답    = 조립 1건
→ 고장 건수 3배, MTBF 1/3
```

### 1-7. 고장 판정이 CT 축이라 유발자를 못 가림

같은 정지의 flow별 수치:

| flow | CT | MT | MT 배율 |
|---|---|---|---|
| 조립 | 252,889 | 216,497 | **46.3×** |
| 이송 | 283,329 | 34,137 | **8.0×** |
| 투입 | 269,601 | 29,899 | 2.2× |
| 가공 | 254,325 | 5,113 | 1.0× |
| 검사 | 252,978 | 5,665 | 1.0× |
| 배출 | 252,830 | 7,448 | 1.0× |

**CT는 6개가 252~283초로 붙어 구분 불가, MT는 1.0×/8.0×/46.3×로 확연히 갈린다.** 정지 길이는 전염되지만 동작 시간은 전염되지 않는다.

기존 `mtOverrun` 판정은 `mt > 평균CT × 2.5`였다. 이송 기준 경계 101,955ms인데 평소 MT가 4,242ms라 **사실상 24배 임계** — 8배 이상은 못 잡는다.

### 1-8. MT 축이 절반만 적용돼 "남 탓 전용" 이 됐다 (리뷰 지적, 자초)

§2-D 로 유발자 귀속을 MT 축으로 옮겼는데 **고장 생성 조건은 CT 축에 남아 있었다.**

```
dtCond = "ct > 0 AND (ct > @Thr OR (mt IS NOT NULL AND mt > @Thr))"
                                                        └ CT 축 경계
```

변경 전엔 사전 수집(`mtOverrunByFlow`)도 `mt > thr×idleMult` 라 dtCond 의 mt 절과 **같은 행**을 찾았다.
사전 수집만 MT 축으로 낮추면서 대칭이 깨졌다 — 결과가 정확히 뒤집힌다:

| | MT 축 (11~33초) | CT 축 (140.7초) |
|---|---|---|
| 유발자 귀속 (형제 강등) | 적용됨 | — |
| **고장 생성 (dtCond)** | **미적용** | 적용됨 |

그래서 "평소 MT 의 17배로 늘어졌지만 CT 는 임계 미달" 인 사이클이 **정상 가동으로 계상되면서 동시에
다른 flow 의 정지를 대기로 강등**시켰다. 문제 설비는 무죄가 되고 여파를 받은 설비의 고장 기록만 지워진다.
리뷰 시점 해당 건 5개 전부 이 상태였다(검사 17.4× / 배출 12.3×·6.7× / 가공 9.0× / 투입 4.2×).

### 1-9. 엔진 워치독은 IN 을 <b>순간값</b>으로 본다 — 정상 완료를 타임아웃으로 발행

기동 오탐(§1-5)과 **별개의 결함**이며, 라인이 정상 운전 중에도 상시 발생한다.

```
12:41:47.638  OUT↑                    검사 / 4th_stp.ADV (임계 5,706ms)
12:41:51.667  IN ↑    ← 4,029ms 에 정상 완료
12:41:52.724  IN ↓    ← IN 이 펄스라 1초 만에 하강
12:41:53.595  ← 워치독 검사 시점 (OUT↑ + Max+1+250ms)
              이 순간 inActive=false → "완료 안 됨" 판정 → 발행
12:41:53.611  알람 기록 (계산값과 16ms 일치)
```

[Composition.fs:349-358](../../Solutions/Runtime/Ds2.Runtime/Engine/EventDriven/Composition/Composition.fs#L349-L358)
의 `inActive` 는 **한 순간의 IN 레벨**이다. IN 이 짧은 펄스인 디바이스는 검사 시점에 이미 off 라서
제시간에 완료해도 타임아웃으로 잡힌다.

완료대기 시계(§2-C)는 **IN 상승 엣지**에 해제하므로 이 버그가 없다 — 위 케이스에서 시계는
12:41:51.667 에 정상 해제됐고 아무것도 내지 않았다.

**오탐 원인 2종 대조**

| 원인 | 엔진 워치독 | 완료대기 시계 |
|---|---|---|
| resync 베이스라인 → 가짜 Going | 취약 | 안전 (관측 엣지에서만 시작) |
| IN 펄스 + 순간값 샘플링 | **취약** | 안전 (엣지 기반) |

---

## 2. 적용한 변경

### A. ActionOver 판정 소유권 = DSPilot

[SimulationEngineService.cs](../DSPilot/Services/SimulationEngineService.cs)

- `engine.AbnormalDetected += OnEngineAbnormalDetected` 구독 (봉인② 해제)
- `edEngine.SetMaxMeasured(_ => true)` — 게이트 개방 (봉인① 해제). 오탐 차단은 게이트가 아니라 절대 여유값이 담당
- `HandleHubAbnormal`에서 **상류 ActionOver 폐기** — Agent 임계(모델 Max)와 DSPilot 임계(+여유)로 이중 계상되는 것 방지. Under·Sensor는 그대로 통과
- teardown 2곳에서 `AbnormalDetected -=` 해제

**kind별 소유권**: ActionOver = DSPilot / ActionUnder·Sensor* = Agent.
ActionUnder는 IN 엣지 정밀 관측이 필요해 `MonitoringAbnormalAdapter`에만 있고 DSPilot은 그 어댑터를 두지 않는다("상태추론 한계"로 제거된 결정 유지).

### B. 임계 = 모델 Max + 여유값 (파일에 굽지 않음)

`ApplyActionOverThresholds` / `RewriteActionOverThresholds`

엔진 초기화 때 `index.WorkDurationRange`(mutable)에 임계를 확정해 넣는다. 이 필드의 소비처는 ActionOver 경로 2곳(overdue 스케줄·판정)뿐이라 정상 Finish 스케줄(`WorkDuration`)·사이클 통계·AASX export에 영향 없다. **store는 안 건드리므로 AASX 파일 불변.**

**이중 가산 방지** — 사이드카 값 == 모델 Max이면 DSPilot이 구운 임계(+5초 포함)로 보고 그대로, 불일치면 여유값을 더한다. calibration-state의 역할이 *발행 게이트* → *여유값 포함 여부 표식*으로 바뀌었다(스키마 변경 없음).

`RefreshActionOverThresholds()`를 설정 저장 경로([SettingsController.cs](../DSPilot/Controllers/SettingsController.cs))에서 호출 → 여유값 변경이 재시작 없이 즉시 반영.

### C. 완료대기 시계 (이송 케이스 대응)

```
OUT↑  →  시계 시작
OUT↓  →  무시 (★ 지우지 않는다)
IN ↑  →  시계 해제
5초 틱 →  경과 > 임계 & 미발행 → ActionOver 발행
```

- `_overClock`, `_outLastActive` 필드 + `ObserveActionOverEdges` (HandleHubTagChanged에서 호출, resync 분기 이후라 재동기 값은 안 닿음)
- `TickAbnormalWatchdog()` — 기존 no-op 스텁을 실제 구현으로
- 엔진 워치독이 먼저 낸 건은 `OnEngineAbnormalDetected`가 시계를 '발행됨'으로 표시 → 사이클당 1건
- 판정 규칙은 순수함수 [ActionOverPolicy.cs](../DSPilot/Services/ActionOverPolicy.cs)로 분리 + 회귀 테스트 10건

### D. 고장 판정 축 CT → MT

| 대상 | 축 | 기본 | 범위 |
|---|---|---|---|
| 정지(비가동) 계상 | CT | 2.5× | 현행 유지 |
| **고장 유발자 판별** | **MT** | **2.5×** | **1~10×** |
| 비생산 제외 | CT | **15×** (구 10×) | 2~100× |

- [OeeMath.cs](../DSPilot/Services/OeeMath.cs) — `NonProductionCtMultiplier` 10→15, `FaultMtMultiplierDefault = 2.5` 신설
- [AppSettingsModel.cs](../DSPilot/Models/AppSettingsModel.cs) — `FaultMtMultiplier` + `FaultMultMin/Max` + `ResolveFaultMtMultiplier()`. CT 축 배수와 대소 제약 없음(축이 달라 비교 무의미)
- [OeeCtStatsService.cs](../DSPilot/Services/OeeCtStatsService.cs) — `ComputeMtThresholdAsync()`: flow별 14일 **중앙** MT. 평균이 아니라 중앙값인 이유는 MT가 정지 사이클 하나에 수십 배로 끌려가기 때문(조립 평상시 4.7초 vs 정지 216초)
- [OeeControllerBase.cs](../DSPilot/Controllers/OeeControllerBase.cs) — mtOverrun 판정 2곳(사전 수집 루프 / 사이클 루프)을 `평균MT × faultMult`로. aggKey **v28**
- ct-multipliers API — `FaultMtMultiplier` 입출력 + flow별 `MedianMtMs` 응답

> **왜 정지 계상은 CT에 남겼나**: 2.5×MT를 정지 계상에 적용하면 같은 4분 정지에서 6개 중 4개(투입·가공·검사·배출)가 "정상 가동"이 되어 A에서 안 빠진다. 정지 시간 계상은 길이 질문이므로 CT 축이 맞다.

### E. dtCond MT 분기 분리 + 적립 범위 (§1-8 대응)

```
DtCondSql = "ct > 0 AND (ct > @Thr OR (mt IS NOT NULL AND mt > @MtThr))"
                              CT축           MT축(중앙MT × 고장배수)
```

- 집계 경로·계측 품질 경로 양쪽에 `@MtThr` 바인딩. **MT 기준 미보유 flow 는 `@MtThr = @Thr` 폴백** —
  0 을 넣으면 `mt > 0` 이 항상 참이라 전 사이클이 비가동이 된다
- SSOT 쌍 `OeeMath.ClassifyCycle` 에 `mtBoundaryMs` 파라미터(0 이면 CT 경계 폴백)

**적립 범위** — `OeeMath.ResolveDowntimeAccrualMs`

| 선택 축 | 적립 |
|---|---|
| CT 초과 | 사이클 전체 (종전 동일) |
| **MT 만 초과** | **평소(중앙 MT) 대비 초과분만** |

MT 만 초과인 행은 여유(wt)가 흡수해 **제때 산출**한 사이클이다. 전체를 넣으면 "제때 생산했는데
100% 비가동" 이 되고, 설정 화면의 "경계 미만의 느린 사이클은 정상 — 속도 저하는 성능 P 가 흡수"
약속과도 충돌한다. 실측 이송 12:43:35 — ct 40,823ms(중앙 40,754 와 동일) / mt 31,191ms(중앙 4,220 의
7.4배) → 40.8초가 아니라 **초과분 27.0초**만 손실로 계상.

초과분은 사이클 끝(rec)에 붙인다 — mt 가 사이클 안 어디서 났는지는 알 수 없고, 행 구간이 rec 기준인
기존 규약과 맞춘다. 분류·신호 매칭 창은 `startMs~rec` 원값을 그대로 쓴다.
`cycleIdleByFlow` 도 사이클 전체를 유지한다(무사이클 갭 판정에서 "여긴 사이클이 있었다"를 빼는 용도라,
적립과 함께 줄이면 정상 생산분이 갭으로 오인된다).

### F. 엔진 device-watchdog 차단 — 판정을 완료대기 시계로 단일화 (§1-5·§1-9 대응)

`SetMaxMeasured` 주입과 `AbnormalDetected` 구독을 제거했다. 주입이 없으면 `engineIsMaxMeasured` 가
기본 `false` 로 남아 워치독이 발행하지 않는다. 공유 F# 코어는 건드리지 않았으므로 Agent 동작은 그대로다.

**닫은 근거** — 오탐 원인 2종(§1-5 resync 베이스라인, §1-9 IN 순간값)이 모두 이 경로에 있고 둘 다
구조적이다. 반면 워치독이 잡는 케이스는 완료대기 시계가 전부 포함한다(상위집합):

| 2026-08-24 12:41~12:44 발행 | 낸 쪽 | 실제 | 차단 후 |
|---|---|---|---|
| 검사/4th_stp.ADV | 워치독 | **오탐** (4,029ms 에 완료) | 사라짐 |
| 이송/Conveyor2.MOVE | 시계 | 정탐 14,697ms (평소 3,028ms) | 그대로 |
| 이송/1st_usb.ADV | 워치독 | 정탐 8,336ms (평소 150ms) | 시계가 대체 |
| 배출/1OUT_CYL.RET | 워치독 | 정탐 31,727ms (평소 161ms) | 시계가 대체 |

**부수 이득** — 워치독은 elapsed 를 `MaxMs+1` 고정값으로 찍어 심각도를 알 수 없었다(5707/5150/7855 가
전부 임계+1). 시계는 실측을 찍으므로 화면만 보고 "3초짜리가 14초" 판단이 된다.

**대가** — 발행이 최대 1틱(기본 5초) 늦다. 초 단위 판정이라 실무 영향은 없다고 보되, 필요하면
StateReconcile 주기를 줄이면 된다.

### G. 관측 단절 시 시계 폐기 (`ClearActionOverClocks`)

시계는 "OUT 올라갔는데 IN 이 아직"을 재는데, 단절 구간에서는 IN 이 실제로 왔는지 <b>알 수 없다</b>.
그대로 두면 복구 직후 틱에서 단절 시간이 통째로 경과로 잡혀 즉시 오탐이 난다(임계 5~30초 vs 단절 수 분).
호출 지점 3곳:

| 지점 | 이유 |
|---|---|
| PLC blackout (`AbandonActiveCyclesOnPlcBlackoutAsync`) | 사이클 abandon 과 같은 이유 |
| resync 베이스라인 수신 | 엣지가 아닌 현재값 스냅샷 — 그 사이 IN 도달 여부 불명 |
| 엔진 재초기화 | Work/ApiCall 매핑이 바뀔 수 있어 이전 시계는 근거 상실 |

`_outLastActive`(OUT 직전값)도 함께 비운다 — 남기면 복구 후 첫 관측이 baseline 이 아니라 상승엣지로
잘못 읽혀 가짜 시계가 시작된다.

---

## 3. 검증 상태

| 항목 | 상태 |
|---|---|
| 컴파일 | ✓ 0 error |
| 테스트 | ✓ 304/304 (신규 16건 포함) |
| DSPilot ActionOver 실발화 | ✓ 확인 — `01:47:56Z 가공/2nd_stp.RET 6620ms` (재기동 후 발행분) |
| 완료대기 시계 실발화 | ✓ 확인 — `03:43:09Z 이송/Conveyor2.MOVE 14132ms`(OUT↑→IN↑ 실측 14,697ms, 평소 3,028ms) |
| MT 축 고장 판정 실검증 | **✗ 미검증** — 재기동 후 정지 발생 시 확인 필요 |
| 설정 UI | **✗ 미구현** (§5) |

---

## 4. 진단에 쓴 방법 (재현용)

- 게이트 상태: `GET /api/settings/calibration-status` → `staleCount`
- ActionOver 출처 판별: `userTagAlertLog.actualValue`가 정확히 `모델Max+1`이면 **엔진 워치독**, 실측 경과면 완료대기 시계
- 태그 엣지 타임라인: `POST /api/call-test/load` (Body UTF-8 필수 — 한글 flow명이 셸에서 깨짐. 파일로 써서 `--data-binary @req.json`)
- flow별 mt/ct: `dspFlowHistory` 직접 조회 (`plc.db`, read-only URI)
- 모델 Max: call-test load 응답의 `apiCalls[].currentMaxMs`

---

## 5. 미완료 작업 (우선순위 순)

### ~~P0 — 기동 직후 오탐~~ (2026-08-24 해결, §2-F·§2-G)

엔진 워치독 차단 + 관측 단절 시 시계 폐기로 처리했다. **실기 재검증은 남아 있다** — 재기동 직후와
통신 단절 복구 직후에 ActionOver 가 뜨지 않는지 확인 필요.

**닫아도 손실이 없다는 것이 실측으로 확인됐다.** 2026-08-24 12:41~12:44 발행 4건 중 3건은
정탐이었고(Conveyor2.MOVE 14.7초/평소 3.0초, 1st_usb.ADV 8.3초/평소 0.15초, 1OUT_CYL.RET 31.7초/평소
0.16초) 완료대기 시계가 같은 것을 잡는다. 나머지 1건이 §1-9 오탐이다.

적용: ① 워치독 차단(§2-F) ② 블랙아웃·재동기·재초기화 시 시계 폐기(§2-G).
③ "기동 후 절대 유예"는 넣지 않았다 — 시계가 관측된 OUT 상승에서만 시작하고 단절 시 폐기되므로
구조적으로 불필요하다고 판단. 실기에서 여전히 뜨면 그때 추가한다.

### P1 — 워밍업 (MT 축 전환의 전제조건)

MT 임계는 12~16초로 **사이클 주기(40초)보다 짧다**. 재동기 베이스라인으로 시작된 가짜 시계가 임계를 넘길 수 있다.

```
T(임계) > L(가짜 시계 수명) → 자연 면역
T(임계) < L               → 오탐
CT축 102초 > 40초 ✓  /  MT축 14초 < 40초 ✗
```

CT 축일 때는 "다음 ct가 알아서 지워준다"가 성립했지만 MT 축에서는 깨진다. **소스 판별 + 워밍업(정상 사이클 1회 완주 또는 관측된 Ready→Going→Ready 1회)이 선택이 아니라 필수.**

### P2 — 설정 UI (슬라이더 3번째)

현재 `FaultMtMultiplier`는 기본값 2.5로만 동작하고 **화면에서 조절 불가**. [settings.html](../DSPilot/wwwroot/app/settings.html) 및 설비효율 현황의 "정지·비생산 판정 기준" 카드에 추가 필요:

- 슬라이더 1개 (1~10×, 기본 2.5), 비생산 슬라이더 최대 100으로 확대
- flow별 환산 칩 — `이송 유발 > 12.7초` 형태 (`MedianMtMs × faultMult`)
- **상단 3분할 막대는 CT 축 전용으로 유지**하고 MT 축은 별도 줄. 두 축을 한 막대에 얹으면 스케일이 달라 거짓말이 됨

### P3 — ClassifyStopWindow 우선순위 교정

현재 순서가 코드 주석의 원칙("going 중 걸린 flow만 고장")과 어긋난다:

```
현재:  ① hasOwnSignal → 고장   ② lineHasCulprit|lineHasMtOverrun → 대기
제안:  ①' 다른 flow MT 과주행 → 대기   ② hasOwnSignal → 고장
```

자기 MT 과주행은 호출부에서 이미 우회하므로, 이 함수 도달 시점엔 자기 MT가 정상임이 보장된다 → "쟤는 움직이다 걸렸고 나는 아니다 = 나는 피해자". 이 교정만으로 오탐이 남아 있어도 고장 건수가 안 흔들린다.

`lineHasCulprit`(다른 flow의 abnormal)까지 올리는 건 **반대** — 둘 다 신호만 있고 MT 증거가 없으면 유발자를 못 가리므로 양쪽 고장이 안전하다. **MT 과주행만** 강등 권한을 가져야 한다.

### P4 — 나머지

- Agent 측 ActionOver 발행 중단 조율 → §2-A의 폐기 가드가 no-op이 되고 Agent 게이트 주입 코드도 정리 가능
- 공유 F# 코어에서 `isMaxMeasured` 게이트 완전 제거 (Agent 배포와 묶여야 함)
- "자동감지 N건 미설정(Max 없음)" 배너 — 지금은 Max 없는 Work가 조용히 판정 제외됨
- doc/25 개정 — 신호 기반 분류가 판정에서 표시(단서)로 강등되는 방향
- Promaker 쓰기 경로(`DurationBatchCommands.cs:71`, `NameEdit.cs:161`)가 사이드카를 갱신하지 않는 문제. `Promaker.Shared.CalibrationState.SetMaxMeasured`는 존재하나 **호출부가 없다**

---

## 6. 주의사항

- **과거 수치 불연속**: 비생산 10×→15×, 고장 판정 축 변경으로 재계산 시 고장 건수↓ MTBF↑. aggKey v28로 캐시는 갱신되나 사용자 공지 필요
- **기존 설치본의 저장값**: `NonProdCtMultiplier`가 Production.json에 10으로 저장돼 있으면 그대로 유지된다(기본값 변경은 신규 설치만). 15× 적용은 별도 마이그레이션 판단 필요
- **작업 파일 겹침**: `OeeControllerBase.cs` / `OeeMath.cs` / `OeeMathTests.cs` / `SettingsController.cs`에는 이 작업 이전부터 사용자의 미커밋 변경(CT축 단일모델 전환, `OwnMtOverrun`, stale-flows 엔드포인트)이 섞여 있다. diff 해석 시 주의
- **빌드 잠금**: dev 인스턴스 실행 중에는 출력 DLL이 잠긴다. `dotnet msbuild -t:Compile`로 컴파일만 검증하거나 `-p:BaseOutputPath=<임시경로>`로 테스트 실행
- **MT 배율 3× 근거는 표본 1건**: 정지 1회 관측(정상 1.0× / 이상 8.0·46.3×)에서 3× 부근이 넓게 비어 있다는 것뿐. 기본값은 2.5로 두었으나 며칠 데이터로 재확인 권장. 투입은 평소 MT 13.3초로 다른 flow의 3배라 변동폭이 클 수 있음
