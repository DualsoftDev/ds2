# DS / EV2 Entity 통합 정리

본 문서는 DualSoft DS / EV2 시스템의 **Entity 모델** 을 한 파일로 통합 정리한 것입니다.

- 참고 자료
  - 개념 (outdated, 기본 컨셉만): `/f/Git/ev2/master/docs/Spec/`
  - 최신 구현: `/f/Git/ds2/main/Solutions/` (특히 `Core/Ds2.Core/Entities.fs`, `Enum.fs`)
  - JSON 포맷: `/f/Git/ds2/main/Solutions/Convert/Ds2.JsonFormatter/json-format.md`
  - DS Language: https://dualsoft.co.kr/HelpDS/ds-language.html
  - AAS Semantics: https://dualsoftdev.github.io/aas-semantics/
- 본 문서는 **개념 / 모델 구조 중심** 이며, DB 스키마 및 JSON 직렬화 세부 형식은 다루지 않습니다.

---

## 1. 시스템 개요

### 1.1 EV2 / DS2 의 위치
- 개발: DualSoft
- 목적: 단일 UI / 단일 목적의 기존 DS 엔진을 확장하여, 다양한 UI(WinForms, Blazor, PowerPoint 등) 및 디바이스(PLC, HMI, 시뮬레이터)와 결합 가능한 **공통 메타모델 + 실행 엔진** 제공.
- 정합성: 실제 설비 / 디지털 트윈(AAS, OPC-UA) 과 1:1 매핑 가능한 모델 정의.

### 1.2 핵심 설계 철학
1. **그래프 기반 구조화**: Vertex(Work, Call) + Edge(Arrow) 로 흐름을 명시적으로 표현
2. **사이클 허용 범위**
   - Work *내부* Call 그래프: **DAG (비순환)**
   - Work *간* (System 직속) 그래프: **순환 허용** (Start/Reset 신호로 안전성 확보)
3. **자기유사성 (Fractal)**: `Project → System → (Flow) → Work → Call → Bit` 모든 레벨에서 동일한 *Work–Call–Arrow* 패턴 반복
   (Flow 는 *논리적 그룹화 단위* 라 자기유사성 차원에서는 생략 가능. 모델 트리 상에는 항상 존재 — §2.1 / §10 참조)
4. **이중성 (Duality)**: 동일 객체가 컨텍스트에 따라 다른 역할을 수행 (System↔Device, Read↔Write, Start↔Reset 등)
5. **AAS 정합성**: System / Work / Call / Api 가 각각 AAS Submodel 또는 ConceptDescription 으로 매핑 가능

---

## 2. Entity 계층 구조

### 2.1 트리 형태 (최신 ds2 기준)

```text
DsStore
 ├─ Project                                              // 최상위
 │   ├─ activeSystemIds  : Guid[]                        // → DsSystem (Active)
 │   ├─ passiveSystemIds : Guid[]                        // → DsSystem (Passive = Device)
 │   ├─ tokenSpecs       : TokenSpec[]
 │   ├─ nameplate?, handoverDocumentation?, technicalData?  // 표준 서브모델 (옵션)
 │   └─ author / version / dateTime
 │
 ├─ DsSystem (Active 또는 Passive)
 │   ├─ properties       : SystemSubmodelProperty[]      // 8종 도메인 DU
 │   ├─ iri / systemType
 │   │
 │   ├─ Flow (parentId = System.Id)
 │   │   ├─ properties   : FlowSubmodelProperty[]
 │   │   │
 │   │   └─ Work (parentId = Flow.Id, Name = "FlowPrefix.LocalName")
 │   │       ├─ properties : WorkSubmodelProperty[]
 │   │       ├─ duration / status4 / position / tokenRole / referenceOf
 │   │       │
 │   │       ├─ Call (parentId = Work.Id, Name = "DevicesAlias.ApiName")
 │   │       │   ├─ properties     : CallSubmodelProperty[]
 │   │       │   ├─ status4 / position / referenceOf
 │   │       │   ├─ apiCalls       : ApiCall[]           // 내장 (별도 최상위 dict 없음)
 │   │       │   │   ├─ apiDefId   : Guid?               // → ApiDef
 │   │       │   │   ├─ inTag / outTag : IOTag?
 │   │       │   │   ├─ inputSpec / outputSpec : ValueSpec
 │   │       │   │   └─ originFlowId : Guid?
 │   │       │   └─ callConditions : CallCondition[]    // 조건 트리
 │   │       │
 │   │       └─ ArrowBetweenCalls (parentId = Work.Id)   // 내부 DAG
 │   │
 │   ├─ ApiDef (parentId = System.Id, Passive System 만 보유)
 │   │   ├─ apiDefActionType : Normal | Push | Pulse | Time(ms)
 │   │   ├─ txGuid : Guid?       // TX Work
 │   │   └─ rxGuid : Guid?       // RX Work (Device 내부 Work)
 │   │
 │   ├─ ArrowBetweenWorks (parentId = System.Id)         // System 레벨 (Cyclic)
 │   │
 │   └─ HW 컴포넌트 (parentId = System.Id, Flow 와 GUID 로 다대다 연결)
 │       ├─ HwButton    : { inTag?, outTag?, flowGuids[] }
 │       ├─ HwLamp      : { inTag?, outTag?, flowGuids[] }
 │       ├─ HwCondition : { inTag?, outTag?, flowGuids[] }
 │       └─ HwAction    : { inTag?, outTag?, flowGuids[] }
```

### 2.2 추상 단위 (DS 언어 / 프랙탈 관점)

| Level | 단위      | 예시              |
|-------|-----------|-------------------|
| 1     | Project   | 스마트 팩토리       |
| 2     | System    | 조립 라인           |
| —     | *(Flow)*  | *(논리 그룹, 추상 단위에서 생략)* |
| 3     | Work      | 스테이션            |
| 4     | Call      | 로봇 동작           |
| 5     | Bit       | 모터 신호           |
| 6     | —         | 전기 신호 (raw)     |

각 레벨에서 **Work–Call–Arrow 패턴** 이 동일하게 반복된다 (Self-Similarity).
Flow 는 모델 트리에는 존재하지만 (§2.1 / §10) 자기유사성 차원에서는 *논리 그룹* 이라 별도 레벨로 카운트하지 않는다.

---

## 3. 공통 베이스

### 3.1 DsEntity / DsChild / DsArrow (`AbstractClass.fs`)

```fsharp
[<AbstractClass>]
type DsEntity(name: string) =
    member val Id : Guid = Guid.NewGuid()       // 항상 non-null
    abstract Name : string with get, set        // 단순 식별자

[<AbstractClass>]
type DsChild(name, parentId: Guid) =
    inherit DsEntity(name)
    member val ParentId = parentId

[<AbstractClass>]
type DsArrow(parentId, sourceId, targetId, arrowType) =
    inherit DsChild("", parentId)               // Arrow 는 이름 없음
    member val SourceId  = sourceId
    member val TargetId  = targetId
    member val ArrowType = arrowType
```

| 속성       | 의미                                                                      |
|------------|---------------------------------------------------------------------------|
| `Id`       | 객체의 GUID (영구 식별자, import/export 기준)                              |
| `Name`     | 사람이 읽는 이름. 일반적으로 중복 허용 (단 Project 명은 unique 권장)        |
| `ParentId` | 트리에서의 부모 entity GUID                                               |

### 3.2 PropertiesBase
- 모든 Submodel Properties 클래스는 `PropertiesBase<'T>` 상속.
- 공통: `Description : string option`.
- `DeepCopy()` 는 JSON 직렬화 기반으로 모든 컬렉션 / option 까지 깊은 복사.

---

## 4. Entity 상세

### 4.1 Project

> 최상위 단위. 다수의 System 을 ID 로 참조 (상호참조 없음).

```fsharp
type Project =
    member Id, Name
    member ActiveSystemIds       : ResizeArray<Guid>
    member PassiveSystemIds      : ResizeArray<Guid>
    member Nameplate             : Nameplate option
    member HandoverDocumentation : HandoverDocumentation option
    member TechnicalData         : TechnicalData option
    member TokenSpecs            : ResizeArray<TokenSpec>
    member Author                : string
    member DateTime              : DateTimeOffset
    member Version               : string
```

| 속성                      | 설명                                                              |
|---------------------------|-------------------------------------------------------------------|
| `Name`                    | 프로젝트 명                                                        |
| `ActiveSystemIds`         | **Active System** (사용자 제어 로직 — CPU 코드 생성 대상) GUID      |
| `PassiveSystemIds`        | **Passive System / Device** (시뮬레이션 / 외부 호출 대상) GUID      |
| `TokenSpecs`              | 토큰 사양 (`{ id, label, fields, workId? }`) — 레시피 / 제품 매핑   |
| `Nameplate`               | IDTA 표준 Nameplate 서브모델 (옵션)                                |
| `HandoverDocumentation`   | IDTA 표준 인계 문서 서브모델 (옵션)                                 |
| `TechnicalData`           | IDTA 표준 기술 데이터 서브모델 (옵션)                              |
| `Author`                  | 최종 수정자 (`{user}@{domain}`)                                    |
| `Version` / `DateTime`    | 버전 / 시각                                                       |

#### 시스템 모델 귀속 규칙
- 기본: System 모델은 Project 에 귀속 → Project 삭제 시 System 도 삭제.
- Public 으로 publish 된 System 은 귀속 해제 → Project 삭제 후에도 존속 (dangling 가능).
- 동일 System 이 A 프로젝트에서는 Active, B 프로젝트에서는 Passive 일 수 있음 (Active/Passive 는 *프로젝트 측* 관리).

### 4.2 DsSystem

> 장치·설비 등 독립 시스템 단위. Active 또는 Passive (Device) 역할.

```fsharp
type DsSystem =
    member Id, Name
    member Properties : ResizeArray<SystemSubmodelProperty>
    member IRI        : string option
    member SystemType : string option       // "Unit" | "Robot" | "Conveyor" | "Cylinder_1" ...
```

#### 분류
| 구분                       | 설명                                                                                 |
|----------------------------|--------------------------------------------------------------------------------------|
| **Active System**          | Project 의 `activeSystemIds` 에 등록. 사용자 제어 로직 보유. CPU 로 직접 제어.          |
| **Passive System (Device)**| Project 의 `passiveSystemIds` 에 등록. ApiDef 만 노출. 외부에서 호출받는 수동 시스템.  |
| White system (구버전 분류)  | 모델 정보 접근 가능 (라이브러리, publish 된 system, cylinder/pin 등)                    |
| Black system (구버전 분류)  | 모델 접근 불가, **ApiCall 호출만 허용**, ApiDef 없음 (외부 CPU 제어)                    |

#### 구버전 매핑 (참고)
- `Target` = Active (모델 + instance 상태)
- `Link` = 외부 호출 API + instance 상태
- `Device` = 호출 API + instance 상태 = Passive

### 4.3 Flow

> System 내부의 *논리적 그룹* (공정 조작 단위). HW 컴포넌트가 모이는 단위.

```fsharp
type Flow =
    inherit DsChild
    member ParentId  : Guid                     // → System.Id
    member Properties : ResizeArray<FlowSubmodelProperty>
```

- Flow **자체는 Work 간 Arrow 정보를 가지지 않는다** (그건 System 이 가짐).
- Flow 와 HW 컴포넌트(Button/Lamp/Condition/Action) 의 연결은 HW 측의 `flowGuids[]` 로 표현 (다대다).

### 4.4 Work

> 작업 단위. R/G/F/H FSM. 내부에 Call DAG 보유.

```fsharp
type Work =
    inherit DsChild
    member ParentId    : Guid                   // → Flow.Id
    member FlowPrefix  : string                 // 부모 Flow 이름
    member LocalName   : string                 // Work 고유 이름
    override Name = $"{FlowPrefix}.{LocalName}" // 자동 계산

    member Properties : ResizeArray<WorkSubmodelProperty>
    member Status4    : Status4                 // Ready=0 Going=1 Finish=2 Homing=3
    member Position   : Xywh option
    member TokenRole  : TokenRole               // None=0 Source=1 Ignore=2 Sink=4 (flags)
    member Duration   : TimeSpan option         // Call 이 없을 때만 의미
    member ReferenceOf: Guid option             // 다른 Work 참조 (인스턴스 복제)
```

#### 4.4.1 WorkBit FSM — R ⊕ G ⊕ F ⊕ H

| 상태       | 진입 조건       | 종료 조건       | 제어 주체  |
|------------|------------------|------------------|------------|
| **Ready**  | Homing 완료       | Start 신호       | 외부       |
| **Going**  | Start 신호        | 내부 작업 완료   | 내부       |
| **Finish** | Going 완료        | Reset 신호       | 외부       |
| **Homing** | Reset 신호        | 초기화 완료      | 내부       |

```text
Ready ── Start 신호 ──▶ Going ── 내부 완료 ──▶ Finish ── Reset 신호 ──▶ Homing ── 초기화 완료 ──▶ Ready
```

- **단일 비트(WorkBit)** 로 4 상태를 표현 (이중성 Case 5).
- **내부 자율 전이** (Going → Finish, Homing → Ready) 와 **외부 신호 전이** (Ready → Going, Finish → Homing) 가 공존.

#### 4.4.2 TokenRole (Flags, OR 조합)
| 값 | 이름   | 의미                  |
|----|--------|-----------------------|
| 0  | None   | 역할 없음              |
| 1  | Source | 토큰 발행 지점         |
| 2  | Ignore | 토큰 무시              |
| 4  | Sink   | 토큰 소멸 지점         |

### 4.5 Call

> Work 내부의 API 호출 노드. 다수의 ApiCall 동시 호출 가능.

```fsharp
type Call =
    inherit DsChild
    member ParentId      : Guid                  // → Work.Id
    member DevicesAlias  : string                // Device 별칭
    member ApiName       : string                // API 이름
    override Name = $"{DevicesAlias}.{ApiName}"  // 자동 계산

    member Properties     : ResizeArray<CallSubmodelProperty>
    member Status4        : Status4
    member Position       : Xywh option
    member ApiCalls       : ResizeArray<ApiCall>          // 내장 (★ 유일한 소스)
    member CallConditions : ResizeArray<CallCondition>    // 조건 트리
    member ReferenceOf    : Guid option
```

#### CallType (`SimulationCallProperties` 내부)
| 값 | 이름                | 의미                             |
|----|---------------------|----------------------------------|
| 0  | `WaitForCompletion` | 완료 대기                         |
| 1  | `SkipIfCompleted`   | 이미 완료면 건너뜀                |

#### Precondition (CallCondition) — 4.8 참고

### 4.6 ApiCall

> ApiDef 를 실제 I/O 값 / 태그와 함께 런타임 바인딩. **Call 내부에 내장** 되며 별도 최상위 컬렉션을 갖지 않는다.

```fsharp
type ApiCall =
    inherit DsEntity
    member InTag        : IOTag option           // 입력 IO 태그
    member OutTag       : IOTag option           // 출력 IO 태그
    member ApiDefId     : Guid option            // → ApiDef
    member InputSpec    : ValueSpec              // 입력 값 명세
    member OutputSpec   : ValueSpec              // 출력 값 명세
    member OriginFlowId : Guid option            // 원본 Flow (참조 추적)
```

> 의미: *"Concrete binding of a Call to an ApiDef with values / tags at runtime."*

### 4.7 ApiDef

> Passive System 이 노출하는 API 인터페이스 정의 (signature: 입출력 + 동작).

```fsharp
type ApiDef =
    inherit DsChild
    member ParentId         : Guid                       // → Passive System.Id
    member ApiDefActionType : ApiDefActionType
    member TxGuid           : Guid option                // TX Work
    member RxGuid           : Guid option                // RX Work (Device 내부 Work)
```

#### ApiDefActionType
| 케이스          | 의미                                                                       |
|-----------------|----------------------------------------------------------------------------|
| `Normal`        | 명령 지속형 (정상 동작)                                                    |
| `Push`          | 펄스 / 트리거 (종료 감지 신호 발생까지 출력 신호 지속)                       |
| `Pulse`         | 단발 펄스                                                                  |
| `Time(ms)`      | 지정 시간(ms) 만큼 지속되는 시간 기반 액션 (소스: `Time of int`)               |

### 4.8 CallCondition (조건 트리)

> Call 의 실행 전제 조건. AND/OR 결합 가능한 재귀 트리.

```fsharp
and CallCondition =
    member Id         : Guid
    member Type       : CallConditionType option         // AutoAux | ComAux | SkipUnmatch
    member Conditions : ResizeArray<ApiCall>             // 이 노드의 조건 (ApiCall 참조)
    member Children   : ResizeArray<CallCondition>       // 하위 트리 (괄호로 묶이는 부분식)
    member IsOR       : bool                              // false=AND, true=OR
    member IsRising   : bool                              // 상승 엣지 (식 끝에 ↑ 표기)
```

#### CallConditionType
| 값 | 이름           | 동작 모드        | 설명                                                    |
|----|----------------|------------------|---------------------------------------------------------|
| 0  | `AutoAux`      | Auto 전용        | 자동 기동 시에만 체크하는 인과 조건                       |
| 1  | `ComAux`       | Auto + Manual    | 공통 전제 조건. 자동/수동 모두에서 만족해야 동작 가능       |
| 2  | `SkipUnmatch`  | —                | 조건 불만족 시 Call 을 실행하지 않고 즉시 Finish 처리       |

#### Boolean 식 표현 패턴
| 식                       | 구조                                                                                       |
|--------------------------|--------------------------------------------------------------------------------------------|
| `A & B`                  | root: `isOR=false`, conditions=[A,B], children=[]                                           |
| `A \| B`                 | root: `isOR=true` , conditions=[A,B], children=[]                                           |
| `A & (B \| C)`           | root: `isOR=false`, conditions=[A], children=[ {isOR=true , conditions=[B,C]} ]             |
| `(A & B) \| (C & D)`     | root: `isOR=true` , conditions=[] , children=[ {isOR=false, [A,B]}, {isOR=false, [C,D]} ]   |
| `A \| (B & (C \| D))`    | root: `isOR=true` , conditions=[A], children=[ {isOR=false, [B], children=[{isOR=true,[C,D]}]} ] |

> 한 노드는 `isOR` 한 가지 연산자만 사용. 다른 연산자는 `children` 으로 중첩 (자동으로 `(...)` 로 감쌈).

### 4.9 Arrow

> 인과 / 흐름 연결.

```fsharp
type DsArrow =
    member ParentId  : Guid
    member SourceId  : Guid
    member TargetId  : Guid
    member ArrowType : ArrowType
```

| 종류                | 위치 (`ParentId`) | 그래프 특성        |
|---------------------|-------------------|--------------------|
| `ArrowBetweenWorks` | System            | Cyclic Directed    |
| `ArrowBetweenCalls` | Work              | DAG (비순환)       |

#### ArrowType
| 값 | 이름           | 설명                                                                        |
|----|----------------|-----------------------------------------------------------------------------|
| 0  | `Unspecified`  | 연결 없음                                                                   |
| 1  | `Start`        | source 완료 → target 시작                                                    |
| 2  | `Reset`        | source 시작 → target 리셋                                                    |
| 3  | `StartReset`   | source 완료 → target 시작 + target 시작 → source 리셋                         |
| 4  | `ResetReset`   | source 시작 → target 리셋 + target 시작 → source 리셋 (마지막→첫 순환 리셋용) |
| 5  | `Group`        | OR 그룹                                                                     |

#### 이중성 Case 8: Arrow = Start ⊕ Reset
하나의 Arrow 가 **두 개 신호** 를 동시에 생성한다.

| 신호    | 감지 방식    | 감지 대상            | 신호 지속성 | 용도                  |
|---------|--------------|----------------------|-------------|-----------------------|
| Start   | 라이징 엣지   | 이전 Work 의 Finish  | 순간        | 다음 Work 실행 트리거 |
| Reset   | 하이 레벨    | 현재 Work 의 Going    | 지속적      | 현재 Work 초기화 조건 |

```text
Work A 상태:  R   G   G   F   F   F   F
Work B 상태:  R   R   R   G   G   F   H
Start 신호:   0   0   0   1   0   0   0      (A 의 Finish↑)
Reset 신호:   0   0   0   1   1   0   0      (B 의 Going 레벨)
```

### 4.10 IOTag

> ApiCall ↔ ApiDef 간 데이터 교환의 매개체. **이중성 Case 4 / Case 7**.

```fsharp
type IOTag =
    member Name         : string                        // 의미 이름 (e.g. "StartCommand")
    member Address      : string                        // PLC 물리 주소 (e.g. "X100", "M300", "DB100.DBX0.2")
    member Description  : string
    member DataType     : IOTagDataType                  // BOOL | SINT | INT | DINT | LINT | USINT | UINT | UDINT | ULINT | REAL | LREAL | STRING
                                                          // (BYTE/WORD/DWORD 등 비트 묶음 타입은 현재 미정의 — 필요 시 정수 타입으로 표현)
    member DefaultValue : obj option
```

#### 이중성 분해
- **SemanticLink**: 의미 이름 (`Name`)
- **PhysicalBinding**: 물리 주소 (`Address`)
- **Read ⊕ Write**: 호출자 / 응답자 맥락에 따라 동일 태그가 R/W 양쪽으로 해석됨.

#### 연결 구조
- 기본: 1:1 Pair → `[Active] → WriteTag → (전송) → ReadTag → [Passive]`
- Shared Memory / DB 매핑 시에만 N:M 허용 (동기화 필요)

### 4.11 ValueSpec

> 호출 / 조건의 값 명세. 단일 / 복수 / 범위 표현.

```fsharp
type ValueSpec<'T> =
    | Single   of 'T                                    // x = 3.14
    | Multiple of 'T list                                // x ∈ {1, 2, 3}
    | Ranges   of RangeSegment<'T> list                  // x < 0 || 20 < x < 30 || ...

type Bound<'T> = 'T * BoundType                          // BoundType: Open | Closed
type RangeSegment<'T> = { Lower: Bound<'T> option; Upper: Bound<'T> option }
```

#### ValueSpec DU (실제 직렬화 케이스, 13종)
- `UndefinedValue`
- `BoolValue`
- `Int8Value` / `Int16Value` / `Int32Value` / `Int64Value`
- `UInt8Value` / `UInt16Value` / `UInt32Value` / `UInt64Value`
- `Float32Value` / `Float64Value`
- `StringValue`

각 케이스는 내부에 `ValueSpec<T>` (`Undefined | Single | Multiple | Ranges`) 를 포함.

#### 표현 예
```text
Single 3.14156952              → "x = 3.14156952"
Multiple [1; 2; 3]             → "x ∈ {1, 2, 3}"
Ranges [(-∞, 3.14)]            → "x < 3.14"
Ranges [(-∞,3.14),(5,6),(7.1,+∞)] → "x < 3.14 || 5.0 < x < 6.0 || 7.1 ≤ x"
```

### 4.12 HW 컴포넌트 (Flow 부속)

> Flow 의 UI / 제어 인터페이스. 모두 동일한 구조를 공유.

```fsharp
type HwButton    = { InTag : IOTag option; OutTag : IOTag option; FlowGuids : Guid[]; ParentId : Guid (System) }
type HwLamp      = ... 동일 ...
type HwCondition = ... 동일 ...
type HwAction    = ... 동일 ...
```

| 컴포넌트       | 용도                                            |
|----------------|--------------------------------------------------|
| `HwButton`     | 사용자 입력 버튼 (Auto / Manual)                  |
| `HwLamp`       | 상태 표시등 (`AutoModeLamp` 의 In/Out 상태 등)    |
| `HwCondition`  | UI 조건 (인터록 등)                               |
| `HwAction`     | UI 액션 (수동 실행 등)                            |

- `flowGuids[]` 로 여러 Flow 와 다대다 연결.

### 4.13 TokenSpec / TokenValue

> 레시피 / 제품 토큰 번호와 데이터를 매핑.

```fsharp
type TokenSpec = { Id: int; Label: string; Fields: Map<string,_>; WorkId: Guid option }
type TokenValue =                                       // DU
    | IntToken    of int
    | StringToken of string
    | ...
```

> AAS Semantics 카탈로그의 `TokenSpec` ConceptDescription 과 1:1 대응.

### 4.14 Bit (개념)

> DS 의 실행 흐름 *최소 관찰점*. 단순 신호 상태가 아니라 **흐름 안에서 원인이자 결과** (이중성 Case 3).

- Bit 는 고정 타입이 아니다. Arrow 컨텍스트에서 Work 처럼 또는 Call 처럼 *해석* 됨.
- 반복 구조: `ApiDef → Work(Bit Group) → Call(Bit Group) → ApiCall(Tag) → ApiDef → ...`

### 4.15 Asset (개념, ev2 Concepts/Requirements 기준)

> 실물 자산을 System 과 분리해서 모델링.

#### 규칙
1. Asset 은 실물 자산과 1:1 (제어 미할당이면 1:0).
2. 한 Asset 을 *제어하는* DsSystem 은 단 하나만 존재 (자연법칙도 단 하나의 system 으로 취급).
   단, 비제어용 사본은 복수 존재 가능.
3. `isController = true` 자산 → Project 의 **Active System** 에 해당.
4. `isController = false` 자산 → **Passive System** (자연법칙 제어).

### 4.16 표준 서브모델 (IDTA)

> Project 에 옵션으로 첨부되는 IEC / IDTA 표준 서브모델.

| 서브모델                  | 설명                                       |
|---------------------------|--------------------------------------------|
| `Nameplate`               | IDTA Digital Nameplate (제조사·모델·시리얼 등) |
| `HandoverDocumentation`   | IDTA 인계 문서 (매뉴얼, 도면, 인증서 링크)     |
| `TechnicalData`           | IDTA 기술 데이터 (전기·기계 사양)             |

---

## 5. SubmodelProperty 계층

각 entity 는 **8 종 도메인 서브모델** 의 Properties 를 *DU 배열* 로 옵셔널 보유한다.

### 5.1 SubmodelType (9종)
| Type                  | IdShort               | RefName            | 역할                        |
|-----------------------|-----------------------|--------------------|-----------------------------|
| `SequenceModel`       | `SequenceModel`       | `ModelRef`         | 모델 구조 자체               |
| `SequenceSimulation`  | `SequenceSimulation`  | `SimulationRef`    | 시뮬레이션 (KPI, 물리, OEE)  |
| `SequenceControl`     | `SequenceControl`     | `ControlRef`       | 제어 (PLC/CPU)              |
| `SequenceMonitoring`  | `SequenceMonitoring`  | `MonitoringRef`    | 모니터링 (폴링, 알람)        |
| `SequenceLogging`     | `SequenceLogging`     | `LoggingRef`       | 로깅 / 감사                  |
| `SequenceMaintenance` | `SequenceMaintenance` | `MaintenanceRef`   | 유지보수 (예측정비 등)        |
| `SequenceHmi`         | `SequenceHmi`         | `HmiRef`           | HMI 표현                     |
| `SequenceQuality`     | `SequenceQuality`     | `QualityRef`       | 품질 관리 (SPC 등)           |
| `SequenceCostAnalysis`| `SequenceCostAnalysis`| `CostAnalysisRef`  | 원가 분석                     |

> 각 타입은 내부적으로 0–8 의 `Offset` (byte) 값을 갖는다 — AASX Submodel ID 생성 / 정렬용 인덱스이며, 모델링 차원에서는 의미가 없으므로 본 표에서는 생략한다.

### 5.2 Entity × Domain 매트릭스 (32 = 4 × 8)

| Entity \ Domain | Simulation       | Control      | Monitoring      | Logging      | Maintenance      | Hmi      | Quality      | CostAnalysis      |
|-----------------|------------------|--------------|-----------------|--------------|------------------|----------|--------------|-------------------|
| **System**      | `SimulationSystem` | `ControlSystem` | `MonitoringSystem` | `LoggingSystem` | `MaintenanceSystem` | `HmiSystem` | `QualitySystem` | `CostAnalysisSystem` |
| **Flow**        | `SimulationFlow`   | `ControlFlow`   | `MonitoringFlow`   | `LoggingFlow`   | `MaintenanceFlow`   | `HmiFlow`   | `QualityFlow`   | `CostAnalysisFlow`   |
| **Work**        | `SimulationWork`   | `ControlWork`   | `MonitoringWork`   | `LoggingWork`   | `MaintenanceWork`   | `HmiWork`   | `QualityWork`   | `CostAnalysisWork`   |
| **Call**        | `SimulationCall`   | `ControlCall`   | `MonitoringCall`   | `LoggingCall`   | `MaintenanceCall`   | `HmiCall`   | `QualityCall`   | `CostAnalysisCall`   |

> Project / ApiDef / ApiCall / Arrow / HW 컴포넌트는 별도 도메인 properties 배열을 갖지 않으며, 직접 멤버로 필요한 속성을 보유한다.

### 5.3 도메인별 대표 필드 예시

| 도메인         | System / Work / Call 에서 자주 보이는 필드                                                              |
|----------------|------------------------------------------------------------------------------------------------------|
| Simulation     | `simulationMode`, `enablePhysicsSimulation`, `timeStepMs`, `taktTime`, `targetOEE`, `motion`, `script`, `numRepeat`, `callType`, `timeout`, `sensorDelay` |
| Control        | 태그 생성, 통신 설정, 안전 인터록, 제어 우선순위                                                          |
| Monitoring     | 폴링 간격, 알람 임계, 성능 추적                                                                          |
| Logging        | LOT 추적, 해시체인, 외부 동기화, `objectName`, `actionName`, `callDirection`                              |
| Maintenance    | 예측정비, 수명관리, MTBF / MTTR                                                                         |
| Hmi            | 웹서버, 레이아웃, 권한, SignalR 연결                                                                    |
| Quality        | SPC, 공정능력 (Cp/Cpk), Western Electric Rules                                                          |
| CostAnalysis   | OEE, 용량, BOM, 품질 비용                                                                                |

---

## 6. 런타임 모드 / 상태

### RuntimeMode
| 값 | 이름            | 설명                                            |
|----|------------------|-------------------------------------------------|
| 0  | `Simulation`     | RGFH 상태 전이만 처리 (가상)                     |
| 1  | `Control`        | IO 실제 읽기/쓰기 (PLC 제어)                     |
| 2  | `Monitoring`     | IO 읽어서 RGFH 상태 추적                         |
| 3  | `VirtualPlant`   | 외부 출력 받아 외부로 입력값 써주기 (가상 플랜트)  |

### Status4 (Work / Call 공용)
| 값 | 이름     | 설명          |
|----|----------|---------------|
| 0  | Ready    | 대기           |
| 1  | Going    | 실행 중        |
| 2  | Finish   | 완료           |
| 3  | Homing   | 리셋 중        |

### FlowTag
| 값 | 이름   | 설명           |
|----|--------|----------------|
| 0  | Ready  | 대기            |
| 1  | Drive  | 구동            |
| 2  | Pause  | 일시정지        |

---

## 7. 이중성 (Duality) 8 Cases

DS 시스템의 핵심 설계 원리. 동일 객체가 컨텍스트에 따라 다른 역할로 해석된다.

### 7.1 구조적 이중성 (Cases 1–4)
| Case | 구성                       | 설명                                                    |
|------|----------------------------|---------------------------------------------------------|
| 1    | System ⊕ Device            | 호출 방향에 따라 능동 / 수동 역할 동적 전환                 |
| 2    | Instance ⊕ Reference       | 실행 가능한 원본 vs 읽기 전용 참조 (`ReferenceOf`)         |
| 3    | 원인(Bit) ⊕ 결과(Bit)      | Bit 는 흐름 속에서 동시에 원인이자 결과                    |
| 4    | ReadTag ⊕ WriteTag         | 호출자 / 응답자 맥락에 따라 R/W 해석                       |

### 7.2 실행적 이중성 (Cases 5–8)
| Case | 구성                          | 설명                                              |
|------|-------------------------------|---------------------------------------------------|
| 5    | WorkBit = R ⊕ G ⊕ F ⊕ H       | 단일 비트로 4 상태 FSM 표현                         |
| 6    | φ(θ) = 위상 표현 ⊕ 상태 유추   | 센서 조합으로 상태를 수치(위상)로 표현               |
| 7    | Tag = Semantic ⊕ Binding     | 의미명과 물리 주소의 이중 연결                       |
| 8    | Arrow = Start ⊕ Reset        | 하나의 Arrow 가 두 종류 신호 생성                    |

### 7.3 Case 1 — System ⊕ Device 동적 전환
```text
[상황 1] A.ApiCall → B.ApiDef    →  A = System,  B = Device
[상황 2] B.ApiCall → A.ApiDef    →  B = System,  A = Device
```
- 시스템 간 관계는 부모-자식이 아닌 **수평적 / 형제적**.
- 순환 호출 발생 시 deadlock 가능 → 사전 경고 필요.

### 7.4 Case 6 — φ(θ) 위상 표현
센서 입력 / 상태 비트 $V_i$ 와 조건 $C_{i,\theta}$ 로부터 상태를 수치화.

정규화 결과는 모두 **반열린구간 $[0,\,2\pi)$** 를 의도한다. (모든 $V_i = 1$ 인 경우에도 $\phi$ 가 정확히 $2\pi$ 에 도달하지 않음 — $\phi_{\max} = (1 - 2^{-n}) \cdot 2\pi$)
폐구간 $[0,\,2\pi]$ 를 원할 경우 분모를 $2^n - 1$ 로 변경.

- Binary 가중:
  $\phi_\theta = \frac{1}{2^n}\sum_{i=1}^{n}(2^{i-1}\cdot V_i\cdot C_{i,\theta}) \cdot 2\pi$
- Exponential 가중:
  $\phi_\theta = \frac{1}{e^n}\sum_{i=1}^{n}(e^{i-1}\cdot V_i\cdot C_{i,\theta}) \cdot 2\pi$
- Log 정규화:
  $\phi_\theta = \ln\Big(\sum_{i=1}^{n} e^{i-1}\cdot V_i\cdot C_{i,\theta}\Big)\cdot \frac{2\pi}{n}$

용도: 디지털 트윈 상태 비교 / 동기화, 알람 / 이상 탐지 (위상 급변 / 역진행 감지).

---

## 8. DS Language (DSL) 요약

### 8.1 기본 선언 구조
```text
Project [name]
  System [name]
    Work [name]
      Call → ApiCall [targetSystem.apiname]
      Tag [semantic] := [binding] (Read/Write)
    Arrow: [fromWork] → [toWork]
  ApiDef [interface_name]
```

### 8.2 주요 문법 요소
| 문법                              | 의미                                  |
|-----------------------------------|----------------------------------------|
| `System name`                     | 새로운 System 정의 (Instance)          |
| `System ref: targetSystem`        | Reference 선언                         |
| `Work [states]`                   | Work 선언 (R/G/F/H 상태)               |
| `Call → ApiCall`                  | 외부 System 호출                       |
| `ApiDef [name]`                   | 인터페이스 정의                         |
| `Tag [semantic] := [binding]`     | 의미명 ↔ 물리 주소 매핑                 |
| `Arrow: A → B`                    | Work A 완료 시 Work B 시작              |
| `new System()`                    | Instance 생성 (실행 가능)              |

### 8.3 핵심 규칙
1. 계층: `Project > System > Flow > Work > Call > Bit`
2. 호출 방향: 하위가 상위 ApiDef 를 호출 가능
3. WriteTag 와 ReadTag 는 1:1 (기본). Shared 구조에서만 N:M
4. 순환 참조 / 중복 연결 사전 방지
5. Arrow: 라이징 엣지 = Start, 하이 레벨 = Reset

---

## 9. AAS Semantics 매핑

> https://dualsoftdev.github.io/aas-semantics/  (AAS V3 ConceptDescription 카탈로그)

### 9.1 Entity ConceptDescription (11종)
각 entity 는 IRI 기반 ConceptDescription 으로 공개되며, AASX export 시 SubmodelElement 의 `semanticId` 로 참조된다.

| idShort     | EN              | KO          | semanticId (IRI 패턴)                                | 정의                                                   |
|-------------|-----------------|-------------|-------------------------------------------------------|--------------------------------------------------------|
| Project     | Project         | 프로젝트     | `…/entity/Project/1/0`                                | DualSoft ds2 의 최상위 엔티티                            |
| System      | Active system   | 활성 시스템   | `…/entity/System/1/0`                                 | Plant / line / cell 등 능동 제어 시스템                   |
| Device      | Device          | 디바이스      | `…/entity/Device/1/0`                                 | Active 가 호출하는 passive device / actuator / sensor     |
| Flow        | Flow            | 플로우       | `…/entity/Flow/1/0`                                   | Active 내 work step 들의 sequential flow                |
| Work        | Work step       | 작업 단계     | `…/entity/Work/1/0`                                   | sequence 의 work step. R/G/F/H 보유                      |
| Call        | Call            | 콜           | `…/entity/Call/1/0`                                   | Work 내 device API consumer                              |
| ApiDef      | API definition  | API 정의     | `…/entity/ApiDef/1/0`                                 | device 가 노출하는 API signature                         |
| ApiCall     | API call        | API 호출     | `…/entity/ApiCall/1/0`                                | Call 의 ApiDef 런타임 바인딩                              |
| TokenSpec   | Token spec      | 토큰 사양     | `…/entity/TokenSpec/1/0`                              | recipe / product 토큰 ↔ 데이터 매핑                       |
| ArrowWork   | Work transition | Work 전이    | `…/entity/ArrowWork/1/0`                              | work step 간 directed edge                                |
| ArrowCall   | Call transition | Call 전이    | `…/entity/ArrowCall/1/0`                              | Call 간 directed edge                                     |

- 모든 CD: `modelType = ConceptDescription`, `dataType = STRING`, `dataSpecification = IEC 61360`.
- 다국어: `EN / DE / KO`.

> **Active System ↔ Passive System (Device) 의 CD 매핑**
> ds2 모델에서는 `DsSystem` 한 타입만 존재하고 분류만 Active / Passive 로 나뉜다 (§4.2 참조). AAS 매핑 단계에서는 두 분류가 별도 ConceptDescription 으로 노출된다:
> - Active System → `System` CD (`…/entity/System/1/0`)
> - Passive System → `Device` CD (`…/entity/Device/1/0`)

### 9.2 Submodel 카탈로그 (9종)
| 카테고리 | idShort       | 한국어         | 역할                                |
|----------|---------------|----------------|-------------------------------------|
| 핵심     | `SeqModelSm`  | 시퀀스 모델     | 모델 구조 (Project/System/...)       |
| 실행     | `SeqSimSm`    | 시뮬레이션      | 시뮬레이션 정의 / 결과                |
| 실행     | `SeqCtrlSm`   | 제어            | 제어 (CPU/PLC) 측 정보                |
| 운영     | `SeqMonSm`    | 모니터링        | 실시간 모니터링                       |
| 운영     | `SeqLogSm`    | 로깅            | 로그 / 이벤트                          |
| 지원     | `SeqMaintSm`  | 유지보수        | 유지보수 정보                         |
| 지원     | `SeqHmiSm`    | HMI            | HMI 표현                              |
| 분석     | `SeqQualSm`   | 품질            | 품질 지표                              |
| 분석     | `SeqCostSm`   | 비용            | 비용 지표                              |

> ds2 의 `SubmodelType` (5.1) 과 1:1 대응. EV2 의 `SequenceControlSubmodel` 은 `SeqCtrlSm` 에 해당.

### 9.3 IRI 형식
```
{baseUrl}/{path}        e.g. https://dualsoftdev.github.io/aas-semantics/entity/Project/1/0
{IRI}/cd.json           ← ConceptDescription JSON
{IRI}/                  ← 웹 뷰어
```

### 9.4 인스턴스 / 타입 예시
- 시스템 인스턴스: `assetType: System`, `kind: Instance`, `globalAssetId: urn:dualsoft:system:HelloDS`
- 디바이스 인스턴스: `assetType: Device`, `kind: Instance`, `globalAssetId: urn:dualsoft:device:STN1__Device1`
- 디바이스 타입 정의: `submodels.idShort = DoubleCylinderTemplate`, 내부 `submodelElements` 에 `ADV` / `RET` 등 동작 명세

---

## 10. 빠른 참조 — 부모 관계 / 자동 생성 이름

### 부모 관계 (`ParentId`)
| Entity              | ParentId 대상      | 비고                                                |
|---------------------|--------------------|-----------------------------------------------------|
| Flow                | System.Id          |                                                     |
| Work                | Flow.Id            | Name = `FlowPrefix.LocalName` (자동 계산)            |
| Call                | Work.Id            | Name = `DevicesAlias.ApiName` (자동 계산)            |
| ApiDef              | System.Id          | (Passive System)                                    |
| ApiCall             | (Call 내장)        | 별도 ParentId 없음, Call.ApiCalls 배열에 보관         |
| ArrowBetweenWorks   | System.Id          | Cyclic 허용                                         |
| ArrowBetweenCalls   | Work.Id            | DAG                                                 |
| HwButton/Lamp/Cond/Action | System.Id    | Flow 와는 `flowGuids[]` 로 다대다                    |
| CallCondition       | (Call 내장)        | DsEntity 비상속, 자체 Id 만 보유                      |

### Project 의 시스템 분류 (재정리)
| 분류                | 등록 위치              | 의미                                                            |
|---------------------|------------------------|-----------------------------------------------------------------|
| Active System       | `activeSystemIds[]`    | 사용자 제어 로직, CPU 코드 생성 대상                              |
| Passive System      | `passiveSystemIds[]`   | Device 역할, ApiDef 노출, 외부에서 호출됨                          |

---

## 11. 정리

- **그래프 기반 구성**: Vertex–Edge 로 흐름을 명시적으로 표현.
- **순환 허용 범위**: Work *간* 은 순환 허용, Work *내부* (Call DAG) 는 비순환.
- **모든 객체는 GUID 기반**: `Id (Guid) + Name + ParentId` 로 식별 / 추적.
- **Properties = Submodel DU 배열** (8 도메인 × 4 레벨 = 32 케이스). 필요한 케이스만 추가.
- **이중성 8 Case** 가 설계 전반 (구조 4 + 실행 4) 을 관통.
- **DS Language** 는 위 구조를 텍스트로 선언하는 DSL.
- **AAS Semantics** 카탈로그 (entity 11 + submodel 9) 와 1:1 의미 대응.
