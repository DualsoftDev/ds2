# Ds2 JSON 파일 규격

Last Sync: 2026-04-10

## 개요

Ds2 프로젝트 파일(`.json`)은 `DsStore`를 `System.Text.Json`으로 직렬화한 것입니다.
이 문서는 외부 시스템에서 Ds2 호환 JSON을 생성하기 위한 규격을 설명합니다.

**도우미 모듈**: `Ds2.JsonFormatter` (F#) — `Builder` + `Exporter` API 제공

---

## 최상위 구조

```json
{
  "projects":     { "<guid>": { ... } },
  "systems":      { "<guid>": { ... } },
  "flows":        { "<guid>": { ... } },
  "works":        { "<guid>": { ... } },
  "calls":        { "<guid>": { ... } },
  "apiDefs":      { "<guid>": { ... } },
  "arrowWorks":   { "<guid>": { ... } },
  "arrowCalls":   { "<guid>": { ... } },
  "hwButtons":    { "<guid>": { ... } },
  "hwLamps":      { "<guid>": { ... } },
  "hwConditions": { "<guid>": { ... } },
  "hwActions":    { "<guid>": { ... } }
}
```

- 키: `Dictionary<Guid, T>` — GUID 문자열 (`"cab2d708-9d11-400c-..."`)
- 값: 엔티티 객체
- **camelCase** 네이밍
- **null 필드 생략** (`WhenWritingNull`)
- `apiCalls`는 **최상위에 포함하지 않음** — `calls[].apiCalls[]` 내장 리스트에서 자동 재구축
- `nameplate`, `handoverDocumentation`은 **option** — 없으면 생략

---

## 공통 타입

### IOTag

```json
{ "name": "TagName", "address": "192.168.0.1", "description": "" }
```

- 모든 필드는 `string`
- HwComponent 및 ApiCall의 `inTag` / `outTag`에서 사용

### Xywh (캔버스 위치)

```json
{ "x": 0, "y": 0, "w": 120, "h": 40 }
```

- Work, Call 등의 `position` 필드에 사용 (option — 없으면 생략)

---

## 엔티티 상세

### Project

Project는 `properties` 필드가 없습니다. 주요 설정은 엔티티 직접 멤버입니다.

```json
{
  "activeSystemIds": ["<guid>", ...],  // Active System GUID 배열
  "passiveSystemIds": ["<guid>", ...], // Passive System (Device) GUID 배열
  "nameplate": { ... },               // option: IDTA Nameplate (없으면 키 자체 생략)
  "handoverDocumentation": { ... },    // option: IDTA 문서 (없으면 키 자체 생략)
  "tokenSpecs": [                      // 토큰 사양 배열
    { "id": 1, "label": "Tester", "fields": {}, "workId": "<guid>?" }
  ],
  "author": "...",                     // string
  "dateTime": "2026-01-01T00:00:00+09:00",  // DateTimeOffset
  "version": "1.0.0",                 // string
  "id": "<guid>",
  "name": "ProjectName"
}
```

### DsSystem

`properties`는 `SystemSubmodelProperty` DU 배열 (`ResizeArray<SystemSubmodelProperty>`)입니다.
각 항목은 `{"Case":"<DU-case>","Fields":[{...}]}` 형태로 직렬화됩니다.

```json
{
  "properties": [
    {
      "Case": "SimulationSystem",
      "Fields": [{
        "description": "...",              // string? (PropertiesBase 공통)
        "engineVersion": "...",            // string?
        "langVersion": "...",              // string?
        "author": "...",                   // string?
        "dateTime": "2026-01-01T00:00:00+09:00",  // DateTimeOffset?
        "iri": "...",                      // string?
        "systemType": "Unit",             // string?: Unit, Robot, Conveyor 등
        "simulationMode": "EventDriven",  // string
        "enablePhysicsSimulation": false,  // bool
        "timeStepMs": 100,                // int
        "simulationRepetitions": 100,     // int
        "confidenceLevel": 0.99,          // float
        "designCapacityPerHour": 60.0,    // float
        "targetThroughputPerHour": 50.0,  // float
        "taktTime": 72.0,                // float
        "targetOEE": 85.0                 // float
        // ... 기타 시뮬레이션 분석 필드 (50+ 필드)
      }]
    }
  ],
  "iri": "https://example.local/system1",  // string? (엔티티 레벨 IRI, properties 내부 IRI와 별도)
  "id": "<guid>",
  "name": "SystemName"
}
```

#### SystemSubmodelProperty 케이스 (8종)

| Case | Properties 클래스 | 주요 용도 |
|------|-------------------|-----------|
| `SimulationSystem` | `SimulationSystemProperties` | 시뮬레이션 설정 (모드, 물리, 용량분석, OEE, TOC 등) |
| `ControlSystem` | `ControlSystemProperties` | PLC 제어 설정 (태그 생성, 통신, 안전 인터록) |
| `MonitoringSystem` | `MonitoringSystemProperties` | PLC 모니터링 (폴링, 알람, 성능 추적) |
| `LoggingSystem` | `LoggingSystemProperties` | 로깅/감사 (LOT 추적, 해시체인, 외부 동기화) |
| `MaintenanceSystem` | `MaintenanceSystemProperties` | 유지보수 (예측정비, 수명관리, MTBF/MTTR) |
| `CostAnalysisSystem` | `CostAnalysisSystemProperties` | 원가 분석 (OEE, 용량, BOM, 품질) |
| `QualitySystem` | `QualitySystemProperties` | 품질 관리 (SPC, 공정능력, Western Electric) |
| `HmiSystem` | `HMISystemProperties` | HMI 설정 (웹서버, 레이아웃, 권한, SignalR) |

- 모든 Properties 클래스는 `PropertiesBase<T>`를 상속하여 `description: string?` 공통 필드 보유
- 배열에 필요한 케이스만 추가 (e.g. 시뮬레이션만 필요하면 `SimulationSystem` 1개만)
- null/기본값 필드는 직렬화 시 생략 가능

**Active System** = 사용자 제어 로직, **Passive System** = Device (자동 생성)

### Flow

`properties`는 `FlowSubmodelProperty` DU 배열입니다 (구조는 DsSystem과 동일 패턴).

```json
{
  "properties": [
    {
      "Case": "SimulationFlow",
      "Fields": [{
        "description": "...",              // string? (PropertiesBase 공통)
        "flowSimulationEnabled": true,     // bool
        "flowSimulationMode": "Normal"     // string
        // ... 기타 도메인별 필드
      }]
    }
  ],
  "parentId": "<system-guid>",         // 부모 System ID
  "id": "<guid>",
  "name": "FlowName"
}
```

#### FlowSubmodelProperty 케이스 (8종)

| Case | Properties 클래스 | 주요 용도 |
|------|-------------------|-----------|
| `SimulationFlow` | `SimulationFlowProperties` | 시뮬레이션 모드 |
| `ControlFlow` | `ControlFlowProperties` | 제어 우선순위 |
| `MonitoringFlow` | `MonitoringFlowProperties` | 모니터링 |
| `LoggingFlow` | `LoggingFlowProperties` | 로깅 |
| `MaintenanceFlow` | `MaintenanceFlowProperties` | 유지보수 |
| `CostAnalysisFlow` | `CostAnalysisFlowProperties` | 원가 분석 |
| `QualityFlow` | `QualityFlowProperties` | 품질 관리 |
| `HmiFlow` | `HMIFlowProperties` | HMI |

### Work

`duration`은 엔티티 직접 멤버이고, 기존 `motion`, `script` 등은 `WorkSubmodelProperty` 배열 내 각 도메인별 Properties로 이동했습니다.

```json
{
  "flowPrefix": "FlowName",           // 부모 Flow 이름 (Name 자동 생성용)
  "localName": "WorkName",            // Work 고유 이름
  "referenceOf": "<guid>",            // option: 참조 대상 원본 Work ID
  "properties": [
    {
      "Case": "SimulationWork",
      "Fields": [{
        "description": "...",            // string? (PropertiesBase 공통)
        "motion": "...",                 // string?
        "script": "...",                 // string?
        "externalStart": false,          // bool
        "isFinished": false,             // bool
        "numRepeat": 0                   // int
        // ... 기타 사이클 타임, OEE 추적 필드
      }]
    }
  ],
  "duration": "00:00:00.3000000",      // TimeSpan? (엔티티 직접 멤버)
  "status4": 0,                        // int: Ready=0, Going=1, Finish=2, Homing=3
  "position": { "x": 0, "y": 0, "w": 120, "h": 40 },  // option: 캔버스 위치
  "tokenRole": 0,                      // int flags: None=0, Source=1, Ignore=2, Sink=4
  "name": "FlowName.WorkName",         // 자동 생성 (flowPrefix + "." + localName)
  "parentId": "<flow-guid>",
  "id": "<guid>"
}
```

**주의**: `name`은 `flowPrefix.localName`으로 자동 계산됩니다. 직접 설정하지 않아도 됩니다.

#### WorkSubmodelProperty 케이스 (8종)

| Case | Properties 클래스 | 주요 용도 |
|------|-------------------|-----------|
| `SimulationWork` | `SimulationWorkProperties` | motion, script, externalStart, isFinished, numRepeat, 사이클 타임 |
| `ControlWork` | `ControlWorkProperties` | 제어 |
| `MonitoringWork` | `MonitoringWorkProperties` | 모니터링 |
| `LoggingWork` | `LoggingWorkProperties` | 로깅 |
| `MaintenanceWork` | `MaintenanceWorkProperties` | 유지보수 |
| `CostAnalysisWork` | `CostAnalysisWorkProperties` | 원가 분석 |
| `QualityWork` | `QualityWorkProperties` | 품질 관리 |
| `HmiWork` | `HMIWorkProperties` | HMI |

### Call

기존 `callType`, `timeout`, `sensorDelay`는 `CallSubmodelProperty` 배열 내 도메인별 Properties로 이동했습니다.

```json
{
  "properties": [
    {
      "Case": "SimulationCall",
      "Fields": [{
        "description": "...",            // string? (PropertiesBase 공통)
        "callType": 0,                   // CallType: WaitForCompletion=0, SkipIfCompleted=1
        "timeout": "00:00:05",           // TimeSpan?
        "sensorDelay": 100               // int?
      }]
    }
  ],
  "status4": 0,
  "position": { "x": 0, "y": 0, "w": 120, "h": 40 },
  "apiCalls": [                        // ApiCall 내장 배열 (★ 이것이 유일한 소스)
    {
      "apiDefId": "<apidef-guid>",     // option: 연결된 ApiDef
      "inTag": {                       // option: 입력 IO 태그
        "name": "InSensor",
        "address": "192.168.0.1",
        "description": ""
      },
      "outTag": { ... },               // option: 출력 IO 태그
      "inputSpec": { "Case": "UndefinedValue" },   // ValueSpec DU
      "outputSpec": { "Case": "UndefinedValue" },
      "originFlowId": "<guid>",        // option: 원본 Flow
      "id": "<guid>",
      "name": "Dev.Api"
    }
  ],
  "callConditions": [],                // CallCondition 배열 (트리 구조)
  "devicesAlias": "Dev",               // Device 별칭
  "apiName": "Api",                    // API 이름
  "name": "Dev.Api",                   // devicesAlias + "." + apiName
  "parentId": "<work-guid>",
  "id": "<guid>"
}
```

#### CallSubmodelProperty 케이스 (8종)

| Case | Properties 클래스 | 주요 용도 |
|------|-------------------|-----------|
| `SimulationCall` | `SimulationCallProperties` | callType, timeout, sensorDelay |
| `ControlCall` | `ControlCallProperties` | 제어 |
| `MonitoringCall` | `MonitoringCallProperties` | 모니터링 |
| `LoggingCall` | `LoggingCallProperties` | 로깅 (objectName, actionName, callDirection 등) |
| `MaintenanceCall` | `MaintenanceCallProperties` | 유지보수 |
| `CostAnalysisCall` | `CostAnalysisCallProperties` | 원가 분석 |
| `QualityCall` | `QualityCallProperties` | 품질 관리 |
| `HmiCall` | `HMICallProperties` | HMI |

### ApiDef

ApiDef는 `properties` 필드가 없습니다. 모든 설정은 엔티티 직접 멤버입니다.

```json
{
  "isPush": false,                     // bool
  "txGuid": "<work-guid>",            // Guid option: TX Work
  "rxGuid": "<work-guid>",            // Guid option: RX Work (Device Work)
  "parentId": "<system-guid>",         // 부모 Passive System
  "id": "<guid>",
  "name": "ApiName"
}
```

### ArrowBetweenWorks

```json
{
  "sourceId": "<work-guid>",
  "targetId": "<work-guid>",
  "arrowType": 1,                      // int: Start=1, Reset=2, StartReset=3, ResetReset=4, Group=5
  "parentId": "<system-guid>",         // 부모 System
  "id": "<guid>",
  "name": ""                           // 항상 빈 문자열
}
```

### ArrowBetweenCalls

```json
{
  "sourceId": "<call-guid>",
  "targetId": "<call-guid>",
  "arrowType": 1,
  "parentId": "<work-guid>",           // 부모 Work — source/target Call이 함께 속한 Work
  "id": "<guid>",
  "name": ""
}
```

- `sourceId` / `targetId` 두 Call은 **반드시 같은 Work 안에 있어야** 합니다 (`Arrows.fs:48-49` 검증). `parentId`는 그 공통 Work의 GUID이며, 새 Arrow 생성 시 `Paste.DirectOps.fs:37`에서 `source Call.ParentId`로 자동 설정됩니다.
- 시뮬레이션 인덱싱은 `Queries.arrowCallsOf w.Id`로 Work 단위 수집됩니다 (`SimIndex.fs:288-290`).

#### Call 간 AND / OR 의미론

한 target Call로 들어오는 다중 ArrowBetweenCalls는 **AND**로 해석됩니다 (`WorkConditionChecker.canStartCall` — `callPreds |> List.forall ...`).

```
{A, B} -> C   ⇒   A & B 가 모두 Finish 여야 C 시작 가능
```

```json
"arrowCalls": {
  "<arrow1-guid>": { "sourceId": "<call-A-guid>", "targetId": "<call-C-guid>", "arrowType": 1, "parentId": "<work-guid>", "id": "<arrow1-guid>", "name": "" },
  "<arrow2-guid>": { "sourceId": "<call-B-guid>", "targetId": "<call-C-guid>", "arrowType": 1, "parentId": "<work-guid>", "id": "<arrow2-guid>", "name": "" }
}
```

#### Call 간 OR — `referenceOf` 기반 OR 그룹

Call에는 OR 화살표 타입이 없습니다. 대신 **동일 의미의 Call을 여러 개로 분리**하고 그 중 하나(또는 다수)에 `referenceOf`로 원본 Call을 가리키게 하면 OR 그룹이 형성됩니다. ArrowBetweenCalls는 동일 Work 내에서만 정의되지만, OR 그룹의 멤버들은 **같은 Work에 있어도 되고 서로 다른 Work에 있어도 됩니다** — 시뮬레이션은 `referenceOf` 체인만 보고 그룹을 식별합니다.

```
Work_W:  A -> C₁                          (원본)
Work_W:  B -> C₂   (C₂.referenceOf = C₁.id)
⇒   A 또는 B 가 Finish 이면 OR 그룹 {C₁, C₂} 의 successor 가 시작 가능
```

**시뮬레이션 동작** (`SimIndex.fs:341-352`, `SimIndex.fs:205-217`, `WorkConditionChecker.fs:98-100`):

1. `CallCanonicalGuids`: 각 Call → `referenceOf ?? 자기 자신` 매핑
2. `CallReferenceGroups`: canonical 기준 그룹화, 멤버 ≥ 2 인 것만 OR 그룹 등록
3. predecessor 검사: `forall preds` 안에서 `orGuids |> List.exists (Finish?)` — 즉 **AND of OR**
4. 참조 Call(`referenceOf` 보유)은 ApiCalls/조건/StartPreds를 **원본 Call로부터 상속** (`SimIndex.addCallData`의 `dataSource` 분기)

**JSON 예시** — 4개 Call이 모두 같은 Work에 있는 패턴 (실제 `OR.json` 구조 기준):

```json
"calls": {
  "<call-A-guid>": {
    "properties": [], "status4": 0,
    "apiCalls": [
      { "apiDefId": "<apidef-A-guid>", "inputSpec": { "Case": "UndefinedValue" }, "outputSpec": { "Case": "UndefinedValue" }, "id": "<apicall-A-guid>", "name": "cyl1.ADV" }
    ],
    "callConditions": [],
    "devicesAlias": "cyl1", "apiName": "ADV", "name": "cyl1.ADV",
    "parentId": "<work-W-guid>",
    "id": "<call-A-guid>"
  },
  "<call-C1-guid>": {
    "properties": [], "status4": 0,
    "apiCalls": [
      { "apiDefId": "<apidef-C-guid>", "inputSpec": { "Case": "UndefinedValue" }, "outputSpec": { "Case": "UndefinedValue" }, "id": "<apicall-C-guid>", "name": "cyl1.RET" }
    ],
    "callConditions": [],
    "devicesAlias": "cyl1", "apiName": "RET", "name": "cyl1.RET",
    "parentId": "<work-W-guid>",
    "id": "<call-C1-guid>"
  },
  "<call-B-guid>": {
    "properties": [], "status4": 0,
    "apiCalls": [
      { "apiDefId": "<apidef-B-guid>", "inputSpec": { "Case": "UndefinedValue" }, "outputSpec": { "Case": "UndefinedValue" }, "id": "<apicall-B-guid>", "name": "Cyl2.ADV" }
    ],
    "callConditions": [],
    "devicesAlias": "Cyl2", "apiName": "ADV", "name": "Cyl2.ADV",
    "parentId": "<work-W-guid>",
    "id": "<call-B-guid>"
  },
  "<call-C2-guid>": {
    "properties": [], "status4": 0,
    "apiCalls": [],                                       // ★ 비워둠 — 원본에서 상속
    "callConditions": [],
    "referenceOf": "<call-C1-guid>",                      // ★ 원본 C₁ 참조 → OR 그룹 형성
    "devicesAlias": "cyl1", "apiName": "RET", "name": "cyl1.RET",
    "parentId": "<work-W-guid>",                          // ★ C₁과 같은 Work도 OK
    "id": "<call-C2-guid>"
  }
},
"arrowCalls": {
  "<arr-AC1>": { "sourceId": "<call-A-guid>", "targetId": "<call-C1-guid>", "arrowType": 1, "parentId": "<work-W-guid>", "id": "<arr-AC1>", "name": "" },
  "<arr-BC2>": { "sourceId": "<call-B-guid>", "targetId": "<call-C2-guid>", "arrowType": 1, "parentId": "<work-W-guid>", "id": "<arr-BC2>", "name": "" }
}
```

> 위 예시는 같은 Work 내에 `cyl1.RET` 이름의 Call이 두 개(C₁, C₂) 공존합니다. 시뮬레이션 엔진은 ParentId/Name 동일성을 강제하지 않고 `referenceOf`만 보므로 정상 동작합니다. OR 그룹 멤버를 서로 다른 Work에 분산시키는 것도 가능하며, 그 경우 각 ArrowBetweenCalls의 `parentId`는 자신의 source/target Call이 속한 Work GUID가 됩니다.

**규칙 요약**:

| 항목 | 값 |
|------|-----|
| OR 그룹 식별 | `referenceOf`가 같은 원본을 가리키는 Call들 (원본 자신 포함) |
| 원본 Call | `referenceOf` 필드 생략(또는 null) |
| 참조 Call | `referenceOf: "<원본 Call GUID>"` |
| 그룹 멤버의 Name | 동일 (`devicesAlias.apiName` 일치) — `name` 필드도 동일하게 작성. 시뮬레이션 동작상 필수는 아니나 의미적으로 권장 |
| 참조 Call의 `apiCalls` / `callConditions` | 비워두고 원본에서 상속받음 (작성하더라도 시뮬레이션은 원본 데이터를 사용) |
| 다중 OR | 같은 원본을 가리키는 Call을 N개 만들면 N-항 OR (그 중 하나라도 Finish이면 통과) |
| 배치 자유도 | 같은 Work / 다른 Work 모두 허용. 시뮬레이션은 ParentId 동일성을 강제하지 않음 |

> **참고**: 에디터의 paste 동작에서는 한 Work 내에 동일 Name Call이 새로 들어오는 것을 차단(`Paste.fs:67-72` `DuplicateCallInWork`)하지만, 이는 paste 시나리오에 한정됩니다. 일반 Call 추가/JSON 직접 작성/에디터 본연의 OR 생성 흐름에서는 같은 Work 내 동일 Name 공존이 자연스럽게 사용됩니다 (`OR.json` 참고).

### CallCondition (트리 구조)

```json
{
  "id": "<guid>",
  "type": 0,                          // CallConditionType option: AutoAux=0(자동 전용), ComAux=1(공통), SkipUnmatch=2
  "conditions": [ ... ],              // ApiCall[] — 조건에 참조되는 ApiCall 목록
  "children": [ ... ],                // CallCondition[] — 재귀 트리 (하위 조건)
  "isOR": false,                       // bool: OR 조건 여부
  "isRising": false                    // bool: 상승 엣지 여부
}
```

Note: CallCondition은 DsEntity를 상속하지 않습니다. 자체 Id(Guid)만 가집니다.

### HwButton / HwLamp / HwCondition / HwAction

```json
{
  "inTag": { "name": "...", "address": "...", "description": "..." },   // IOTag option
  "outTag": { "name": "...", "address": "...", "description": "..." },  // IOTag option
  "flowGuids": ["<guid>", ...],       // 연결된 Flow GUID 배열
  "parentId": "<system-guid>",
  "id": "<guid>",
  "name": "ComponentName"
}
```

---

## 관계 규칙 (parentId)

| 엔티티 | parentId | 의미 |
|--------|----------|------|
| Flow | System.Id | System의 자식 |
| Work | Flow.Id | Flow의 자식 |
| Call | Work.Id | Work의 자식 |
| ApiDef | System.Id | Passive System의 자식 |
| ArrowBetweenWorks | System.Id | System 레벨 화살표 |
| ArrowBetweenCalls | Work.Id | Work 레벨 화살표 |
| HwButton / HwLamp / HwCondition / HwAction | System.Id | System의 자식 |

---

## 엔티티 계층

```
Project
 ├─ System (Active) ← activeSystemIds
 │   ├─ Flow
 │   │   └─ Work ← Duration, TokenRole
 │   │       ├─ Call ← DevicesAlias.ApiName
 │   │       │   ├─ ApiCall[] ← ApiDefId, IOTag
 │   │       │   └─ CallCondition[]
 │   │       └─ ArrowBetweenCalls (parent=Work)
 │   └─ ArrowBetweenWorks (parent=System)
 └─ System (Passive = Device) ← passiveSystemIds
     ├─ Flow → Work (Device Work)
     └─ ApiDef ← TxGuid, RxGuid → Device Work
```

---

## 열거형 값 참조

### ArrowType
| 값 | 이름 | 설명 |
|----|------|------|
| 0 | Unspecified | 미지정 |
| 1 | Start | 선행 완료 → 후속 시작 |
| 2 | Reset | 리셋 신호 |
| 3 | StartReset | Start + Reset 동시 |
| 4 | ResetReset | 마지막→첫 번째 순환 리셋 |
| 5 | Group | OR 그룹 |

### TokenRole (Flags, OR 조합 가능)
| 값 | 이름 | 설명 |
|----|------|------|
| 0 | None | 역할 없음 |
| 1 | Source | 토큰 발행 지점 |
| 2 | Ignore | 토큰 무시 |
| 4 | Sink | 토큰 소멸 지점 |

### CallConditionType
| 값 | 이름 | 설명 |
|----|------|------|
| 0 | AutoAux | 자동 기동(Auto) 상태에서만 체크하는 전제 조건 |
| 1 | ComAux | 공통(Common) 전제 조건 — 자동/수동 상관없이 만족해야 action 가능 |
| 2 | SkipUnmatch | 불일치 시 건너뛰기 — 조건 불만족 시 Call을 실행하지 않고 Finish 처리 |

### Status4
| 값 | 이름 | 설명 |
|----|------|------|
| 0 | Ready | 대기 |
| 1 | Going | 실행 중 |
| 2 | Finish | 완료 |
| 3 | Homing | 리셋 중 |

### CallType
| 값 | 이름 | 설명 |
|----|------|------|
| 0 | WaitForCompletion | 완료 대기 |
| 1 | SkipIfCompleted | 완료 시 건너뛰기 |

### FlowTag
| 값 | 이름 | 설명 |
|----|------|------|
| 0 | Ready | 대기 |
| 1 | Drive | 구동 |
| 2 | Pause | 일시정지 |

### ValueSpec (Discriminated Union)

13가지 Case:

| Case | 값 타입 |
|------|---------|
| `UndefinedValue` | 없음 (미정의) |
| `BoolValue` | bool |
| `Int8Value` | int8 |
| `Int16Value` | int16 |
| `Int32Value` | int32 |
| `Int64Value` | int64 |
| `UInt8Value` | uint8 |
| `UInt16Value` | uint16 |
| `UInt32Value` | uint32 |
| `UInt64Value` | uint64 |
| `Float32Value` | float32 |
| `Float64Value` | float64 |
| `StringValue` | string |

각 Case의 Fields는 `ValueSpec<T>` 중첩 DU:

| ValueSpec\<T\> Case | 설명 | 예시 |
|---------------------|------|------|
| `Undefined` | 미정의 | `{"Case": "UndefinedValue"}` |
| `Single` | 단일 값 | `{"Case": "Int32Value", "Fields": [{"Case": "Single", "Fields": [42]}]}` |
| `Multiple` | 값 목록 | `{"Case": "Int32Value", "Fields": [{"Case": "Multiple", "Fields": [[1, 2, 3]]}]}` |
| `Ranges` | 범위 세그먼트 목록 | `{"Case": "Int32Value", "Fields": [{"Case": "Ranges", "Fields": [[...]]}]}` |

**RangeSegment** 구조:
```json
{
  "lower": { "Item1": 0, "Item2": { "Case": "Closed" } },   // Bound option (없으면 null)
  "upper": { "Item1": 100, "Item2": { "Case": "Open" } }    // Bound option
}
```

- `Bound` = `(value, BoundType)` 튜플
- `BoundType`: `{"Case": "Open"}` 또는 `{"Case": "Closed"}`
  - `Open` = 경계값 미포함 (< 또는 >)
  - `Closed` = 경계값 포함 (≤ 또는 ≥)

### TokenValue

```json
{ "Case": "IntToken", "Fields": [1] }
```

---

## 최소 유효 JSON 예시

```json
{
  "projects": {
    "00000000-0000-0000-0000-000000000001": {
      "activeSystemIds": ["00000000-0000-0000-0000-000000000002"],
      "passiveSystemIds": [],
      "tokenSpecs": [],
      "author": "",
      "version": "1.0.0",
      "id": "00000000-0000-0000-0000-000000000001",
      "name": "MyProject"
    }
  },
  "systems": {
    "00000000-0000-0000-0000-000000000002": {
      "properties": [
        {
          "Case": "SimulationSystem",
          "Fields": [{ "systemType": "Unit" }]
        }
      ],
      "id": "00000000-0000-0000-0000-000000000002",
      "name": "MySystem"
    }
  },
  "flows": {
    "00000000-0000-0000-0000-000000000003": {
      "properties": [],
      "parentId": "00000000-0000-0000-0000-000000000002",
      "id": "00000000-0000-0000-0000-000000000003",
      "name": "MyFlow"
    }
  },
  "works": {
    "00000000-0000-0000-0000-000000000004": {
      "flowPrefix": "MyFlow",
      "localName": "Work1",
      "properties": [],
      "duration": "00:00:00.3000000",
      "status4": 0,
      "tokenRole": 1,
      "name": "MyFlow.Work1",
      "parentId": "00000000-0000-0000-0000-000000000003",
      "id": "00000000-0000-0000-0000-000000000004"
    }
  },
  "calls": {},
  "apiDefs": {},
  "arrowWorks": {},
  "arrowCalls": {},
  "hwButtons": {},
  "hwLamps": {},
  "hwConditions": {},
  "hwActions": {}
}
```

---

## Auto/Comm Aux 조건 예시

Call의 실행 전제 조건을 `callConditions` 배열로 지정합니다.

| 조건 종류 | `type` | 설명 |
|-----------|--------|------|
| **AutoAux** | 0 | 자동 기동(Auto) 상태에서만 체크하는 전제 조건 |
| **ComAux** | 1 | 공통(Common) 전제 조건 — 자동/수동 상관없이 만족해야 action 가능 |
| **SkipUnmatch** | 2 | 조건 불만족 시 Call을 건너뛰고 바로 Finish 처리 |

### conditions 배열 내 ApiCall 필드 역할

`conditions` 배열에는 ApiCall 객체가 들어갑니다. 시뮬레이션에서 실제 사용하는 필드는 3개입니다:

| 필드 | 시뮬레이션 사용 | 역할 |
|------|:-:|------|
| **`id`** | O | IOValues 맵 키 — 런타임 값 조회용 |
| **`apiDefId`** | O | ApiDef → RxWork(Device Work) 해석 — RxWork가 Finish인지 체크 |
| **`inputSpec`** | O | 조건의 기대값 (ApiDef 것과 별개, 조건마다 독립 지정 가능) |
| `outputSpec` | X | 에디터 UI 표시용 (생략 가능) |
| `name` | X | 에디터 UI 표시용 (생략 가능) |
| `inTag` / `outTag` | X | 에디터 UI 표시용 (생략 가능) |

### 최소 AutoAux 예시 — type: 0

```json
"callConditions": [
  {
    "id": "<guid>",
    "type": 0,
    "conditions": [
      {
        "id": "<apicall-guid>",
        "apiDefId": "<apidef-guid>",
        "inputSpec": { "Case": "UndefinedValue" }
      }
    ],
    "children": [],
    "isOR": false,
    "isRising": false
  }
]
```

- `type: 0` → AutoAux (자동 기동 시에만 체크)
- `inputSpec: UndefinedValue` → 값 무관, RxWork가 Finish이면 통과

### ComAux 예시 — type: 1 (children 포함)

`A | B | (C & D)` 형태의 식을 children으로 중첩하여 표현합니다.

```json
"callConditions": [
  {
    "id": "<guid>",
    "type": 1,
    "conditions": [
      {
        "id": "<apicall-guid-1>",
        "apiDefId": "<apidef-guid-1>",
        "inputSpec": { "Case": "Int32Value", "Fields": [{ "Case": "Single", "Fields": [1] }] }
      },
      {
        "id": "<apicall-guid-2>",
        "apiDefId": "<apidef-guid-2>",
        "inputSpec": { "Case": "BoolValue", "Fields": [{ "Case": "Single", "Fields": [true] }] }
      }
    ],
    "children": [
      {
        "id": "<guid-child>",
        "type": 1,
        "conditions": [
          {
            "id": "<apicall-guid-3>",
            "apiDefId": "<apidef-guid-3>",
            "inputSpec": { "Case": "BoolValue", "Fields": [{ "Case": "Single", "Fields": [true] }] }
          },
          {
            "id": "<apicall-guid-4>",
            "apiDefId": "<apidef-guid-4>",
            "inputSpec": { "Case": "Int32Value", "Fields": [{ "Case": "Single", "Fields": [10] }] }
          }
        ],
        "children": [],
        "isOR": false,
        "isRising": false
      }
    ],
    "isOR": true,
    "isRising": false
  }
]
```

- `type: 1` → ComAux (자동/수동 모두 체크)
- `inputSpec`에 기대값 지정 — RxWork의 IO 값과 매칭
- 부모 `isOR: true` → 부모의 conditions(A, B)와 child(`(C & D)`) 사이를 OR로 결합
- child `isOR: false` → child 내부의 C, D를 AND로 결합

### 임의의 Boolean 식 표현

한 노드는 `isOR` 한 가지 연산자만 사용합니다. 서로 다른 연산자는 **children에 중첩**해서 표현하며, child 식은 자동으로 `(...)`로 감싸집니다(`ConditionFormulaProjection.formatItems`). 같은 노드의 `conditions`와 `children`은 동일한 연산자로 함께 결합됩니다.

| 식 | 구조 |
|----|------|
| `A & B` | root: `isOR=false`, conditions=[A, B], children=[] |
| `A \| B` | root: `isOR=true`, conditions=[A, B], children=[] |
| `A & (B \| C)` | root: `isOR=false`, conditions=[A], children=[ {`isOR=true`, conditions=[B, C]} ] |
| `(A & B) \| (C & D)` | root: `isOR=true`, conditions=[], children=[ {`isOR=false`, conditions=[A, B]}, {`isOR=false`, conditions=[C, D]} ] |
| `A \| (B & (C \| D))` | root: `isOR=true`, conditions=[A], children=[ {`isOR=false`, conditions=[B], children=[ {`isOR=true`, conditions=[C, D]} ]} ] |

#### `(A & B) | (C & D)` 예시

```json
"callConditions": [
  {
    "id": "<guid-root>",
    "type": 1,
    "conditions": [],
    "children": [
      {
        "id": "<guid-left>",
        "type": 1,
        "conditions": [
          { "id": "<ac-A>", "apiDefId": "<ad-A>", "inputSpec": { "Case": "BoolValue", "Fields": [{ "Case": "Single", "Fields": [true] }] } },
          { "id": "<ac-B>", "apiDefId": "<ad-B>", "inputSpec": { "Case": "Int32Value", "Fields": [{ "Case": "Single", "Fields": [1] }] } }
        ],
        "children": [],
        "isOR": false,
        "isRising": false
      },
      {
        "id": "<guid-right>",
        "type": 1,
        "conditions": [
          { "id": "<ac-C>", "apiDefId": "<ad-C>", "inputSpec": { "Case": "BoolValue", "Fields": [{ "Case": "Single", "Fields": [true] }] } },
          { "id": "<ac-D>", "apiDefId": "<ad-D>", "inputSpec": { "Case": "Int32Value", "Fields": [{ "Case": "Single", "Fields": [2] }] } }
        ],
        "children": [],
        "isOR": false,
        "isRising": false
      }
    ],
    "isOR": true,
    "isRising": false
  }
]
```

#### `A | (B & (C | D))` 예시 (3단 중첩)

```json
"callConditions": [
  {
    "id": "<guid-root>",
    "type": 1,
    "conditions": [
      { "id": "<ac-A>", "apiDefId": "<ad-A>", "inputSpec": { "Case": "UndefinedValue" } }
    ],
    "children": [
      {
        "id": "<guid-mid>",
        "type": 1,
        "conditions": [
          { "id": "<ac-B>", "apiDefId": "<ad-B>", "inputSpec": { "Case": "UndefinedValue" } }
        ],
        "children": [
          {
            "id": "<guid-leaf>",
            "type": 1,
            "conditions": [
              { "id": "<ac-C>", "apiDefId": "<ad-C>", "inputSpec": { "Case": "UndefinedValue" } },
              { "id": "<ac-D>", "apiDefId": "<ad-D>", "inputSpec": { "Case": "UndefinedValue" } }
            ],
            "children": [],
            "isOR": true,
            "isRising": false
          }
        ],
        "isOR": false,
        "isRising": false
      }
    ],
    "isOR": true,
    "isRising": false
  }
]
```

### isOR / isRising / children 시뮬레이션 동작

| 필드 | 시뮬레이션 사용 | 용도 |
|------|:-:|------|
| `isOR` | X | 에디터 수식 표시: `false` → `A & B`, `true` → `A \| B` |
| `isRising` | X | 에디터 수식 표시: `true`이면 식 끝에 `↑` (상승 엣지) 표기 |
| `children` | X | 에디터 UI 수식 트리 표시용 (괄호로 감싸짐) |

현재 시뮬레이션 엔진은 동일 type의 모든 최상위 `conditions`만 AND(`List.forall`)로 평가하며, `children` / `isOR` / `isRising`은 평가하지 않습니다. 위 boolean 식 표현은 에디터 UI(`ConditionFormulaProjection.formatCondition`)에서만 의미가 있습니다.

---

## Ds2.JsonFormatter 사용법 (F#)

```fsharp
open Ds2.JsonFormatter
open Ds2.Core

// 1. Store 생성 (Project + System + Flow)
let store, projectId, systemId, flowId =
    Builder.createStore "MyProject" "MySystem" "MyFlow"

// 2. Work 추가
let w1 = Builder.addWork store flowId "PickPart" (Some (TimeSpan.FromMilliseconds 300.)) TokenRole.Source
let w2 = Builder.addWork store flowId "WeldJoint" (Some (TimeSpan.FromMilliseconds 500.)) TokenRole.None

// 3. 화살표 연결
Builder.addArrowWork store systemId w1 w2 ArrowType.Start |> ignore

// 4. Device 추가
let devSysId, _, _, apiDefId = Builder.addDevice store projectId "Robot1" "ADV"

// 5. Call + ApiCall 생성
let callId, apiCallId = Builder.addCall store w1 "Robot1" "ADV" apiDefId

// 6. IO 태그 설정
Builder.setApiCallIOTags store apiCallId
    (Some (IOTag("OutCmd", "QW100", "")))
    (Some (IOTag("InSensor", "IW100", "")))

// 7. 두 번째 Device + Call
let devSysId2, _, _, apiDefId2 = Builder.addDevice store projectId "Robot1" "RET"
let callId2, apiCallId2 = Builder.addCall store w2 "Robot1" "RET" apiDefId2

// 8. AutoAux 조건: 자동 기동 시 apiCallId(Robot1.ADV)가 Finish여야 callId2 실행
Builder.addCondition store callId2 CallConditionType.AutoAux [apiCallId] false |> ignore

// 9. ComAux 조건: 자동/수동 모두에서 apiCallId2(Robot1.RET)가 Finish여야 실행
Builder.addCondition store callId2 CallConditionType.ComAux [apiCallId2] false |> ignore

// 10. 저장
Exporter.save store "output.json"
```

## C#에서 사용

```csharp
using Ds2.JsonFormatter;
using Ds2.Core;

var (store, projectId, systemId, flowId) = Builder.createStore("MyProject", "MySystem", "MyFlow");
var w1 = Builder.addWork(store, flowId, "PickPart", TimeSpan.FromMilliseconds(300), TokenRole.Source);
var w2 = Builder.addWork(store, flowId, "WeldJoint", TimeSpan.FromMilliseconds(500), TokenRole.None);
Builder.addArrowWork(store, systemId, w1, w2, ArrowType.Start);

// Device + Call
var (devSysId, _, _, apiDefId) = Builder.addDevice(store, projectId, "Robot1", "ADV");
var (callId1, apiCallId1) = Builder.addCall(store, w1, "Robot1", "ADV", apiDefId);
var (devSysId2, _, _, apiDefId2) = Builder.addDevice(store, projectId, "Robot1", "RET");
var (callId2, apiCallId2) = Builder.addCall(store, w2, "Robot1", "RET", apiDefId2);

// AutoAux 조건 추가
Builder.addCondition(store, callId2, CallConditionType.AutoAux,
    new FSharpList<Guid>(apiCallId1, FSharpList<Guid>.Empty), false);
// ComAux 조건 추가
Builder.addCondition(store, callId2, CallConditionType.ComAux,
    new FSharpList<Guid>(apiCallId2, FSharpList<Guid>.Empty), false);

Exporter.save(store, "output.json");
```
