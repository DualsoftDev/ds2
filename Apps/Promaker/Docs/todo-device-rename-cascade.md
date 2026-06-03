# TODO / Orchestrator: 디바이스 / Action 이름 일괄 변경 (cascade rename)

> 다른 세션 또는 `--orch` 가 그대로 이어받아 자동 진행할 수 있는 orchestrator 문서. **구현(코드)은 아직 시작 안 함.**
> rev3 (`--orch-check` 통과 목표): §0 진행 표 / §5 Phase 명세 / §6 박제 결정 / §9 sub agent prompt 골격 / §10 공통 규약 / §11 차단지점 추가.
> rev2 (외부 reviewer 3인 교차검증): cascade 정확판(본체+Condition 2경로 등). 상세는 §12.
> **자동 진행 차단 지점: 없음** (§11). `--orch` 는 §0 진행 표의 미시작 첫 Phase 부터 시작할 것.

## 0. Orchestrator 진행 표  ← `--orch` 진입점

| Phase | 상태 | 작업 요약 | 종료(완료 판정) 조건 |
|---|---|---|---|
| **P1** F# 쿼리·헬퍼 | ✅ 완료 | `apiCallsReferencingApiDef`(본체 한정)+`splitApiCallName`(첫'.') 추가 · Paste.DeviceOps 3곳 치환 · System 유일성=기존 `isSystemNameUniqueInProject`(Queries.fs:150) 재활용 | 빌드 통과 · 테스트 444 통과(회귀0) ✓ |
| **P2** F# cascade 본체 | ✅ 완료 | `RenameDevice.fs`: `RenameDevicePreview` DTO · `CollectRenameImpact`(SSOT, 본체+Condition 2경로) · `RenameDeviceBatch`(단일 트랜잭션) · Undo dict추적 버그 수정 | 빌드 통과 · 신규 8케이스 통과 · 회귀0(452) · 검열 C/M 0(Minor 2 보류) ✓ |
| **P3** C# 다이얼로그 | ⬜ 미시작 | `DeviceRenameDialog`(xaml+cs) · `DeviceApiRenameRow : BatchRowBase` · 미리보기 바인딩 | 빌드 통과(§10) |
| **P4** C# 진입·통합 | ⬜ 미시작 | `EntityKindRules` 분기 · Device 탭 우클릭 → 다이얼로그 → `RenameDeviceBatch` 적용 | 빌드 통과 |
| **P5** 검증·검열 | ⬜ 미시작 | 전체 빌드+테스트 · 자가검열(W2) sub agent 위임 | 빌드+테스트 통과 · 검열 Critical 0 |

> 의존: P1 → P2 (P2가 P1 헬퍼 사용) → P3 → P4 (P4가 P2 store API 호출). P3는 P2와 병행 가능하나 미리보기 DTO(P2) 확정 후가 안전. **순차 진행 권장.** 상태 칸은 phase 완료 시 ✅ 로 갱신(`--transfer` 재호출 또는 `--orch` 가 update).

## 1. 작업 목표

Promaker Explorer **Device 탭**에서 디바이스(Passive System) 노드를 우클릭 → "이름 바꾸기…" →
**전용 다이얼로그**로 **디바이스 이름 + 그 디바이스의 Action(API) 이름들**을 한꺼번에 편집하고,
확인 시 연관된 모든 위치를 **1 트랜잭션(Undo 1단계)** 으로 cascade 변경한다.

- 기존 문제: Call 속성 창에서 Call 이름 뒷부분(Action명=ApiName)이 read-only로 막혀 변경 불가
  (`Entities.fs` Call.Name setter가 ApiName 변경을 명시적으로 차단).
- 같은 이름이 여러 위치에 **문자열로 중복 저장**되어 한 곳만 바꾸면 표류(실제 데이터에 `REINFORCE`/`Reinforce` 불일치 존재).
- 근본 원인(확정): `RenameEntity`의 System/ApiDef 분기는 `e.Name <- ...` 한 줄뿐 **cascade 없음**(`Nodes.fs:390,399`). 신규 cascade 함수가 필요하다는 방향은 정확.

## 2. 배경 / 맥락 (논의 경위)

1. 출발점: Call `Robot231a.LOAD_S_COMPL_TO_QTR_BOOST_JIG` 속성에서 앞부분만 바뀌고 뒷부분(Action명)은 안 바뀜.
2. "진짜 바꾸려는 대상 = Passive system의 ApiDef(Action) 이름"으로 수렴(= 이름의 SSOT).
3. 속성창 인라인 편집은 "즉시 적용"이라 **확인 절차에 부적합** → Device 탭 우클릭 + 별도 다이얼로그.
4. 범위 = **"이 디바이스의 이 Action만"**(디바이스 단위 독립). 단 multi-device 공유 사용처도 함께 변경.
5. 다이얼로그 필드 = **디바이스명 + Action명 목록만**(Flow 직접 편집 제외). Flow(Passive 내부 Work)는 Action명 변경에 연동.

## 3. 핵심 데이터 모델 (실제 데이터 + 코드로 확정)

샘플: `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from\0601\SV_SideLine_revised.yaml`

```
[Active System "NewSystem"]
  Flow "S231"
    Work "S231_Robots"                     ← Active Work (디바이스와 무관!)
      Call "Robot231a.LOAD_..._JIG"        ← Call.Name = "{DevicesAlias}.{ApiName}"
        DevicesAlias = "Robot231a" / ApiName = "LOAD_..._JIG"(=Action명)
        ApiCalls[*].ApiDefId = Guid ─────┐
[Passive System "Robot231a"]             │  ← 이름 = 디바이스명 그대로(prefix 없음!)
  (internal Flow) Work.LocalName = "..."  │  ← Action명과 동일(ApiDef.Tx/RxGuid 가 가리킴)
  ApiDefs  ApiDef.Name = "LOAD_..._JIG" ◀─┘  ← SSOT
```

- **Passive System 이름 = `Robot231a`**(YAML `system:` 값 그대로). 추측했던 `S231_Robot231a`는 틀림.
- 이름 규칙 2가지: YAML import = 디바이스명 그대로 / UI 수동 추가(`Device.fs` ensureSystem) = `{flowName}_{devAlias}`.
- Active Call ↔ Passive ApiDef = import 시 **Ordinal 이름 매칭** 1회뿐, 이후 메모리에선 **전부 Guid**(`ApiCall.ApiDefId`).

### 3.1 ★중대 제약 — Condition 내 ApiCall 은 store dict 에 없는 독립 인스턴스

`DsStore.fs:58-75`:
```fsharp
member internal this.RebuildApiCallsDictionary() =
    this.ApiCalls.Clear()
    let register (ac: ApiCall) = this.ApiCalls.[ac.Id] <- ac
    for call in this.Calls.Values do
        for ac in call.ApiCalls do register ac
        // 조건 내 ApiCall은 등록하지 않음 — Call 본체 ApiCall과 동일 ID이지만 독립 인스턴스
// RewireApiCallReferences() 도 조건 내 ApiCall 은 rewire 제외(독립 인스턴스 유지)
```
함의(반드시 준수):
- `store.ApiCalls`(=`allApiCalls`)에는 **본체 ApiCall만** 존재. `Call.Conditions`/`Work.Conditions` 트리 내 ApiCall은 **같은 Id의 별개 인스턴스**로 dict 에 없음.
- ApiCall 갱신은 **반드시 2경로**: (a) 본체 = `apiCallsReferencingApiDef`/소유 `Call.ApiCalls`, (b) Condition 내 = 소유 Call/Work를 `TrackMutate` 하며 `Conditions` 트리 **직접 재귀 순회**.
- `TrackMutate(store.ApiCalls,id,...)` 로는 (b)를 못 바꿈(dict 에 없음).

## 4. 확정 설계

### 4.1 진입점
Device 탭에서 디바이스(Passive System, `EntityKind.System`) 노드 우클릭 → "이름 바꾸기…" → 다이얼로그.
(현재 즉시 rename(System.Name만) 동작 → 다이얼로그 호출로 교체)

### 4.2 다이얼로그 (DurationBatchDialog 패턴 차용)
```
┌─ 디바이스 일괄 이름 변경: Robot231a ─────────────────┐
│ 디바이스 이름 : [ Robot231a                    ]     │
│ Action(API) 이름   현재 → 새 이름                    │
│   LOAD_S_COMPL_TO_QTR_REINFORCE_JIG [______________] │
│   HOME                              [______________] │
│ ── 영향 미리보기 ── Call/ApiCall 외 총 N건            │
│   ⚠ 대소문자 표류로 자동 정리 안 되는 항목 K건         │
│                                   [확인]  [취소]     │
└──────────────────────────────────────────────────────┘
```
변경 행만 모아 확인 → **1 `WithTransaction`** → Undo 1단계 → `EmitRefreshAndHistory()` 1회.

### 4.3 cascade 매핑 (메모리 Guid 기준, 본체+Condition 2경로)

**디바이스 이름 변경** (`systemId` 기준)
- `DsSystem.Name` 교체 (중복검사: 프로젝트 내 System 이름 유일성)
- 영향 ApiCall 식별: `ac.ApiDefId` → ApiDef.ParentId == `systemId`(= `apiDefsOf systemId` 집합 포함)인 것만. **dummy(ApiDefId=None) 제외**
  - (a) 본체: `findCallsReferencingPassiveSystem systemId` → 각 Call 해당 ApiCall `Name` 앞부분 교체
  - (b) Condition 내: 모든 `Call.Conditions`/`Work.Conditions` 순회 → 해당 ApiCall `Name` 앞부분 교체
- `Call.DevicesAlias` 는 **현재 값 == 옛 디바이스명일 때만** 조건부 교체(multi-device 대표명 오염 방지)

**Action 이름 변경** (`apiDefId` 기준)
- `ApiDef.Name` 교체 (중복검사: `isApiDefNameUniqueInSystem`)
- 내부 `Work.LocalName` 교체: `[apiDef.TxGuid; apiDef.RxGuid] |> List.choose id |> List.distinct` 후 각 `getWork`(정방향) → `Work.LocalName` 교체 (중복검사: `isLocalNameUniqueInFlow`). **Tx=Rx 동일 Work 가능 → distinct 필수** (= "Flow는 Action명 변경에 연동"의 실체)
- ApiCall `Name` 뒷부분 + 소유 Call `ApiName` 교체:
  - (a) 본체: `apiCallsReferencingApiDef apiDefId`(본체 한정) → 각 뒷부분 교체 + 소유 Call(`findOwnerCallByApiCallId`) `ApiName` 교체 (중복검사: `isCallNameUniqueInWork`)
  - (b) Condition 내: 모든 `Call.Conditions`/`Work.Conditions` 순회 → `ApiDefId == apiDefId` ApiCall 뒷부분 교체

> **ApiCall.Name cascade 목적**: export 는 Guid 동적 resolve(무관)지만, PropertyPanel/Condition/Batch UI 가 `ac.Name` 을 직접 읽어 표시하므로 stale 방지용.

## 5. Phase 별 상세 명세 (작업 범위 / 산출물 / 종료 조건)

### P1 — F# 쿼리·헬퍼
- **범위**:
  - `Queries.fs`: `apiCallsReferencingApiDef (apiDefId) (store) : ApiCall list` 추가. **본체 한정**(`allApiCalls store |> List.filter (fun ac -> ac.ApiDefId = Some apiDefId)`), Condition 미포함을 주석 명시.
  - ApiCall.Name 앞/뒤 split 공용 헬퍼 추출(첫 `'.'` 기준) + 기존 3곳(`Paste.DeviceOps.fs:167-169,199-201,280-282`) 치환.
  - System 이름 유일성 검사 헬퍼 확보: 기존 존재 여부 확인 후 없으면 `passiveSystemsOf`/전체 systems 로 추가.
- **산출물**: 수정된 `Queries.fs`, split 헬퍼 모듈/함수, `Paste.DeviceOps.fs` 치환.
- **종료**: §10 빌드 통과 + 기존 테스트 회귀 0.

### P2 — F# cascade 본체
- **범위**:
  - `RenameDevicePreview` DTO(record 또는 DU): 영향 Call/ApiCall/Work 목록 + 건수 + "정리 불가 표류" 항목.
  - `collectRenameImpact (store) (systemId) (newDeviceName option) ((apiDefId*newName) list) : RenameDevicePreview` — §4.3 대상 수집(본체+Condition **2경로**, dummy 제외, Tx/Rx distinct). **preview/apply 공유 SSOT**.
  - `RenameDeviceBatch` store 확장 메서드: `collectRenameImpact` 결과로 단일 `store.WithTransaction(label, fun () -> ...)` 내 `TrackMutate` 프리미티브로 교체 수행 + 중복검사. **종료 emit `EmitRefreshAndHistory()`**.
- **금지**(§8 위반 시 Critical): `RenameEntity` 함수 호출(nested), `TrackMutate(store.ApiCalls,..)` 로 Condition ApiCall 변경 시도, 이름 파싱으로 디바이스 식별.
- **산출물**: `Nodes.fs`(또는 신규 `RenameDevice.fs`)에 위 3종 + 테스트.
- **종료**: 빌드 통과 + **신규 단위테스트 통과**(아래) + 기존 회귀 0.
  - 테스트(`Ds2.Store.Editor.Tests` 에 추가): ① 본체 ApiCall 뒷부분 교체 ② **Condition 내 ApiCall 교체**(2경로 핵심) ③ multi-device Call.DevicesAlias 조건부(대표 오염 없음) ④ Tx=Rx 동일 Work distinct(1회만) ⑤ 중복명 거부(invalidOp) ⑥ Undo 1단계 복원.

### P3 — C# 다이얼로그
- **범위**: `Dialogs/DeviceRename/DeviceRenameDialog.xaml(.cs)`, `DeviceApiRenameRow : BatchRowBase`(현재명/새이름/`IsChanged`). 디바이스명 TextBox + Action 그리드 + 미리보기(영향 건수 + 표류 경고). `ChangedRows` 반환. `collectRenameImpact` 결과로 미리보기 채움.
- **산출물**: xaml+cs+row model.
- **종료**: 빌드 통과.

### P4 — C# 진입·통합
- **범위**: `EntityKindRules.isMenuOperationAllowed`(이미 `isDeviceTree` 인자 보유)에 "Device 탭 + System = 다이얼로그" 분기. ExplorerPane 우클릭 핸들러 → `DeviceRenameDialog` → 결과를 `store.RenameDeviceBatch(...)` 적용(`TryEditorAction` 래핑). 현재 즉시 rename 경로 교체.
- **산출물**: `EntityKindRules.fs`, `ExplorerPane.xaml.cs`/ViewModel 커맨드.
- **종료**: 빌드 통과.

### P5 — 검증·검열
- **범위**: 전체 빌드 + `dotnet test`(§10). 자가검열(W2) sub agent 위임(§9 검열 골격).
- **종료**: 빌드+테스트 통과 + 검열 Critical 0(Major 는 처리/사유 기록).

## 6. 박제 결정 (그대로 채택 — 추측 금지, 자동 진행 전제)

- **디바이스 이름 의미** = `DsSystem.Name`(= 디바이스 별칭, prefix 없음). 이름 파싱 불필요.
- **진입 범위** = **Device 탭의 System 노드만** 다이얼로그. **Control 탭 System rename 은 기존 인라인 유지**(이번 미통일).
- **B경로(Call 속성에서 Action명 편집) = 이번 범위 제외**(후속 작업).
- **미리보기 수준** = 영향 **건수 + 항목 목록(Call/ApiCall) + 대소문자 표류 경고**.
- **dry-run/apply** = `collectRenameImpact` 단일 SSOT 공유(미리보기 N ≠ 실제 M 회귀 차단).
- **cascade 키** = `systemId`/`apiDefId`(Guid). **이름 파싱 금지.**
- **Flow 연동** = Action명 변경 시 Tx/Rx 대응 `Work.LocalName` 자동 교체(distinct). Flow.Name 자체는 미편집.
- **multi-device** = 사용처 ApiCall 모두 교체. `Call.DevicesAlias` 는 옛 이름 일치 시만 조건부.
- **emit** = `EmitRefreshAndHistory()` 1회. **Undo = `WithTransaction` 1회 = 1단계.**
- **신규 프로젝트 없음**(기존 프로젝트에 파일 추가 — wildcard 자동 포함). 솔루션 동기화 규칙 비해당.

## 7. 관련 파일 / 경로 (루트 = `F:\Git\ds2\rename-call-action`)

### F#
- `Solutions/Core/Ds2.Core/Entities.fs` — Call(146-179, **Name setter ApiName 차단 173-175**), ApiCall/ApiDef(63-88), Work(111-143), Condition(95-104)
- `Solutions/Core/Ds2.Core/Store/DsStore.fs` — **RebuildApiCallsDictionary(58-63)/RewireApiCallReferences(65-75) — §3.1 근거**
- `Solutions/Core/Ds2.Editor/Editor/Authoring.fs` — withTransaction(30-59, **nested 금지 32-33**), EmitAndHistory(269), **EmitRefreshAndHistory(277)**, TrackAdd/TrackMutate
- `Solutions/Core/Ds2.Editor/Store/Nodes/Nodes.fs` — RenameEntity(339-416, 자체 WithTransaction 387 / System·ApiDef cascade 없는 분기 390,399 / emit 412) [패턴 참고, **함수 직접 호출 금지**]
- `Solutions/Core/Ds2.Core/Store/DsQuery/Queries.fs` — findCallsReferencingPassiveSystem(279-292, **본체만**), apiDefsOf(265), isApiDefNameUniqueInSystem(269), allApiCalls(302), getApiDef, getWork
- `Solutions/Core/Ds2.Core/Store/Queries/ConditionQueries.fs` — findOwnerCallByApiCallId(62), referencedApiCallIdsOfCall(36)/OfWork(80), findCallsByApiCallId(53), findWorksByApiCallId(94)
- `Solutions/Core/Ds2.Editor/Store/Paste/Paste.DeviceOps.fs` — ApiCall.Name split 중복 3곳(167-169,199-201,280-282) [헬퍼 추출 대상]
- `Solutions/Core/Ds2.Editor/Store/Nodes/MoveCall.fs` — CrossFlowMoveValidation DU [preview DTO 선례]
- `Solutions/Core/Ds2.Editor/Projection/TreeProjection.fs` — buildDeviceTree(100-122), buildDeviceSystemChildren(18-54)
- `Solutions/Core/Ds2.Editor/Queries/EntityKindRules.fs` — isMenuOperationAllowed(11-29, **isDeviceTree 인자 보유**)
- `Solutions/Core/Ds2.Editor/Store/Nodes/Device.fs` — ensureSystem/ensureApiDef [UI 추가 경로 참고]

### C#
- `Apps/Promaker/Promaker/Controls/Shell/ExplorerPane.xaml(.cs)` — Device/Control 탭, 컨텍스트 메뉴(236-264), 더블클릭(310-334)
- `Apps/Promaker/Promaker/ViewModels/NodeCommands/SelectionEdit.cs` — RenameSelected(75-93)
- `Apps/Promaker/Promaker/Dialogs/DurationBatch/DurationBatchDialog.xaml(.cs)` — **배치 다이얼로그 패턴(최근접 참고)**
- `Apps/Promaker/Promaker/Dialogs/Common/BatchDialogHelper.cs`, `BatchRowBase`
- `Apps/Promaker/Promaker/Services/IDialogService.cs`, `DialogService.cs` — `ShowDialog<T>`

### 테스트
- `Solutions/Tests/Ds2.Store.Editor.Tests/Ds2.Store.Editor.Tests.fsproj` (**Promaker.sln 미포함 → 별도 `dotnet test`**), 기존 `DsStore.ConditionTests.fs` 등 참고.

## 8. 주의사항 / 금지사항 (검열 점검 대상)

- **Condition 내 ApiCall 2경로(§3.1).** `allApiCalls`/`TrackMutate(store.ApiCalls)` 로는 못 잡고/못 바꿈. 소유 Call/Work `TrackMutate`+`Conditions` 재귀 순회 필수.
- **Nested transaction 금지.** `RenameEntity`/`AddSystem` 등 자체 transaction 개시 함수 **호출 금지**, 프리미티브만.
- **emit = `EmitRefreshAndHistory`**(다건). 단일 `EmitAndHistory(evt)` 아님.
- **cascade = Guid 기준, 이름 파싱 금지.**
- **`Call.Name` setter ApiName 차단** → `c.ApiName <-`/`c.DevicesAlias <-` 필드 직접 set(TrackMutate 내).
- **multi-device**: ApiCall 은 `ApiDefId→ParentId==systemId` 로 식별해 그 ApiCall 만 교체. `Call.DevicesAlias` 는 옛 이름 일치 시만 조건부.
- **dummy(ApiDefId=None) ApiCall 제외**(`.` 없는 raw 심볼/`__inverter__`).
- **Tx/RxGuid distinct** 후 각 Work 갱신.
- **중복 검사**: ApiDef(`isApiDefNameUniqueInSystem` — RenameEntity 분기엔 없음), Work(flow), Call(work), System(프로젝트).
- **대소문자 표류는 cascade 범위 밖** → 미리보기 경고만.
- **Line ending**: F#/C# 모두 LF. newline 직전 공백 제거.

## 9. Sub agent prompt 골격 (`--orch` 가 그대로 사용)

### 9.1 작업(implement) prompt 골격
```
[역할] 너는 F#/C# 구현 담당. 아래 Phase 작업만 수행한다.
[Phase] <P번호: 범위 — §5 해당 항목 그대로>
[박제 결정] §6 전체를 그대로 따른다(추측·변경 금지).
[금지사항] §8 전체. 특히: RenameEntity 등 transaction 개시 함수 호출 금지(nested),
  store.ApiCalls dict 경로로 Condition ApiCall 변경 금지(§3.1), 이름 파싱 금지.
[참고] §7 파일·라인. 기존 패턴(DurationBatchDialog/CrossFlowMoveValidation/RenameEntity) 재활용 우선.
[산출물] §5 해당 Phase 산출물. 신규 함수보다 기존 헬퍼 재활용/refactoring 우선.
[검증] §10 빌드(+P2/P5는 테스트) 통과까지 본인이 확인.
[보고] ① 변경 파일+라인수 ② 빌드/테스트 결과 ③ 재활용한 기존 자산 ④ 잔여 우려(없으면 "없음").
```

### 9.2 검열(review) prompt 골격
```
[역할] 너는 코드 검열관. 이번 Phase commit 범위 + unstaged diff 를 검토한다.
[검증 0번 — 최우선] 사용자 명시 의도("orch 가능하도록 / 이 디바이스의 이 Action만 / device명+Action명만,
  Flow는 Action명 연동")를 verbatim 인용하고 patch ↔ 의도 1:1 매핑. 누락 의도 있으면 Critical.
[검증 1] §3.1 위반: Condition ApiCall 을 본체 경로로만 처리(누락)했는가? store.ApiCalls dict 변경으로
  Condition 인스턴스를 바꾸려 했는가? → Critical.
[검증 2] §8 금지사항 위반(nested transaction / 이름 파싱 / EmitAndHistory 오용 / dummy 미제외 /
  Tx·Rx distinct 누락 / 중복검사 누락) 점검.
[검증 3] 논리 오류·누락·refactoring 기회(중복 3회+ → 헬퍼).
[보고] ① 검열 대상(파일+라인수) ② 발견 이슈(없음/건수+Critical/Major/Minor) ③ 자가수정 결과
  ④ 잔여 우려(없으면 "없음").
```

## 10. 공통 규약

- **빌드**(코어+앱; Promaker.sln 이 Ds2.Core/Ds2.Editor 포함):
  `dotnet build Apps/Promaker/Promaker.sln -c Debug`  (repo 루트 기준)
- **테스트**(별도 — Promaker.sln 미포함):
  `dotnet test Solutions/Tests/Ds2.Store.Editor.Tests/Ds2.Store.Editor.Tests.fsproj -c Debug`
- **검증 순서**(각 Phase): 코드 → 빌드 →(P2/P5)테스트 → 자가검열(§9.2) → commit.
- **commit 정책**(`--orch` 기본 자동, `manual-commit` 옵션 시 생략): Phase 완료마다 `--gc` 절차.
  - 현재 branch `rename-call-action`. remote branch 존재 시 push, 없으면 local commit 만.
  - 제목 prefix `[rename-call-action]`. 본문 summary 1줄 + itemize. `Co-Authored-By` 미기입.
- **보고 형식**: §9 작업/검열 보고 형식 사용. Phase 종료 시 §0 진행 표 상태 ✅ 갱신.
- **자가 검열(W2)**: 신규 함수 3개+/다중 파일 → 각 Phase commit 전 §9.2 검열 의무.

## 11. 자동 진행 차단 지점

- **없음.** §6 박제로 미결정 사항(Control 탭 통일/B경로/미리보기 수준) 해소. 외부 cert/환경변수/승인 prerequisite 없음.
- 유일한 잠재 멈춤: P2 신규 테스트가 **기존 스위트 회귀**를 유발하는 경우 → 회귀 원인 수정 후 진행(자동 처리 대상, 사용자 개입 불요). 회귀가 설계 근본 충돌로 판명되면 그때 보고.

## 12. 외부 리뷰(3인 교차검증) 반영 내역 (rev2)

전 항목 **실제 코드 재검증 후 수용**(반론 없음). 검증 시 Grep/Glob `path` 를 `Solutions` 로 지정할 것(현재 디렉토리 `Apps/Promaker` 기본 루트로는 `Solutions/Core` 코드 미검색).

- **Condition 내 ApiCall 누락(진원지)**: `allApiCalls|>filter` 는 본체만 잡음(`DsStore.fs:60-63,73-75`). rev1 이 §경고와 동시에 그 쿼리를 제안한 자기모순 → §3.1 신설 + §4.3 2경로.
- **ID 만으로 Condition ApiCall 못 바꿈**: 소유 Call/Work `TrackMutate`+`Conditions` 순회로 정정.
- **RenameEntity 함수 호출 불가**: 자체 WithTransaction(`Nodes.fs:387`) → nested invalidOp(`Authoring.fs:32-33`). "함수 호출 금지, 프리미티브만"으로 강화.
- **multi-device DevicesAlias 오염**: `findCallsReferencingPassiveSystem` 은 Call 만 반환(`Queries.fs:286-292`) → ApiCall Guid 식별 + DevicesAlias 조건부.
- **ApiDef 중복검사 누락**: RenameEntity 분기에 없음(`Nodes.fs:372-386`) → `isApiDefNameUniqueInSystem`(`Queries.fs:269`) 명시.
- **종료 emit**: 다건은 `EmitRefreshAndHistory`(`Authoring.fs:277`, `ImportPlanApply.fs:52`/`Paste.fs` 선례).
- **Tx/RxGuid distinct**: 정방향 `getWork` 충분, 역방향 쿼리 부재 우려는 기각, distinct 필요성만 승계.
- **split 헬퍼/preview DTO/집계 SSOT/ApiCall.Name 목적/진입분기 일원화/경로 통일**: §5·§6·§8·§10 반영.
- **긍정(3/3 유지)**: Guid cascade·이름 파싱 금지, 단일 WithTransaction=Undo 1단계, store 확장 레이어, C# BatchRowBase/ChangedRows 차용.
