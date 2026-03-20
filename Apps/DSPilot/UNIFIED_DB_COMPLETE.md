# 단일 데이터베이스 통합 모드 구현 완료

## 🎯 목표 달성

**단일 `plc.db` 파일로 EV2 + DSPilot 데이터 통합 저장**

## ✅ 완료된 작업

### 1. 데이터베이스 스키마 설계

#### EV2 테이블 (기존 유지)
- `flow`, `call`, `work` - EV2 프로세스 정의
- `plc`, `plcTag`, `plcTagLog` - PLC 데이터 수집

#### DSPilot 테이블 (신규 추가)
- `dspFlow` - DSP 플로우 메트릭 (MT/WT/CT, State)
- `dspCall` - DSP Call 상태 + 통계 (ProgressRate, GoingTime 등)
- `dspCallIOEvent` - Cycle 분석 이벤트

**핵심**: DSPilot 테이블이 EV2 테이블과 **완전히 독립**적으로 설계됨

### 2. 스키마 생성 서비스

**파일**: `DSPilot/Services/Ev2BootstrapService.cs`

```csharp
public class Ev2BootstrapService : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. EV2 base schema (placeholder - PlcCaptureService가 생성)
        await InitializeEv2SchemaAsync();

        // 2. DSPilot extension schema (dspFlow, dspCall, dspCallIOEvent)
        await InitializeDspSchemaAsync();
    }
}
```

### 3. 안전한 데이터베이스 액세스

#### DspRepository.cs
**모든 쿼리 메서드에 테이블 존재 확인 추가**:

```csharp
private async Task<bool> TablesExistAsync(SqliteConnection connection)
{
    var sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (@flowTable, @callTable)";
    var count = await connection.ExecuteScalarAsync<int>(sql, new { flowTable = _flowTable, callTable = _callTable });
    return count >= 2;
}

public async Task<string> GetCallStateAsync(CallKey key)
{
    using var connection = CreateConnection();
    await connection.OpenAsync();

    // ✅ 테이블 존재 확인
    if (!await TablesExistAsync(connection))
    {
        _logger.LogDebug("Tables do not exist yet, returning default state");
        return "Ready";
    }

    // 정상 쿼리 실행
    var sql = $"SELECT State FROM {_callTable} WHERE FlowName = @FlowName ...";
    // ...
}
```

#### DspDbService.cs
**DB 파일 및 테이블 존재 확인**:

```csharp
private void TryRefresh()
{
    try
    {
        // ✅ DB 파일 존재 확인
        if (!File.Exists(_dbPath))
        {
            _logger.LogDebug("DB file not found: {Path}", _dbPath);
            return;
        }

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // ✅ 테이블 존재 확인
        if (!TableExists(conn, _flowTable) || !TableExists(conn, _callTable))
        {
            _logger.LogDebug("Tables do not exist yet. Waiting for schema initialization.");
            return;
        }

        // 정상 쿼리 실행
        // ...
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to read dsp.db");
    }
}

private bool TableExists(SqliteConnection connection, string tableName)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName";
    cmd.Parameters.AddWithValue("@tableName", tableName);
    var count = (long)(cmd.ExecuteScalar() ?? 0L);
    return count > 0;
}
```

### 4. 동적 테이블 이름 지원

**Unified Mode**:
- 테이블: `dspFlow`, `dspCall`, `dspCallIOEvent`
- DB 파일: `%APPDATA%/Dualsoft/DSPilot/plc.db`

**Split Mode**:
- 테이블: `Flow`, `Call`, `CallIOEvent`
- DB 파일: `sample/db/dsp.db`

```csharp
public DspRepository(IDatabasePathResolver pathResolver, ILogger<DspRepository> logger)
{
    var dbPath = pathResolver.GetDspDbPath();
    var isUnified = pathResolver.IsUnified;

    // ✅ 모드에 따라 테이블 이름 자동 선택
    _flowTable = isUnified ? "dspFlow" : "Flow";
    _callTable = isUnified ? "dspCall" : "Call";
    _callIOEventTable = isUnified ? "dspCallIOEvent" : "CallIOEvent";
}
```

### 5. 설정 파일

**appsettings.json**:
```json
{
  "Database": {
    "Unified": true,
    "SharedDbPath": "%APPDATA%/Dualsoft/DSPilot/plc.db"
  },
  "DspDatabase": {
    "Path": "%APPDATA%/Dualsoft/DSPilot/plc.db",
    "AutoCreate": true,
    "RecreateOnStartup": false
  }
}
```

## 🔄 시스템 흐름

1. **앱 시작** → `Program.cs`
2. **서비스 등록** → `Ev2BootstrapService` (IHostedService)
3. **스키마 생성** → `InitializeDspSchemaAsync()`
   - `dspFlow` 테이블 생성
   - `dspCall` 테이블 생성
   - `dspCallIOEvent` 테이블 생성
   - 인덱스 생성
4. **서비스 초기화** → `DspDbService`, `DspRepository` 등
5. **안전한 작동**:
   - 테이블 없음 → 로그만 남기고 대기
   - 테이블 있음 → 정상 작동
   - 주기적 재시도 (1초마다)

## 📊 테스트 결과

### QuickTest 실행 결과
```
=== Unified Database Mode Quick Test ===

Step 1: Initialize DatabasePathResolver
  DB Path: C:\Users\dual\AppData\Roaming\Dualsoft\DSPilot\plc.db
  Unified Mode: True

Step 2: Bootstrap Database Schema
  ✓ Schema created

Step 3: Test Repository Operations
  ✓ Inserted flows
  ✓ Inserted calls
  ✓ Retrieved: TestCall1 (State: Ready)

=== ALL TESTS PASSED ===
```

### 생성된 DB 파일
- **위치**: `C:\Users\dual\AppData\Roaming\Dualsoft\DSPilot\plc.db`
- **크기**: 5.6MB
- **테이블 수**: 30개 (EV2 + DSPilot)

### 테이블 목록 (일부)
```
- apiCall, apiCallState, apiDef
- call, callState
- dspCall          ← DSPilot
- dspCallIOEvent   ← DSPilot
- dspFlow          ← DSPilot
- flow, flowState
- plc, plcTag, plcTagLog
- work, system, project
```

## 🛡️ 안전성 보장

### 예외 처리
✅ **DB 파일 없음**: 조기 반환, 예외 없음
✅ **테이블 없음**: 조기 반환, 예외 없음
✅ **스키마 초기화 대기**: 로그만 남기고 안전하게 대기
✅ **주기적 재시도**: 1초마다 자동 재확인

### 로그 수준
- **Debug**: 테이블 없음 등 정상 상태
- **Warning**: DB 읽기 실패 등 일시적 문제
- **Error**: 심각한 오류 (재시도 불가능)

## 📁 수정된 파일 목록

### 스키마 생성
- `DSPilot/Services/Ev2BootstrapService.cs`

### 데이터베이스 액세스
- `DSPilot/Repositories/DspRepository.cs` - 테이블 존재 확인 추가
- `DSPilot/Services/DspDbService.cs` - 테이블 존재 확인 추가

### 설정 및 경로
- `DSPilot/Services/DatabasePathResolver.cs`
- `DSPilot/appsettings.json`

### 테스트
- `DSPilot/QuickTest/Program.cs` - 통합 테스트

## 🚀 배포 방법

1. **기존 DB 백업** (선택사항)
   ```
   xcopy "%APPDATA%\Dualsoft\DSPilot\*.db" backup\
   ```

2. **앱 실행**
   ```
   dotnet run --project DSPilot/DSPilot.csproj
   ```

3. **자동 스키마 생성**
   - `Ev2BootstrapService`가 자동으로 스키마 생성
   - 로그 확인: "DSPilot schema initialized"

4. **정상 작동 확인**
   - Dashboard 페이지 로드
   - DB 파일 확인: `%APPDATA%\Dualsoft\DSPilot\plc.db`

## 🎯 주요 장점

1. **단일 파일**: 모든 데이터가 `plc.db` 하나로 통합
2. **Clean Separation**: DSP와 EV2 테이블 완전 분리
3. **안전성**: 테이블 없어도 예외 없이 안전하게 대기
4. **하위 호환성**: Split 모드도 계속 지원
5. **자동 초기화**: 첫 실행 시 자동으로 스키마 생성

## ✨ 완료!

**모든 예외 처리가 완료되어 안전하게 작동합니다!** 🎉
