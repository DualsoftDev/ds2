# Phase 5: 성능 최적화 및 정리

**우선순위**: 하  
**예상 기간**: 2-3일  
**목표**: 코드 품질 향상 및 성능 최적화

---

## 📋 작업 개요

사용하지 않는 코드 제거, DB 쿼리 최적화, 렌더링 성능 개선, 에러 처리 강화를 통해 프로덕션 수준의 코드 품질을 달성합니다.

---

## 🎯 작업 항목

### 1. 사용하지 않는 서비스 제거

#### 1.1. 제거 대상 파일 목록
- `InMemoryCallStateStore.cs` - DB 기반으로 변경되어 불필요
- `Ev2PlcEventSource.cs` - PlcCaptureService로 통합
- 중복 모델 파일 정리

#### 1.2. 의존성 정리
**파일**: `Program.cs`

```csharp
// 제거할 서비스
// builder.Services.AddSingleton<InMemoryCallStateStore>();
```

### 2. DB 쿼리 최적화

#### 2.1. 인덱스 추가
**파일**: `DSPilot.Engine/Database/Initialization.fs`

```fsharp
// 인덱스 생성 SQL 추가
let createIndexSql = """
CREATE INDEX IF NOT EXISTS idx_dspCall_CallName ON dspCall(CallName);
CREATE INDEX IF NOT EXISTS idx_dspCall_State ON dspCall(State);
CREATE INDEX IF NOT EXISTS idx_dspCall_FlowId ON dspCall(FlowId);
CREATE INDEX IF NOT EXISTS idx_dspCall_Direction ON dspCall(Direction);
CREATE INDEX IF NOT EXISTS idx_dspFlow_FlowName ON dspFlow(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspFlow_State ON dspFlow(State);
"""
```

#### 2.2. 쿼리 배치 처리
```csharp
// 여러 Call 상태를 한번에 조회
public async Task<List<CallData>> GetCallsByStateAsync(string state)
{
    var sql = "SELECT * FROM dspCall WHERE State = @State";
    return (await _connection.QueryAsync<CallData>(sql, new { State = state })).ToList();
}

// 한번에 업데이트
public async Task UpdateCallStatesAsync(List<(Guid CallId, string State)> updates)
{
    var sql = "UPDATE dspCall SET State = @State WHERE Id = @CallId";
    await _connection.ExecuteAsync(sql, updates.Select(u => new { CallId = u.CallId, State = u.State }));
}
```

### 3. 렌더링 최적화

#### 3.1. 가상화 (Virtualization)
**파일**: `Components/Pages/FlowWorkspace.razor`

```razor
@* 큰 테이블에 가상화 적용 *@
<Microsoft.AspNetCore.Components.Web.Virtualization.Virtualize Items="@_callRows" Context="row">
    <tr>
        <td>@row.CallName</td>
        <td><span class="state-badge state-@row.State.ToLower()">@row.State</span></td>
        <td>@row.CycleCount</td>
        <td>@FormatDuration(row.MT)</td>
        <td>@FormatDuration(row.WT)</td>
        <td>@FormatDuration(row.CT)</td>
    </tr>
</Microsoft.AspNetCore.Components.Web.Virtualization.Virtualize>
```

#### 3.2. ShouldRender 최적화
```csharp
private bool _shouldRender = true;

protected override bool ShouldRender()
{
    if (!_shouldRender)
        return false;

    _shouldRender = false;
    return true;
}

private void TriggerRender()
{
    _shouldRender = true;
    StateHasChanged();
}
```

### 4. 에러 처리 강화

#### 4.1. Global Exception Handler
**새 파일**: `DSPilot/ErrorBoundary.razor`

```razor
@inherits ErrorBoundaryBase
@inject NotificationService NotificationService
@inject ILogger<ErrorBoundary> Logger

@if (CurrentException is not null)
{
    <div class="error-boundary">
        <div class="error-content">
            <span class="material-icons error-icon">error_outline</span>
            <h5>오류가 발생했습니다</h5>
            <p>@CurrentException.Message</p>
            <button class="btn btn-filled" @onclick="Recover">
                <span class="material-icons">refresh</span>
                다시 시도
            </button>
        </div>
    </div>
}
else
{
    @ChildContent
}

@code {
    protected override void OnError(Exception exception)
    {
        Logger.LogError(exception, "Unhandled exception in component");
        NotificationService.ShowError($"오류 발생: {exception.Message}");
    }
}
```

#### 4.2. Try-Catch 표준화
```csharp
// 모든 비동기 메서드에 표준 에러 처리
private async Task<T> ExecuteWithErrorHandling<T>(Func<Task<T>> operation, T defaultValue, string operationName)
{
    try
    {
        return await operation();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error in {OperationName}", operationName);
        _notificationService.ShowError($"{operationName} 중 오류 발생");
        return defaultValue;
    }
}
```

### 5. 로깅 개선

#### 5.1. 구조화된 로깅
```csharp
// 기존
_logger.LogInformation($"Call {callName} state changed to {newState}");

// 개선
_logger.LogInformation(
    "Call state changed: {CallName} → {NewState} (Direction: {Direction}, Duration: {DurationMs}ms)",
    callName, newState, direction, durationMs);
```

#### 5.2. 로그 레벨 조정
**파일**: `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "DSPilot.Services.PlcEventProcessorService": "Debug",
      "DSPilot.Services.MonitoringBroadcastService": "Debug",
      "DSPilot.Engine": "Information"
    }
  }
}
```

### 6. 메모리 최적화

#### 6.1. IDisposable 구현 확인
```csharp
// 모든 서비스에 Dispose 패턴 적용
public class MyService : IDisposable
{
    private bool _disposed = false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 관리 리소스 해제
            _subscription?.Dispose();
        }

        _disposed = true;
    }
}
```

#### 6.2. 메모리 프로파일링
```bash
# dotMemory 또는 Visual Studio 프로파일러 사용
# 메모리 누수 확인
# 장기 실행 후 메모리 사용량 모니터링
```

---

## 📊 성능 벤치마크

### 1. 목표 지표
- [ ] 페이지 로드 시간: < 1초
- [ ] PLC 이벤트 처리: < 50ms
- [ ] DB 쿼리: < 100ms
- [ ] SignalR 레이턴시: < 200ms

### 2. 측정 방법
```csharp
// Stopwatch를 사용한 성능 측정
var sw = Stopwatch.StartNew();
await operation();
sw.Stop();
_logger.LogInformation("Operation completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
```

---

## 📝 구현 체크리스트

- [ ] 사용하지 않는 서비스 제거
- [ ] DB 인덱스 추가
- [ ] 쿼리 배치 처리 구현
- [ ] 가상화 적용
- [ ] ShouldRender 최적화
- [ ] Global Error Boundary 구현
- [ ] Try-Catch 표준화
- [ ] 구조화된 로깅 적용
- [ ] IDisposable 구현 확인
- [ ] 메모리 프로파일링
- [ ] 성능 벤치마크 측정
- [ ] 빌드 0 warnings 달성

---

## ✅ 완료 기준

- 빌드 경고 0개
- 모든 단위 테스트 통과
- 성능 벤치마크 목표 달성
- 메모리 누수 없음
- 에러 처리 완비

---

## 🎉 Phase 5 완료 후

**전체 리팩토링 완료!**

`REFACTORING_PROGRESS.md`에 최종 결과 기록
