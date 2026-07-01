# 이상감지(Abnormal) 미표시 진단 리포트 — "usertag 등록/삭제" 연관 신고

- **작성일**: 2026-07-01
- **대상**: DSPilot + Promaker.Agent + 공용 Runtime(Ds2.Runtime / Ds2.Backend)
- **증거**: `Apps/DSPilot/PromakerLog/Agent/logs/promaker-agent.log`, `Apps/DSPilot/PromakerLog/ds2-20260701.log`, `C:\ProgramData\DualSoft\Shared\agent\calibration-state.json`, `C:\ProgramData\DualSoft\Shared\PlcConnection.json`, 현재 `project.aasx` 해시
- **판정**: **usertag는 직접 원인이 아님.** 하드 증거로 확정된 두 개의 usertag-무관 억제 요인이 실제 범인.

---

## 0. 신고된 증상

1. AASX에 usertag가 **없는** 경우 → abnormal 4종이 정상으로 뜬다.
2. usertag를 **등록**해 업로드하면 → abnormal이 안 뜨고 **usertag 알람만** 뜬다.
3. usertag를 **지우고 다시 업로드**하면 → 모든 에러가 감지되지 않는다.

---

## 1. 요약 (결론)

| # | 확정 사실 | 증거 |
|---|---|---|
| 1 | **감지 주체는 DSPilot이 아니라 Promaker.Agent다.** DSPilot은 로컬 감지를 끄고(`_monitoringAbnormal = null`) Agent의 `OnAbnormal` SignalR 피드를 화면에 중계만 한다. | `SimulationEngineService.cs:168-175`, `:708` |
| 2 | **ActionOver·ActionUnder(시간계열 2종)는 현재 완전 차단 상태.** 캘리브레이션 사이드카의 AASX 해시가 현재 모델 해시와 불일치 → 게이트 전부 `false`. | `calibration-state.json` vs 현재 `project.aasx` 해시(§3) |
| 3 | **SensorShort·SensorOpen(센서 2종)은 PLC 연결 flapping으로 반복 차단.** PLC가 계속 끊겨 `commBlackout`이 전체 abnormal을 억제. | 로그 13:41~13:58 blackout 반복(§4) |
| 4 | **usertag 알람은 별도 경로(`plcTagLog` 폴링)라 위 게이트/블랙아웃을 안 탄다.** → abnormal이 죽어도 usertag는 살아있어 "usertag만 뜬다". | §5 |
| 5 | **Agent는 정상 기동한다.** 매 activation마다 IOMap 빌드·BackendHost 기동 성공. "Agent가 idle로 죽는다"는 가설은 폐기. | 로그 활성화 시퀀스(§6) |
| 6 | **테스트에 쓴 두 파일은 usertag만 다른 게 아니라 서로 다른 모델이다.** 깨끗한 A/B 비교가 아니었음. | §6 |

> **핵심 메시지**: 사용자가 관찰한 "usertag ↔ abnormal" 상관은 **오귀인(misattribution)**이다. 실제 트리거는 "모델을 편집·재업로드했다"는 사실 자체(→ 해시 변경 → timing 게이트 닫힘)와 "불안정한 PLC 연결"(→ blackout)이며, usertag의 의미와는 무관하다.

---

## 2. 아키텍처 — 감지/억제 위치

```
[PLC] --scan--> [Promaker.Agent : Monitoring 엔진]
                     │  MonitoringAbnormalAdapter : SensorShort / SensorOpen / ActionUnder
                     │  engine device-watchdog     : ActionOver
                     │        │
                     │        └── broadcastAbnormal ── (게이트: blackout, calibration) ──┐
                     │                                                                    ▼
                     └── usertag 주소 scan ──> plcTagLog ─────────(별도 경로)──────> [DSPilot]
                                                                                        │  UserTagAlertService 폴링 → usertag 알람
                                                                    OnAbnormal(SignalR) ─┘  HandleHubAbnormal → 화면 표시(중계만)
```

- **DSPilot은 abnormal을 감지하지 않는다.** `SimulationEngineService.cs:175` 에서 `_monitoringAbnormal = null`. 수신은 `SimulationEngineService.cs:708` `HandleHubAbnormal`.
- **실제 감지는 Agent.** 배선은 `EventDrivenEngineRuntimeHubSession.fs`.
  - ActionUnder: `MonitoringAbnormalAdapter.fs:246` (`isMinMeasured` 게이트)
  - ActionOver: `Composition.fs:343` (`engineIsMaxMeasured` 게이트)
  - SensorShort/SensorOpen: 어댑터, **캘리브레이션 게이트 없음**
- **usertag 알람은 abnormal 파이프라인과 무관**: Agent가 usertag 주소도 스캔 → `plcTagLog` → DSPilot `UserTagAlertService`가 폴링.

---

## 3. 근본 원인 A — 캘리브레이션 해시 불일치 (시간계열 2종 완전 차단) ✅확정

### 3.1 증거 (파일 직접 비교)

| 항목 | 값 |
|---|---|
| `calibration-state.json` → `aasxSha256` (기록 시각 `2026-06-25T00:46:24Z`) | `2CB55787459970C9C5985F51EA042BDE0337EA54461040CE787CCBD6A9B80BFF` |
| **현재** `C:\ProgramData\DualSoft\Shared\project.aasx` 실제 SHA-256 | `A7EDEDADACDA6751C072D286DB5214FCC1271D890E7300549BF5E28BCA5CB8C3` |
| 비교 | **불일치** |

사이드카에 기록된 26개 Work는 **전부 `maxMeasured: true`, `minMeasured: false`** (`measuredAtUtc` 모두 6/25).

### 3.2 코드 경로

1. Agent 기동 시 게이트 함수 구성 — `MonitoringSupervisor.cs:410-413`
   ```csharp
   var calibState = CalibrationState.Load();
   var calibHash  = RuntimeModelHash.compute(session.AasxPath);   // AASX 원본 바이트 SHA-256
   Func<Guid,bool> isMinMeasured = g => calibState.IsMinMeasured(g, calibHash);
   Func<Guid,bool> isMaxMeasured = g => calibState.IsMaxMeasured(g, calibHash);
   ```
2. 게이트 본체 — `CalibrationState.cs:70-81` (해시 불일치면 **전 Work `false`**)
   ```csharp
   public bool IsMaxMeasured(Guid workGuid, string currentAasxSha256) {
       if (string.IsNullOrEmpty(AasxSha256) || AasxSha256 != currentAasxSha256) return false; // stale
       return Works.TryGetValue(Key(workGuid), out var w) && w.MaxMeasured;
   }
   ```
3. 해시 계산은 **파싱 모델이 아니라 파일 원본 바이트** — `HubContracts.fs:256-263`. usertag는 AASX 내부(`LoggingSystemProperties.UserTags`)에 저장되므로 추가/삭제 시 바이트가 바뀌어 해시가 바뀐다.
4. 게이트 소비:
   - ActionOver — `Composition.fs:343` : `if not inActive && engineIsMaxMeasured workGuid then ...`
   - ActionUnder — `MonitoringAbnormalAdapter.fs:246` : `... when ... && isMinMeasured rxWork -> emit`

### 3.3 결과 및 증상 ③과의 정합

- 현재 모델 해시(`A7ED…`) ≠ 사이드카 해시(`2CB5…`) → **ActionOver 전부 stale → 발행 불가.**
- `minMeasured`가 애초에 전부 false → **ActionUnder는 원래부터 OFF.**
- **증상 ③(빼도 복구 안 됨)의 정체**: 6/25 캘리브레이션 이후 모델을 **한 번이라도 편집(usertag 추가/삭제 포함)**하면 해시가 `2CB5…`로 다시는 돌아오지 않는다(파일 바이트가 달라짐). 게이트는 재확정 전까지 영구 닫힘. usertag를 빼도 원복되지 않는다.
- 로그 정합: 전체 로그에서 **ActionOver / ActionUnder / SensorOpen 발행 0건**. (§6)

---

## 4. 근본 원인 B — PLC 연결 flapping (전체 abnormal 반복 차단) ✅확정

### 4.1 증거

`PlcConnection.json`:
```json
{ "vendor": "LsXgi", "name": "PLC#1", "ipAddress": "192.168.9.102", "port": 2004,
  "timeoutMs": 3000, "scanIntervalMs": 10, "isUdp": true, "autoDurationCalibrate": false }
```

`promaker-agent.log` (blackout 타임라인, 발췌):
```
13:41:14  [CommBlackout] PLC down (PLC#1: reconnect failed …192.168.9.102:2004) — abnormal suppressed, observations invalidated
13:41:46  [CommBlackout] PLC down …
13:44:49  [CommBlackout] resync baseline received — REARMING
13:48:08  PLC down …   13:48:55  PLC down …   13:49:38  PLC down …   13:53:46  PLC down …
13:54:18  resync — REARMING
13:54:24  PLC down (all reads failed (PLC connection is not established.))
13:56:49  resync — REARMING   13:56:56  PLC down …   13:57:35  resync — REARMING
13:58:10  [CommBlackout] all mapped calls re-armed — abnormal evaluation fully resumed   ← 전 구간에서 딱 한 번
```

### 4.2 코드 경로

- PLC down 전이 → `commBlackout` 진입 + 관측 무효화 — `EventDrivenEngineRuntimeHubSession.fs:672-689` (`NotifyPlcConnectionAsync`)
- blackout/REARMING 중 **모든 record 억제** — `EventDrivenEngineRuntimeHubSession.fs:72-86` (`isSuppressedByBlackout`), 발행 지점 `:89-125` (`broadcastAbnormal`)
- 해제는 resync 배치 도착 → REARMING → **전 Call이 새 OUT rising을 봐야** 완전 재개.

### 4.3 결과

- blackout 중에는 **게이트 없는 SensorShort/SensorOpen까지 전부 억제**된다.
- 13:41~13:58 구간은 사실상 abnormal 전면 정지. "완전 재개"는 13:58:10에 딱 한 번.
- PLC 미도달(`reconnect failed …192.168.9.102:2004`)이 반복되는 **환경/네트워크 이슈**로 보인다(코드 버그 아님). 단, 불안정 PLC 하에서 abnormal이 신뢰 불가라는 점이 핵심.

---

## 5. "usertag 알람만 뜬다"의 이유

usertag 알람은 **`plcTagLog` 폴링**이라는 별도 경로다(DSPilot `UserTagAlertService`). abnormal의 `commBlackout` 억제·캘리브레이션 게이트를 **전혀 타지 않는다.** 따라서:

- 시간계열 abnormal = 해시 게이트로 OFF
- 센서 abnormal = blackout으로 억제
- **usertag 알람 = 정상 동작**

→ 사용자 눈에는 "abnormal은 죽고 usertag만 뜬다"로 보인다. (증상 ②)

---

## 6. 부수 확정 사실 — Agent 정상 + 두 모델 상이

`promaker-agent.log` 활성화 시퀀스(발췌):

| 시각 | Systems/Flows | IOMap OUT/IN | UserTag 주소 | AutoCalibrate |
|---|---|---|---|---|
| 13:28:34 | 17 / 22 | 26 / 26 | **224** | ON |
| 13:29~13:33 | 17 / 22 | 26 / 26 | 224 | OFF |
| 13:35~13:41 | **22 / 27** | **33 / 33** | **0** | OFF |
| 13:48~14:12 | 17 / 22 | 26 / 26 | 224 | ON |

- **Agent는 매번 정상 기동.** `IOMap built OUT=… IN=…` 항상 출력, `BackendHost … Monitoring` 기동 성공. `Gateway config build failed` / `AASX not found` / `AASX load failed` **0건**. → "빈 게이트웨이로 Agent가 idle 착지" 가설 **폐기**.
- **두 테스트 파일은 서로 다른 모델**:
  - usertag판 = **17 시스템 / 22 플로우 / 26 IO / usertag 224개**
  - `project_noUserTag.aasx` = **22 시스템 / 27 플로우 / 33 IO / usertag 0개** (DSPilot 로그 `ds2-20260701.log:4-6`: `AASX opened … project_noUserTag.aasx`, `SimIndex built: 40 works, 33 calls`)
- usertag만 다른 것이 아니라 **토폴로지가 통째로 다르다** → usertag 단독 효과를 분리할 수 없는 실험이었다.

전체 로그의 abnormal 발행은 **5건, 전부 SensorShort, 13:21:04~13:24:07** 뿐이다(그 이후 0건).

---

## 7. 증상 재해석

| 신고 증상 | 실제 메커니즘 |
|---|---|
| ① usertag 없음 → 정상 | 최초 배포·캘리브레이션 모델(해시 `2CB5…`)에서 ActionOver 무장 + PLC 안정 시절. usertag와 무관. (단 ActionUnder는 그때도 OFF) |
| ② usertag 등록 → abnormal 안 뜨고 usertag만 | 모델 편집으로 **해시 변경 → timing 게이트 닫힘** + 재기동/PLC 재연결로 **blackout**. usertag 알람은 별도 경로라 생존. |
| ③ usertag 삭제·재업로드 → 전부 미감지 | 해시가 또 바뀌어 **여전히 `2CB5…`와 불일치 → timing 영구 OFF** + PLC flapping 지속. "빼도 복구 안 됨"은 해시 게이트의 지문. |

---

## 8. 코드/파일 위치 인덱스

| 관심사 | 위치 |
|---|---|
| DSPilot 로컬 감지 비활성 + OnAbnormal 중계 | `Apps/DSPilot/DSPilot/Services/SimulationEngineService.cs:168-175`, `:708` |
| Agent 게이트 함수 주입 | `Apps/Promaker/Promaker.Agent/MonitoringSupervisor.cs:410-421` |
| 캘리브레이션 stale 게이트 | `Apps/Promaker/Promaker.Shared/CalibrationState.cs:70-81`, 해시 변경 시 전체 clear `:105-108` |
| AASX 원본 바이트 해시 | `Solutions/Backend/Ds2.Backend.Common/HubContracts.fs:256-263` |
| ActionOver 게이트 | `Solutions/Runtime/Ds2.Runtime/Engine/EventDriven/Composition/Composition.fs:343` |
| ActionUnder 게이트 | `Solutions/Runtime/Ds2.Runtime/Engine/Abnormal/MonitoringAbnormalAdapter.fs:246` |
| blackout 억제 | `Solutions/Backend/Ds2.Backend.Runtime/EventDrivenEngineRuntimeHubSession.fs:72-86`, `:89-125`, `:672-689` |
| 캘리브레이션 쓰기(재확정) | `Apps/DSPilot/DSPilot/Services/DsProjectService.cs:439-450` |
| `CalibrationState` 복제본(주의: 2벌 존재) | `Apps/Promaker/Promaker.Shared/CalibrationState.cs` (Agent가 사용) / `Apps/DSPilot/DSPilot/Infrastructure/CalibrationState.cs` |

---

## 9. 권장 조치

### 즉시(운영)
1. **PLC 연결 안정화** — `192.168.9.102:2004`(LsXgi/UDP) 도달성·방화벽·케이블·PLC 상태 확인. flapping이 지속되면 어떤 수정도 abnormal 신뢰성을 담보 못 함.
2. **시간계열 이상 되살리기** — 현재 모델에서 **실측 재확정(캘리브레이션) 1회** 수행 → `calibration-state.json`의 `aasxSha256`가 현재값(`A7ED…`)으로 갱신되어 게이트가 열린다.

### 근본 수정(코드)
3. **duration과 무관한 편집이 캘리브레이션을 무효화하지 않도록** 게이트 기준을 바꾼다. 후보:
   - (a) raw 파일 SHA 대신 **Work duration 관련 부분만의 시맨틱 해시**로 stale 판정.
   - (b) 해시가 바뀌어도 **Work GUID가 생존하면 확정을 승계**(전체 clear 대신 GUID 단위 마이그레이션 — `CalibrationState.cs:105-108`).
   - (c) 최소 대증: usertag CRUD는 재확정 없이 게이트 유지.
   - 주의: `CalibrationState`가 **DSPilot/Promaker.Shared 2벌 복제**이므로 수정 시 동기화 필요.

### 재현/검증
4. **격리 재현** — PLC 안정 + 캘리브 완료 상태에서, **동일 모델에 usertag만 토글**해 재검증. 현재 로그는 모델 스왑·PLAY 재기동·PLC 드롭·AutoCalibrate 토글이 동시에 움직여 변수 분리가 불가능했다.
5. **로그 확인 포인트** — 정상/고장 케이스 각각에서:
   - `IOMap built. OUT=… IN=…` 의 OUT/IN 개수(0이면 IO 매핑 문제)
   - `[CommBlackout]` 발생 여부(PLC 안정성)
   - `[Abnormal발행]` 종류(SensorShort만인지, ActionOver/Under가 나오는지)

---

## 10. 부록 — 증거 원본

### A. calibration-state.json (요지)
- `aasxSha256`: `2CB55787…B80BFF`, 기록 `2026-06-25T00:46:24Z`
- 26 Works, 전부 `maxMeasured:true / minMeasured:false`
- 현재 `project.aasx` SHA-256: `A7EDEDAD…5CB8C3` → **불일치**

### B. PlcConnection.json (요지)
- LsXgi, `192.168.9.102:2004`, UDP, timeout 3000ms, scan 10ms, `autoDurationCalibrate:false`

### C. abnormal 발행 (전체 로그)
- 총 5건, 전부 `SensorShort`, 13:21:04~13:24:07. 이후 0건. ActionOver/ActionUnder/SensorOpen 0건.

### D. blackout (전체 로그)
- 13:41:14 최초 PLC down → 13:58:10 "fully resumed"(유일). 그 사이 down/resync 반복.
