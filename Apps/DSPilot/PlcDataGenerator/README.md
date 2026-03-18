# PlcDataGenerator

실제 PLC 설비 없이 DSPilot을 테스트하기 위한 PLC 데이터 생성기입니다.

AASX 파일에서 Flow와 Call 정보를 읽어와서 **Flow별로 동시에** 반복적인 사이클 신호를 생성합니다.

## 주요 기능

- AASX 파일에서 프로젝트 구조 로드 (Flow, Work, Call)
- **ApiCall InTag/OutTag Address 기반 자동 매핑**
- **Flow별 병렬 실행** - 각 Flow의 Call들이 동시에 실행
- InTag (Going 신호) / OutTag (Finish 신호) 시뮬레이션
- plcTagLog 테이블에 실시간 데이터 기록

## 설정 파일 (appsettings.json)

```json
{
  "AasxFilePath": "../DSPilot/DsCSV_0317_A.aasx",
  "PlcDatabase": {
    "Path": "../DSPilot/sample/db/DsDB.sqlite3"
  },
  "CycleSettings": {
    "CycleIntervalMs": 5000,        // 사이클 간 대기 시간
    "CallDurationMs": 500,           // Call Going 신호 지속 시간
    "CallGapMs": 200,                // Call 간 대기 시간
    "FinishSignalDurationMs": 100,   // Finish 신호 지속 시간
    "AutoLoopEnabled": true          // 자동 반복 활성화
  }
}
```

## 실행 방법

### 1. 프로젝트 빌드
```bash
cd /mnt/c/ds/ds2/Apps/DSPilot/PlcDataGenerator
dotnet build
```

### 2. 프로그램 실행

**정상 실행:**
```bash
dotnet run
```

**백그라운드 실행:**
```bash
nohup dotnet run > plc_generator.log 2>&1 &
```

**데이터베이스 확인:**
```bash
dotnet run -- --check-db
```

### 3. 출력 예시
```
=== PLC Data Generator ===

📄 Loading configuration...
   DB Path: C:\ds\ds2\Apps\DSPilot\DSPilot\sample\db\DsDB.sqlite3
   Cycle Interval: 5000ms

📦 Loading AASX file...
   Project loaded: DsCSV_0317
   Flows found: 132

🏷️  Loading PLC tags from database...
   Tags loaded: 724

   Sample tag names:
      - Main OP Touch   Total   Start SW (Address: X1000)
      - Main OP Touch   Panel   Stop SW (Address: X1001)
      ...

🔗 Mapping Calls to PLC tags (by Address)...
   Calls with mapped tags: 287
   Total tag mappings: 433

   Sample mappings:
      - Diverter Cylinder.Up → InTag: Diverter#1      DiverterUp (X10A0)
      - Diverter Cylinder.Up → OutTag: Diverter#1      DiverterUp SOL (Y10B0)
      ...

🔄 Starting cycle generation (Flow-based parallel execution)...
   Total Flows: 18
   Total Calls: 287
   Press Ctrl+C to stop

--- Cycle #1 ---
  Flow: Diverter#1 (11 calls)
    ▶ Diverter Cylinder.Up [In:X10A0=1] [Out:Y10B0=1] ✓
    ▶ Diverter Cylinder.Down [In:X10A1=1] [Out:Y10B1=1] ✓
    ▶ Damping Cylinder.Damp [In:X10A2=1] [Out:Y10B2=1] ✓
    ...
  Flow: Slider Conveyor (4 calls)
    ▶ Slide Conveyor Cylinder.Fwd [In:X10A6=1] [Out:Y10B6=1] ✓
    ...
Waiting 5000ms until next cycle...
```

**데이터베이스 확인 출력:**
```
=== PLC Data Generator ===

📄 Loading configuration...
   DB Path: C:\ds\ds2\Apps\DSPilot\DSPilot\sample\db\DsDB.sqlite3
   Cycle Interval: 5000ms

📊 Checking plcTagLog database...

Total rows in plcTagLog: 6,630

Recent 20 entries:
==================================================================================================================================
ID: 2690464 | Tag: Diverter#4 PowerMoller  Run A            | Addr: Y1275    | Time: 2026-03-17 23:48:03.418 | Val: 0
ID: 2690462 | Tag: Diverter#4      DiverterDown SOL         | Addr: Y1271    | Time: 2026-03-17 23:48:03.262 | Val: 0
...
```

## 데이터 흐름

1. **AASX 로드**: Ds2.Aasx.AasxImporter를 통해 프로젝트 구조 읽기
2. **PLC 태그 로드**: DsDB.sqlite3의 plcTag 테이블에서 724개 태그 정보 읽기
3. **매핑**:
   - 각 Call의 ApiCall에서 InTag/OutTag 추출
   - **Address 기반 매핑** (TagMatchMode: "Address")
   - InTag.Address → PLC Tag Address 매칭
   - OutTag.Address → PLC Tag Address 매칭
4. **사이클 실행** (Flow별 병렬):
   - Flow별로 그룹화
   - **각 Flow의 모든 Call을 동시에 실행** (Task.WhenAll)
   - InTag: Going 신호 발생 (1 기록)
   - CallDurationMs 대기
   - OutTag: Finish 신호 발생 (1 기록)
   - FinishSignalDurationMs 대기
   - 신호 리셋 (0 기록)
   - CallGapMs 대기
5. **데이터 기록**: plcTagLog 테이블에 UTC 시간으로 INSERT

## 주의사항

- PLC 데이터베이스는 ReadWrite 모드로 열립니다
- DSPilot 앱이 SimulationMode를 false로 설정해야 실제 데이터를 읽습니다
- Ctrl+C로 중지할 수 있습니다

## DSPilot 설정 변경

PlcDataGenerator를 사용할 때는 DSPilot의 appsettings.json에서 SimulationMode를 false로 변경하세요:

```json
{
  "PlcDatabase": {
    "SourceDbPath": "sample/db/DsDB.sqlite3",
    "ReadIntervalMs": 1000,
    "SimulationMode": false,  // false로 변경
    "TagMatchMode": "Address"
  }
}
```

## 동작 원리

PlcDataGenerator는 실제 PLC와 동일하게 동작하도록 설계되었습니다:

1. **Flow별 병렬 실행**: 각 Flow의 Call들이 동시에 실행됩니다 (실제 공정의 병렬 처리 시뮬레이션)
2. **Address 기반 매핑**: AASX ApiCall의 InTag/OutTag Address를 PLC Tag Address와 매칭
3. **신호 순서**: InTag (Going) 신호 → CallDurationMs 대기 → OutTag (Finish) 신호 → 리셋
4. **UTC 타임스탬프**: 모든 신호는 UTC 시간으로 plcTagLog에 기록

### 매핑 예시

AASX에서:
- Call: "Diverter Cylinder.Up"
- ApiCall InTag Address: "X10A0"
- ApiCall OutTag Address: "Y10B0"

PLC 데이터베이스:
- plcTag (id=100, name="Diverter#1 DiverterUp", address="X10A0")
- plcTag (id=200, name="Diverter#1 DiverterUp SOL", address="Y10B0")

매핑 결과:
- InTag: X10A0 → id=100
- OutTag: Y10B0 → id=200

실행 시:
```
plcTagLog.INSERT(tagId=100, value="1", dateTime="2026-03-17 23:48:02.081")  // Going
plcTagLog.INSERT(tagId=200, value="1", dateTime="2026-03-17 23:48:02.595")  // Finish
plcTagLog.INSERT(tagId=100, value="0", dateTime="2026-03-17 23:48:03.123")  // Reset
plcTagLog.INSERT(tagId=200, value="0", dateTime="2026-03-17 23:48:03.262")  // Reset
```

이를 통해 DSPilot의 모든 기능(실시간 모니터링, 사이클 분석, Flow 메트릭, 통계 등)을 실제 설비 없이 테스트할 수 있습니다.
