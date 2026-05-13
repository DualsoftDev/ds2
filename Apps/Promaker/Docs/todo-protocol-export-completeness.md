# TODO — ModelProtocol export 도메인 완결성 보강

> 본 문서는 `done-yaml-save-format.md` 후속 작업. `.yaml`/`.json` 저장 경로의 *의도된 lossy 4-set* (GUID / position / alias / 시뮬 결과) 외에 **암묵적으로 누락되어 있는 도메인 데이터** 를 식별하고 emit/apply 양방향 보강.

---

## 1. 작업 목표

`ModelProtocol.exportToJson` / `ModelProtocol.apply` (= protocol v0 의 source-of-truth export) 가 store 의 도메인 데이터를 **선언적으로 완결** 표현하도록 누락 항목 보강.

- **현재 lossy**: GUID / position / alias / 시뮬 결과 — 의도된 4-set (yaml-protocol-v0 §1 SSOT).
- **암묵 lossy (보강 대상)**: SSOT 에 명시되지 않았으나 store 에 존재하고 protocol export 시 누락되는 도메인 데이터.

→ 사용자 의도: ".yaml/.json 저장 후 재오픈 시 모델 의미가 동등 (GUID/position/alias/시뮬 제외)" 보장.

---

## 2. 알려진 누락 항목 (사용자 명시)

| 항목 | 정의 위치 | 도메인 의미 | 우선순위 |
|---|---|---|---|
| **`ComAux`** (`CallConditionType.ComAux`) | `Ds2.Core/Enum.fs:17` | CallCondition 분기 조건 — "Com_X" 보조 코일 (게이팅 없음, `Ds2.Core/SequenceSubmodels/02_Control.fs:180,188`) | 高 |
| **`AutoAux`** (`CallConditionType.AutoAux`) | `Ds2.Core/Enum.fs:16` | CallCondition 분기 조건 — "Auto_X" 보조 코일 (WorkGoing ∧ preds ∧ CallCondition.AutoAux, `02_Control.fs:179,187`) | 高 |
| **`SkipUnmatch`** (`CallConditionType.SkipUnmatch`) | `Ds2.Core/Enum.fs:18` | CallCondition 분기 조건 — 추가 분기 (ComAux/AutoAux 와 형제) | 高 |
| **`OperationConditions`** | (탐색 필요 — Enum.fs / Entities.fs / Panel.Condition.fs 후보) | 운전 조건 (시퀀스 실행 조건) | 中 |

추가 탐색 필요 항목들 (사용자 "..." 부분):
- [ ] `ApiCall.ContactKind` 변형 (`NoContact`/`NcContact`/`RisingPulse`/`FallingPulse`/`Inverter` — Enum.fs:21-26) — emit 여부 확인
- [ ] `Call.CallType` (`WaitForCompletion`/`SkipIfCompleted` — Enum.fs:36-38) — emit 여부 확인
- [ ] `ArrowKind` enum 의 `StartReset`/`ResetReset`/`Group` (Enum.fs:10-12) — protocol 의 ArrowBetween 표현이 이들 분기를 모두 표현하는지
- [ ] `Work.AuxKind` (`02_Control.fs:190`, "AutoAux"/"ComAux") — Work 차원의 aux 분류 emit 여부
- [ ] `ApiCall` / `Call` / `Work` 의 기타 property — Entities.fs 전수 점검

---

## 3. Pre-check (구현 전 grep 점검)

- [ ] **누락 항목 식별** — `ModelProtocol.exportToJson` 의 emit 코드 (ModelProtocol.fs) 와 `Ds2.Core/Entities.fs` / `Enum.fs` 의 property/case 를 cross-reference 하여 *현재 emit 되지 않는* 항목 전수 list 화
- [ ] **SSOT 갱신 동반 결정** — `Apps/Promaker/Docs/yaml-protocol-v0.md` 의 §2 emit 키 표 / §4 apply 룰 갱신 책임
- [ ] **테스트 baseline** — `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs` 의 round-trip 8건이 누락 항목 변화에 어떻게 반응하는지 (현재 통과 = 누락 항목이 store↔JSON 양쪽 모두에서 동등하게 *없음* 으로 처리됨)
- [ ] **lossy 4-set 갱신 여부** — 보강 후 lossy 4-set 라벨 (GUID/position/alias/시뮬) 유지. 보강 항목은 lossy 가 아닌 *원래 의도된 보존 대상*

---

## 4. 남은 할 일 (구현 순)

### 4.1 식별 단계
- [ ] `ModelProtocol.exportToJson` 의 emit 키 전수 list 작성
- [ ] `Ds2.Core.Entities.fs` 의 모든 entity property 와 cross-check → diff = 누락 항목
- [ ] 각 누락 항목의 *복원 필수성* 분류:
  - **必 보존**: 모델 의미에 기여 (ComAux/AutoAux/OperationCondition 등 — 보강 대상)
  - **派 보존**: 도출 가능 (RuntimeStatus, Revision 등 — 보강 불필요)
  - **意 lossy**: 의도된 4-set (GUID/position/alias/시뮬)

### 4.2 코드 변경 (예상)
- [ ] `ModelProtocol.fs` — emit/apply 양쪽에 누락 항목 분기 추가
- [ ] `ModelProtocol.Yaml.fs` — JSON ↔ YAML 변환은 generic 이라 자동 흡수 (확인 필요)
- [ ] `yaml-protocol-v0.md` SSOT — §2 emit 키 표 갱신 + §4 apply 룰 갱신

### 4.3 테스트
- [ ] `ModelProtocolTests.fs` 에 보강 항목 별 round-trip 1건씩 추가
- [ ] `ModelProtocolYamlIOTests.fs` 의 GUID-무시 semantic equivalence 6번째 테스트가 보강 후에도 통과 검증

### 4.4 자가 검열
- [ ] CLAUDE.md trigger ⑤ (public API / SSOT 상수 갱신) 충족 → sub-agent 위임 review

---

## 5. 관련 파일

| 파일 | 역할 |
|---|---|
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` | emit/apply dispatcher — 보강 대상 |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` | JSON↔YAML transformer (generic) |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.YamlIO.fs` | store↔YAML 합성 wrapper |
| `Solutions/Core/Ds2.Core/Enum.fs` | CallConditionType / ContactKind / CallType / ArrowKind / Status4 |
| `Solutions/Core/Ds2.Core/Entities.fs` | entity property 전수 (보강 항목 source) |
| `Solutions/Core/Ds2.Core/SequenceSubmodels/02_Control.fs` | ComAux/AutoAux 의미 정의 + Work.AuxKind |
| `Solutions/Core/Ds2.Editor/Store/Panel/Panel.Condition.fs` | CallCondition 의 Panel 측 처리 |
| `Apps/Promaker/Docs/yaml-protocol-v0.md` | SSOT — §2 emit 키 표 / §4 apply 룰 갱신 책임 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs` | round-trip 8건 baseline |
| `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolYamlIOTests.fs` | wiring 6건 + GUID-무시 equivalence |

---

## 6. 주의 사항

1. **SSOT 갱신 동반 필수** — protocol 의 emit 키 변경은 항상 `yaml-protocol-v0.md` 갱신 동반. SSOT/코드 drift 방지.
2. **lossy 4-set 라벨 의미 유지** — 보강 항목은 lossy 가 *아닌* 의도된 보존 대상. dialog/title bar 의 "[YAML, lossy]" 배지 + "GUID·위치·alias·시뮬" 4-set 안내 메시지는 변경 없음.
3. **기존 .yaml 파일 호환성** — 보강 항목 부재 시 default 값 처리 (apply 측 분기). 본 작업 이전에 저장된 .yaml 도 무 오류 reopen 가능해야 함.
4. **점진 보강 권장** — 큰 single PR 대신 분류 별 (CallCondition / OperationCondition / AuxKind / ...) 분리 commit. 각 분류 별 자가 검열.
5. **CLAUDE.md trigger** — emit/apply 양쪽 분기 추가 시 신규 함수/타입 3개 이상 가능성 → 자가 검열 의무.
6. **누락 항목 발견 channel** — 본 todo 외에도 reviewer / 실 사용 중 누락 발견 시 본 문서 §2 표에 추가 누적.

---

## 7. 진척 표

| 단계 | 상태 |
|---|---|
| §1 작업 목표 정의 | ✅ 완료 |
| §2 누락 항목 1차 list (사용자 명시) | ✅ 완료 (ComAux / AutoAux / SkipUnmatch / OperationConditions) |
| §3 pre-check (전수 grep) | ⏳ |
| §4.1 식별 단계 (emit 키 전수 list + entity diff) | ⏳ |
| §4.2 코드 변경 + SSOT 갱신 | ⏳ |
| §4.3 테스트 추가 | ⏳ |
| §4.4 자가 검열 | ⏳ |
| commit / push | ⏳ |

---

## 8. 참고 — 관련 done 문서

- `Apps/Promaker/Docs/done-yaml-save-format.md` — `.yaml` 저장 포맷 도입 (본 작업의 전제). lossy 4-set 라벨 / wiring / 자가 검열 결과 archive.
- `Apps/Promaker/Docs/done-yaml-protocol-implementation.md` — protocol v0 Phase 0~3, 5 구현 history.
