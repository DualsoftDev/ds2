# ActionOver(감지시간 초과) Monitoring 미발화 — Promaker.Agent 엔진 수정 핸드오프

작성 2026-07-03. **이 문서는 다른 세션이 맥락 없이 실행할 수 있도록 쓴 자체완결 설계 프롬프트다.** 아래 "확정"과 "미확정(열린 질문)"을 반드시 구분해서 읽을 것 — 확정 사실은 라이브 로그·코드로 검증했고, 미확정은 추측이므로 코드 계측으로 먼저 확인한 뒤 수정할 것.

---

## 0. 당신(다른 세션)이 할 일

1. §5의 **열린 질문(왜 스케줄된 overdue check가 블랙아웃 중 발행되지 않았나)** 을 §4 코드와 §6 계측으로 **먼저 확정**한다. 추측으로 바로 고치지 말 것.
2. 확정된 실패 모드에 맞춰 §7의 수정안(A/B 중 필요한 것, 또는 둘 다)을 설계·구현한다.
3. §7의 **가드(commBlackout·게이트·dedup)** 를 반드시 함께 태운다 — 안 그러면 통신 끊김이 가짜 ActionOver로 둔갑한다.
4. §8 부수 사각(짧은 device 고정 grace)은 별개 이슈 — 같은 PR로 묶을지는 판단.
5. §9 회귀 체크리스트로 검증.

작업 대상 코드베이스: `ds2` 리포, 엔진은 **`Solutions/Runtime/Ds2.Runtime/Engine/`** (F#). 이 abnormal 감지는 Promaker.Agent 가 Monitoring 모드로 이 엔진을 구동하며 수행한다.

---

## 1. 문제 (증상)

Passive **Monitoring** 모드에서, 디바이스(특히 **컨베이어**)가 **실제로 시간 초과(감지시간 초과)** 됐는데도 자동 **ActionOver** abnormal 이 발행되지 않는다. 실제 물리적 타임아웃이 감지 누락되고, PLC 자체 usertag 비트만 뜬다.

- 임계값(캘리브레이션 Max)은 정상이고, 무장(Going 진입)도 정상인데 안 뜬다 → 임계/무장 문제가 **아니다**.
- 짧은 행정 device(_CYL/_stp/_usb)는 잘 뜨는데, 컨베이어/장행정은 특정 상황(라인 전체 정지)에서 안 뜬다.

## 2. 환경

- **Promaker.Agent** = Windows 서비스(`PromakerAgentService`, `C:\Program Files\Promaker\Agent`). abnormal 감지의 **유일 주체**.
- **DSPilot** = 별도 프로세스. Monitoring abnormal 을 **감지하지 않고**(`_monitoringAbnormal=null`) Agent 가 SignalR 로 보낸 것을 중계만 함.
- 로그: `C:\Program Files\Promaker\Agent\logs\promaker-agent.log` (logger 이름 `PassiveInference`), raw PLC = `plc-raw.log`.
- 엔진 RuntimeMode = `Monitoring` (passive). Control 모드와 abnormal 경로가 다름(아래).

## 3. 확정된 라이브 증거 (2026-07-03, promaker-agent.log)

대상: **Conveyor2.MOVE = Call `77f2520e-2e53-4b0f-9016-84df0ccc0c52`**, OUT=`%QX0.1.21`, IN=`%IX0.0.21`. (매핑은 `DSPilot/wwwroot/uploads/cctv-overlays.json` 로 교차확인.)
정상 사이클: OUT rising → `Call 77f2520e → Going`, ~3.2초 뒤 IN rising → `Call 77f2520e → Finish`.

**16:33 사건 (진짜 타임아웃, 미발화):**
```
16:32:59.481  Call 77f2520e → Going              ← 무장 성공 (arming OK)
16:33:14.486  obs %QX0.1.21=false → 0 action(s)   ← OUT 내려감 (Going 15초째)
16:33:14.486  obs %MX1256=true                     ← PLC 자체 감지시간초과 usertag ON
16:33:14.486  [ClockSync] sim clock jumped 9258ms on hub-thread drain (stale stamp window)
16:34:45.389  [ClockSync] sim clock jumped 10012ms ...        ← 이 사이 실제 PLC 이벤트 0
16:46:47.455  [ClockSync] sim clock jumped 10027ms ...
16:46:55.647  obs %MX1256=false                     ← usertag OFF (~13.5분 지속)
16:46:58.157  obs %IX0.0.21=true → Call 77f2520e → Finish   ← 14분만에 Finish
```
- **Going 이 16:32:59 → 16:46:58, 약 14분 지속.** Finish 조건은 IN(`%IX0.0.21`) 도착인데 IN 이 안 옴 → OUT 만 내려간 걸로는 Finish 안 됨 → Going 박제.
- 그 14분간 `[ClockSync] ... stale stamp window` 3줄 외에 **실제 PLC 이벤트 없음**(라인 전체 정지 = 관측 블랙아웃).
- **ActionOver 발행 0.** 그때 임계 Max ≈ 8.17초(p60 중앙값+5초).

**대조 — 컨베이어도 뜬다(“0/131 구조적 사각”은 거짓):**
```
15:31:43.801  [Abnormal발행] ActionOver call=77f2520e apiCall=... work=... elapsed=2001
```
같은 call 이 초과량 작고 라인 살아있을 때(관측 tick 있을 때)는 정상 발행.

**로그 elapsed 값은 실측 아님**: 코드가 `range.MaxMs + 1` 을 넣음(§4). 다른 발행들(elapsed=5753/6482/7320 등)도 그 work 의 MaxMs+1.

## 4. 확정된 코드 아키텍처 (모두 실제 file:line 검증됨)

**(a) ActionOver 발행 지점 — Monitoring**
`Engine/EventDriven/Composition/Composition.fs:319-346` `onDeviceDurationExpired`:
```fsharp
let onDeviceDurationExpired (workGuid: Guid) =
    match abnormalAdapter with
    | Some adapter -> adapter.OnTick(int scheduler.CurrentTimeMs)   // Control 경로
    | None ->                                                       // Monitoring 경로
        if runtimeMode = RuntimeMode.Monitoring then
            match index.WorkDurationRange |> Map.tryFind workGuid with
            | Some range ->
                let ioState = (stateManager.GetState()).IOValues
                for m in ioMap.Mappings do
                    if m.RxWorkGuid = Some workGuid
                       && stateManager.GetCallState(m.CallGuid) = Status4.Going then
                        let inActive = (* ApiCall IN 이 active 입력값인지 *)
                        if not inActive && engineIsMaxMeasured workGuid then          // ← 게이트
                            abnormalDetectedEvent.Trigger(
                                Abnormal.actionOver target (range.MaxMs + 1) nowUtc)   // ← 발행
            | None -> ()
```
- Monitoring 에서 `abnormalAdapter = None` 이라 이 inline 분기가 유일 발행 경로.
- 발행 조건: **call 이 Going && IN 미도달(not inActive) && `engineIsMaxMeasured workGuid` (게이트)**.

**(b) overdue check 무장/스케줄**
`Engine/EventDriven/Transitions/WorkTransitions.fs:55-85` `scheduleDeviceOverdueCheck`:
- device work && `range.MaxMs > 0` 일 때만 스케줄.
- 발화 지연 = `max 1L (int64 range.MaxMs + 1L + graceMs)`, Monitoring 에서 `graceMs = monitoringObservationGraceMs = 250L` (`:55`).
- 즉 **Going + MaxMs + 1 + 250ms** 시점에 `ScheduledEventType.DeviceOverdueCheck(workGuid, workEpoch)` 예약.

**(c) 예약 이벤트 디스패치**
- `Engine/Core/Scheduling/Scheduler.fs:15` `| DeviceOverdueCheck of workGuid: Guid * workEpoch: int`
- `Engine/EventDriven/Lifecycle/Runtime.fs:81-82` → `ctx.HandleDeviceOverdueCheck workGuid workEpoch`
- `Composition.fs:361-363` `handleDeviceOverdueCheck`: **workEpoch 가 현재와 같을 때만** `onDeviceDurationExpired` 호출(재무장 시 stale 이벤트 무시).

**(d) 클록 전진/드레인**
- `Composition.fs:590-600` `AdvanceSimulationTo` / `AdvanceSimulationToRealTime`. 후자는 `getStatus()=Running` 이면 `syncClockToRealTimeWhileRunning`, 아니면 `advanceStepRuntime scheduler.CurrentTimeMs`.
- `Runtime.fs:156-160` `syncClockToRealTimeWhileRunning` → `advanceAndDrainWhileRunning ctx targetMs`.
- `Runtime.fs:177-185` `simulationLoop`: `while Running` 루프, `nextWaitTimeoutMs`(`:162-171`)로 **다음 예약 이벤트 시각까지 대기 후 깨어남**(`WaitHandle.WaitAny([wakeSignal, ct], timeoutMs)`). 즉 이론상 다음 overdue 이벤트 시각에 자동 wake 가능.
- `[ClockSync] ... on hub-thread drain (stale stamp window)` 로그는 Engine 폴더 밖(Hub/Session 계층, `Solutions/Runtime` 내 RuntimeHubSession/Gateway 로 추정 — 확인 필요)에서 `AdvanceSimulationToRealTime` 를 호출할 때 찍힘.

**(e) 게이트 `engineIsMaxMeasured`**
- `Composition.fs:53` 기본 `fun _ -> false`(비활성=오탐 차단). `:343` 발행 게이트. `:536-539` `SetMaxMeasured` 로 호스트(DSPilot/Agent)가 calibration-state 기반 함수를 주입.
- 의미: 해당 work 의 Max 가 **실측 확정**(calibration-state.json)됐는가. 미확정이면 ActionOver 발행 안 함.
- 배경/함정: `project_dspilot_abnormal_usertag_calibration_hash_gate` 및 커밋 `3edcce2f`(해시 기반→duration 기반 게이트로 수정). **모델 스왑/재캘리브 직후 게이트가 닫힐 수 있음.**

**(f) 관련 adapter (혼동 방지)**
- `Engine/Abnormal/MonitoringAbnormalAdapter.fs`: ActionUnder 전용(학습), ActionOver 영향 없음(`:132`). Monitoring 에서 device-watchdog 는 (a) 의 inline 경로지 이 adapter 가 아님.
- `Engine/Abnormal/ControlAbnormalAdapter.fs`: Control 모드 전용. `OnTick` 에서 ActionOver(`:201-204`, `isMaxMeasured` 게이트). Monitoring 과 경로 다름.

## 5. 확정 vs 미확정 (핵심 열린 질문)

**확정:**
- 무장(Going 진입) 발생 → arming 실패 아님.
- device work + MaxMs>0 이므로 overdue check 가 **스케줄됐어야 함**.
- 14분 Going 박제, 그 사이 실제 PLC 이벤트 0(블랙아웃), ActionOver 0.
- 같은 call 이 다른 때(15:31)엔 정상 발행 → 메커니즘·게이트는 최소 한 번은 열려 동작함.

**미확정(반드시 계측으로 확정할 것) — “스케줄된 DeviceOverdueCheck 가 왜 블랙아웃 중 ActionOver 를 못 냈나?”** 후보:
1. **드레인이 due 이벤트를 디스패치 안 함**: hub-drain(`AdvanceSimulationToRealTime`)이 클록만 당기고 `advanceAndDrainWhileRunning` 의 due-이벤트 처리를 (getStatus≠Running 등으로) 건너뜀. → Monitoring 에서 `simulationLoop` 이 실제로 Running 상태로 돌며 next-event 로 자동 wake 하는지 확인 필요(`Runtime.fs:177-185`, `Composition.fs:597`). **passive 는 관측구동일 것**이라는 메모 가설이 여기 해당.
2. **디스패치는 됐으나 게이트 `engineIsMaxMeasured`=false**: 15:31~16:33 사이 사용자가 p60 재캘리브 중이었음 → AASX/sidecar 변경으로 현재 Max 미확정 → `:343` 게이트 닫힘. (15:31 발행됐으니 그 시점엔 열려 있었음. 이후 닫혔을 가능성.)
3. **workEpoch stale**: `handleDeviceOverdueCheck` 의 epoch 불일치로 스킵(`Composition.fs:362`).
4. **디스패치·게이트 통과했으나 `inActive` 판정 오류**: (16:33:14 시점엔 IN=false 라 not inActive=true 여야 함 → 이 후보는 약함.)

후보 1 vs 2 가 유력. **텍스트 로그만으론 구분 불가** — 엔진 내부 상태(scheduler due-queue, getStatus, engineIsMaxMeasured 반환값, workEpoch)가 로그에 없음.

## 6. 검증 계획 (계측)

수정 전, 다음 로그를 임시 추가해 재현 캡처:
1. `WorkTransitions.fs:78` 스케줄 직후: `[overdue-sched] work=%A epoch=%d dueMs=%d (Going+Max+grace)`.
2. `Composition.fs:361` `handleDeviceOverdueCheck` 진입/epoch비교 결과: `[overdue-fire] work=%A epoch cur=%d sched=%d match=%b`.
3. `Composition.fs:343` 게이트 직전: `[overdue-eval] work=%A callGoing=%b inActive=%b maxMeasured=%b → emit=%b`.
4. Monitoring 에서 `simulationLoop`/`syncClockToRealTimeWhileRunning` 가 도는지: `getStatus()` 값과 drain 호출자(hub thread vs sim loop) 로깅.
5. 재현: 컨베이어를 물리적으로 정지시켜 라인 전체 blackout 을 만들고(또는 IN 센서 미도달 유도), 위 4개 로그로 후보 1~4 중 무엇인지 확정.

## 7. 수정안 (확정된 실패 모드에 맞춰 택)

### 옵션 A — OUT-falling 기반 발행 (값싸고 확실, 16:33 케이스 직격)
16:33:14 의 `%QX0.1.21=false` 는 **블랙아웃 중에도 실제로 들어온 이벤트**다(로그 확인). 그 순간 elapsed=15초>8.17초.
- **설계**: Monitoring 관측 처리(`Engine/Passive/Inference.fs` 계열, OUT falling 핸들)에서 — 해당 OUT 이 매핑된 device work 의 call 이 **Going 이고**, Going 경과가 **effective Max 초과**이며, **IN 미도달**이면 → `onDeviceDurationExpired workGuid`(또는 직접 `abnormalDetectedEvent.Trigger(Abnormal.actionOver …)`) 호출.
- **장점**: 스케줄러/드레인/자동tick 의존 없음. OUT 이 꺼지는 순간(=기계가 동작 중단) 곧바로 판정.
- **한계**: OUT 이 계속 켜진 채 라인이 얼면 OUT-falling 이 없음 → 옵션 B 필요.

### 옵션 B — 관측-독립 overdue 평가 (OUT 안 꺼지는 케이스 커버)
- **설계**: due 시각(Going+Max+grace)에 실제 PLC 이벤트가 없어도 overdue check 를 평가하도록 보장. 방법:
  - (B1) Monitoring 에서도 `simulationLoop` 이 next-event 시각에 자동 wake 하도록 보장(§5 후보1 이 “drain 이 due 를 스킵”이면 여기가 근본). 또는
  - (B2) 별도 경량 워치독 타이머(주기 예: 1s)가 Going 인 device work 들의 경과를 스캔해 Max 초과+IN미도달+게이트통과면 발행. (DSPilot 구 `MonitoringAbnormalAdapter.OnTick` 워치독의 Agent판 — 그 어댑터는 DSPilot 에서 제거됨: `DSPilot/Services/SimulationEngineService.cs:634-635` `TickAbnormalWatchdog(){}` no-op.)
- **장점**: 블랙아웃 무관하게 due 시각에 발행.
- **주의**: 아래 가드 필수.

### 가드 (A/B 공통, 필수)
1. **commBlackout 억제**: 침묵이 PLC 통신 끊김(flapping)이면 발행 금지 — 안 그러면 통신 손실이 가짜 ActionOver 로 둔갑. 기존 blackout/resync 경로와 연동: `project_dspilot_comm_blackout_cycle_invalidation`, `project_dspilot_plc_lsadapter_connect_result_ignored`, `HubSource.Resync`, `AbandonActiveCyclesOnPlcBlackout*`. 재연결 baseline 확정 전엔 억제.
2. **게이트 유지**: `engineIsMaxMeasured` 게이트(`Composition.fs:343`)를 우회하지 말 것. (단 §5 후보2 가 근본이면, “재캘리브 직후 게이트가 부당하게 닫히는” 별도 버그를 함께 봐야 함 — calibration-state 갱신 타이밍.)
3. **dedup**: A 와 B 를 동시에 넣으면 같은 사이클에 이중 발행 금지. 기존 `AbnormalDetector`(`Engine/Abnormal/AbnormalDetector.fs`, ILatchPolicy dedup, `:127`/`:139`)의 (Kind,Target) dedup 를 재사용.

### 범용성
이 수정은 컨베이어 전용 패치가 아니라 **“지연 중 관측 블랙아웃” 클래스 전체**(라인 끝 배출기·단독 스테이션·시퀀스 마지막 무버 등, 자기 정지가 곧 전라인 정지인 device)를 닫는다. 컨베이어는 그 클래스의 최빈 표본일 뿐.

## 8. 부수 사각 (별개 이슈, 판단해서 묶기) — 짧은 device 고정 250ms grace

- ActionOver 임계 T = CalibMax + 1 + **250ms 고정 grace**(`WorkTransitions.fs:55`). CalibMax = rawMax×(1+여유율) 또는 p95(DSPilot `AutoCalibrationService.cs`).
- 고정 250ms 라 **짧은 device 일수록 상대비중 폭증**(μ=200ms→T≈491ms=2.46×; μ=2000ms→1.33×; μ=9000ms→1.23×) → 짧은 device 의 경미한 실지연이 임계 밑으로 떨어져 false negative.
- 이건 §1~7 의 “장행정+블랙아웃” 사각과 **다른** 사각(짧은 device 여유임계 둔감).
- 개선: 고정 grace → **상대 grace**(scan 배수) 또는 임계를 mean 배수(%)/`μ+max(kσ, rμ)+c·scan` 로. 기존 자산: `DeviceDurationLearner`(ActionUnder margin), `ApiSpanMath`(mean/pct, Welford σ), CV.

## 9. 회귀/테스트 체크리스트

- [ ] §6 계측으로 실패 모드(후보1~4) 확정 후 그 원인을 직접 수정했는가.
- [ ] 16:33 재현 시나리오(컨베이어 정지·IN 미도달)에서 ActionOver 가 due 근처(±grace)에 1회 발행되는가.
- [ ] 짧은 device 정상 사이클에서 **오탐 없음**(false positive 0).
- [ ] PLC 통신 끊김/재연결 구간에서 **가짜 ActionOver 없음**(commBlackout 가드 작동).
- [ ] 이중 발행 없음(옵션 A+B 동시 시 dedup).
- [ ] 발행 페이로드 elapsed 는 여전히 `MaxMs+1` 규약(또는 실측으로 바꾸려면 명시적 결정 + 소비측 확인).
- [ ] `engineIsMaxMeasured` 게이트가 정상 동작(미확정 work 발행 안 함), 재캘리브 직후 부당히 닫히지 않는지 확인.
- [ ] 엔진 테스트(`Solutions/Tests/Ds2.Core.Tests/AbnormalTests.fs`) 추가/통과.

## 10. 참고 파일 (검증됨)

- `Engine/EventDriven/Composition/Composition.fs` — `onDeviceDurationExpired`(:319-346, 발행), `handleDeviceOverdueCheck`(:361-363), `engineIsMaxMeasured`(:53/:343/:536-539), `AdvanceSimulation*`(:590-600), `advanceStepRuntime`(:405)
- `Engine/EventDriven/Transitions/WorkTransitions.fs` — `scheduleDeviceOverdueCheck`(:55/:68-85)
- `Engine/EventDriven/Lifecycle/Runtime.fs` — `syncClockToRealTimeWhileRunning`(:156-160), `nextWaitTimeoutMs`(:162-171), `simulationLoop`(:177-185), overdue 디스패치(:81-82)
- `Engine/Core/Scheduling/Scheduler.fs` — `DeviceOverdueCheck`(:15)
- `Engine/Abnormal/AbnormalDetector.fs` — dedup 발행(:127/:139)
- `Engine/Abnormal/MonitoringAbnormalAdapter.fs` / `ControlAbnormalAdapter.fs` — ActionUnder/Control 경로(혼동 방지)
- `Engine/Passive/` — passive 관측 처리(옵션 A 훅 지점). 확정된 파일: `Inference.fs`(로그 `[Infer] obs %… → N action(s)` 소스), `HubSession.fs`, `WorkCycle.fs`, `ModeSession.fs`, `SessionEffects.fs`, `InferenceTypes.fs`. OUT-falling 핸들 정확 함수는 `Inference.fs` 내에서 특정할 것(로그상 OUT falling 은 `obs %QX…=false → 0 action(s)` 로 처리되어 현재 아무 상태전이/overdue 평가를 안 함 — 여기가 훅 지점).
- DSPilot 쪽(참고): `DSPilot/Services/SimulationEngineService.cs:634-635`(제거된 워치독), `cctv-overlays.json`(call 매핑)
- 관련 메모리: `project_dspilot_abnormal_actionover_clock_starvation`, `project_dspilot_abnormal_usertag_calibration_hash_gate`, `project_dspilot_comm_blackout_cycle_invalidation`, `project_dspilot_actionover_arming_ownership`

---
**끝. 확정 사실은 라이브 로그·코드 검증. 미확정(§5)은 반드시 계측으로 확인 후 수정할 것.**
