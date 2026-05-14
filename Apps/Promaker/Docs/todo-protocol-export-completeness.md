# TODO — ModelProtocol export 도메인 완결성 보강

> 본 문서는 `done-yaml-save-format.md` 후속 작업. `.yaml`/`.json` 저장 경로의 *의도된 lossy 4-set* (GUID / position / alias / 시뮬 결과) 외에 **암묵적으로 누락되어 있는 도메인 데이터** 를 식별하고 emit/apply 양방향 보강.
>
> **2026-05-14 개정** — N=5 cross-validation review (`--inspect 5`) 결과 반영. §2 누락 항목 list 가 사용자 명시 3 case 에서 실제 누락 표면 9+ 카테고리로 확장. ArrowKind→ArrowType naming fix, Work.AuxKind 위치 정정, schema-shape 결정 phase (§4.1.5) 신설, default 매핑 표 (§6.3) / SSOT 갱신 책임 표 (§6.1) 신설, 자가 검열 trigger ②③④⑤ 4건 동시 충족 명시.

---

## 1. 작업 목표

`ModelProtocol.exportToJson` / `ModelProtocol.apply` (= protocol v0 의 source-of-truth export) 가 store 의 도메인 데이터를 **선언적으로 완결** 표현하도록 누락 항목 보강.

- **현재 lossy**: GUID / position / alias / 시뮬 결과 — 의도된 4-set (yaml-protocol-v0 §1 SSOT).
- **암묵 lossy (보강 대상)**: SSOT 에 명시되지 않았으나 store 에 존재하고 protocol export 시 누락되는 도메인 데이터.

→ 사용자 의도: ".yaml/.json 저장 후 재오픈 시 모델 의미가 동등 (GUID/position/alias/시뮬 제외)" 보장.

---

## 2. 누락 항목 list (cross-validation 검증 완료)

### 2.1 사용자 명시 (1차 baseline)

| 항목 | 정의 위치 | 도메인 의미 | 우선순위 | 분류 (§4.1) |
|---|---|---|---|---|
| **`CallConditionType.AutoAux`** | `Ds2.Core/Enum.fs:16` (`AutoAux = 0`) | CallCondition 분기 조건 — "Auto_X" 보조 코일 (WorkGoing ∧ preds ∧ CallCondition.AutoAux, `02_Control.fs:179,187`) | 高 | 必 |
| **`CallConditionType.ComAux`** | `Ds2.Core/Enum.fs:17` (`ComAux = 1`) | CallCondition 분기 조건 — "Com_X" 보조 코일 (게이팅 없음, `02_Control.fs:180,188`) | 高 | 必 |
| **`CallConditionType.SkipUnmatch`** | `Ds2.Core/Enum.fs:18` (`SkipUnmatch = 2`) | CallCondition 분기 조건 — 추가 분기 (ComAux/AutoAux 형제) | 高 | 必 |

→ 위 3 case 는 단독 누락이 아니라 **`Call.CallConditions` 콜렉션 전체 + `CallCondition` entity 6 property 의 부분집합** (§2.2 1 행 참조).

### 2.2 cross-validation 추가 발견 (review 산출 — 작업 진입 전 baseline 확장)

`ModelProtocol.fs` grep 결과 emit 0건으로 확정된 추가 누락 항목.

| 카테고리 | 항목 | 정의 위치 | 우선순위 | 분류 |
|---|---|---|---|---|
| **CallCondition tree** | `Call.CallConditions` 콜렉션 + `CallCondition` entity 6 property (`Id`/`Type`/`IsOR`/`IsInverted`/`Conditions`/`Children`) | `Entities.fs:94,143-152` | 高 | 必 |
| **ApiCall property** | `ContactKind` (NoContact/NcContact/RisingPulse/FallingPulse/Inverter) | `Entities.fs:136`, `Enum.fs:21-26` | 高 | 必 |
| | `SkipInputSensor` — Work.Duration / ApiDef.ActionType 과 직교한 완료 판정 정책 | `Entities.fs:139` | 高 | 必 |
| | `InTag`/`OutTag`/`InputSpec`/`OutputSpec`/`ApiDefId`/`OriginFlowId` | `Entities.fs:133-138` | 中 | 必 (식별 후 확정) |
| **Call property** | `CallType` (WaitForCompletion/SkipIfCompleted) | `Enum.fs:36-38` | 高 | 必 |
| | `ReferenceOf` — Call aliasing | `Entities.fs` Call section | 中 | 必 (※ alias lossy 와 boundary case — §4.1 검토) |
| **ApiDef property** | `ApiDefActionType` (Normal/Push/Pulse/TimeTotal/TimeAppend/MultiAction 6 case) | `Entities.fs:157`, `Enum.fs:80-86` | 高 | 必 |
| | `Description` | `Entities.fs:158-160` | 低 | 必 |
| | `TxGuid`/`RxGuid` | 同上 | — | 意 (GUID 4-set 정합) |
| **Work property** | `TokenRole` / `ReferenceOf` | `Entities.fs:68-71` | 中 | 必 (※ `Status4` 는 시뮬 결과 lossy 정합 — 意) |
| **Project meta** | `TokenSpecs` / `Author` / `Version` / `Nameplate` / `HandoverDocumentation` / `TechnicalData` | `Entities.fs:28-31` | 中 | 必 (재오픈 시 reset 회귀) |
| | `SimulationResult` / `DateTime` | 同上 | — | 意 (시뮬 4-set 정합) |
| **DsSystem property** | `IRI` | `Entities.fs:41` | 中 | 必 |
| | `Properties` (SystemSubmodelProperty 콜렉션) | 同上 | 中 | 必 (§4.1 식별 단계에서 *派 도출 가능* 여부 판정) |
| **PLC code-gen metadata** | `ControlSystemProperties` (FBTagMapPreset / AuxPortMapEntry / SignalPatternEntry / BaseAddressOverride / EnableHardwareControl / WorkTimeout 등) | `02_Control.fs:242-370` | 中 | **新 카테고리 — §4.1 (§4.1.6) 결정 필요** |
| **Submodel property** | `Flow.Properties` / `Work.Properties` / `Call.Properties` (각 *SubmodelProperty 콜렉션) | `Entities.fs` 각 entity | 中 | 必/派 boundary (식별 후 결정) |

### 2.3 검증 완료 — emit 정상 (제외 항목)

다음 항목은 review 결과 *이미 정상 emit* 되므로 보강 대상에서 제외.

| 항목 | 검증 근거 |
|---|---|
| `ArrowType` 6 case (Unspecified/Start/Reset/StartReset/ResetReset/Group) | `formatArrowType` (`ModelProtocol.fs:1194-1202`) + `parseArrowType` (`:176-184`) 양방향 round-trip 정상. ※ 본 todo 가 `ArrowKind` 로 표기했던 것은 실제 타입명 `ArrowType` (Enum.fs:6) — naming 오류 |

### 2.4 라인 번호 silent drift 주의

본 §2 표의 `파일:line` 표기는 작성 시점 기준. 코드 변경 시 line 번호 drift 발생 가능 — 작업 진입 전 *함수명/case 명 기준 grep 재확인* 필수.

---

## 3. Pre-check (구현 전 grep 점검)

각 항목 통과 후 해당 §4 단계 진입 가능 (1:1 매핑).

- [ ] **PC1 → §4.1**: `ModelProtocol.exportToJson` 의 emit 코드 전수 list 와 `Entities.fs` / `Enum.fs` / `02_Control.fs` 의 property·case 를 cross-reference → §2.2 표 외 추가 누락 발견 시 §2.2 누적
- [ ] **PC2 → §4.1.5**: §2 항목별 wire schema shape 결정 (calls 배열 element 의 scalar→object 승격 vs sibling 키 신설) — 본 결정 자체가 SSOT breaking decision 이므로 별도 review
- [ ] **PC3 → §4.2**: `yaml-protocol-v0.md` 의 §1.7 / §2.1 / §2.2 / §2.3 / §2.4 / §2.7 / §4 갱신 책임 배정 (§6.1 매핑 표 참조)
- [ ] **PC4 → §4.3**: `ModelEquivalence.fs:247` 의 `captureShape` 가 신규 키를 cover 하도록 동시 보강 (capturer 미보강 시 round-trip 통과 = false-positive)
- [ ] **PC5 → §4.3**: 현재 round-trip 8건 (ModelProtocolTests) + wiring 6건 (ModelProtocolYamlIOTests) 의 통과/실패 예상 분석. 누락 항목이 store↔JSON 양쪽 모두에서 *동등하게 없음* 으로 통과 중 → 보강 시 negative assertion 동반 추가
- [ ] **PC6 → §6.3**: lossy 4-set 라벨 (GUID/position/alias/시뮬) 유지 + 보강 항목은 lossy 가 *아닌* 원래 의도된 보존 대상. 단 *이전 .yaml 이 사실 lossy 였던 retroactive note* changelog 별도 안내 (m5)
- [ ] **PC7 → §4.2**: `ModelProtocol.Yaml.fs` / `ModelProtocol.YamlIO.fs` 의 generic transformer 가 emit 키 추가를 자동 흡수하는지 함수 단위 검증

---

## 4. 남은 할 일 (구현 순)

### 4.1 식별 단계 + 분류

- [ ] §2.2 표 외 추가 누락 항목 grep 으로 확정 누적
- [ ] 각 누락 항목의 *복원 필수성* 분류:
  - **必 보존**: 모델 의미에 기여 (보강 대상)
  - **派 보존**: 도출 가능 (RuntimeStatus 등 — 보강 불필요)
  - **意 lossy**: 의도된 4-set (GUID/position/alias/시뮬)
  - **メ PLC metadata**: PLC 코드 생성 메타 (FBTagMapPreset 등) — 사용자 명시 설정 부분 必, 도출 가능 부분 派

→ 본 4분류는 §2 우선순위 (高/中/低) 와 **직교 차원**. 必 항목만 §2 표에 누적 후 우선순위 적용. 派/意/メ 항목은 §6.6 정책 부록에 단순 list.

**Boundary handling sub-rule**:
1. *fallback 으로 보존되는 派* (예: `Call.DevicesAlias` 가 `Queries.tryResolveCallTargetSystem` 실패 시 fallback emit, `ModelProtocol.fs:1270-1271`) → **必 으로 격상**
2. *도출 식 자체가 PoC 가정 의존* (예: `workDuration` "첫 Work 만 대표", `ModelProtocol.fs:1363-1365`) → **必** (가정 깨질 위험 대비)
3. *runtime-only / 단순 cache* → 派
4. *4-set 명시 lossy* → 意
5. *PLC 코드 생성 메타* → メ (사용자 명시 설정만 必 으로 격상)

### 4.1.5 schema-shape 결정 phase

- [x] **결정 채택: 옵션 C — dual format (default 시 string scalar 유지 / non-default 시 object 승격)** — 2026-05-14 phase 1 산출
  - 사유: 기존 schema 무변경 (legacy .yaml 호환 100%) + apply 측 dispatcher 추가 분기 1건 (`JsonValueKind.String → 기존 처리, Object → 신규 처리`) + LLM 부담 0 증가 (default 케이스는 기존 string 그대로) + `§6.3 (b) "default 생략 emit" 정책` 완벽 정합
- [x] 검토한 대안:
  - 옵션 A (object 강제 승격): SSOT 정합 ↑ but wire breaking, legacy 거부
  - 옵션 B (sibling 키 신설 `callConditions:` 등): 기존 schema 무변경 but 같은 ApiDef N회 호출 (§1.7 "ApiDef 중복 Call 허용") 시 식별 불가
- [x] 산출 shape (Active calls 안 — non-default 1개 이상 시 object 승격):
  ```yaml
  calls:
    - Z1_C1.ADV                              # default → string scalar (legacy 동일)
    - ref: Z2_C2.SENSOR                      # non-default → object
      contactKind: NcContact                 # ApiCall.ContactKind (1:1 invariant)
      skipInputSensor: true                  # ApiCall.SkipInputSensor
      callType: SkipIfCompleted              # SimulationCallProperties.CallType
      callCondition:                         # Call.CallConditions[0]
        type: ComAux                         # AutoAux default 면 생략
        isOR: false
        isInverted: false
        conditions: [<ApiCall path/object>]  # recursive
        children: [...]
  ```
- [x] 추가 신규 키: `apiDetails` (Passive ApiDef 별 actionType/description), `iri` (System), `duration`/`tokenRole`/`referenceOf` (Work), `author`/`version`/`tokenSpecs` (Project root)
- [x] schema breaking decision → 자가 검열 trigger ④⑤ 발동, sub-agent 위임 의무 — phase 1 §4.1.5 결정 단계 review 1회 수행 완료

### 4.2 코드 변경 (분류 별 분리 commit — §6.4 정책 정합)

- [x] **C-1 enum/string 변환 helper** (2026-05-14 완료): `ModelProtocol.fs` 에 `formatArrowType` 패턴 답습으로 8 helper 추가 (`parseCallConditionType`/`formatCallConditionType` × CallConditionType, ContactKind, CallType, ApiDefActionType). ApiDefActionType 은 DU 인자 grammar (`TimeTotal(500)`/`MultiAction(3, 100)`) regex parser 포함. 호출처 연결은 C-3~C-5 phase. 빌드 통과 (경고 0 / 오류 0). 자가 검열 통과 (Critical/Major 0, Minor 3 현 상태 수용)
- [x] **C-2 SSOT 동시 갱신** (2026-05-14 완료): `yaml-protocol-v0.md` §1.7 결정 row 4건 추가 (4분류 必/派/意/メ + 옵션 C dual format + SSOT 갱신 책임 표 + drift caveat) + §2.4.1 'Enum 라벨 사전' 신설. 자가 검열 통과 (Critical/Major 0건, Minor 3건 후속 phase 동반 처리 가능)
- [x] **C-3 CallCondition tree** (2026-05-14 완료): `ModelProtocol.fs` 의 `dispatchWork` calls 처리 + `exportToJson` calls emit 양쪽에 dual format 구현 (옵션 C). `parseCallCondition` / `emitCallCondition` recursive helper 추가. `tryFindCallInPlan` helper + `callHasEnhancement` 검사. PoC scope: `Call.CallConditions[0]` 만 emit (multiple root 후속 phase). SSOT §2.2 / §2.2.1 동반 갱신. ModelProtocolTests 72/72 통과 (기존 70 + 신규 2: round-trip + legacy compat). 자가 검열 통과 (Critical/Major 0건, Minor 4건 모두 수용)
- [x] **C-4 ApiCall 추가 property** (2026-05-14 완료): `SkipInputSensor` (bool) + `InTag` / `OutTag` (IOTag — Name/Address 두 키 PoC scope) emit/apply 추가. `parseIOTag` / `writeIOTag` helper 신설. round-trip 테스트 1건 (73/73 통과). `InputSpec` / `OutputSpec` (ValueSpec union 12 case) + ApiCall.ApiDefId/OriginFlowId (派 도출) 은 **별도 phase 분리** — SSOT §2.2.1 명시
- [x] **C-5 CallType + ApiDefActionType + Description** (2026-05-14 완료): `SimulationCallProperties.CallType` (Call.Properties 콜렉션 mutation) emit/apply + Passive `apiDetails.<ApiDef>.actionType` / `description` 신설. `callTypeOf` / `setCallType` helper + `tryFindApiDefInPlan` helper. SSOT §2.3 apiDetails sub-section + §2.2.1 callType. round-trip 테스트 1건 (74/74 통과)
- [x] **C-6 Project meta + DsSystem.IRI + Work.TokenRole** (2026-05-14 완료): `Project.Author` / `Version` + `DsSystem.IRI` + `Work.TokenRole` 단순 leaf 키 (PoC scope: 단일 Flags 만). `parseTokenRole` / `formatTokenRole` helper + `tryFindProjectInPlan` / `tryFindSystemInPlan` / `tryFindWorkInPlan` helper. SSOT §2.1 / §2.2 / §2.4.1 (TokenRole row) 갱신. round-trip 테스트 1건 (75/75 통과). **별도 phase 분리**: `Work/Call.ReferenceOf` (path resolution) / `Project.TokenSpecs` / `Project.Nameplate` / `HandoverDocumentation` / `TechnicalData` (복잡 Submodel objects) / 복합 Flags TokenRole — SSOT 미명시 (todo 만 표기)
- [x] **C-4/5/6 통합 자가 검열 통과** (2026-05-14): Critical/Major 0건. Minor 4건 모두 의도된 PoC scope 또는 후속 phase 위임
- [x] **외부 review (`--inspect-diff 5`) 반영** (2026-05-14): Critical 0건, Major 6건 + Minor 다수. 본 phase 동반 처리 6건:
  - **TokenRole 복합 Flags round-trip 불가 경고**: `formatTokenRole` 이 복합 (`Source ||| Sink` 등) store 값을 forensic `Combined(<int>)` 로 emit, `parseTokenRole` 은 즉시 거부 — round-trip 비대칭. SSOT §2.4.1 TokenRole row 에 "복합 Flags = round-trip 불가 (의도된 PoC 제약)" 1줄 추가. 후속 phase 가 `"Source|Sink"` pipe 표기 dual 처리 도입.
  - **빈 IOTag (`Some empty`) emit 가드**: `Name`/`Address` 모두 빈 string 인 IOTag 인스턴스가 emit `{}` 으로 출력되고 `parseIOTag` 는 None 반환하여 `Some empty ↔ None` 비대칭. `ioTagHasContent` 헬퍼 (`callHasEnhancement` 전 위치) 추출하여 emit 자체 skip + `callHasEnhancement` 의 IOTag 검사 강화 (`Option.exists ioTagHasContent`).
  - **`apiDetails` 적용 범위 정정**: `device` 키 부재 Passive 는 ApiDef 미생성이라 `apiDetails` entry 가 모두 entry.ApiDefIds lookup 실패 → forensic diag 거부. 코드 주석 정정 + SSOT §2.2.1 `apiDetails` 정의에 "device sugar 가 있는 Passive 한정" 명시.
  - **빈 `callCondition: {}` 정규화**: 빈 object 가 의미 0 의 CallCondition 인스턴스 추가하지 않도록 `parseCallCondition` 진입 시 `EnumerateObject() |> Seq.isEmpty` 체크 → None 반환.
  - **`description` 빈 string apply 정규화**: apply 측이 `Some ""` 로 set 하면 emit 측 default-skip 정책 (Some 이고 빈 string 아닐 때만 emit) 과 비대칭 → 2-pass round-trip drift. apply 측 `Option.filter (not << IsNullOrEmpty)` 로 빈 string → None 정규화.
  - **테스트 4건 추가**: nested CallCondition children round-trip (children Type 은 `SkipUnmatch` non-default 로 설계 — `AutoAux` default-skip 의 비대칭 회피) / 빈 IOTag emit-skip 검증 / 빈 `callCondition: {}` None 정규화 검증 / 모든 default 시 신규 키 emit 0건 종합 lock-in. ModelProtocolTests 79/79 통과 (기존 75 + 신규 4)
- [x] **외부 review — 별도 phase 분리 (todo §7 후속 결정 등록)**:
  - **helper 3종 추출**: `tryFindXxxInPlan` 5종 + `tryFind + Option.orElseWith Queries.getXxx` fallback 5+ 회 + `tryProp + bind tryString + iter` 6+ 회 → `resolveSystem` / `resolveCall` / `resolveApiDef` / `resolveProject` / `resolveWork` (fallback 포함), `applyStringProp`, `applyEnumProp` 추출. 후속 leaf 키 추가 시 누적 효과.
  - **negative-test 묶음**: `parseTokenRole` / `parseIOTag` non-object / `skipInputSensor` non-bool / `apiDetails` non-object / unknown ApiDef name / `parseCallCondition` non-array `conditions` 등 7개 분기 `[<Theory>]` + `[<InlineData>]` 묶음. `parseCallCondition` 의 `conditions` 분기에 ValueKind != Array silent skip 도 진단 추가가 정석.
  - **SSOT magic literal 분리**: `Project.Version="1.0.0"` / `CallType.WaitForCompletion` / `ApiDefActionType.Normal` 등 entity-default 와 SSOT 의 hardcode 듀얼 SSOT — 후속 phase 에서 단일 source 화.
  - **`tryFindCallInPlan` SSOT 분산**: `ToolOperations.fs:114` + `ModelProtocol.fs` 양쪽 file-scoped private 으로 중복 — helper 3종 추출 시 일원화.
  - **IRI 처리 시점 Active/Passive 비대칭**: Active 는 `try` 블록 안, Passive 는 별도 블록 — helper 3종 추출 시 자동 해소.
  - **테스트 helper Queries 체이닝 패턴**: 신규 테스트 4건에서 `proj/ctrl/flow/work/call` 체이닝 4줄 반복 — `findAdvCall` 만 일부 흡수했으나 generic test helper 미완성.
- [ ] **C-7 Submodel property + PLC metadata**: §4.1.5 결과에 따라 별도 phase 분할 (분량 클 경우 §4 단계 외부로 분리)
- [ ] **C-8 ModelProtocol.Yaml.fs / YamlIO.fs**: PC7 자동 흡수 검증 (generic transformer 가정 확인) — 코드 변경 없어야 정상

→ 의존 그래프: `C-1 → C-2 → C-3 → C-4 → C-5 → C-6 → (C-7 별도)`. C-3/C-4 는 schema 가 같이 묶이므로 같은 commit 권장.

### 4.3 테스트

- [ ] **TC-1 capturer 보강**: `Helpers/ModelEquivalence.fs:247` `captureShape` 가 신규 키 (callCondition / contactKind / callType / auxKind / apiDefActionType / 기타) 를 cover 하도록 동시 확장. **본 단계 미수행 시 round-trip 통과 = false-positive** (M4)
- [ ] **TC-2 round-trip 보강**: 보강 항목 별 round-trip 1건씩 추가 (`ModelProtocolTests.fs`)
- [ ] **TC-3 전수 property round-trip 매트릭스 (옵션, 권장)**: `genSampleStore` 가 각 entity 의 모든 mutable property 를 default 와 다른 값으로 세팅 → `exportToJson |> apply |> exportToJson` deep-equal (lossy 4-set 만 ignore). 본 baseline 통과 시 *§2 표 누락 자체* 가 테스트 실패로 자동 발견
- [ ] **TC-4 negative assertion**: 보강 항목 명시 없는 .yaml 도 통과 (default 처리 정합) — `ModelProtocolTests.fs:654~657` 패턴 답습
- [ ] **TC-5 fixture round-trip**: `WithCyl.json` 등 기존 fixture 가 보강 후 *새 키 emit 0건* 인지 확인 assertion 추가 (silent emit drift 차단)
- [ ] **TC-6 wiring 6건 유지**: `ModelProtocolYamlIOTests.fs` 의 GUID-무시 semantic equivalence 테스트 통과 검증

### 4.4 자가 검열

CLAUDE.md trigger 평가 — 본 작업은 **②③④⑤ 4건 동시 충족** (cross-validation 결과):
- **② 신규 함수/타입 3개 이상**: parser/formatter 페어 × 최소 4 enum × 2방향 = 8+ 신규 helper 예상
- **③ 단일 파일 100 line 이상 변경 또는 2 file 이상 동시 변경**: `ModelProtocol.fs` (1,750 line) + `yaml-protocol-v0.md` (756 line) + `Helpers/ModelEquivalence.fs` 최소 3 file
- **④ dispatch / control flow 재작성**: CallCondition entity walker 신규 dispatcher (단순 leaf 키 아님)
- **⑤ public API / SSOT 상수 갱신**: `yaml-protocol-v0.md` 7개 절 (§1.7 / §2.1 / §2.2 / §2.3 / §2.4 / §2.7 / §4)

- [ ] 위 4 trigger 동시 발동 — sub-agent 위임 review 의무. *schema-shape 결정 단계 (§4.1.5)* 와 *최종 commit 전* 2회 위임 권장
- [ ] 검열 미수행 상태에서 commit / push / 다음 phase 진입 차단

---

## 5. 관련 파일

| 파일 | 역할 | 본 작업 영향 |
|---|---|---|
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` | emit/apply dispatcher | **주 변경 (C-1~C-7)** |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` | JSON↔YAML transformer (generic) | PC7 자동 흡수 검증 (코드 변경 없음 기대) |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.YamlIO.fs` | store↔YAML 합성 wrapper. `done-yaml-save-format.md` 의 `fc4f6c4` 후속으로 *이미 신규 commit 됨* — 본 작업은 emit/apply 분기 추가 | 코드 변경 가능성 (transformer 가 generic 아닐 경우) |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Mermaid.fs` | Mermaid export — CallCondition 분기 표현 정책 미결 | **m8 결정 필요** |
| `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` | LLM prompt schema 안내 | 신규 키 등장 시 prompt 갱신 가능성 |
| `Solutions/Core/Ds2.Core/Enum.fs` | CallConditionType / ContactKind / CallType / ArrowType / Status4 / ApiDefActionType | 참고 (변경 없음) |
| `Solutions/Core/Ds2.Core/Entities.fs` | entity property 전수 (보강 항목 source) | 참고 (변경 없음) |
| `Solutions/Core/Ds2.Core/SequenceSubmodels/02_Control.fs` | ComAux/AutoAux 의미 정의 + AuxPortMapEntry.AuxKind (※ Work property 아님 — naming 정정) | 참고 (변경 없음) |
| `Solutions/Core/Ds2.Editor/Store/Panel/Panel.Condition.fs` | apply 측 기존 default 패턴 (`CallConditionType.AutoAux`, line 35) — §6.3 default 매핑 표 참조 source | **참고** (수정 아님) |
| `Apps/Promaker/Docs/yaml-protocol-v0.md` | SSOT — 7개 절 갱신 (§6.1 매핑 표 참조) | **주 변경 (C-2)** |
| `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` (line 164, 221) | C# interop — `apply` / `exportToJsonScoped` 호출처. partial export depth 와 신규 키 상호작용 검증 | **검증 대상** (m7) |
| `Apps/Promaker/Promaker/ViewModels/Shell/FileCommands.cs` (line 540~564) | lossy 4-set 안내 dialog | 변경 없음, 단 retroactive note 별도 changelog 필요 (m5) |
| `Solutions/Tests/Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs` (line 247) | shape capturer | **주 변경 (TC-1)** — 미보강 시 round-trip false-positive |
| `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs` | round-trip 8건 baseline | **주 변경 (TC-2/3/4/5)** |
| `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolYamlIOTests.fs` | wiring 6건 + GUID-무시 equivalence | TC-6 검증 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolMermaidTests.fs` | Mermaid export 테스트 | 신규 키 무시 무해 검증 |

---

## 6. 주의 사항

### 6.1 SSOT 갱신 책임 매핑 표

`yaml-protocol-v0.md` 의 영향 절을 todo 변경 항목과 1:1 매핑. 누락 회귀 차단.

| todo 변경 (§4.2) | yaml-protocol-v0.md 갱신 절 | 갱신 내용 |
|---|---|---|
| C-1 enum/string helper | §2.4 arrows / §2.1 top-level enum 표 | 신규 enum 라벨 표 추가 (CallConditionType / ContactKind / CallType / ApiDefActionType) |
| C-3 CallCondition tree | §2.2 active works/calls 안 callCondition shape | §4.1.5 schema 결정 결과 반영 (object vs sibling) |
| C-4 ApiCall property | §2.2 / §2.3 (passive apis 도 cover) | leaf 키 enumeration |
| C-5 CallType / ApiDefActionType | §2.2 calls / §2.3 apis | leaf 키 enumeration |
| C-6 ReferenceOf / Project meta / IRI | §2.2 / §2.3 / project root | leaf 키 enumeration |
| C-7 PLC metadata | §2.1 view scope + §2.3 passive (분리 phase 가능) | 별도 키 group |
| 전체 | §1.7 결정 사항 표 | 보강 항목 결정 배경 + 4분류 (必/派/意/メ) 정의 row 추가 |
| 전체 | §2.7 validate 룰 | unknown 키 거부 분기에 신규 키 추가 |
| 전체 | §4 apply 룰 | default fallback 정책 (§6.3 참조) 명문화 |

→ §6.1 의 9 row 모두 갱신 완료 전까지 commit 금지. 자가 검열 trigger ⑤ 정합.

### 6.2 lossy 4-set 라벨 의미 유지

- 보강 항목은 lossy 가 *아닌* 의도된 보존 대상.
- Dialog/title bar 의 `[YAML, lossy]` 배지 + "GUID·위치·alias·시뮬" 4-set 안내 메시지 (`FileCommands.cs:551`) 는 변경 없음.
- 단 **retroactive note** (m5): 보강 *이전* 사용자에게 *이전 .yaml 의 의미 손실 가능성* (callCondition / contactKind / callType / apiDefActionType 등) 안내 → done 문서 또는 별도 changelog 에 1줄.

### 6.3 기존 .yaml 호환성 + default 매핑 표 + silent fallback 금지 정책

**Critical 정책 (C3)**: enum 0-default 가 **non-trivial 의미** (AutoAux / NoContact / WaitForCompletion) 인 경우 silent fallback 시 *원본 store 가 non-zero 였던 모델* 의 의미가 round-trip 후 변경됨 → §1 목표 "재오픈 시 모델 의미 동등" 과 모순.

차단 메커니즘 (택 1 의무):
- **(a) Diagnostic 발행**: apply 측이 새 키 부재 → default fallback 시 `Diagnostics.warn "key X missing — fallback to <default>"` 발행. silent 금지.
- **(b) Default 생략 emit**: emit 측이 *store 값이 default 와 동일* 한 경우 키 자체 생략. apply 측은 키 부재 → default 적용 (이때는 emit 측이 동등성 보장).

→ (b) 가 wire payload 도 작아 권장. SSOT §1.7 에 정책 채택 row 추가.

**Default 매핑 표** (기존 store 패턴 인용 — CLAUDE.md "기존 함수 재활용 90점" 정합):

| 항목 | default | 인용 source |
|---|---|---|
| `CallCondition.Type` | `CallConditionType.AutoAux` | `Panel.Condition.fs:35` `cond.Type \|> Option.defaultValue CallConditionType.AutoAux` |
| `ApiCall.ContactKind` | `ContactKind.NoContact` | `Entities.fs:136` entity default |
| `ApiCall.SkipInputSensor` | `false` | `Entities.fs:139` entity default |
| `Call.CallType` | `CallType.WaitForCompletion` | `Entities.fs` Call section entity default |
| `ApiDef.ApiDefActionType` | `Normal` | `Entities.fs:157` entity default |
| `CallCondition.IsOR` / `IsInverted` | `false` | entity default |
| 기타 | — | 신규 default 결정 시 별도 review trigger ⑤ |

### 6.4 점진 보강 commit 분리 정책

분류 별 (§4.2 C-1 ~ C-7) 분리 commit. 각 분류 별 자가 검열 (§4.4 trigger 평가) 독립 수행. 권장 순서:
1. **C-1**: enum/string helper (no schema impact, 안전)
2. **C-2**: SSOT 부분 갱신 (해당 분류 한정)
3. **C-3 + C-4 + C-5**: schema 결정 (§4.1.5) 동반 (object 승격 시 같은 commit)
4. **C-6**: leaf 키 추가
5. **C-7**: PLC metadata (별도 phase 분할 가능)
6. **TC-1 ~ TC-6**: 분류 별 commit 끝마다 동반 / 또는 최종 일괄

### 6.5 자가 검열 trigger 종합 (§4.4 참조)

본 작업은 trigger **②③④⑤** 4건 동시 충족. sub-agent 위임 review 의무 (§4.4).

### 6.6 누락 항목 발견 channel

본 todo 외에도 reviewer / 실 사용 중 누락 발견 시 §2.2 표에 추가 누적. §6.6 정책 정합.

---

## 7. 후속 결정 항목 (코드 진입 전 해결 필요)

| 항목 | 현 상태 | 결정 필요 사항 |
|---|---|---|
| **Schema shape** (§4.1.5) | 미결 | `calls` element scalar→object 승격 vs sibling 키 신설 |
| **partial export 상호작용** (m7) | 미결 | `exportToJsonScoped` depth=2 에서 work 내부 신규 키 절단 정책 |
| **Mermaid export 표현** (m8) | 미결 | `ModelProtocol.Mermaid.fs` 가 CallCondition 분기를 graph 노드로 표현할지 |
| **`Work.AuxKind` (PLC metadata)** | naming 혼동 정리 — 실제는 `AuxPortMapEntry.AuxKind: string` (02_Control.fs:190), Work entity property 아님 | §4.1 メ 카테고리 (PLC metadata) 로 분리. Work 차원이라는 표현 사용 금지 |

---

## 8. 진척 표

| 단계 | 상태 |
|---|---|
| §1 작업 목표 정의 | ✅ 완료 |
| §2.1 사용자 명시 1차 list | ✅ 완료 (CallConditionType 3 case) |
| §2.2 cross-validation 추가 발견 | ✅ 완료 (review 산출, baseline 확장) |
| §2.3 emit 정상 항목 분리 (ArrowType) | ✅ 완료 |
| §3 pre-check PC1~PC7 | ✅ 완료 (2026-05-14) — PC2/PC3/PC6 은 §4.1.5/§6.1/§6.2 산출물로 흡수, PC7 generic transformer 검증 통과 (Yaml.fs/YamlIO.fs 코드 변경 불필요) |
| §4.1 식별 + 4분류 (必/派/意/メ) | ✅ 완료 (2026-05-14) — boundary handling sub-rule 적용 (DevicesAlias 意 유지, Work.Duration 必 격상, PLC metadata メ 분리) |
| §4.1.5 schema-shape 결정 | ✅ 완료 (2026-05-14) — 옵션 C 채택 (dual format) |
| §4.2 C-1 enum/string helper | ✅ 완료 (2026-05-14) — 8 helper + ApiDefActionType regex parser. 빌드 통과 / 자가 검열 통과 |
| §4.2 C-2 SSOT 부분 갱신 | ✅ 완료 (2026-05-14) — §1.7 결정 row 4건 + §2.4.1 enum 사전 신설 |
| §4.2 C-3 CallCondition tree | ✅ 완료 (2026-05-14) — dual format dispatcher + parse/emit recursive helper + round-trip 테스트 2건 추가 (72/72 통과) + SSOT §2.2/§2.2.1 갱신 |
| §4.2 C-4 ApiCall property | ✅ 완료 (2026-05-14) — SkipInputSensor + InTag/OutTag (IOTag) emit/apply. InputSpec/OutputSpec 별도 phase |
| §4.2 C-5 CallType / ApiDefActionType | ✅ 완료 (2026-05-14) — SimulationCallProperties.CallType + Passive apiDetails (actionType / description) |
| §4.2 C-6 leaf 키 (단순) | ✅ 완료 (2026-05-14) — Project.Author/Version + DsSystem.IRI + Work.TokenRole. ReferenceOf / Project.TokenSpecs 등 복잡 항목 별도 phase |
| §4.2 C-7.1 PLC metadata leaf scalar | ✅ 완료 (2026-05-14) — 4 entity (ControlSystem/Flow/Work/Call) leaf scalar 54건 emit/apply. type-default 비교 + dual format (Call) + TimeSpan ISO grammar (`"00:00:05"`). runtime-only 4건 제외 (派). 복합 collection (`FBTagMapPresets` / `BaseAddressOverride` / `InterlockConditions` / `SignalCounts`) **별도 phase (C-7.2)** 로 분리 |
| §4.2 C-7.2 PLC 복합 collection | ⏳ — `FBTagMapPresets` (System, `Dictionary<string, FBTagMapPreset>` — 복합 객체 + ResizeArray 다중) / `BaseAddressOverride` (Flow, `FBBaseAddressSet option`) / `InterlockConditions` (Work, `string array`) / `SignalCounts` (Call, `Dictionary<string, int>`). C-7.1 진입 전 §4.1.5 schema-shape 재결정 필요 (FBTagMapPreset 자체가 IwPatterns / QwPatterns / MwPatterns / AuxPortMap 등 ResizeArray 컴포지트 — wire 표현 복잡도 크다) |
| §4.2 C-8 Yaml/YamlIO 자동 흡수 검증 | ✅ 완료 (PC7 산출물 — generic transformer / wiring only 확정) |
| §4.3 TC-1 capturer 보강 | ✅ 완료 (`8e360aa` + C-7.1) — `CallDetail` + `ApiDefDetails` + `SystemShape.IRI` + `StoreShape.ProjectAuthor·Version` + 4 entity `PlcXxxSummary` 비교 확장 |
| §4.3 TC-2 round-trip 테스트 | ✅ 완료 (2026-05-15) — Phase 7 신규 9건 + `8e360aa` 후속 M-F 7분기 + `9d12093` [<Theory>] 분리 / negative test 3건 / `ApiCallCount` guard + 추가 negative test 4건 + C-7.1 round-trip 4건 + all-default 1건 + plc negative theory 5건 + **#17/#18/#19** (Passive plc round-trip + `Some ""` vs `None` + plc type 위반 3건) + **M1 IRI 3 case Theory** (`""` / `null` / 부재). **ModelProtocolTests 111/111 통과** (전체 377/377) |
| §4.3 TC-3~6 추가 보강 | ⏳ — 전수 매트릭스 / fixture / wiring 부분 잔존 |
| §4.4 자가 검열 (trigger ②③④⑤) | ✅ 완료 (총 8회 위임 — C-1/C-2 / C-3 / C-4-5-6 / 외부 review / `9d12093` / `71ee9fa` / C-7.1 / **#13~#20 통합 + M-1**. 모두 Critical/Major 0건. **2026-05-15 외부 메타리뷰** (`--review` N=3) 산출 Major 4 / Minor 9 — M1/M2/M3/R2 본 phase 처리, M4 반론 docstring 만 후속, 나머지 #21~#26 등록) |
| commit (사용자 명시 시) | ✅ 완료 (`906b327` / `5d01ca8` / `322c7ca` / `8e360aa` / `9d12093` / `71ee9fa` / `bb82365` C-7.1, **2026-05-15 #13~#20 + M-1 + 외부 메타리뷰 후속 — `--gc` 대기**). upstream 미설정 — push 보류 |
| §7 후속 결정 항목 해결 | ⏳ |

---

## 9. 참고 — 관련 done 문서

- `Apps/Promaker/Docs/done-yaml-save-format.md` — `.yaml` 저장 포맷 도입 (본 작업의 전제). lossy 4-set 라벨 / wiring / 자가 검열 결과 archive.
- `Apps/Promaker/Docs/done-yaml-protocol-implementation.md` — protocol v0 Phase 0~3, 5~6 구현 history.

---

## 10. 다음 세션 이어받기 가이드 (transfer entry point)

> 본 sub-section 은 `--transfer` (2026-05-14) 산출 — 새 Claude Code 세션이 본 todo 를 이어받아 작업을 계속할 수 있도록 *남은 할 일* 중심으로 정리. 본 가이드만 읽고도 진입 가능하도록 핵심 컨텍스트 포함.

### 10.1 현재 상태 (2026-05-15 기준)

**2026-05-15 추가 turn — `1~8 까지 연속 진행` 사용자 지시로 follow-up 8건 + M-1 + 외부 메타리뷰 (`--review` N=3) Major 4/Minor 9 처리**:
- **#14** `parseCallCondition` 진단 키 잔여 3건 `joinDiagKey` 치환 + **M-1 follow-up 14건** 일괄 (`path + "." + key` SSOT 전부 통일)
- **#15** `lookup*ById` 5종 → generic `lookupById` + 5 wrapper alias (`Queries.getXxx` 시그니처 일관성 활용)
- **#16** IRI 빈 string 정규화 — `applyStringOptProp` 신설 (description 패턴 답습) + emit 측 `Option.filter` 가드. **외부 review M1+M2 반영**: `applyNonEmptyStringProp` 으로 rename + null 분기 추가 (reset semantic) + docstring 강화. IRI 3 case Theory 1건 (3 InlineData) 추가
- **#17** Passive system `plc:` round-trip 직접 테스트 1건
- **#18** `string|null` leaf `Some ""` vs `None` 명시 round-trip 1건 (`tagPrefix` 사용)
- **#19** plc type 위반 negative Theory 3건 (bool/float/string|null)
- **#13** namespace internal module `Ds2.LlmAgent.Internal.PlanLookup` 신설 (`PlanLookup.fs`, 34 line) — `tryFindXxxInPlan` SSOT 완전 일원화. `ToolOperations.fs` + `ModelProtocol.fs` 양쪽 file-scoped private 중복 제거. `lookupFlowById` 신설 + line 1391 ad-hoc 호출 정리
- **#20** PLC SSOT 4 위치 분산 통합 (production 측 3 위치 통합 — emit/apply/hasNonDefault). `PlcLeafKind<'cp>` DU (9 case) + `PlcLeaf<'cp>` record + 4 entity leaves table (`plcSystemLeaves` 23 / `plcFlowLeaves` 2 / `plcWorkLeaves` 21 / `plcCallLeaves` 8 = 54 leaf). generic helper 3종 (`parsePlcLeaves` / `emitPlcLeaves` / `plcHasNonDefault`). 기존 `plc*KnownKeys` Set 4개 제거 (leaves 에서 derive). capturer (test project) 통합은 **#25** 로 분리 — `InternalsVisibleTo` 정책 결정 필요
- **R2** `plcHasNonDefault ≡ emitPlcLeaves` invariant 1줄 주석 추가
- **외부 메타리뷰 후속 등록** (todo §10.2 #21~#26): M4 docstring (반론) / m1 SourceGenerator / m3 plc Theory boilerplate / capturer 통합 / m6 namespace 책임 경계 docstring

**Phase 7 §4.2 C-1 ~ C-6 + 외부 review 6건 + `8e360aa` / `9d12093` / `71ee9fa` 후속 + C-7.1 PLC metadata leaf scalar 완료** (2026-05-14):
- C-1 enum/string helper 8개 + ApiDefActionType regex parser
- C-2 SSOT §1.7 결정 row 4건 + §2.4.1 'Enum 라벨 사전' 신설
- C-3 CallCondition tree + ContactKind dual format dispatcher (옵션 C)
- C-4 ApiCall.SkipInputSensor + InTag/OutTag (IOTag: Name/Address PoC scope)
- C-5 SimulationCallProperties.CallType + Passive apiDetails (ApiDef.ApiDefActionType / Description)
- C-6 Project.Author/Version + DsSystem.IRI + Work.TokenRole 단순 leaf
- 외부 review (`--inspect-diff 5`) 결과 6건 fix 동반 반영 + 테스트 4건 추가
- **`8e360aa`**: `applyStringProp` / `applyEnumProp` / `lookup*ById` 5종 helper 추출 + M-F 7분기 진단 + capturer 보강 + SSOT §2.7 룰 #10~#24 / §6.3 default fallback 표
- **`9d12093` 후속 — #0 leading-dot fix** (`joinDiagKey` helper), **#10 M-F [<Theory>] + [<InlineData>] 7건 분리**, **#9 parser-error / non-string negative test 3건**, **#8 `ApiCallCount: int` PoC invariant guard**
- **`71ee9fa` 후속 — #3 부분 helper SSOT 일원화** (`tryFindXxxInPlan` 5종 generic 통합), **#11 line 801 `joinDiagKey` 치환**, **#4 추가 negative-test 4건**
- **C-7.1 PLC metadata leaf scalar (2026-05-14)** — 4 entity (`ControlSystemProperties` 23 leaf / `ControlFlowProperties` 2 / `ControlWorkProperties` 21 / `ControlCallProperties` 8 = 54 leaf) emit/apply. **Wire 위치**: entity 안 `plc:` sub-section (System/Flow/Work/Call 4 레벨 — Project 차원 아님). **Default 정책**: type-default 와 다른 값만 emit (Option C dual format 정합 — 모두 default 면 `plc:` 키 자체 생략). **TimeSpan grammar**: `TimeSpan.ToString("c")` = `"00:00:05"` / `"00:00:00.005"` (ms). **Runtime-only 4건 제외 (派)**: `CurrentState` / `LastExecutionTime` / `ExecutionCount` / `ErrorCount` (Work). **Call dual format 합성**: `plcCallHasNonDefault` 가 `callHasEnhancement` 에 추가됨 — Call 의 plc 변경 시 string scalar → object 승격. **복합 collection (FBTagMapPresets / BaseAddressOverride / InterlockConditions / SignalCounts) 별도 phase C-7.2 로 분리**. 자가 검열 (sub-agent general-purpose) 위임 1회 — Critical/Major 0건, Minor 4건 모두 defer 권장 (Passive plc round-trip 직접 테스트 누락 / `Some ""` vs `None` 명시 round-trip 누락 / bool/float 위반 negative 누락 / SSOT 4 위치 분산 — 모두 동일 helper 경로 → silent drift 위험 낮음). SSOT 갱신: §1.7 결정 row 신설 / §2.2 schema 안 plc 키 / §2.2.2 신설 (4 entity leaf 표 — 54 leaf 정의) / §2.3 passive plc / §2.4.1 TimeSpan grammar 추가 / §2.7 룰 #25~#28 / §6.3 default 매핑 표 확장

**Git history** (branch `yaml-save`, upstream 미설정):
- `906b327` Phase 7 §4.2 C-1/C-2 — enum helper + SSOT 갱신
- `5d01ca8` Phase 7 §4.2 C-3 — CallCondition tree + ContactKind dual format
- `322c7ca` Phase 7 §4.2 C-4/C-5/C-6 + 외부 review 반영
- `8e360aa` Phase 7 §4.2 후속 — apply helper 추출 + 진단 강화 + transfer 가이드
- `9d12093` Phase 7 §4.2 후속 — leading-dot fix + Theory 분리 + invariant guard
- `71ee9fa` Phase 7 §4.2 후속 — helper SSOT 부분 일원화 + negative-test 보강
- `bb82365` Phase 7 §4.2 C-7.1 — PLC metadata leaf scalar (54 leaf) emit/apply + SSOT 갱신
- **2026-05-15 — `--gc` 대기**: follow-up 8건 (#13~#20) + M-1 (`path + "." + key` 14건 일괄) + 외부 메타리뷰 후속 (M1+M2 rename / M3 doc / R2 invariant)

**테스트**: `ModelProtocolTests` 111/111 통과 (전체 `Ds2.LlmAgent.Tests` 377/377). 회귀 0건.

### 10.2 다음 진입 후보 (우선순위 순)

> **2026-05-14 추가 phase 산출** (C-7.1 PLC metadata leaf scalar 완료): #6 부분 (C-7.1 = leaf scalar 만, 복합 collection 은 #6.2 로 분리). 자가 검열 Minor 4건 follow-up 등록 (#17~#20).

| # | 작업 | 분량 | 사유 |
|---|---|---|---|
| ~~0~~ | ~~`--inspect-diff 5` M-1 leading-dot fix~~ | ✅ 완료 | `joinDiagKey` helper 추출 — `path = ""` (root-level `author`/`version`) 호출 site cover |
| ~~1~~ | ~~§2.7 unknown 키 거부 룰 갱신~~ | ✅ 완료 (`8e360aa`) | yaml-protocol-v0.md §2.7 룰 #10~#24 신규 키 11개 모두 반영 |
| ~~2~~ | ~~§4 apply 룰 default fallback 정책 명문화~~ | ✅ 완료 (`8e360aa`) | yaml-protocol-v0.md line 707~737 명문화 + default 매핑 표 |
| ~~3~~ | ~~외부 review M-D — helper SSOT 부분 일원화~~ | ✅ 부분 완료 (`71ee9fa`) | `ModelProtocol.fs` 의 `tryFindXxxInPlan` 5종 본문을 generic `tryFindInPlan` + picker 로 통합. 완전 일원화 (namespace internal module 신설) 는 #13 으로 분리 |
| ~~4~~ | ~~M-F 별도 phase — 추가 negative-test 4건~~ | ✅ 완료 (`71ee9fa`) | enum 라벨 위반 3건 + shape 위반 1건. 359/359 통과 |
| ~~5~~ | ~~§4.3 TC-1 capturer 보강~~ | ✅ 완료 (`8e360aa` + C-7.1) | `CallDetail` + `ApiDefDetails` + `SystemShape.IRI` + `StoreShape.ProjectAuthor·Version` + 4 entity `PlcXxxSummary` 비교 확장 |
| ~~6.1~~ | ~~**C-7.1 PLC metadata leaf scalar (4 entity × 54 leaf)**~~ | ✅ 완료 (`--gc` 대기) | 4 entity (System 23 / Flow 2 / Work 21 / Call 8) 단순 leaf scalar emit/apply. type-default 비교 + `plc:` sub-section + TimeSpan `"00:00:05"` grammar + Call dual format 합성 + runtime 4건 제외 (派). SSOT §1.7/§2.2.2 신설/§2.7 룰 #25~#28 + capturer 4 entity + 신규 테스트 10건. 자가 검열 통과 (Critical/Major 0건) |
| **6.2** | **C-7.2 PLC 복합 collection** — `FBTagMapPresets` (System, `Dictionary<string, FBTagMapPreset>` — IwPatterns/QwPatterns/MwPatterns/AuxPortMap 다중 ResizeArray 포함) / `BaseAddressOverride` (Flow, `FBBaseAddressSet option`) / `InterlockConditions` (Work, `string array`) / `SignalCounts` (Call, `Dictionary<string, int>`) | 大 (별도 phase 분량) | §4.1.5 schema-shape 재결정 필요 — FBTagMapPreset 자체가 IwPatterns/QwPatterns/MwPatterns + AuxPortMapEntry/SignalPatternEntry/FBBaseAddressSet 복합. 진입 전 wire 표현 검토 (dict-of-object vs array-of-pair 등) |
| **7** | **별도 phase 분리 항목** (외부 review 등록) | 中~大 | SSOT magic literal 분리 / IRI 시점 비대칭 / 테스트 helper 일반화 |
| ~~8~~ | ~~`ApiCalls[0]` PoC invariant guard~~ | ✅ 완료 | `CallDetail.ApiCallCount: int` 필드 추가 |
| ~~9~~ | ~~enum parser-error / IRI non-string negative test~~ | ✅ 완료 | [<Theory>] 3건 |
| ~~10~~ | ~~M-F 7분기 `[<Theory>]` 분리~~ | ✅ 완료 | 단일 [<Fact>] → [<Theory>] + [<InlineData>] 7건 분리 |
| ~~11~~ | ~~`ModelProtocol.fs:801` `joinDiagKey` 치환 (parseBoolKey)~~ | ✅ 완료 (본 추가 phase) | line 801 1줄 치환. 동일 함수 내 다른 호출처 (793/809/849) 는 #14 로 분리 |
| ~~12~~ | ~~`callCondition.type` 라벨 위반 검증~~ | ✅ 완료 (본 추가 phase, #4 흡수) | `parseCallConditionType` Error 분기 cover (`callConditionTypeInvalidLabel` InlineData) |
| **13** | **namespace internal module 신설 — `tryFindXxxInPlan` SSOT 완전 일원화** — `ToolOperations.fs` + `ModelProtocol.fs` 양쪽 file-scoped private 중복 제거. 새 internal module (e.g. `Ds2.LlmAgent.Internal.PlanLookup`) 신설 후 양쪽에서 참조. fsproj 컴파일 순서 검토 (ToolOperations 보다 *앞*) | 中 (cross-module refactor) | 자가 검열 Minor M-N2 — 의도된 deferral. 본 추가 phase 코멘트 (line 351-353) 에 "후속 module 통합 시 무손실 이전 가능" 명시 |
| **14** | **`parseCallCondition` 진단 키 합성 일관성 — 잔여 3건 `joinDiagKey` 치환** — line 793/809/849 의 `path + "." + key` 를 `joinDiagKey path key` 로 통일. **진입 전 호출 path grep 1차 확인 필수** — 현 호출자가 path="" (root) 를 전달하는 분기가 있다면 진단 키가 `.type` → `type` 으로 leading-dot 제거되어 *변경됨* (회귀 위험 가능). 모든 호출 path 가 non-empty 임을 grep 으로 확인 후 치환 (외부 reviewer M-2) | 小 (3 line + 검증) | 자가 검열 Minor M-N1 — 의도된 deferral. follow-up |
| **15** | **`lookup*ById` 5종 generic 통합** — `let private lookupById planFind storeFind ctx id = planFind ctx.Plan id \|> Option.orElseWith (fun () -> storeFind id ctx.Store)` + 5 wrapper. 가독성 손실 없이 -5 line. `Queries.getXxx` 매개변수 순서 일관성 사전 검증 필요 | 小 (refactor) | 자가 검열 Minor M-N3 — 의도된 deferral. follow-up |
| **16** | **IRI 빈 string 정규화 정책 결정** — `Some ""` ↔ entity-default 비대칭 가능성. emit-skip 정책 (default-skip) vs apply 정규화 (`Option.filter (not << IsNullOrEmpty)` — description 패턴 답습) 중 선택 | 小 (정책 + 1-2 line) | 회귀 보호 미진 분기 — todo #12 잔여 |
| **17** | **C-7.1 후속 — Passive system 의 `plc:` round-trip 직접 테스트** | 小 (테스트 1건) | C-7.1 자가 검열 Minor — Active 만 round-trip 검증. emit 측 `dispatchPassiveSystem` 분기와 apply 측 Passive 분기에 helper 호출 추가됐으나 직접 테스트 없음. 동일 helper 경로 → 위험 낮음 but coverage 강화 |
| **18** | **C-7.1 후속 — `string \| null` leaf 의 `Some ""` vs `None` 명시 round-trip** | 小 (테스트 1건) | capturer `optStr` 가 둘을 구별 (`""` ↔ `"<null>"`) — 우연 catch 가능. 명시 round-trip 추가 시 silent drift 직접 가시화 |
| **19** | **C-7.1 후속 — plc type 위반 negative 추가 (bool/float/string\|null)** | 小 (Theory case 3건) | 현재 `int 기대` 1 case 만. helper 동일 패턴이라 silent drift 위험 낮음 but Theory case 3건 추가로 coverage 강화 |
| ~~20~~ | ~~C-7.1 후속 — PLC SSOT 4 위치 분산 통합~~ | ✅ 부분 완료 (2026-05-15) | production 측 3 위치 (emit/apply/hasNonDefault) → 1 위치 (`PlcLeaf<'cp>` SSOT) 통합. capturer (test project) 는 **#25** 로 분리 |
| **21** | **M1+M2 외부 review** | ✅ 완료 (2026-05-15) | `applyStringOptProp` → `applyNonEmptyStringProp` rename + null 분기 추가 + docstring 강화. IRI 3 case (`""` / `null` / 부재) Theory 1건 (3 InlineData) — round-trip + emit 시 `iri` 키 부재 검증 포함 |
| **22** | **M4 외부 review — plan-first vs store-first SSOT 분산** — `ModelProtocol.lookupById` (plan-first → store) vs `ToolOperations.requireFromStoreOrPlan` (store-first → plan) 두 정책 합리적 의도된 비대칭 (apply 단계 vs commit 후 require). **반론**: 통합 시 의미 손실 위험. docstring 명문화 만 권장 (각 helper 의 사용 시나리오 명시) | 小 (docstring 만) | 통합 시 의미 통일 손실. helper 통합은 보류 — 사용 시점 docstring 추가 정도 |
| **23** | **m1 외부 review — leaves table getter/setter mirror lambda 108개 압축** — F# 8 reflection 한계상 현 형태 합리. 후속 SourceGenerator 도입 시 자동 생성 후보 | 大 (SourceGenerator 도입) | F# 8 LangVersion 제약 — type provider 또는 별도 build step 필요 |
| **24** | **m3 외부 review — plc Theory 신규 case Controller/Cyl1 boilerplate** — `mkPlcYaml` helper 압축 후보. 가독성 trade-off | 小 (test helper) | 8 yaml fixture 의 공통 부분 압축 — yaml string template helper 도입 |
| **25** | **capturer (test project `ModelEquivalence`) leaves table 통합** — production 측 #20 후속. test project 가 `Ds2.LlmAgent.Internal` 참조 가능하면 같은 leaves table 재사용 검토 (internal scope 노출 정책 결정 필요). 또는 `[<assembly: InternalsVisibleTo("Ds2.LlmAgent.Tests")>]` 추가 | 中 (정책 + refactor) | SSOT 4 위치 → 1 위치 완전 통합. ROI = capturer 54 leaf 중복 제거 |
| **26** | **m6 외부 review — `Ds2.LlmAgent.Internal` namespace 책임 경계 docstring** — `PlanLookup.fs` 가 첫 inhabitant. namespace 정책 (assembly 내부 한정, public API 노출 금지) 명문화 | 小 (docstring) | 첫 사용 시점에 정책 정립 — 후속 internal helper 위치 결정 가이드 |

### 10.3 진입 전 필독 — 코드 invariant + PoC scope 가정

**Helper 위치 invariant** (forward-ref 회피 — `ModelProtocol.fs`. **2026-05-15 갱신**: #13/#15/#20 + M3 외부 review 반영. line range 는 추가 refactor 후 재산출):
- **PlanLookup module (`PlanLookup.fs`, namespace `Ds2.LlmAgent.Internal`, #13 신설)**: `tryFindInPlan` / `tryFindSystem` / `tryFindFlow` / `tryFindWork` / `tryFindApiDef` / `tryFindCall` / `tryFindProject` 7종. `ToolOperations.fs` + `ModelProtocol.fs` 양쪽이 file-scoped alias 로 참조.
- `ApplyContext` 정의 직후 (line ~340): `tryFindCallInPlan` / `tryFindApiDefInPlan` / `tryFindProjectInPlan` / `tryFindSystemInPlan` / `tryFindWorkInPlan` / `tryFindFlowInPlan` (6종 — PlanLookup alias) / `callTypeOf` / `setCallType`.
- **`lookupXxxById` 6종 (#15 generic 통합 + #13 후속 lookupFlowById 추가 — M3 외부 review)**: line ~395 즈음. `lookupById planFinder storeFinder ctx id` generic helper + 6 wrapper (`lookupSystemById` / `lookupCallById` / `lookupApiDefById` / `lookupProjectById` / `lookupWorkById` / `lookupFlowById`).
- `joinDiagKey` / `applyStringProp` / `applyEnumProp` / `applyNonEmptyStringProp` (line ~410~440): leaf 키 setter 패턴 통합. **#16/M1/M2 외부 review**: `applyNonEmptyStringProp` (구 `applyStringOptProp`) 는 IRI 등 wire 정규화 정책 — `null`/`""`/부재 모두 None. `readStringOptKey` (plc) 와 정반대 정책 (PoC 보존). 같은 `string option` 도메인이라도 *wire 정규화 vs PoC 보존* 두 정책 공존.
- **PLC apply helpers (C-7.1, #20 SSOT 통합 후 line ~470~720)**: `readBoolKey` ~ `readTimeSpanOptKey` (9종) / `warnUnknownPlcKeys` / **`PlcLeafKind<'cp>` DU + `PlcLeaf<'cp>` record + 4 entity leaves table (`plcSystemLeaves` / `plcFlowLeaves` / `plcWorkLeaves` / `plcCallLeaves`)** / **`parsePlcLeaves` generic helper** / 4 entity wrapper (`parsePlcSystem` / `parsePlcFlow` / `parsePlcWork` / `parsePlcCall`). known keys 는 leaves 에서 derive (별도 Set 정의 제거).
- `resolveApiDef` (line ~740) 정의 직후: `parseIOTag` / `parseCallCondition` — `ApplyContext` + `resolveApiDef` 의존.
- emit 측 helper 순서 (line ~1950 부근): `ioTagHasContent` → `plcCallHasNonDefault` → `callHasEnhancement` → `writeIOTag` → `emitCallCondition` 순. `plcCallHasNonDefault` 가 `callHasEnhancement` 보다 앞이어야 (합성 호출 forward-ref 회피).
- **PLC emit helpers (#20 SSOT 통합 후 line ~2030~2100)**: **`emitPlcLeaves` / `plcHasNonDefault` generic helper** + 4 entity wrapper (`emitPlcSystem` / `emitPlcFlow` / `emitPlcWork` / `emitPlcCall`). 기존 entity 별 inner `writeBool` / `writeStringOpt` 등 closure 9종은 generic helper 의 `PlcLeafKind` 9-case match 로 통합. **invariant**: `plcHasNonDefault cp def leaves = true ⟺ emitPlcLeaves` 가 동일 인자로 1+ leaf 발행 — leaves table SSOT 자동 보장 (R2 외부 review).

**Dual format 정책 (`SSOT §2.2.1`)**:
- store 의 Call entity 가 모든 보강 property 가 entity-default → `calls` element 는 string scalar (legacy 동일)
- 하나라도 non-default → object 승격 (`ref` + 보강 키들)
- emit 측 `callHasEnhancement` 가 분기 판정 — `c.CallConditions.Count > 0 || ContactKind 변경 || SkipInputSensor || InTag/OutTag content || CallType 변경 || plcCallHasNonDefault c`. apply 측 `callsList` 가 tuple `(callRef, callObjOpt)` 로 dual 처리.
- **C-7.1 합성**: `plcCallHasNonDefault c` 가 `callHasEnhancement` 에 OR 로 추가 — Call 의 plc 변경 단독으로도 object 승격 → `plc:` 키 emit 가능. Call 자체 instance 가 있어도 모든 plc leaf default 면 `plcCallHasNonDefault = false` → string scalar 유지. (Call.GetControlProperties() = None 이면 자명히 default.)
- **wire normalization**: object{ref-only} 형태 input 은 emit 시 string 으로 압축 — 입력 형태 보존 안 함, store 값 기준 canonical emit

**PLC metadata 정책 (C-7.1, `SSOT §2.2.2`)**:
- 4 entity (`ControlSystemProperties` 23 leaf / `ControlFlowProperties` 2 / `ControlWorkProperties` 21 / `ControlCallProperties` 8 = 54 leaf) 단순 leaf scalar 만 emit/apply.
- **type-default 비교**: 각 `emitPlcXxx` 안에서 `let def = ControlXxxProperties()` 빈 생성자 호출 후 cur ≠ def 인 leaf 만 emit. 모두 default 면 `openIfNeeded()` 호출 안 됨 → `plc:` 키 자체 emit 0건.
- **TimeSpan grammar**: `TimeSpan.ToString("c")` = `[-][d.]hh:mm:ss[.fffffff]` (예: `"00:00:05"` / `"00:00:00.005"`). `TimeSpan.TryParse` round-trip. `parseDuration` (`^\d+(ms|s)$`) 과 **별개 grammar** — `plc:` 안 시간 키 한정. **`parseDuration` 으로 잘못 부르면 안 됨** (workDuration 등 외부 키와 분리).
- **runtime-only 제외 (派)**: `ControlWorkProperties` 의 `CurrentState` / `LastExecutionTime` / `ExecutionCount` / `ErrorCount` 4건 — emit 안 함. apply 측 입력 시 `warnUnknownPlcKeys` 가 unknown 으로 거부 (派 분류 강제).
- **Option 타입 비대칭**: 본 phase 의 모든 `Option` leaf type-default 가 `None`. emit 측은 `Some s, _ when cur <> defv -> emit string` — `cur = None, def = None → skip`, `cur = Some s, def = None → emit string s`. apply 측은 `null → setter None / string → setter Some s`. round-trip 정합. **단** type-default 가 `Some "foo"` 인 leaf 가 후속 phase 에 추가되면 `cur = None, def = Some "foo"` 케이스에서 emit 비대칭 가능 — emit 측 `writeStringOpt` 패턴 재검토 필요.
- **SSOT 4 위치 분산** (의도된 PoC trade-off): emit (`emitPlcXxx`) / apply (`parsePlcXxx`) / hasNonDefault (`plcCallHasNonDefault`) / capturer (`summarizePlcXxx`) 가 각각 54 leaf list 보유. silent drift 방지 = round-trip 4건 + capturer 비교 + negative theory 5건 + sub-agent review. C-7.2 진입 시 metadata table 통합 후보 (todo #20).
- **2026-05-15 #20 완료**: production 측 3 위치 (emit/apply/hasNonDefault) → 1 위치 (`PlcLeaf<'cp>` SSOT) 통합. capturer (test project `ModelEquivalence`) 는 internal namespace 외 → **별도 후속 #25 등록**.

**PoC scope 가정** (회귀 시 invariant 깨질 위험):
- `Call.ApiCalls` 는 *1:1 매핑* (cylinder/clamp/robot sugar 한정). `ApiCalls[0]` 의 ContactKind / SkipInputSensor / InTag / OutTag 만 emit/apply.
- `Call.CallConditions` 는 multiple root 가능하나 *첫 root 만 emit*. Apply 측도 `callCondition` 단일 키로 받음 — multiple root 입력 미지원.
- `TokenRole` 복합 Flags (e.g. `Source ||| Sink`) 는 emit 시 forensic `Combined(<int>)` 으로 표기, parse 측은 즉시 거부 → round-trip 비대칭 (의도). 후속 phase 가 pipe 표기 dual 처리.
- `apiDetails` 는 device sugar (cylinder/clamp/robot/custom) 가 있는 Passive 한정. device 키 부재인 Passive 는 ApiDef 미생성 → apiDetails entry 가 모두 forensic diag 거부.
- `IOTag` 는 Name/Address 두 키만 emit. `Some empty` IOTag 는 emit 자체 skip (`ioTagHasContent` 가드).
- `Project.Version="1.0.0"` / `CallType.WaitForCompletion` / `ApiDefActionType.Normal` / `TokenRole.None` / `ContactKind.NoContact` / `CallConditionType.AutoAux` 등 entity-default 는 emit 시 키 생략. apply 측은 키 부재 → entity-default 적용.
- **PLC metadata (C-7.1)**: Passive system 도 `plc:` 키 (ControlSystemProperties) 가능. Flow/Work/Call 차원 plc 는 Active 의 internal entity 한정 (sugar 가 생성한 internal Passive Flow/Work/Call 은 GUI 에서 ControlXxxProperties 설정 surface 없음). C-7.2 가 internal entity 차원 plc 지원 도입 시 spec 확장 필요.
- **PLC TimeSpan parse 책임 분리**: `parseDuration` (workDuration ms/s suffix) 와 `TimeSpan.TryParse` (plc 시간 키 `"00:00:05"` 형식) — 두 grammar 가 *동일 함수* 처리하지 않도록 주의. 신규 시간 키 도입 시 어느 grammar 에 속하는지 판정 후 helper 선택.

**별도 phase 분리된 보강 항목** (SSOT §2.2.1 / §2.2.2 / §2.3 명시):
- `ApiCall.InputSpec` / `OutputSpec` (`ValueSpec` union 12 case + Ranges 변형) — wire 표현 복잡
- `Work` / `Call.ReferenceOf` (`Guid option`) — path resolution mechanism 필요 (`pathOf` helper SSOT §2.5.1)
- `Project.TokenSpecs` / `Nameplate` / `HandoverDocumentation` / `TechnicalData` (복잡 Submodel objects)
- `Project.SimulationResult` (意 시뮬 4-set 정합 — 보강 대상 아님)
- `IOTag` 부속 property (Description / DataType BOOL/SINT/INT/.../ DefaultValue)
- 복합 Flags `TokenRole` (`Source ||| Sink` 등 pipe 표기)
- `Call.CallConditions` multiple root 정책
- **PLC 복합 collection (C-7.2)**: `FBTagMapPresets` (Dictionary<string, FBTagMapPreset> — IwPatterns/QwPatterns/MwPatterns/AuxPortMap 다중 ResizeArray 포함 — 복합 객체) / `BaseAddressOverride` (FBBaseAddressSet option — IO/QW/MW 3 base 주소) / `InterlockConditions` (string array) / `SignalCounts` (Dictionary<string, int>)

### 10.4 빌드 / 테스트 명령

```bash
# 빌드 (Ds2.LlmAgent.fsproj — 본 작업 영향 범위)
cd /f/Git/ds2/yaml-save && dotnet build Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj --nologo -v minimal

# 테스트 (ModelProtocolTests 103건 — Phase 7 신규 분 + C-7.1 plc 10건 포함)
cd /f/Git/ds2/yaml-save && dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj --nologo --filter "FullyQualifiedName~ModelProtocolTests"

# 전체 테스트 (369건 — 회귀 확인용)
cd /f/Git/ds2/yaml-save && dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj --nologo
```

**현재 baseline** (commit `bb82365` 기준): ModelProtocolTests 103/103 / 전체 Ds2.LlmAgent.Tests 369/369 통과. 빌드 경고 0 / 오류 0.

### 10.5 신규 round-trip 테스트 추가 패턴

```fsharp
let private xxYaml = """
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv:
          calls:
            - ref: Cyl1.ADV
              <보강 키>: <값>
        Ret:
          calls: [Cyl1.RET]
      arrows:
        - Adv -> Ret : Start
  - system: Cyl1
    kind: passive
    device: cylinder
"""

[<Fact>]
let ``Phase 7 §4.2 C-N — 보강 항목 round-trip`` () =
    let store = DsStore()
    let _ = parseApplyCommit store xxYaml
    // 1. store inspect — 보강 값 적용 확인
    // 2. exportToJson — emit 키 등장 확인 (Assert.Contains compact 패턴)
    // 3. round-trip — 새 store apply → 의미 동등 확인
```

기존 테스트 끝 (`ModelProtocolTests.fs` 의 `findAdvCall` helper 다음) 에 추가. **child Type 이 default 면 emit 측이 키 생략 → re-apply 시 None 으로 normalize** — `Some AutoAux ↔ None` 비대칭 trap 주의 (M-E 검증 사례).

**PLC metadata round-trip 패턴 (C-7.1 산출)**: `plcSystemYaml` / `plcFlowYaml` / `plcWorkYaml` / `plcCallYaml` 4 fixture 가 entity 별 plc 키 사용 예시. `ModelEquivalence.roundTripShape` 가 capturer 비교로 의미-동등 검증 — emit/apply 한 쪽만 leaf 누락해도 즉시 diff 발생.

### 10.6 진입 전 추가 권장 작업

- **C-7.2 진입 전 schema-shape 재결정 필수**: 복합 collection 4종 — `FBTagMapPresets` (Dictionary<string, FBTagMapPreset>) / `BaseAddressOverride` (FBBaseAddressSet option) / `InterlockConditions` (string array) / `SignalCounts` (Dictionary<string, int>). 각 wire 표현 후보 (dict-of-object / array-of-pair / nested object / 단순 array) 검토 + dual format 확장 정책. 단순 leaf scalar (C-7.1) 와 달리 wire 표현 자유도 크고 LLM 부담 증가 가능 — schema 결정 단계 (§4.1.5 식 별도 phase) 권장.
- **C-7.1 후속 SSOT 통합 검토 (#20)**: emit/apply/hasNonDefault/capturer 의 54 leaf list 중복. C-7.2 진입 시 metadata table (`[(name, getter, setter, default, emitter, parser, summarizer)]`) 패턴 도입 — C-7.2 의 복합 collection 도 같은 table 로 통합 가능. ROI 가 더 큼.
- 본 todo 의 **§7 후속 결정 항목 표** 에 *partial export (`exportToJsonScoped` depth) 와 신규 키 상호작용 — m7* / *Mermaid export 가 CallCondition 분기 표현 정책 — m8* 등이 미해결. C-7.2 와 직접 연관성 없으나 별도 결정 필요. `Work.AuxKind` 카테고리는 C-7.1 진입 전 `AuxPortMapEntry.AuxKind` 로 정리 완료 — `FBTagMapPreset.AuxPortMap` 의 복합 객체 안 property 라 C-7.2 scope.
- **C-7.1 follow-up 4건 (#17~#20)**: Passive plc round-trip 직접 테스트 (#17) / `Some ""` vs `None` 명시 round-trip (#18) / plc type 위반 negative 3건 추가 (#19, bool/float/string|null) / SSOT 4 위치 분산 통합 (#20). 모두 Minor — C-7.2 직전에 일괄 처리 권장 (helper 통합 ROI 와 함께).
- **#13~#16 (이전 phase follow-up)**: namespace internal module (`tryFindXxxInPlan` SSOT 완전 일원화 — #13) / `parseCallCondition` joinDiagKey 잔여 3건 (#14) / `lookup*ById` generic 통합 (#15) / IRI 빈 string 정규화 정책 (#16). C-7.2 진입 전 정리 권장 — `parsePlcXxx` 가 `lookup*ById` 패턴과 유사 (entity get-or-create + Properties mutate) 하므로 SSOT 통합 효과 ↑.
