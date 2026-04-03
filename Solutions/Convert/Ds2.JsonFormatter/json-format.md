# Ds2 JSON 파일 규격

Last Sync: 2026-04-03

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

```json
{
  "properties": {
    "iriPrefix": "http://...",         // string?, IRI 접두사
    "splitDeviceAasx": true,           // bool, AASX Device 분리 저장
    "author": "...",                   // string?
    "version": "...",                  // string?
    "globalAssetId": "...",            // string?
    "description": "...",              // string? (모든 Properties 공통)
    "dateTime": "2026-01-01T00:00:00+09:00",  // DateTimeOffset? (option)
    "presetSystemTypesStorage": "..."  // string? (사전 설정 SystemType 저장소)
  },
  "activeSystemIds": ["<guid>", ...],  // Active System GUID 배열
  "passiveSystemIds": ["<guid>", ...], // Passive System (Device) GUID 배열
  "nameplate": { ... },               // option: IDTA Nameplate (없으면 키 자체 생략)
  "handoverDocumentation": { ... },    // option: IDTA 문서 (없으면 키 자체 생략)
  "tokenSpecs": [                      // 토큰 사양 배열
    { "id": 1, "label": "Tester", "fields": {}, "workId": "<guid>?" }
  ],
  "id": "<guid>",
  "name": "ProjectName"
}
```

### DsSystem

```json
{
  "properties": {
    "systemType": "Unit",              // string?: Unit, Robot, Conveyor 등
    "engineVersion": "...",            // string?
    "author": "...",                   // string?
    "description": "...",              // string? (모든 Properties 공통)
    "langVersion": "...",              // string? (언어 버전)
    "dateTime": "2026-01-01T00:00:00+09:00",  // DateTimeOffset? (option)
    "iri": "..."                       // string? (IRI)
  },
  "id": "<guid>",
  "name": "SystemName"
}
```

**Active System** = 사용자 제어 로직, **Passive System** = Device (자동 생성)

### Flow

```json
{
  "properties": {
    "description": "..."               // string? (모든 Properties 공통)
  },
  "parentId": "<system-guid>",         // 부모 System ID
  "id": "<guid>",
  "name": "FlowName"
}
```

### Work

```json
{
  "flowPrefix": "FlowName",           // 부모 Flow 이름 (Name 자동 생성용)
  "localName": "WorkName",            // Work 고유 이름
  "referenceOf": "<guid>",            // option: 참조 대상 원본 Work ID
  "properties": {
    "description": "...",              // string? (모든 Properties 공통)
    "duration": "00:00:00.3000000",    // TimeSpan?: ISO 8601 duration
    "motion": "...",                   // string?
    "script": "...",                   // string?
    "externalStart": false,            // bool
    "isFinished": false,               // bool
    "numRepeat": 0                     // int
  },
  "status4": 0,                        // int: Ready=0, Going=1, Finish=2, Homing=3
  "position": { "x": 0, "y": 0, "w": 120, "h": 40 },  // option: 캔버스 위치
  "tokenRole": 0,                      // int flags: None=0, Source=1, Ignore=2, Sink=4
  "name": "FlowName.WorkName",         // 자동 생성 (flowPrefix + "." + localName)
  "parentId": "<flow-guid>",
  "id": "<guid>"
}
```

**주의**: `name`은 `flowPrefix.localName`으로 자동 계산됩니다. 직접 설정하지 않아도 됩니다.

### Call

```json
{
  "properties": {
    "description": "...",              // string? (모든 Properties 공통)
    "callType": 0,                     // int: WaitForCompletion=0, SkipIfCompleted=1
    "timeout": "00:00:05",             // TimeSpan?
    "sensorDelay": 100                 // int?
  },
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

### ApiDef

```json
{
  "properties": {
    "description": "...",              // string? (모든 Properties 공통)
    "isPush": false,                   // bool
    "txGuid": "<work-guid>",           // option: TX Work
    "rxGuid": "<work-guid>"            // option: RX Work (Device Work)
  },
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
  "parentId": "<work-guid>",           // 부모 Work (★ System이 아님)
  "id": "<guid>",
  "name": ""
}
```

### CallCondition (트리 구조)

```json
{
  "id": "<guid>",
  "type": 0,                          // CallConditionType option: AutoAux=0, ComAux=1, SkipUnmatch=2
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
| 0 | AutoAux | 자동 보조 |
| 1 | ComAux | 통신 보조 |
| 2 | SkipUnmatch | 불일치 건너뛰기 |

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
      "properties": {},
      "activeSystemIds": ["00000000-0000-0000-0000-000000000002"],
      "passiveSystemIds": [],
      "tokenSpecs": [],
      "id": "00000000-0000-0000-0000-000000000001",
      "name": "MyProject"
    }
  },
  "systems": {
    "00000000-0000-0000-0000-000000000002": {
      "properties": { "systemType": "Unit" },
      "id": "00000000-0000-0000-0000-000000000002",
      "name": "MySystem"
    }
  },
  "flows": {
    "00000000-0000-0000-0000-000000000003": {
      "properties": {},
      "parentId": "00000000-0000-0000-0000-000000000002",
      "id": "00000000-0000-0000-0000-000000000003",
      "name": "MyFlow"
    }
  },
  "works": {
    "00000000-0000-0000-0000-000000000004": {
      "flowPrefix": "MyFlow",
      "localName": "Work1",
      "properties": {
        "duration": "00:00:00.3000000"
      },
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

// 7. 저장
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
Exporter.save(store, "output.json");
```
