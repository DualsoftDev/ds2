# Ev2.Backend.PLC API Summary

## DLL Inspection Results (2026-03-19)

### 1. PLCBackendService

**Namespace**: `Ev2.Backend.PLC`

**Constructor**:
```csharp
new PLCBackendService(
    ScanConfiguration[] scanConfigs,
    FSharpOption<TagHistoricWAL> tagHistoricWAL
)
```

**Methods**:
- `IDisposable Start()` - PLC 스캔 시작
- `void Stop(string name)` - 특정 연결 중지
- `string[] GetTagNames(string connectionName)` - 연결의 태그 이름 목록
- `FSharpOption<TagSpec> TryGetTagSpec(string connectionName, string tagName)` - 태그 스펙 조회
- `FSharpResult<PlcValue, string> RTryReadTagValue(string connectionName, string tagName)` - 태그 값 읽기
- `FSharpResult<Unit, string> RTryWriteTagValue(string connectionName, string tagName, PlcValue value)` - 태그 값 쓰기

**Properties**:
- `string[] ActiveConnections { get; }` - 활성 연결 목록
- `string[] AllConnectionNames { get; }` - 모든 연결 이름

---

### 2. ScanConfiguration

**Namespace**: `Ev2.Backend.Common`

**Constructor**:
```csharp
new ScanConfiguration(
    IConnectionConfiguration connection,
    TagSpec[] tagSpecs
)
```

**Properties**:
- `IConnectionConfiguration Connection`
- `TagSpec[] TagSpecs`

---

### 3. TagSpec

**Namespace**: `Ev2.PLC.Common.TagSpecModule+TagSpec` (F# nested module)

**Constructor**:
```csharp
new TagSpec(
    string name,
    string address,
    PlcDataType dataType,
    FSharpOption<WAL> walType,
    FSharpOption<string> comment,
    FSharpOption<PlcValue> plcValue
)
```

**Properties**:
- `string Name`
- `string Address`
- `PlcDataType DataType`
- `WAL WALType`
- `string Comment`
- `FSharpResult<PlcValue, string> Value`

---

### 4. TagHistoricWAL

**Namespace**: `Ev2.Backend.PLC`

**Constructor**:
```csharp
new TagHistoricWAL(
    int walSize,
    TimeSpan flushInterval,
    MemoryWalBuffer memoryBuffer,
    FileWalBuffer diskBuffer
)
```

**Methods**:
- `void Enqueue(WAL walType, string connectorName, string tagName, string tagAddress, PlcValue value, DateTime timestamp)`
- `void Flush()`
- `void InsertRestartMarker()`
- `void SyncWalTypesFromConfig(ScanConfiguration[] scanConfigs)`
- `void LoadLastTagValues()`

**Properties**:
- `int TotalCount { get; }`
- `bool ShouldFlush { get; }`

---

### 5. TagLogEntry

**Namespace**: `Ev2.Backend.PLC`

**Constructor**:
```csharp
new TagLogEntry(
    string connectorName,
    string tagName,
    string tagAddress,
    string tagDataType,
    string valueJson,
    DateTime timestamp,
    string walType
)
```

**Properties**:
- `string ConnectorName`
- `string TagName`
- `string TagAddress`
- `string TagDataType`
- `string ValueJson`
- `DateTime Timestamp`
- `string WalType`

---

## Architecture Analysis

### Ev2.Backend.PLC는 **WAL 기반** 아키텍처

- SubjectC2S나 IObservable 같은 Reactive 타입이 **존재하지 않음**
- Write-Ahead Log (WAL) 패턴 사용
- `PLCBackendService`가 PLC 통신의 메인 진입점
- `TagHistoricWAL`이 태그 변경 이력을 WAL에 기록

### DSPilot 통합 전략

1. **Ev2PlcEventSource 재설계**:
   - PLCBackendService를 래핑
   - 주기적 polling 대신 PLCBackendService.Start()로 스캔 시작
   - TagHistoricWAL에서 이벤트를 읽어서 Subject로 변환

2. **ScanConfiguration 생성**:
   - IConnectionConfiguration 구현체 필요 (타입 미발견 - 추가 조사 필요)
   - TagSpec[] 생성 (AASX 파일에서 태그 정보 파싱)

3. **WAL 통합**:
   - MemoryWalBuffer, FileWalBuffer 생성
   - TagHistoricWAL을 통해 이력 관리
   - DSPilot의 CallIOEvent와 매핑

## 다음 단계

1. ✅ DLL 타입 분석 완료
2. ⏳ IConnectionConfiguration 구현체 찾기 또는 생성
3. ⏳ Ev2PlcEventSource 재구현 (PLCBackendService 기반)
4. ⏳ AASX에서 TagSpec 생성
5. ⏳ 실제 PLC 연결 테스트
