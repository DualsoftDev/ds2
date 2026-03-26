# Ds2.IOList Generator - 핵심 규격서

> DS2 모델 기반 PLC IO 신호 자동 생성 시스템

**Version**: 4.0
**Last Updated**: 2026-03-27

---

## 1. 시스템 개요

### 목적
DS2 프로젝트 모델에서 PLC I/O 신호 목록을 자동 생성

### 입출력
```
Input:
  - DS2 Store (DsStore)              # 프로젝트 모델
  - Template Directory (*.txt)        # 신호 패턴 템플릿
  - address_config.txt                # 주소 설정

Output:
  - IoSignals (IW/QW)                # 실제 입출력 신호
  - DummySignals (MW)                # 내부 메모리 신호
  - Errors                           # 생성 실패 목록
```

---

## 2. DS2 모델 계층 구조

```
Project (프로젝트)
  └─ System (장치 시스템)              # SystemType: "RBT", "CNV", "PIN"
      └─ Flow (작업 흐름)              # FlowName: "S301", "S302"             [매크로: $(F)]
          └─ Work (작업)               # WorkName: "LoadPart", "UnloadPart"  [매크로 아님]
              └─ Call (호출)            # CallName: "RBT_HOME"                [매크로 아님]
                  │                     # DevicesAlias: "RBT_1"              [매크로: $(D)]
                  └─ ApiCall (API 호출) # 신호 생성 단위 (1 ApiCall → 신호 set)
                      ↓
                   ApiDef (API 정의)     # ApiDefName: "HOME", "PICK"         [매크로: $(A)]
```

---

## 3. 매크로 변수 추출 원칙

3가지 매크로 변수를 DS2 모델에서 추출:

| 매크로 | 의미 | 추출 경로 | 예시 |
|--------|------|-----------|------|
| **$(F)** | Flow 이름 | `ApiCall → Call → Work → Flow → Flow.Name` | "S301" |
| **$(D)** | Device 이름 (Passive System) | `ApiCall.ApiDefId → ApiDef → ApiDef.ParentId → System → System.Name` | "S301_RBT_1" |
| **$(A)** | ApiDef 이름 | `ApiCall.ApiDefId → ApiDef → ApiDef.Name` | "HOME" |

> ⚠ **주의**:
> - Work 이름($(W)): 사용자가 임의 작명, 표준화 안 됨 → 매크로 사용 금지
> - Call 이름($(C)): $(F), $(D), $(A)에 정보 중복 → 매크로 불필요

### 추출 알고리즘

```fsharp
// GenerationContext 구성
let buildContext (store: DsStore) (apiCall: ApiCall) =
    // 1. ApiDef 조회
    let apiDef = DsQuery.getApiDef(apiCall.ApiDefId, store)
    let apiDefName = apiDef.Name                              // $(A) - ApiDef 이름

    // 2. PassiveSystem (Device) 조회
    let passiveSystem = DsQuery.getSystem(apiDef.ParentId, store)
    let deviceName = passiveSystem.Name                       // $(D) - Device 이름 (Passive System)
    let systemType = passiveSystem.SystemType                 // "RBT"

    // 3. Call 조회 (매칭용)
    let call = DsQuery.getCall(apiCall.ParentId, store)
    let callName = call.Name                                  // 매칭용만, 매크로 아님

    // 4. Work 조회 (계층 탐색용)
    let work = DsQuery.getWork(call.ParentId, store)
    let workName = work.Name                                  // 매칭용만, 매크로 아님

    // 5. Flow 조회
    let flow = DsQuery.getFlow(work.ParentId, store)
    let flowName = flow.Name                                  // $(F) - Flow 이름
```

### 실전 예시

DS2 모델:
```
System: RBT (Active System, SystemType)
  └─ PassiveSystem: "S301_RBT_1" (Device)
      └─ ApiDef: HOME
          └─ (참조됨) ApiCall #1234
              └─ Call: RBT_HOME
                  └─ Work: S301.LoadPart
                      └─ Flow: S301
```

추출 경로:
```
ApiCall #1234 기준:
  ├─ ApiCall.ApiDefId → ApiDef "HOME"                          → $(A) = "HOME"
  ├─ ApiDef.ParentId → PassiveSystem "S301_RBT_1"             → $(D) = "S301_RBT_1"
  └─ ApiCall.ParentId → Call → Work → Flow "S301"             → $(F) = "S301"
```

추출 결과:
```
$(F) = "S301"          (Flow 이름)
$(D) = "S301_RBT_1"    (Device 이름 - PassiveSystem.Name)
$(A) = "HOME"          (ApiDef 이름)
```

신호 이름 예시:
```
템플릿: W_$(F)_Q_$(D)_$(A)_CMD
결과:    W_S301_Q_S301_RBT_1_HOME_CMD
```

---

## 4. 신호 생성 프로세스

```
┌──────────────┐
│  DS2 Store   │
└──────┬───────┘
       │
       ▼
┌──────────────────────────────────────────┐
│ Step 1: Context Building                 │
│ - 모든 ApiCall 순회                       │
│ - 각 ApiCall의 GenerationContext 생성    │
│ - $(F), $(D), $(A) 추출                  │
└──────┬───────────────────────────────────┘
       │
       ▼
┌──────────────────────────────────────────┐
│ Step 2: Template Loading                 │
│ - SystemType별 템플릿 파일 로드          │
│ - [RBT.IW], [RBT.QW], [RBT.MW] 섹션 파싱│
│ - MacroSlot 리스트 생성                  │
└──────┬───────────────────────────────────┘
       │
       ▼
┌──────────────────────────────────────────┐
│ Step 3: Address Configuration            │
│ - address_config.txt 로드                │
│ - @SYSTEM (Global) / @FLOW (Local) 구분 │
│ - Base Address 매핑                      │
└──────┬───────────────────────────────────┘
       │
       ▼
┌──────────────────────────────────────────┐
│ Step 4: Signal Generation (Loop)         │
│                                           │
│ For each GenerationContext:              │
│   1. SystemType으로 템플릿 선택          │
│   2. ApiDefName과 Slot 매칭              │
│   3. 매크로 치환: Pattern → VarName      │
│   4. 주소 할당: BaseAddr + Offset        │
│   5. SignalRecord 생성                   │
│   6. Offset 증가                         │
└──────┬───────────────────────────────────┘
       │
       ▼
┌──────────────────────────────────────────┐
│ Step 5: Result Aggregation               │
│ - IoSignals 수집 (IW + QW)              │
│ - DummySignals 수집 (MW)                │
│ - Errors 수집                            │
└──────┬───────────────────────────────────┘
       │
       ▼
┌──────────────┐
│GenerationResult│
└──────────────┘
```

---

## 5. 템플릿 형식

### 파일 구조

```
@META <SystemType>
@CATEGORY <Category>

[<SystemType>.IW]
<ApiDefName>: <Pattern>
-
<ApiDefName>: <Pattern>

[<SystemType>.QW]
<ApiDefName>: <Pattern>

[<SystemType>.MW]
<ApiDefName>: <Pattern>
```

### 예시: RBT.txt

```
@META RBT
@CATEGORY RBT

[RBT.IW]
HOME: W_$(F)_I_$(D)_HOME_POS
READY: W_$(F)_I_$(D)_READY
-
PICK_DONE: W_$(F)_I_$(D)_PICK_DONE

[RBT.QW]
HOME: W_$(F)_Q_$(D)_HOME_CMD
PICK: W_$(F)_Q_$(D)_PICK_CMD
```

### 매크로 치환 예시

```
Pattern:  "W_$(F)_Q_$(D)_HOME_CMD"
Context:  $(F)="S301", $(D)="RBT_1"
Result:   "W_S301_Q_RBT_1_HOME_CMD"
```

### 특수 슬롯

- **`-`**: Empty Slot (주소만 소비, 신호 미생성)
- **`#`**: 주석 (라인 전체 무시)

---

## 6. 주소 할당 방식

### Global Allocation (전역)

**특징**: SystemType 단위로 연속된 주소 공간 사용

**설정**:
```
@SYSTEM RBT
@IW_BASE 3070
@QW_BASE 3070
@MW_BASE 9110
```

**할당 예시**:
```
S301.RBT_1.HOME → %QW3070.0
S301.RBT_1.PICK → %QW3070.1
S302.RBT_2.HOME → %QW3070.2  (계속 이어짐)
```

### Local Allocation (로컬)

**특징**: Flow 단위로 독립된 주소 공간 (주소 재사용)

**설정**:
```
@FLOW S301
@IW_BASE 3200
@QW_BASE 3200

@FLOW S302
@IW_BASE 3200  # 같은 주소 재사용 가능
@QW_BASE 3200
```

**할당 예시**:
```
S301.CNV_1.START → %QW3200.0
S301.CNV_1.STOP  → %QW3200.1
S302.CNV_1.START → %QW3200.0  (S301과 독립)
```

### Mixed Strategy (권장)

```
# 중요 장치: Global (통합 관리)
@SYSTEM RBT
@QW_BASE 3070

# 일반 장치: Local (주소 재사용)
@FLOW S301
@QW_BASE 3200

@FLOW S302
@QW_BASE 3200
```

---

## 7. 주소 계산 알고리즘

### Word.Bit 방식 (LS Electric)

```
주소 = BaseAddress + (SlotIndex / 16)
비트 = SlotIndex % 16

예시:
BaseAddress = 3070
SlotIndex = 92

Word = 92 / 16 = 5
Bit = 92 % 16 = 12
결과 = %QW3075.12
```

### Slot 인덱스 관리

```fsharp
// Global: SystemType별 카운터
type AllocationState = {
    GlobalState: Map<string, MemoryAreaState>
}

// Local: Flow별 독립 카운터
type AllocationState = {
    LocalState: Map<string, MemoryAreaState>
}

type MemoryAreaState = {
    mutable InputWordOffset: int   // IW 슬롯 인덱스
    mutable OutputWordOffset: int  // QW 슬롯 인덱스
    mutable MemoryWordOffset: int  // MW 슬롯 인덱스
}
```

---

## 8. 핵심 데이터 모델

### GenerationContext

```fsharp
type GenerationContext = {
    ApiCallId: Guid
    SystemType: string          // "RBT", "CNV", "PIN"
    ApiDefName: string          // "HOME", "PICK", "PLACE"  [매크로: $(A)]
    FlowName: string           // "S301"                  [매크로: $(F)]
    WorkName: string           // "S301.LoadPart"         [매칭용, 매크로 아님]
    CallName: string           // "RBT_HOME"              [매칭용, 매크로 아님]
    DeviceName: string         // "RBT_1"                 [매크로: $(D) - Passive System]
    InputDataType: IecDataType
    OutputDataType: IecDataType
}
```

### SignalRecord

```fsharp
type SignalRecord = {
    VarName: string       // "W_S301_Q_RBT_1_HOME_CMD"
    Address: string       // "%QW3070.0"
    DataType: string      // "BOOL", "INT", "DINT"
    IoType: string        // "IW", "QW", "MW"
    Category: string      // "RBT"
    FlowName: string      // "S301"                  [ProMaker 매칭용]
    WorkName: string      // "S301.LoadPart"         [ProMaker 매칭용, 매크로 아님]
    CallName: string      // "RBT_HOME"              [ProMaker 매칭용]
    DeviceName: string    // "HOME" (ApiDefName)     [ProMaker 매칭용]
}
```

### MacroTemplate

```fsharp
type MacroTemplate = {
    SystemType: string
    Category: string
    InputSlots: TemplateSlot list
    OutputSlots: TemplateSlot list
    MemorySlots: TemplateSlot list
}

type TemplateSlot =
    | MacroSlot of apiDefName: string * pattern: string
    | EmptySlot
```

---

## 9. 에러 처리

### 에러 유형

```fsharp
type GenerationError =
    | TemplateNotFound of systemType: string
    | NoBaseAddress of systemType: string * flowName: string
    | InvalidTemplate of systemType: string * message: string
    | ContextBuildError of apiCallId: Guid * message: string
```

### 처리 원칙

**Fail-fast per ApiCall, Continue overall**
- ApiCall별 독립 처리
- 개별 실패 시 해당 ApiCall만 건너뛰고 계속 진행
- 모든 에러는 GenerationResult.Errors에 수집

---

## 10. C# API

### 기본 사용법

```csharp
using Ds2.Store;
using Ds2.IOList;

// 1. Store 로드
var store = new DsStore();
store.LoadFromFile("project.json");

// 2. 신호 생성
var api = new IoListGeneratorApi();
var result = api.Generate(store, @"C:\Templates");

// 3. 결과 확인
Console.WriteLine($"IO Signals: {result.IoSignals.Count}");
Console.WriteLine($"Dummy Signals: {result.DummySignals.Count}");
Console.WriteLine($"Errors: {result.Errors.Count}");

// 4. CSV 내보내기
api.ExportIoList(result, "io_list.csv");
api.ExportDummyList(result, "dummy_list.csv");
```

---

## 11. IEC 61131-3 데이터 타입

| Type | Bit Size | 설명 |
|------|----------|------|
| BOOL | 1 | TRUE/FALSE |
| INT | 16 | -32768 ~ 32767 |
| DINT | 32 | -2^31 ~ 2^31-1 |
| REAL | 32 | IEEE 754 float |
| LREAL | 64 | IEEE 754 double |
| STRING | Variable | 텍스트 |

---

**Document Status**: ✅ Complete
**Contact**: support@dualsoft.com
