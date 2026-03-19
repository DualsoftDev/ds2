# DSPilot 사이클 추적 시스템 개선 리포트

## 개요

DSPilot의 PLC 실시간 데이터 모니터링 → Flow/Call 상태 추적 → MT/WT/CT 메트릭 계산 파이프라인에서
발견된 문제들을 단계적으로 분석하고 수정한 내역입니다.

---

## 1단계: Flow 상태가 업데이트되지 않는 문제 (레이스 컨디션)

### 증상
- 모든 Flow가 "Ready" 상태에서 변하지 않음
- PLC 데이터가 들어오고 Call 상태는 변하는데, Flow 상태만 갱신 안 됨

### 원인
`PlcDataReaderService`가 시작할 때 `_flowMovingNames` 딕셔너리를 DB에서 캐시로 로드하는데,
이 시점에 `FlowMetricsService`가 아직 MovingStartName/MovingEndName을 DB에 기록하지 않은 상태였음.

```
시간순서:
1. PlcDataReaderService.PostInitializeAsync() → _flowMovingNames 캐시 로드 (비어있음!)
2. FlowMetricsService.InitializeAsync() → MovingStartName/MovingEndName DB에 기록
→ 캐시는 이미 빈 상태로 로드되어, Flow 상태 업데이트 조건이 항상 실패
```

### 수정 내용
**파일: `PlcDataReaderService.cs`**
- `_flowMovingNames` 정적 캐시를 완전히 제거
- `TryUpdateFlowStateAsync`에서 `_dspDbService.Snapshot.Flows`를 직접 읽도록 변경
- Snapshot은 항상 최신 DB 데이터를 반영하므로 캐시 타이밍 문제 해소

```csharp
// 변경 전: 캐시에서 읽음 (빈 캐시 문제)
if (!_flowMovingNames.TryGetValue(flowName, out var names)) return;

// 변경 후: Snapshot에서 직접 읽음 (항상 최신)
var flow = _dspDbService.Snapshot.Flows.FirstOrDefault(f => f.FlowName == flowName);
if (flow == null) return;
```

---

## 2단계: ObjectDisposedException 오류 수정

### 증상
- 페이지 이동 시 `System.ObjectDisposedException` 발생
- `OnDbDataChanged` 이벤트 핸들러에서 이미 Dispose된 컴포넌트에 접근

### 원인
Blazor 컴포넌트가 Dispose된 후에도 `DspDbService.OnDataChanged` 이벤트가 발생하여
`InvokeAsync(StateHasChanged)`를 호출하면서 예외 발생.

### 수정 내용
**파일: `Dashboard.razor`, `Chart.razor`**
- `_disposed` 플래그 추가
- 이벤트 핸들러에서 dispose 체크 + try-catch 보호

```csharp
private bool _disposed;

private async void OnDbDataChanged()
{
    if (_disposed) return;
    _dbSnapshot = DbService.Snapshot;
    try { await InvokeAsync(StateHasChanged); }
    catch (ObjectDisposedException) { }
}

public void Dispose()
{
    _disposed = true;
    DbService.OnDataChanged -= OnDbDataChanged;
}
```

---

## 3단계: Flow 상태 변경이 Snapshot에 반영되지 않는 문제

### 증상
- Flow 상태가 DB에는 업데이트되지만 Dashboard UI에 반영되지 않음
- Call 상태 변경은 UI에 잘 반영됨

### 원인
`DspDbService.TryRefresh()`의 `hasChanged` 판정 로직이 Call 변경만 검사하고
Flow 상태/MT/WT 변경은 검사하지 않았음.

```csharp
// 변경 전: Call만 비교
bool hasChanged = calls.Count != _snapshot.Calls.Count || ...call 비교...;

// Flow 상태가 바뀌어도 hasChanged=false → Snapshot 갱신 안 됨 → UI 갱신 안 됨
```

### 수정 내용
**파일: `DspDbService.cs`**
- `hasChanged` 조건에 Flow.State, Flow.MT, Flow.WT 비교 추가

```csharp
bool hasChanged =
    calls.Count != _snapshot.Calls.Count ||
    flows.Count != _snapshot.Flows.Count ||
    flows.Any(f => !oldFlowsMap.TryGetValue(f.Id, out var old) ||
                   old.State != f.State ||
                   old.MT != f.MT ||
                   old.WT != f.WT) ||
    calls.Any(c => ...기존 Call 비교...);
```

---

## 4단계: 파이프라인 생산 시 MT 오계산 방지 (IsCycleActive)

### 증상
- 파이프라인 방식 생산(이전 제품이 끝나기 전에 다음 제품 투입)에서 MT가 잘못 계산될 가능성

### 원인
다중 Call Flow에서 Head Call이 Tail 완료 전에 다시 Going되면 `CurrentCycleStart`가 덮어써짐.

```
T1: Head Going → CurrentCycleStart = T1 (제품 A)
T3: Head Going → CurrentCycleStart = T3 (제품 B, T1 덮어씀!)
T4: Tail Finish → MT = T4 - T3 (잘못됨! 실제는 T4 - T1)
```

### 수정 내용
**파일: `FlowMetricsService.cs`**
- `FlowCycleState`에 `IsCycleActive` 플래그 추가
- Head Going ~ Tail Finish 사이에서 true로 유지
- 이미 사이클이 진행 중이면 `CurrentCycleStart`를 덮어쓰지 않음

```csharp
// 다중 Call Flow: 진행 중인 사이클이 없을 때만 시작
if (!state.IsCycleActive)
{
    state.CurrentCycleStart = timestamp;
    state.IsCycleActive = true;
}
// else: 파이프라인 상황 - CurrentCycleStart 보호

// Tail 완료 시 플래그 해제
state.IsCycleActive = false;
```

**수정 후 동작:**
```
T1: Head Going → IsCycleActive=true, CurrentCycleStart=T1
T3: Head Going → IsCycleActive=true → CurrentCycleStart 보호! (T1 유지)
T4: Tail Finish → MT = T4 - T1 (올바름), IsCycleActive=false
T5: Head Going → IsCycleActive=false → CurrentCycleStart=T5, WT=T5-T4
```

---

## 5단계: Flow Tail Call 진단 로그 추가

### 증상
- 일부 Flow가 Going 상태에서 영구 고정 (MT/WT = 0s)
- Turn Zone#2, Diverter#3, Check Zone#1, 제품투입, BCR Zone 등

### 원인 분석
F# StateTransition 규칙상 `Going → Finish` 전이 경로:
- **(a)** InTag Rising (hasInTag=true일 때)
- **(b)** OutTag Falling (hasInTag=**false**일 때만)

hasInTag=true이면 (b) 경로가 차단됨.
InTag가 AASX에 정의되고 PLC에도 존재하면 ValidateWithPlcTags가 제거하지 않으므로,
해당 InTag에서 실제 Rising 신호가 안 오면 Call이 Going에서 영구 고정됨.

### 수정 내용
**파일: `PlcDataReaderService.cs`**
- `PostInitializeAsync`에서 `ValidateWithPlcTags` 이후 교차 검증 메서드 호출
- 각 Flow의 Tail Call(MovingEndName)의 PLC 태그 매핑 상태를 점검

```csharp
private void ValidateFlowTailCallMappings(PlcToCallMapperService mapper)
{
    foreach (var flow in _dspDbService.Snapshot.Flows)
    {
        // Tail Call에 PLC 태그 매핑이 없으면 → Flow가 절대 Finish 못함
        // Tail Call에 InTag가 있으면 → InTag Rising이 와야만 Finish 가능
        // 두 경우 모두 [DIAG] 경고 로그 출력
    }
}
```

**실행 시 로그 예시:**
```
[DIAG] Flow 'Turn Zone#2': Tail Call 'SomeCall' has InTag='DI_xxx' → Finish requires InTag Rising
[DIAG] Flow 'Diverter#3': Tail Call 'SomeCall' has NO PLC tag mapping → Flow will never reach Finish
[DIAG] 11 Flow(s) may have Tail Call mapping issues
```

---

## MT/WT/CT 계산 원리 요약

```
사이클 1회차:
  Head Call Going (T1) → Tail Call Finish (T2)
  → MT = T2 - T1 (가동 시간)
  → WT = 아직 없음 (이전 사이클 없으므로)

사이클 2회차:
  Head Call Going (T3)
  → WT = T3 - T2 (대기 시간: 이전 Tail Finish ~ 현재 Head Going)
  → CT = MT + WT (사이클 타임)
  Tail Call Finish (T4)
  → MT = T4 - T3 (이번 사이클 가동 시간)
```

- **MT (Machine Time)**: 1사이클 완료 후 기록 (Head Going → Tail Finish)
- **WT (Wait Time)**: 2사이클 시작 시 기록 (이전 Tail Finish → 현재 Head Going)
- **CT (Cycle Time)**: MT + WT

---

## 수정된 파일 목록

| 파일 | 변경 내용 |
|------|----------|
| `PlcDataReaderService.cs` | _flowMovingNames 캐시 제거, Snapshot 직접 읽기, Tail Call 진단 로그 추가 |
| `DspDbService.cs` | hasChanged에 Flow State/MT/WT 비교 추가 |
| `Dashboard.razor` | ObjectDisposedException 방어 (_disposed 플래그) |
| `Chart.razor` | ObjectDisposedException 방어 (_disposed 플래그) |
| `FlowMetricsService.cs` | IsCycleActive 플래그로 파이프라인 MT 오계산 방지 |

---

## 현재 남은 이슈

일부 Flow가 여전히 Going 상태에서 고정되어 있음.
→ 5단계에서 추가한 `[DIAG]` 로그를 통해 원인 식별 필요.
→ Tail Call의 InTag/OutTag PLC 매핑 상태를 확인하여 AASX 설정 수정 또는 추가 코드 수정 결정.
