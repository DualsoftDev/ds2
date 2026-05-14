# TODO — Promaker `.yaml` 저장 포맷 도입

> 본 문서는 *논의 단계* (`--plan`) 에서 결정된 설계를 이어받기 위한 transfer 메모입니다.
> 다음 세션에서 본 문서 + [`yaml-protocol-v0.md`](./yaml-protocol-v0.md) (※ **phase6-readsurface worktree 의 최신본**, `F:\Git\ds2\phase6-readsurface\Apps\Promaker\Docs\yaml-protocol-v0.md`) 를 함께 읽고 진입.
>
> **r1 review 반영 완료** — 5인 reviewer 의 외부 review (C1~C4 / M1~M12 / m1~m11) 를 §8 에 처리 내역으로 풀어 적었습니다. 본문 §2~§7 은 review 후 정정된 내용 기준.

---

## 1. 작업 목표

Promaker 의 File > Open / Save 진입점에 **`.yaml` 확장자 분기** 를 추가하여, 모델을 v0 protocol 의 YAML 표현으로 직렬화/역직렬화한다.

- 의미: v0 protocol 의 `export_model_doc(format=yaml)` / `apply_model_doc` 의 **disk-file 표면 연장**
- 본질: **lossy** — GUID·xyz position·`Call.DevicesAlias`·시뮬 결과 미보존. *사람·LLM 친화 포맷*.
- 사용자 명시 각오: ".yaml 로 저장했다가 .yaml/.json 으로 다시 열면 GUID 신규 발행, 위치 정보 보존 안 됨"

---

## 2. 배경 / 맥락

- SSOT = `yaml-protocol-v0.md` (※ **phase6-readsurface 의 e85edba commit 기준** — main worktree 의 동명 파일은 outdated). 본 문서는 그 SSOT 의 *Promaker UI wiring 결정* 만 추가.
- v0 protocol 구현 진척 (`done-yaml-protocol-implementation.md` §2):
  - Phase 0~3, 5 ✅ 완료
  - Phase 6 (read surface GUID-free 정렬) — 설계 commit `e85edba` 완료. **다만 `ModelProtocol.fs:1099-1106` (view 거부 분기) + `:1196-1198` (view emit) 은 이미 구현 commit 됨** — phase6 worktree 직접 확인 결과. Promaker MCP 도구 surface 갱신 (read 4종 → export_model_doc 인자) 부분만 미착수.
  - Phase 4 (UI YAML preview/apply) 변형 진행 중 (chat dialog 의 YAML/Mermaid view 만 도입)
- 본 `.yaml` 저장 포맷 작업은 *Phase 4 의 별도 갈래* — chat dialog 와 별개로 File 메뉴 진입점에만 wiring. Phase 4 의 preview/apply 결정과는 독립.

**구현 비용 — 정정**: 처음 transfer 작성 시 "신규 비즈니스 로직 0, 얇은 wrapper 만" 으로 진술했으나 ModelProtocol.Yaml.fs 의 public surface 직접 확인 결과 store-level wrapper 가 부재 — 실 구현은 **합성 chain 1~2 함수 신설 필요**:
- Save 경로: `ModelProtocol.exportToJson(store)` → `JsonDocument` parse → `ModelProtocolYaml.jsonElementToYaml(root)` (2-step 합성)
- Open 경로: `ModelProtocolYaml.yamlToJson(text)` → `JsonDocument` parse → `ImportPlanBuilder` → `ModelProtocol.apply` → `DsStoreImportPlanExtensions.ApplyImportPlan` (4-step 합성)

→ "비즈니스 로직 0" 은 유지 (모두 기존 함수 재호출). 다만 *합성 wrapper* 가 `ModelProtocol.Yaml.fs` 또는 Promaker 측에 1~2개 필요.

---

## 3. 확정된 설계 (논의 결정 표)

§3 row 번호는 *결정 ID* — §4 의 절 번호와 별개 (m1 review 정합).

| Row | 항목 | 결정 | 근거 |
|---|---|---|---|
| D1 | 메뉴 위치 | **Open/Save 통합**. `FileFilter` 에 `.yaml` 그룹만 추가. 별도 Import/Export 메뉴 없음 | 사용자 결정 — 진입 일관성 우선 |
| D2 | `_currentFilePath` | **`.yaml` 경로 그대로 유지**. SaveAs 강제 없음 | Save 는 store→file 일방향 emit 이므로 GUID 재발행과 무관. 처음 답변의 "Ctrl+S 마다 GUID 재발행" 우려는 잘못된 추론 — 사용자 지적으로 정정 |
| D3 | Open(`.yaml`) 후 IsDirty | **`IsDirty = true` 강제** + `AfterFileLoad` race 회피 (§4.2 D3 참조 — `_loadedAsLossy` flag 도입) | lossy 재구성 (시뮬 결과·position·alias 잃음). 사용자에게 *영구 보존은 .sdf SaveAs* 시그널 |
| D4 | `view:` flag 처리 | Save 시 **`view: full` 강제 emit** (phase6 의 `ModelProtocol.fs:1196-1198` 가 이미 처리 — wiring 추가 작업 없음). Open 시 **`view: partial` 거부** (phase6 의 `:1103-1104` 가 이미 처리). **`view:` 키 부재 처리는 미결정** — §8 r1 C1 참조 (SSOT 룰 #8 = 부재 거부 vs 실 코드 = 부재 통과). 본 작업의 default = **실 코드 동작 답습 (부재 통과)** + 친절 안내 메시지로 보강. SSOT/코드 갭 fix 는 별도 cycle | SSOT 룰 #7 / phase6 commit 실 코드 |
| D5 | Position 복원 | 기존 `AfterFileLoad` 의 auto-layout 자연 흡수. 별도 sidecar 없음 | protocol 의미 (lossy) 깨뜨림 회피 |
| D6 | 사용자 안내 dialog | **최초 Save(`.yaml`) 1회 one-shot** — "GUID·위치·alias·시뮬 결과 비저장, 영구 보존은 .sdf 사용" + "다시 보지 않기" 옵션 | 사용자가 lossy 의미를 *암묵 인지* 가 아닌 *명시 인지* 하도록. lossy 항목 4-set (GUID / position / alias / 시뮬) 모든 노출 위치에서 sync (§6 / dialog / §1) |
| D7 | UTF-8 BOM | **BOM 없음** — `new UTF8Encoding(false)` 명시. read 측도 동일 | YAML 표준 BOM 비권장. `.NET File.WriteAllText(., Encoding.UTF8)` 은 BOM 포함이므로 명시 override |
| D8 | `.yaml` vs `.yml` | **동등 취급** — `IsYaml(path)` 가 OR 매치. SaveAs default 는 `.yaml` (긴 형) | 일관성 |
| D9 | Title bar 표시 | `_currentFilePath` 가 `.yaml` 일 때 title 에 `[YAML, lossy]` 영구 배지 (UpdateTitle 분기) | silent overwrite UX 보강 (§8 r1 M10 반영) |

### GUID 발행 타이밍 (정정된 모델)

| 시점 | store 의 GUID |
|---|---|
| 새 store (빈 프로젝트) | 1회 발행 |
| **Open(`.yaml`)** | 빈 store + yaml apply → **1회 신규 발행** |
| Open(`.sdf` / `.json`) | 파일에 박힌 GUID 그대로 복원 |
| **Save(`.yaml`)** | 발행 무관 — store→file 일방향. store GUID 그대로, yaml 에는 GUID 미 emit |
| Ctrl+S 반복 | store GUID 보존 |

→ **GUID 재발행은 *Open(`.yaml`) 1회당 정확히 1번***. 같은 파일 두 번 열면 두 인스턴스가 다른 GUID — 사용자 각오 사항.

---

## 4. 남은 할 일 (구현 순)

### 4.1 Pre-check (코드 구현 전 grep 점검)

- [ ] **`ModelProtocol.Yaml.fs` public surface 확인** — `yamlToJson` / `jsonElementToYaml` / `jsonToYaml` 3종 존재 (r1 C2 확인 완료). store-level wrapper 부재 → §4.2 D1 의 합성 chain 작성 필요
- [ ] **`view:` 부재 시 통과 동작 확인** (phase6 `ModelProtocol.fs:1101` 의 `match ... with None -> ()` 추정) — D4 의 default 결정이 이 동작 답습. 갭 fix 별도 cycle 결정 필요
- [ ] **healMissingOriginFlowIds 무해성 확인** — YAML apply 가 OriginFlowId 를 올바르게 채우는지 (r1 M5). `ReplaceOpenedStore` 경로의 `healed = 0` 가정의 정당성. ModelProtocol dispatcher 결과를 ApplyImportPlan 통과 시점에 OriginFlowId 가 항상 set 되어 있어야 함 → 어긋나면 silent 데이터 변형
- [ ] **`ExternalFileWatcher` 의 .yaml reload 영향** (r1 M11) — `CheckExternalFileChange` 가 `.yaml` 도 watch. 외부 에디터 편집 후 reload 수락 시 GUID 재발행 발생. dialog 메시지에 lossy 안내 추가 검토
- [ ] **`DialogHelpers.ShowThemedMessageBox` 의 "다시 보지 않기" 일반화** (r1 M1) — 현재 "다음 시뮬레이션까지" 라벨 hardcoded (`DialogHelpers.cs:288` 추정). 라벨 인자화 + AppSettingStore 영구 persistence helper 추가
- [ ] **`ModelProtocolTests.fs` 의 store ↔ JSON round-trip 8건 통과 여부** (r1 M3) — 통과면 의미 보장 끝. wiring 책임 (path / IsDirty / UTF-8 / view 부착) 만 별도 검증

### 4.2 코드 변경 (≈150 line — r1 review 보강 반영)

#### D0 — 상수 / 확장자

- [ ] `Apps/Promaker/Promaker/Presentation/FileExtensions.cs:4-12` — `public const string Yaml = ".yaml";` + `public const string YamlAlt = ".yml";`

#### D1 — `ModelProtocol.Yaml.fs` 또는 Promaker 측 합성 wrapper

Save 측 (가칭):
```fsharp
// ModelProtocol.Yaml.fs 또는 신규 ModelProtocol.YamlIO.fs
let exportStoreToYamlText (store: DsStore) : string =
    let json = ModelProtocol.exportToJson store  // 이미 view: full emit
    use doc = JsonDocument.Parse(json)
    ModelProtocolYaml.jsonElementToYaml doc.RootElement
```

Open 측 (가칭):
```fsharp
let parseYamlToImportPlan (yamlText: string) : ImportPlan =
    let json = ModelProtocolYaml.yamlToJson yamlText
    use doc = JsonDocument.Parse(json)
    let plan = ImportPlanBuilder.build doc.RootElement
    ModelProtocol.apply plan  // view 거부/통과 분기는 ModelProtocol 안
    plan
```

→ 함수명은 PoC 진입 시 surface 점검 결과로 확정. *비즈니스 로직 추가 0* — 모두 기존 함수 chain.

#### D2 — `FileCommands.cs` 분기 추가

- [ ] `:20` `FileFilter` 갱신: "All Supported" 에 `*.yaml;*.yml` 포함 (r1 m5 반영) + YAML 단독 그룹 추가
- [ ] `:22-27` 영역에 `IsYaml(path)` helper 추가 (OR 매치: `.yaml` / `.yml`)
- [ ] `OpenFilePathCore` (`:141~196`) 에 `IsYaml` 분기:
  - File.ReadAllText (UTF-8) → parseYamlToImportPlan → newStore
  - `PrepareForLoadedStore` → `ReplaceOpenedStore(filePath, newStore, "YAML")`
  - **추가**: `_loadedAsLossy = true` (D3 참조)
- [ ] `SaveToPath` (`:350~452`) 에 `IsYaml` 분기:
  - `BusyMessage = "YAML 저장 중..."` + `IsBusy = true` (r1 M7 반영 — 대형 store)
  - `exportStoreToYamlText(_store)` → `File.WriteAllText(filePath, text, new UTF8Encoding(false))` (D7)
  - 최초 1회 안내 dialog (D6 + r1 M1 helper)
  - `CompleteSave(filePath, "YAML")`
- [ ] **(refactoring 기회 r1 M8)** — Save/Mermaid/AASX/else 4분기의 try/catch 반복 → `TrySaveFileOperation` helper 추출. CLAUDE.md "3줄 이상 반복 → refactoring" trigger 충족. 단 try/catch 자체 절약은 안 됨 (각 분기의 에러 메시지 다름) — 합리적 helper 형태는 *분기마다 inner action 만 받는 함수*

#### D3 — `_loadedAsLossy` flag + AfterFileLoad race 회피 (r1 C3 반영)

`CompleteOpen:64` → `IsDirty = false` 후 `:75` `RequestRebuildAll(AfterFileLoad)` 큐잉. `AfterFileLoad:198-216` 가 후행 실행되며 `:213-214` `ClearHistory(); IsDirty = false;` 재설정 → **단순 IsDirty=true 강제는 rebuild 완료 시 덮어쓰임**.

해법:
- `MainViewModel` 에 `private bool _loadedAsLossy;` flag 도입
- `OpenFilePathCore` 의 `IsYaml` 분기에서 `_loadedAsLossy = true` 세팅
- `AfterFileLoad:213-214` 직후 — `if (_loadedAsLossy) { IsDirty = true; _loadedAsLossy = false; }`
- `OpenFilePathCore` 의 다른 분기 진입에서는 `_loadedAsLossy = false` 명시 reset (안전)

#### D4 — Drag-drop 분기 추가 (r1 C4 반영)

`MainWindow.xaml.cs:130-200` 직접 확인 결과 누락 확인:

- [ ] `SupportedExtensions:131` 배열에 `FileExtensions.Yaml, FileExtensions.YamlAlt` 추가
- [ ] `GetDragFileType:139-156` switch 에 `if (ext == FileExtensions.Yaml || ext == FileExtensions.YamlAlt) return "yaml";` 추가
- [ ] `UpdateDragDropOverlay:169-201` switch 에 `case "yaml"` 분기 추가
- [ ] `MainWindow.xaml` 의 `DragDropOverlay` 안에 `DragDropYamlIcon` element 신규 추가 (기존 SDF/JSON/AASX/Mermaid 와 동일 패턴)
- [ ] `UpdateDragDropOverlay:172-175` 의 Hide-all 4 줄에 `DragDropYamlIcon.Visibility = Collapsed;` 추가

#### D5 — Title bar 배지 (r1 M10 반영)

`UpdateTitle` 분기에 `_currentFilePath` 가 `.yaml` / `.yml` 일 때 `[YAML, lossy]` 영구 배지 부착 — silent overwrite UX 위험 차단.

#### D6 — `TrySaveFileAs` DefaultExt 동적 선택 (r1 M9 반영)

`SaveFileDialog.DefaultExt = FileExtensions.Sdf` hardcoded (`FileCommands.cs:341`). `_currentFilePath` 가 `.yaml` 인 상태에서 SaveAs → default 가 `.sdf` 면 사용자 의도 위반 → 현 경로 확장자 기준 동적 선택:
```csharp
var defaultExt = _currentFilePath is null
    ? FileExtensions.Sdf
    : Path.GetExtension(_currentFilePath).ToLowerInvariant();
```

#### D7 — 안내 dialog 메시지 (D6 + r1 M6 / m9 반영)

최초 Save(`.yaml`) 1회:
> "`.yaml` 저장은 모델의 선언적 표현만 보존합니다.
>  GUID·위치·alias·시뮬 결과는 저장되지 않으며, 다시 열 때 자동 재발행됩니다.
>  영구 보존 / 시뮬 결과 공유 / 위치 보존이 필요하면 `.sdf` 를 사용하세요.
>  YAML 은 *사람이 읽고 LLM 이 다루기 좋은 공유 포맷* 입니다."

톤 = 위협 아닌 가치-기반 (AASX 안내 dialog 의 패턴 답습). "다시 보지 않기" checkbox 는 D2 의 helper 일반화 결과 사용.

Open(`.yaml`) 거부 dialog (view: partial 인 경우):
- SSOT 의 친절 에러 메시지 그대로 노출: "partial export 결과는 view-only — apply/validate 재입력 불가. 전체 export (view: full) 로 다시 호출하거나 'view:' 키를 제거하세요."

### 4.3 테스트 추가 — wiring 책임 한정 (r1 M3 반영)

기존 `ModelProtocolTests.fs` 의 store↔JSON round-trip 8건이 의미 보장 완결 — 본 작업 측은 *file IO wiring 책임* 만 별도 검증:

- [ ] `Save(.yaml)` 의 BOM 무 UTF-8 byte sequence 검증 (D7 정합)
- [ ] `Save(.yaml)` 결과의 첫 줄에 `protocol: promaker/v0`, 어딘가에 `view: full` 포함 검증 (phase6 코드의 emit 검증)
- [ ] `Open(.yaml)` 후 `IsDirty == true` 검증 (D3 의 `_loadedAsLossy` race 회피 검증)
- [ ] `Open(view: partial)` 거부 검증 — phase6 의 `ModelProtocol.fs:1103-1104` 가 처리하므로 통합 테스트 (Promaker FileCommands 경로) 1건
- [ ] `Save→Open round-trip` 의 GUID-무시 semantic equivalence — *helper 부재* 이므로 신규 helper 작성 또는 기존 ModelProtocolTests 의 assert 패턴 차용

### 4.4 자가 검열 (CLAUDE.md §자가 검열)

본 작업은 trigger ③ (2개 이상 파일 동시 변경) 충족 → 빌드/테스트 통과 후 sub-agent 위임 review 필요. commit/push 전 단계에 차단점 명시.

---

## 5. 관련 파일 / 경로

### A. 수정 대상 — Promaker 측

| 파일 | 변경 |
|---|---|
| `Apps/Promaker/Promaker/Presentation/FileExtensions.cs` | 상수 +2 |
| `Apps/Promaker/Promaker/ViewModels/Shell/FileCommands.cs` | FileFilter / IsYaml / Open·Save 분기 / DefaultExt 동적 / 안내 dialog / refactoring helper |
| `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs` | `_loadedAsLossy` flag |
| `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Lifecycle.cs` | `AfterFileLoad` 의 lossy 분기 (또는 FileCommands 의 AfterFileLoad 가 있다면 그쪽) — `IsDirty` race 회피 |
| `Apps/Promaker/Promaker/MainWindow.xaml.cs` | SupportedExtensions / GetDragFileType / UpdateDragDropOverlay 의 yaml 분기 (D4) |
| `Apps/Promaker/Promaker/MainWindow.xaml` | DragDropYamlIcon element 신규 |
| `Apps/Promaker/Promaker/Services/SettingsPaths.cs` | "YamlSaveNoticeShown" key (또는 동등) |
| `Apps/Promaker/Promaker/Dialogs/DialogHelpers.cs` | ShowThemedMessageBox "다시 보지 않기" 라벨 일반화 + 영구 persistence helper (r1 M1) |
| (가칭) `Solutions/Core/Ds2.LlmAgent/ModelProtocol.YamlIO.fs` | store ↔ yaml text 합성 wrapper 2개 — 또는 Promaker 측 helper 로 두는 것도 가능 |

### B. 참조만 — Ds2.LlmAgent / Phase 6 측 (본 작업에서 미수정 권장)

- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` — `apply` (`:1099-1106` view 거부) + `exportToJson` (`:1196-1198` view emit)
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` — `yamlToJson` / `jsonElementToYaml` / `jsonToYaml` 3 helper
- `Solutions/Core/Ds2.LlmAgent/ImportPlanBuilder.fs` — Open 측 합성 chain 의 중간 단계
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` — MCP 도구 (변경 없음, 참조만)
- `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.ExternalFileWatcher.cs` — `.yaml` reload 영향 검토 (r1 M11)

### C. SSOT / history 문서

- ✅ **권위 SSOT**: `F:\Git\ds2\phase6-readsurface\Apps\Promaker\Docs\yaml-protocol-v0.md`
- ⚠️ **outdated**: `F:\Git\ds2\main\Apps\Promaker\Docs\yaml-protocol-v0.md` — top-level `view` flag 절·§2.5.1 path resolver·§2.8 partial spec 미반영
- history: `F:\Git\ds2\main\Apps\Promaker\Docs\done-yaml-protocol-implementation.md`
- Phase 6 작업 SSOT: `F:\Git\ds2\phase6-readsurface\Apps\Promaker\Docs\todo-read-surface-guid-cleanup.md`
- 테스트 baseline: `F:\Git\ds2\main\Solutions\Tests\Ds2.LlmAgent.Tests\ModelProtocolTests.fs`

⚠️ **라인 번호 silent drift 주의** (r1 m2 반영): 본 문서의 `FileCommands.cs:20`, `:75`, `:213-214` 등은 작성 시점 snapshot. 코드 진화 시 drift 가능 — 작업 시 함수명/심볼명 기준 Grep 재확인.

---

## 6. 주의 사항

1. **SSOT 위치 — phase6-readsurface worktree** (사용자 명시). main worktree 의 yaml-protocol-v0.md 는 outdated.

2. **GUID 와 Save 는 별개 사건** — Save 는 store→file 일방향, store GUID 보존. 처음 transfer 의 "Ctrl+S 마다 재발행" 우려는 잘못된 추론, 사용자 지적으로 정정.

3. **lossy 의미를 *명시 인지*** — 안내 dialog (§4.2 D7) 가 본 작업의 UX 핵심. lossy 4-set (GUID / position / alias / 시뮬) 모든 노출 위치에서 sync. 누락하면 사용자가 `.yaml` 을 `.sdf` 동급으로 오해.

4. **Phase 6 의 view emit/거부 코드는 이미 commit 됨** (r1 M12 반영 정정) — phase6 worktree 직접 확인. `ModelProtocol.fs:1099-1106` view 거부, `:1196-1198` view emit. 본 작업 Save 측은 *별도 부착 불필요* — exportToJson 호출만으로 view: full 자동 부착.

5. **`view:` 키 부재 정책 미결정** (r1 C1 반영) — SSOT 룰 #8 = 부재 거부 vs phase6 실 코드 = 부재 통과. 갭 존재. 본 작업의 default = **실 코드 답습 (부재 통과)** — 사용자가 외부에서 수기 작성한 yaml round-trip UX 우선. SSOT 정합은 별도 cycle (phase6 코드 fix or SSOT 갱신).

6. **기존 코드 베이스 수정 최소화 철학** (CLAUDE.md) — `ModelProtocol.fs` dispatcher 변경 회피. wiring 만으로 끝내는 것이 정상.

7. **`.yaml` ↔ `.yml` 동등** (D8). 둘 다 같은 분기.

8. **빈 store 시나리오 외 처리** — File > Open 진입은 `ConfirmDiscardChanges()` 로 자동 빈 store 화 → v0 §4 의 "빈 store + project: 있음" 시나리오 자연 흡수. project: 키 누락 / 다른 project: 명시 등의 시나리오는 LLM apply 책임이라 File > Open 에서는 등장 안 함.

9. **`ExternalFileWatcher` 의 .yaml reload** (r1 M11) — 외부 에디터로 `.yaml` 편집 후 Promaker 로 돌아오면 reload prompt → 수락 시 GUID 재발행 발생. dialog 메시지에 "lossy reload — GUID 재발행" 안내 추가 검토.

10. **healMissingOriginFlowIds 무해성** (r1 M5) — YAML apply 가 OriginFlowId 를 올바르게 채워야 healed=0 (무해). 안 채우면 silent 데이터 변형 — pre-check 항목 (§4.1) 으로 ModelProtocol dispatcher 의 OriginFlowId 처리 확인.

11. **atomic write 미적용** (r1 m8) — `File.WriteAllText` 는 atomic 아님. 디스크 풀/권한 거부 등의 중간 실패 시 잘린 .yaml 잔존 가능. 우선순위 낮음 — 별도 cycle.

---

## 7. 진척 표

| 단계 | 상태 |
|---|---|
| §3 설계 결정 (논의) + r1 review 반영 | ✅ 완료 (본 transfer commit 시점) |
| §4.1 pre-check (public surface / view 부재 동작 / heal 무해성 / ExternalFileWatcher / DialogHelpers) | ⏳ |
| §4.2 코드 변경 (D0~D7) | ⏳ |
| §4.3 테스트 추가 (wiring 책임 한정) | ⏳ |
| §4.4 자가 검열 (sub-agent review) | ⏳ |
| commit / push | ⏳ |

---

## 8. r1 외부 review 5인 처리 내역

외부 reviewer 5명이 본 transfer 초안 (r0) 에 대한 Critical 4 / Major 12 / Minor 11 건 review 를 제공. 각 항목을 코드/SSOT 직접 cross-check 후 적용/반론. 본 절은 항목 번호 없이 *완전 풀어 적기* (CLAUDE.md --review 정책 정합 — 다른 문서가 review 내용을 따로 가지고 있지 않다고 가정한 완전 기술).

### 8.1 Critical 4건 처리

**C1 — view: 키 부재 거부 정책의 SSOT 인용 오류 주장 (consensus 2/5)**:

reviewer 주장 = "SSOT 룰 #8 은 비인식 값 거부지 부재 거부가 아니며, §2.8 가 부재 허용을 명시한다. transfer 가 그대로 구현하면 사용자 직접 작성 YAML round-trip 회귀."

직접 검증 결과 — **SSOT 인용은 정정해야 하나 reviewer 결론과 사실 다름**:
- phase6 yaml-protocol-v0.md `:340` 룰 #8: "export 결과에 view: 키 누락 — ERROR (v0 이전 export 결과는 'view: full' 추가 후 재시도)"
- `:376` §2.8 legacy/unknown-key 절: "export 결과에 view: 키 부재 시 ERROR — v0 이전 export 호환성 미보장"
- `:355` §2.8 enum 절: "값 enum = full | partial. 다른 값은 사전 거부"
- SSOT 는 *부재 거부 + 비인식 값 거부* 양쪽 모두 명시. reviewer 의 "§2.8 가 부재 허용" 인용은 SSOT 와 정반대.

**다만** phase6 worktree 의 실 코드 `ModelProtocol.fs:1101` 의 `match tryProp root "view" |> Option.bind tryString with` 패턴은 `None` (부재) 시 분기 없이 통과 — 룰 #8 미적용 (코드↔SSOT 갭).

→ **부분 수용**: reviewer 결론은 SSOT 기준에서는 틀렸으나, *실 코드 동작* 기준에서는 맞음. 본 작업의 default 정책을 **실 코드 답습 (부재 통과)** 으로 정정. SSOT/코드 갭은 별도 cycle. row D4 + §6.5 에 반영.

**C2 — exportFullStoreToYaml / applyYamlToFreshStore 부재 + "신규 로직 0" 모순 (consensus 4/5)**:

직접 검증 결과 — `ModelProtocol.Yaml.fs` 의 public surface = `yamlToJson` / `jsonElementToYaml` / `jsonToYaml` 3종 transformer 만 존재. store-level wrapper 부재. 실제 wiring 은 Save 측 2단계 / Open 측 4단계 합성 chain.

→ **수용**: §2 의 "신규 비즈니스 로직 0" → "비즈니스 로직 0, 합성 wrapper 1~2개" 로 완화. §4.2 D1 에 실제 합성 chain 풀어 명시 (exportToJson + jsonElementToYaml / yamlToJson + JsonDocument + ImportPlanBuilder + apply + ApplyImportPlan).

**C3 — CompleteOpen → AfterFileLoad 가 IsDirty=false 재설정 race (consensus 2/5)**:

직접 검증 — `FileCommands.cs:64` `CompleteOpen` 안 `IsDirty = false`, `:75` `RequestRebuildAll(AfterFileLoad)` 큐잉, `:213-214` `AfterFileLoad` 끝에서 `ClearHistory(); IsDirty = false;` 재설정. 단순 IsDirty=true 강제는 rebuild 완료 시 덮어쓰임.

→ **수용**: §4.2 D3 에 `_loadedAsLossy` flag 도입 + AfterFileLoad 끝부분 분기 추가 명시.

**C4 — MainWindow.xaml.cs drag-drop 분기 누락 (consensus 1/5, 단 코드 직접 확인 기반)**:

직접 검증 — `MainWindow.xaml.cs:130-200` 의 `SupportedExtensions` / `GetDragFileType` / `UpdateDragDropOverlay` 모두 존재. `.yaml` / `.yml` 미포함. xaml 의 `DragDropYamlIcon` element 도 미존재.

→ **수용**: §4.2 D4 / §5 에 drag-drop + xaml 신규 element 항목 추가.

### 8.2 Major 12건 처리

**M1 — ShowThemedMessageBox "다시 보지 않기" 영구 persistence 미지원 (3/5)**: 현재 helper 의 라벨이 시뮬 hardcoded (`DialogHelpers.cs:288` 추정), session-scoped HashSet 만 사용. → **수용**: §4.1 pre-check + §4.2 D7 에 helper 일반화 항목 추가. `AppSettingStore` 영구 persistence 보강.

**M2 — UTF-8 BOM 결정 미정 (3/5)**: `.NET File.WriteAllText(., Encoding.UTF8)` 은 BOM 포함, YAML 표준은 BOM 비권장. → **수용**: D7 row 신설 — BOM 없음 (`new UTF8Encoding(false)`).

**M3 — round-trip 테스트 중복 + GUID-무시 helper 부재 (3/5)**: 기존 `ModelProtocolTests.fs` 가 store↔JSON 의미 보장 완결. 본 작업 측은 *file IO wiring 책임* 만 별도. → **수용**: §4.3 정정 — BOM / view: full byte / IsDirty 강제 / view: partial 거부 (통합) / GUID-무시 helper 부재 명시.

**M4 — view: full emit 책임 분배 미결정 (2/5)**: phase6 코드가 이미 emit. → **수용 + 정정**: §2 / §6.4 의 "Phase 6 미구현이면 wrapper 부착" 진술 제거. phase6 ModelProtocol.fs:1196-1198 이 이미 처리 → wiring 측 부착 불필요.

**M5 — healMissingOriginFlowIds 와 YAML apply 상호작용 (2/5)**: `FileCommands.cs:84` 의 healed=0 가정의 정당성 검토 필요. → **수용**: §4.1 pre-check 항목 + §6.10 주의사항 추가.

**M6 — 외부 yaml 거부 시 메시지 차별화 (2/5 partial)**: dialog 의 거부 사유 / 누락 키 / 복구 버튼 보강 권고. "Notepad 열기" 등은 over-spec — SSOT 정의된 친절 에러 메시지 그대로 노출이 충분. → **부분 수용**: §4.2 D7 에 view: partial 거부 dialog 메시지 명시 (SSOT 의 친절 에러 그대로).

**M7 — BusyMessage Save 미적용 (2/5)**: SaveToPath 에 BusyMessage 미적용. 대형 store 의 yaml export 1-2초 freeze. → **수용**: §4.2 D2 의 SaveToPath 분기에 BusyMessage 추가.

**M8 — TrySaveFileOperation helper 신설 기회 (1/5 outlier)**: SaveToPath 의 4분기 try/catch 반복. CLAUDE.md 의 "3줄 이상 반복 → refactoring" trigger. → **수용**: §4.2 D2 에 refactoring 항목 추가. 단 *try/catch 자체 제거* 가 아닌 *inner action delegate* 형태로.

**M9 — TrySaveFileAs DefaultExt = .sdf hardcoded (1/5 outlier)**: `_currentFilePath = .yaml` 에서 SaveAs default .sdf 면 사용자 의도 위반. → **수용**: §4.2 D6 항목 추가 — `_currentFilePath` 확장자 기준 동적 선택.

**M10 — silent overwrite UX 위험 (2/5)**: 첫 Save 후 Ctrl+S 반복 → silent overwrite + IsDirty 해제. → **수용**: D9 row + §4.2 D5 — title bar 영구 `[YAML, lossy]` 배지.

**M11 — ExternalFileWatcher 의 .yaml reload lossy 누적 (2/5)**: 직접 검증 — `CheckExternalFileChange:41-89` 가 확장자 무관 watch. → **수용**: §4.1 pre-check + §6.9 주의사항 추가.

**M12 — Phase 6 코드 구현 미착수 vs wrapper 부착 모순 (1/5 단독 정밀)**: 직접 검증 — phase6 `ModelProtocol.fs:1099-1106` view 거부 코드, `:1196-1198` view emit 코드 *이미 존재*. → **수용 + 정정**: §2 / §6.4 의 "Phase 6 미구현이면..." 진술 정정. phase6 의 view 처리는 이미 commit 됨, Promaker MCP 도구 surface 갱신만 미착수.

### 8.3 Minor 11건 처리

- **m1** §3 표 항목번호 vs §4 절번호 충돌: **수용** — §3 의 row ID 를 D1~D9 로 변경 (§4 의 4.1~4.4 절번호와 분리).
- **m2** 라인 번호 silent drift: **수용** — §5 끝에 drift 주의 문구 추가.
- **m3** §5 의 "수정 / 참조 / SSOT" 3-그룹 시각 분리: **수용** — §5 를 A/B/C 표 분리.
- **m4** dialog icon 등 결정 부재: **부분 수용** — `.yaml ↔ .yml` 동등은 D8 명시. dialog icon 은 PoC 진입 후 결정.
- **m5** FileFilter "All Supported" 그룹 명시: **수용** — §4.2 D2 첫 항목 명시.
- **m6** lossy 4-item 세트 sync: **수용** — D6 + §6.3 + dialog 모두 GUID / position / alias / 시뮬 4-set 명시.
- **m7** `.yml` 우선순위: **수용** — D8 + SaveAs default = `.yaml` 명시.
- **m8** atomic write 결정 부재: **수용** — §6.11 별도 cycle 명시.
- **m9** dialog 메시지 톤 — 위협 아닌 가치-기반: **수용** — §4.2 D7 dialog 메시지 정정 (AASX 패턴 답습).
- **m10** SaveAs filter 라벨 "lossy 공유 포맷": **부분 수용** — §4.2 D2 의 FileFilter 갱신 시 그룹 라벨에 반영 가능. 우선순위 낮음.
- **m11** `.sdf` 백업 옵션 dialog: **반론** — over-spec. D6 의 안내 메시지에 "영구 보존은 .sdf" 시그널만 충분. dialog 의 *액션* 버튼 추가는 사용자 결정권 침해 가능.

### 8.4 Hallucination 기각

- 없음. reviewer 5명 모두 코드/SSOT 직접 cross-check 기반 review — 라인 번호·경로·인용 정확. C1 의 SSOT 룰 #8 해석만 SSOT 와 *코드* 사이 갭을 reviewer 가 *SSOT 인용 오류* 로 잘못 분류한 케이스.

### 8.5 자가 검열 리포트

- **검열 대상**: 본 transfer 문서 `todo-yaml-save-format.md` (코드 변경 없음 — review 는 transfer 문서 자체에 대한 것)
- **Reviewer 발견 이슈**: Critical 4 / Major 12 / Minor 11
- **자가 수정 결과**: Critical 4 모두 수용 (정정 반영). Major 12 중 10 수용 / 2 부분 수용 / 0 거부. Minor 11 중 9 수용 / 2 부분 수용 / 1 반론 (m11). 모두 본문 §2~§7 또는 §8 처리 내역 풀어 적기에 반영.
- **잔여 우려**: §6.5 의 "SSOT/코드 갭 (view: 키 부재 정책)" — 본 작업 default 는 실 코드 답습이나, 별도 cycle 에서 phase6 코드 fix 또는 SSOT 갱신 결정 필요.
