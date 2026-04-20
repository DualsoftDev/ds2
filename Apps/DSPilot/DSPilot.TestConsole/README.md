# DSPilot Test Console

DSPilot 핵심 기능을 테스트하는 2-모드 콘솔 애플리케이션

## 2가지 모드

### 1. Replay Mode (DB → PLC)
**목적**: DB 로그를 PLC에 리플레이하여 가상 현장 환경 재현

**동작**:
- DB에서 plcTagLog 읽기
- TagSpec 동적 생성
- PLCBackendService 시작
- 타임스탬프 간격 유지하며 PLC에 쓰기
- 무한 반복 지원

**사용**:
```bash
dotnet run
# Select: 1
# Enter DB path: C:/ds/DSPilot/sample/db/DsDB.sqlite3
```

### 2. Capture Mode (AASX → PLC → DB + Events)
**목적**: AASX 파일에서 태그 정보를 읽어 PLC 데이터 수집 및 DB 저장

**동작**:
- AASX 파일 로드 (DsStore)
- ApiCall/HwComponent에서 IOTag 추출
- appsettings.json 자동 생성 (TagSpecs)
- log4net 초기화
- SubjectC2S 먼저 구독 (첫 스캔 값 유실 방지)
- ModuleInitializer.Initialize() 호출
  - appsettings.json 로드
  - AppDbApi 자동 생성
  - TagHistoricWAL 자동 생성 (WAL 버퍼)
  - PLCBackendService 자동 생성 및 시작
- 실시간 이벤트 모니터링
- WAL flush로 DB 자동 저장

**사용**:
```bash
dotnet run
# Select: 2
# Enter AASX path: C:/ds/ds2/Apps/DSPilot/DsCSV_0318_C.aasx
# → 자동으로 태그 추출 및 appsettings.json 생성
# → PLC 데이터 수집 시작
# → dsdb_capture.sqlite3에 저장
```

## 실행 방법

```bash
cd /mnt/c/ds/ds2/Apps/DSPilot/DSPilot.TestConsole
dotnet run
```

### 메뉴
```
╔════════════════════════════════════════════╗
║     DSPilot Test Console - 2 Modes         ║
╚════════════════════════════════════════════╝

Select mode:

  1. Replay Mode  (DB → PLC)
  2. Capture Mode (PLC → DB + Events)
  3. DB Verifier
  0. Exit
```

## 파일 구조

```
DSPilot.TestConsole/
├── Program.cs                    # 메인 메뉴
├── ReplayMode.cs                 # DB → PLC 리플레이
├── CaptureMode.cs                # PLC → DB + Events
├── DbVerifier.cs                 # DB 검증 유틸리티
├── appsettings.json              # EV2 설정 (Capture Mode용)
├── log4net.config                # 로깅 설정
└── README.md                     # 이 문서
```

## DB 경로 구분

### Replay Mode (읽기 전용)
- 기본 경로: `C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3`
- Capture Mode에서 수집한 데이터를 읽어서 PLC에 리플레이

### Capture Mode (쓰기 전용)
- 기본 경로: `C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3`
- AASX에서 추출한 태그의 실시간 PLC 데이터를 저장

## appsettings.json 형식 (Capture Mode - 자동 생성)

Capture Mode는 AASX 파일에서 태그를 추출하여 자동으로 appsettings.json을 생성합니다:

```json
{
  "_목적": "DSPilot TestConsole - Capture Mode (AASX → PLC → DB)",
  "_설명": {
    "읽기DB": "ReplayMode에서 사용하는 DB",
    "쓰기DB": "CaptureMode에서 실시간 데이터 저장하는 DB"
  },
  "Database": {
    "Type": "Sqlite",
    "ConnectionString": "Data Source=C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3;Version=3;BusyTimeout=20000"
  },
  "TagHistoric": {
    "WALBufferSize": 1000,
    "FlushInterval": "00:00:05"
  },
  "ScanConfigurations": [
    {
      "Connection": {
        "$type": "Ev2.PLC.Protocol.MX.MxConnectionConfig, Ev2.PLC.Protocol.MX",
        "IpAddress": "192.168.9.120",
        "Port": 5555,
        "Name": "MitsubishiPLC",
        "EnableScan": true,
        "ScanInterval": "00:00:00.500",
        "FrameType": "QnA_3E_Binary",
        "Protocol": "UDP"
      },
      "TagSpecs": [
        {
          "Name": "TagFromAASX",
          "DataType": {"Case": "Bool"},
          "Address": "M100",
          "WALType": {"Case": "Memory"},
          "Comment": "Auto-generated from AASX"
        }
      ]
    }
  ]
}
```

## 핵심 개념

### EV2 ModuleInitializer 패턴
Capture Mode는 EV2의 ModuleInitializer를 사용하여 모든 설정을 자동화:
- `Ev2.Backend.PLC.ModuleInitializer.Initialize(log)` 한 줄로 모든 것이 자동 처리
- appsettings.json에서 설정 로드
- AppDbApi, TagHistoricWAL, PLCBackendService 자동 생성

### SubjectC2S vs SubjectS2C
- **SubjectC2S**: Server → Client (PLC 스캔 결과 수신) - Capture Mode용
- **SubjectS2C**: Client → Server (PLC 쓰기 요청) - Replay Mode용

### TagHistoricWAL
Write-Ahead Log 시스템:
- Memory 버퍼: 빠른 이벤트 처리
- Disk 버퍼: 크래시 안전성
- 주기적 flush로 DB 저장

## 빌드 요구사항

- .NET 9.0 SDK
- NuGet 패키지:
  - System.Reactive 6.1.0
  - Microsoft.Data.Sqlite 9.0.3
  - Dapper 2.1.35
  - log4net 3.2.0

- External DLLs (ExternalDlls/):
  - Ev2.Backend.PLC.dll
  - Ev2.Backend.Common.dll
  - Ev2.PLC.Common.FS.dll
  - Ev2.PLC.Protocol.{AB,MX,S7}.dll
  - Ev2.Core.FS.dll
  - Ev2.Aas.FS.dll
  - Dual.Common.*.dll
  - System.Data.SQLite.dll

## 참고 문서

- **TESTCONSOLE_REORGANIZATION.md**: 상세 아키텍처 설계 문서
