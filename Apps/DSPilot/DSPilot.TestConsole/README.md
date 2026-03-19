# DSPilot.TestConsole

DSPilot의 Ev2.Backend.PLC 통합을 위한 테스트 콘솔입니다.

## 목적

1. **DLL Inspection**: Ev2.Backend.PLC 및 관련 DLL의 타입 구조 분석
2. **API 사용법 학습**: PLCBackendService의 실제 사용 방법 예제
3. **통합 검증**: DSPilot과 Ev2.Backend.PLC 간의 통합 가능성 검증

## 실행 방법

```bash
cd /mnt/c/ds/ds2/Apps/DSPilot/DSPilot.TestConsole
dotnet run
```

## 주요 파일

### 1. Program.cs
- 메인 진입점
- DLL inspection, 타입 탐색, 사용 예제 실행

### 2. DllInspector.cs
- Reflection을 사용한 DLL 타입 분석
- Public 타입, 생성자, 메서드, 프로퍼티 출력

### 3. Ev2TypeExplorer.cs
- PLCBackendService, TagHistoricWAL, ScanConfiguration 등 핵심 타입 탐색
- 컴파일 타임에 타입 정보 추출

### 4. ConnectionConfigExplorer.cs
- IConnectionConfiguration 인터페이스 및 구현체 탐색
- S7ConnectionConfig, AbConnectionConfig, MxConnectionConfig 발견

### 5. PLCBackendServiceExample.cs
- **실제 사용 가능한 코드 예제**
- Allen-Bradley PLC 연결 설정
- TagSpec 생성
- PLCBackendService 인스턴스화 및 사용법

### 6. EV2_API_SUMMARY.md
- DLL inspection 결과 종합 문서
- API 요약 및 사용법 가이드

## 발견 사항

### Ev2.Backend.PLC는 WAL 기반 아키텍처

이전에 가정했던 Reactive(SubjectC2S, IObservable) 패턴이 **존재하지 않습니다**.

실제 아키텍처:
- **PLCBackendService**: PLC 통신의 메인 진입점
- **TagHistoricWAL**: Write-Ahead Log를 통한 태그 변경 이력 관리
- **ScanConfiguration**: PLC 연결 및 태그 스펙 설정
- **IConnectionConfiguration**: 프로토콜별 연결 설정 (S7, AB, MX 등)

### 주요 타입

```csharp
// PLCBackendService 생성
var service = new PLCBackendService(
    scanConfigs: ScanConfiguration[],
    tagHistoricWAL: FSharpOption<TagHistoricWAL>
);

// 사용
service.Start();
var result = service.RTryReadTagValue("ConnectionName", "TagName");

// 연결 설정 예제 (Allen-Bradley)
var config = AbConnectionConfig.Create(
    ipAddress: "192.168.1.100",
    port: FSharpOption<int>.Some(44818),
    name: FSharpOption<string>.Some("PLC1"),
    //...
);

// TagSpec 생성
var tagSpec = new TagSpec(
    name: "Flow1_Call1_In",
    address: "DB1.DBX0.0",
    dataType: PlcDataType.Bool,
    walType: FSharpOption<WAL>.None,
    comment: FSharpOption<string>.Some("Input signal"),
    plcValue: FSharpOption<PlcValue>.None
);
```

## 다음 단계

1. ✅ **DLL 타입 분석 완료**
2. ✅ **IConnectionConfiguration 구현체 발견** (S7, AB, MX)
3. ✅ **PLCBackendService 사용 예제 작성 완료**
4. ⏳ **Ev2PlcEventSource 재구현** - PLCBackendService 기반으로 재설계 필요
5. ⏳ **AASX 파일에서 TagSpec 생성** - 태그 매핑 로직 구현
6. ⏳ **실제 PLC 연결 테스트** - 실제 하드웨어 또는 시뮬레이터로 검증

## 빌드 요구사항

- .NET 9.0 SDK
- 필수 NuGet 패키지:
  - System.Reactive 6.1.0 (중요: 6.0.0과 호환 안됨)
  - StackExchange.Redis 2.8.16
  - Newtonsoft.Json 13.0.3

- 필수 External DLLs:
  - Ev2.Backend.PLC.dll
  - Ev2.Backend.Common.dll
  - Ev2.PLC.Common.FS.dll
  - Ev2.PLC.Protocol.AB.dll
  - Ev2.PLC.Protocol.S7.dll
  - Ev2.PLC.Protocol.MX.dll

## 실행 결과

```
=== DSPilot Ev2.Backend.PLC DLL Inspector ===
=== Ev2 Type Explorer (compile-time) ===
=== PLCBackendService Usage Example ===

Created connection config:
  Type: AbConnectionConfig

Created 3 TagSpec(s)
  - Flow1_Call1_In @ DB1.DBX0.0
  - Flow1_Call1_Out @ DB1.DBX0.1
  - Flow2_Call1_In @ Program:MainProgram.Flow2_Call1_In

Created PLCBackendService
  Active connections:
  All connection names: TestPLC

✅ PLCBackendService successfully created and configured
```

## 참고자료

- **EV2_API_SUMMARY.md**: 전체 API 요약
- **PLCBackendServiceExample.cs**: 실제 코드 예제
- Ev2.Backend.PLC 소스코드 (F#)
