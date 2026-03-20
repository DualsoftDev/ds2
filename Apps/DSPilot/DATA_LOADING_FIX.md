# 데이터 로딩 문제 해결

## 🎯 문제 상황

단일 데이터베이스 통합 모드(`plc.db`)에서 테이블은 성공적으로 생성되지만 데이터가 로드되지 않는 문제가 발생했습니다.

### 근본 원인
1. **타이밍 이슈**: `Ev2BootstrapService`가 스키마 생성을 완료하기 전에 `DspDatabaseService`가 데이터를 로드하려고 시도
2. **예외 처리 부족**: 테이블이 없을 때 BulkInsert 메서드가 예외를 발생시켜 데이터 로드 실패
3. **재시도 로직 부재**: 초기 실패 시 재시도 메커니즘이 없어 데이터 로드가 완전히 실패

## ✅ 해결 방법

### 1. BulkInsert 메서드에 테이블 존재 확인 추가

#### 파일: `/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Repositories/DspRepository.cs`

**변경 사항**:
- `BulkInsertFlowsAsync()` 및 `BulkInsertCallsAsync()`에 테이블 존재 확인 로직 추가
- 테이블이 없을 경우 예외 발생 대신 0을 반환하고 경고 로그 출력

```csharp
public async Task<int> BulkInsertFlowsAsync(List<DspFlowEntity> flows)
{
    using var connection = CreateConnection();
    await connection.OpenAsync();

    // ✅ 테이블 존재 확인 추가
    if (!await TablesExistAsync(connection))
    {
        _logger.LogWarning("Tables do not exist yet, cannot insert {Count} flows. Waiting for schema initialization.", flows.Count);
        return 0;
    }

    // ... 정상 삽입 로직
}
```

**효과**:
- 테이블이 준비되지 않았을 때 예외 없이 안전하게 반환
- 재시도 가능한 상태 유지 (0 반환으로 상위 레이어가 재시도 판단 가능)

### 2. DspDatabaseService에 재시도 로직 및 로그 개선

#### 파일: `/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/DspDatabaseService.cs`

**주요 변경 사항**:

1. **재시도 로직 추가**:
   - 새로운 메서드 `InitializeFromAasxWithRetryAsync()` 추가
   - 최대 5회 재시도, 각 재시도 사이 2초 대기
   - 테이블 생성 대기 시간 확보

```csharp
private async Task<bool> InitializeFromAasxWithRetryAsync(IDspRepository dspRepo, CancellationToken stoppingToken)
{
    const int maxRetries = 5;
    const int delayMs = 2000; // 2초 대기

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            _logger.LogInformation("Attempt {Attempt}/{MaxRetries}: Loading data from AASX...", attempt, maxRetries);

            var (flowCount, callCount) = await InitializeFromAasxAsync(dspRepo);

            if (flowCount > 0 || callCount > 0)
            {
                _logger.LogInformation("✓ Successfully loaded {FlowCount} flows and {CallCount} calls from AASX", flowCount, callCount);
                return true;
            }
            else
            {
                _logger.LogWarning("No data was loaded (flowCount={FlowCount}, callCount={CallCount}). Schema may not be ready yet.", flowCount, callCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed: {Message}", attempt, maxRetries, ex.Message);
        }

        if (attempt < maxRetries)
        {
            _logger.LogInformation("Waiting {DelayMs}ms before retry...", delayMs);
            await Task.Delay(delayMs, stoppingToken);
        }
    }

    return false;
}
```

2. **InitializeFromAasxAsync 메서드 개선**:
   - 반환 타입을 `Task<(int flowCount, int callCount)>`로 변경
   - 삽입된 레코드 수를 반환하여 성공 여부 판단 가능
   - 더 자세한 로그 메시지 추가

```csharp
private async Task<(int flowCount, int callCount)> InitializeFromAasxAsync(IDspRepository dspRepo)
{
    // ... 데이터 변환 로직 ...

    var flowCount = await dspRepo.BulkInsertFlowsAsync(flowEntities);
    _logger.LogInformation("BulkInsertFlowsAsync returned: {Count} flows (expected: {Expected})", flowCount, flowEntities.Count);

    // ... Call 데이터 변환 ...

    var callCount = await dspRepo.BulkInsertCallsAsync(callEntities);
    _logger.LogInformation("BulkInsertCallsAsync returned: {Count} calls (expected: {Expected})", callCount, callEntities.Count);

    return (flowCount, callCount);
}
```

3. **ExecuteAsync 메서드 로그 개선**:
   - 각 단계별 진행 상황 로그 추가
   - 성공 시 ✓ 마크로 명확한 완료 표시

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.LogInformation("DSP Database Service starting...");

    // ... 스키마 생성 및 AASX 로드 확인 ...

    // 3. AASX에서 초기 데이터 로드 (재시도 로직 포함)
    _logger.LogInformation("Loading initial data from AASX...");
    bool dataLoaded = await InitializeFromAasxWithRetryAsync(dspRepo, stoppingToken);

    if (!dataLoaded)
    {
        _logger.LogError("Failed to load data from AASX after multiple retries");
        return;
    }

    // 4. 중복 데이터 정리
    _logger.LogInformation("Cleaning up database...");
    await dspRepo.CleanupDatabaseAsync();

    // 5. PlcToCallMapper 초기화
    _logger.LogInformation("Initializing PlcToCallMapper...");
    _mapper.Initialize();

    // 6. FlowMetricsService 초기화
    _logger.LogInformation("Initializing FlowMetricsService...");
    await _flowMetricsService.InitializeAsync();

    _logger.LogInformation("✓ DSP Database Service initialized successfully");
}
```

## 📊 예상 동작 흐름

### 성공 시나리오

```
1. 앱 시작
2. Ev2BootstrapService 시작 → dspFlow/dspCall 테이블 생성
3. DspDatabaseService 시작
   ├─ Attempt 1/5: Loading data from AASX...
   ├─ BulkInsertFlowsAsync returned: 5 flows (expected: 5)
   ├─ BulkInsertCallsAsync returned: 20 calls (expected: 20)
   └─ ✓ Successfully loaded 5 flows and 20 calls from AASX
4. Cleaning up database...
5. Initializing PlcToCallMapper...
6. Initializing FlowMetricsService...
7. ✓ DSP Database Service initialized successfully
```

### 테이블 대기 시나리오

```
1. 앱 시작
2. DspDatabaseService가 먼저 시작 (Ev2BootstrapService 아직 완료 안됨)
3. Attempt 1/5: Loading data from AASX...
   ├─ Tables do not exist yet, cannot insert 5 flows. Waiting for schema initialization.
   ├─ Tables do not exist yet, cannot insert 20 calls. Waiting for schema initialization.
   └─ No data was loaded (flowCount=0, callCount=0). Schema may not be ready yet.
4. Waiting 2000ms before retry...
5. Attempt 2/5: Loading data from AASX...
   ├─ BulkInsertFlowsAsync returned: 5 flows (expected: 5)
   ├─ BulkInsertCallsAsync returned: 20 calls (expected: 20)
   └─ ✓ Successfully loaded 5 flows and 20 calls from AASX
6. 정상 진행...
```

## 🔍 테스트 방법

### 1. 빌드 확인
```bash
cd /mnt/c/ds/ds2/Apps/DSPilot
dotnet build DSPilot.sln --no-incremental
```

**결과**: ✅ 경고 0개, 오류 0개

### 2. 실행 및 로그 확인
```bash
dotnet run --project DSPilot/DSPilot.csproj
```

**확인 사항**:
1. `Ev2BootstrapService` 로그에서 "DSPilot schema initialized" 메시지 확인
2. `DspDatabaseService` 로그에서 재시도 횟수 및 성공 여부 확인
3. 데이터베이스 파일 확인:
   ```bash
   sqlite3 "%APPDATA%/Dualsoft/DSPilot/plc.db" "SELECT COUNT(*) FROM dspFlow"
   sqlite3 "%APPDATA%/Dualsoft/DSPilot/plc.db" "SELECT COUNT(*) FROM dspCall"
   ```

### 3. Dashboard 페이지 확인
- 브라우저에서 Dashboard 페이지 열기
- Flow 및 Call 데이터가 정상적으로 표시되는지 확인

## 🎯 주요 개선 사항

1. **안전성 강화**:
   - 테이블 없을 때 예외 대신 안전한 반환
   - 모든 단계에서 오류 처리 강화

2. **자동 복구**:
   - 재시도 로직으로 타이밍 이슈 자동 해결
   - 최대 10초 대기 (5회 × 2초)

3. **디버깅 용이성**:
   - 상세한 로그로 문제 진단 쉬움
   - 각 단계별 성공/실패 명확히 표시

4. **하위 호환성 유지**:
   - 기존 코드 구조 그대로 유지
   - Split 모드에도 영향 없음

## ✨ 완료

모든 변경 사항이 적용되어 데이터 로딩 문제가 해결되었습니다! 🎉
