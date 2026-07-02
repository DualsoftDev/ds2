# 이상감지 준비상태 헤더 표시 — 설계 (2026-07-02)

> 상태: **설계만 (구현 보류)**. 관련: [ABNORMAL_USERTAG_DIAGNOSIS_2026-07-01.md](ABNORMAL_USERTAG_DIAGNOSIS_2026-07-01.md)

## 1. 문제

DSPilot 헤더 실시간 상태에 abnormal 이 뜰 거라 기대했는데 안 뜨는 경우가 있다.
DSPilot 은 abnormal 을 **직접 감지하지 않고** Promaker.Agent 가 보낸 이벤트를 **중계·표시만** 한다
(`_monitoringAbnormal = null`). 따라서 "에이전트가 emit 안 함 = DSPilot 화면에 아무것도 없음".

에이전트가 abnormal 을 안 주는 상태는 사용자에게 **아무 신호 없이 침묵**한다. 이 상태를 헤더에
드러내 "지금 이상감지가 준비 안 됐다"를 사용자가 인지하게 하는 것이 목표.

## 2. abnormal 이 침묵하는 4가지 상태와 데이터 소재

| # | 상태 | 성격 | 소재(source of truth) | DSPilot 단독 가시? |
|---|------|------|----------------------|:---:|
| A | 첫 사이클 대기 (중간 합류/방금 켜짐, OUT rising 미관측) | 런타임 | `MonitoringAbnormalAdapter` — `goingClock`/`everOutRisingSeen` (에이전트 F# 메모리) | ❌ |
| B | 학습 미확정 (실측 < 3사이클, prime 안 됨) | 런타임 | `DeviceDurationLearner.confirmed/provisional` (에이전트 F# 메모리) | ❌ |
| C | 보정 게이트 닫힘 (Work duration 미확정) | 설정/확정 (정적) | `calibration-state.json` + 모델 duration | ✅ (사이드카·모델 둘 다 DSPilot 이 이미 읽음) |
| D | blackout/flapping 억제 중 | 런타임 | 어댑터 억제 플래그 (PLC 끊김은 PlcConnectionStatusTracker 로 부분 인지) | △ |

**핵심 결론**: 사용자가 인지하고 싶은 "에이전트 준비 안 됨" 은 주로 **A·B** 인데, 둘 다
에이전트 메모리에만 존재 → **DSPilot 단독으로는 불가**. C만 DSPilot 단독 가능.

### 2.1 1단계(DSPilot 단독)의 한계 — false green

C만 DSPilot 이 정적 파일로 계산하면, 에이전트가 방금 켜져 아직 한 사이클도 못 봤어도(=A)
사이드카에 이전 확정이 남아 **🟢 "보정 12/12 준비됨"** 으로 뜬다. "설정은 맞다"는 보증일 뿐
"지금 감지가 살아 동작한다"는 보증이 아니다. → 원래 목표 달성엔 **2단계(에이전트 발행) 필수**.

## 3. 설계 — 에이전트 준비상태 발행 (2단계)

### 3.1 신호 정의 (`Ds2.Backend.Common`)

```csharp
// HubMethod 상수 추가
public const string OnAbnormalReadiness = "OnAbnormalReadiness";

// payload (전체 요약 1줄 표시가 목표 → 집계값 중심, 디바이스 배열은 상세용 옵션)
public sealed record AbnormalReadinessStatus(
    string  Overall,          // "ready" | "warming_up" | "learning" | "calib_gap" | "suppressed"
    int     DevicesTotal,     // 평가 대상(canEvaluate) device Work 수
    int     DevicesReady,     // OUT rising 관측 완료 + range 확보 + 게이트 열림
    int     DevicesWarming,   // A: 첫 사이클(OUT rising) 대기
    int     DevicesLearning,  // B: 학습 N/minSamples 진행중 (prime 없음)
    int     DevicesCalibGap,  // C: 보정 미확정으로 게이트 닫힘
    bool    Suppressed,       // D: blackout 억제 활성
    string  StatusUtc);       // ISO8601
```

전체 요약 1줄이 선택된 granularity이므로 **집계 카운트만** 필수. 디바이스별 배열(`Details[]`)은
상세 패널을 원할 때 추후 추가(payload 비대화 방지).

### 3.2 에이전트: 준비상태 산출

`MonitoringAbnormalAdapter` 가 이미 A·B·D의 원천 상태를 들고 있다
([MonitoringAbnormalAdapter.fs](../Solutions/Runtime/Ds2.Runtime/Engine/Abnormal/MonitoringAbnormalAdapter.fs)):

- A: `everOutRisingSeen` / `goingClock` — device별 OUT rising 관측 여부
- B: `DeviceDurationLearner.HasLearned` + `provisional`/`confirmed` + 샘플 수
- D: `InvalidateObservations` 호출 이후 재무장 전 구간

**추가 필요**:
- 어댑터에 `ReadinessSnapshot()` 멤버 신설 — 평가 대상 device Work 순회하며 위 상태 집계
  (learner 샘플 수 노출 위해 `DeviceDurationLearner` 에 `SampleCount(workGuid)` getter 추가).
- C(게이트)는 이미 `isMinMeasured`/`isMaxMeasured` Func 로 주입돼 있음
  ([MonitoringSupervisor.cs:416-417](../Apps/Promaker/Promaker.Agent/MonitoringSupervisor.cs#L416-L417)) → 같은 Func 로 계산.

### 3.3 에이전트: 발행

`EventDrivenEngineRuntimeHubSession` (HubSession) 또는 `MonitoringSupervisor` 에서 주기 broadcast:

- 트리거: (a) 주기 타이머(예: 4s, 헤더 폴링과 정합) 또는 (b) 상태 변화 시(learner 확정/OUT rising 최초 관측).
- 경로: `hubCtx.Clients.All.SendAsync(HubMethod.OnAbnormalReadiness, status)` —
  기존 `broadcastOut`/`OnLearnedDuration` 배선과 동형
  ([MonitoringSupervisor.cs:437-453](../Apps/Promaker/Promaker.Agent/MonitoringSupervisor.cs#L437-L453)).

### 3.4 DSPilot: 수신 → 캐시 → 노출

기존 PLC 상태 파이프라인을 그대로 복제:

| 단계 | 파일 | 변경 |
|------|------|------|
| 수신 | [HubSubscriberService.cs:268](../Apps/DSPilot/DSPilot/Services/HubSubscriberService.cs#L268) | `.On<AbnormalReadinessStatus>(HubMethod.OnAbnormalReadiness, _readinessTracker.Apply)` |
| 캐시 | 신규 `AbnormalReadinessTracker` (PlcConnectionStatusTracker 패턴, TTL/최신값 보관) | 신규 파일 |
| 노출 | [NavController.cs:238](../Apps/DSPilot/DSPilot/Controllers/NavController.cs#L238) `NavAgentDto` | readiness 필드 추가 + GetSummary 채움 |
| 렌더 | [shell.js:823-886](../Apps/DSPilot/DSPilot/wwwroot/app/shell.js#L823-L886) | 팝오버 4번째 줄 |

### 3.5 헤더 표시 (전체 요약 1줄)

도트 색 + 요약 텍스트, 우선순위 = 가장 "안 됨"에 가까운 상태:

```
🟢 이상감지: 준비됨 (12/12)
🟠 이상감지: 준비중 — 첫 사이클 대기 (3)        // A 있음
🟠 이상감지: 준비중 — 학습 2/3 (2)              // B 있음
🔴 이상감지: 일부 비활성 — 보정 미확정 (4)       // C 있음
🔴 이상감지: 억제중 — 통신 불안정              // D
⚫ 이상감지: 에이전트 미연결                    // Hub disconnected (기존 상태 재사용)
```

**false green 방지**: Hub 미연결(⚫)이면 readiness 는 stale 로 간주해 "준비됨" 을 절대 표시하지 않음.
readiness payload 의 `StatusUtc` 가 오래됐으면(폴링 주기의 3배 초과 등) "상태 미상" 처리.

## 4. 작업 범위 요약

- **에이전트(필수)**: `HubMethod` 상수, `AbnormalReadinessStatus` 레코드, adapter `ReadinessSnapshot()`,
  `DeviceDurationLearner.SampleCount`, HubSession 주기 발행. (F# + C#)
- **DSPilot**: `AbnormalReadinessTracker`, HubSubscriber 리스너, `NavAgentDto` 필드, shell.js 1줄.
- **테스트**: adapter ReadinessSnapshot 단위테스트(A/B/C/D 상태 조합), false-green(Hub down·stale) 회귀.

## 5. 미결 결정

1. 발행 트리거: 주기 vs 변화기반 vs 혼합 — 헤더 폴링(4s)과 맞춰 주기 발행이 단순.
2. 상세 패널(device별 배열) 지금 넣을지 vs 요약만 — 현재 granularity 결정 = **요약만**.
3. C(보정 게이트)를 에이전트 발행에 포함 vs DSPilot 자체계산 — 에이전트가 이미 Func 보유하므로
   **에이전트 발행에 포함**해 SSOT 단일화 권장(DSPilot 이중계산 시 모델/사이드카 로드 타이밍 불일치 위험).
4. Control 세션(Promaker 자체 구동)에도 같은 표시가 필요한지 — 현재 목표는 DSPilot 헤더 한정.
