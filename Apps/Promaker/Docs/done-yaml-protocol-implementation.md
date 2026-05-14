# DONE — YAML 기반 LLM ↔ MCP 프로토콜 구현 (history)

> 본 문서는 YAML 기반 LLM ↔ MCP 프로토콜 도입 작업 (Phase 0~5) 의 *완료된* history 입니다.
> 설계 본문은 [`yaml-protocol-v0.md`](./yaml-protocol-v0.md) 가 normative SSOT — 본 문서는 작업 진행 시점의 transfer/플랜 메모 + 단계별 결과를 그대로 보존 (역사적 참조).
>
> **주의**: 본 문서의 `.fs:307`, `.cs:16` 같은 라인 번호는 작성 시점 snapshot — 코드 진화 시 silent drift 가능. 작업 시 Grep 으로 함수명·심볼명 재확인 권고.

> **새 세션 진입 빠른 안내**: ① §2 현재 위치 표 — Phase 0~3 ✅ 완료, Phase 4 가 현 후보 / ② §3.0 우선순위 가이드 — 현 후보 + 보류 후속 cycle 목록 / ③ §4 Phase 4/5 본문. §3.1~§3.4 / §7.X 는 *역사적 참조* (옛 진입 가이드 보존). 현 working tree clean 가정 (Phase 3 commit 후).

---

## 1. 작업 목표

Promaker 의 LLM ↔ MCP 통신 layer 를 현재 *op-level CRUD primitive* (현재 21개의 `[McpServerTool]`, GUID chaining 책임이 LLM 측) 에서 **doc-level 선언적 모델** (도구 3개 + 보조 2개, GUID 추상화) 로 전환합니다.

구체 의도:
- LLM 책임을 *자연어 → 선언적 모델 정규화* 로 한정
- MCP 책임을 *모델 → entity graph 변환 + cascade + 검증 + 트랜잭션* 으로 응축
- 사용자에게 YAML 을 SSOT 로 노출 (디스크 저장, diff/PR/revert, 수동 편집 가능)

**Wire format = JSON object** (LLM tool_use native, escape 0) / **Presentation = YAML** (사람·디스크 SSOT). 두 포맷은 schema 1:1 동형 — 자세한 결정 배경은 `yaml-protocol-v0.md` §8 부록.

배경·설계 결정·이점 비교·schema 정의·예시·마이그레이션 단계: **[`yaml-protocol-v0.md`](./yaml-protocol-v0.md) 참조**.

---

## 2. 현재 위치

| 단계 | 상태 |
|---|---|
| Phase 0 (schema v0 확정) | ✅ 완료 — SSOT (`yaml-protocol-v0.md`) + 본 transfer 가 4차 외부 review 누적 반영하여 commit (`0b5fce4` 초안, `87e5035` 4차 정정) |
| Phase 1 (PoC 구현) | ✅ **완료** — commit `ed6c7c3` (PoC) + `0d1dc6f` (외부 review 반영). 자가 검열 + 외부 review 결과는 §7 참조 |
| Phase 1.5 (1차 외부 review 반영) | ✅ **완료** — commit `0d1dc6f`. 회귀 위험 3건 + 중간 우려 4건 + 개선 4건 + 보너스 1건 (dispatcher Active Work workDuration 누락) 처리. 신규 19 테스트 → **289/289 통과** |
| Phase 1.6 (2차 외부 review 반영) | ✅ **완료** — commit `2b40746`. refactor 4건 (flow dedup single-pass / Option.map 우회 정리 / ArrowType 테스트 export 경유 / firstWorkDur 가정 주석). 무회귀 (289/289 유지) |
| Phase 2 (export_model_doc 정교화) | ✅ **완료** — Phase 2 §3.1 #1/#3/#4/#5/#6 6항목 모두 적용. cycle1 (alias→systemName SSOT 결정 + WithCyl.json round-trip), cycle2 (multi-sugar deterministic + YAML plain scalar + device fingerprint), cycle3 (외부 review 5명 종합 M1/M2 logWarn + Minor 5건). **310 통과** (baseline 289 + 신규 21). 잔여 = M3-M5 후속 cycle 분리 |
| Phase 2.5 (refactor backlog) | ✅ **완료** — cycle1: M5/M4/m1/m3/m4/m5/m6/m7 (8건). cycle2 (5인 외부 review): Critical 1 (C1 defaultOpposing SSOT 통합) + Major 5 (M1 Custom 상수화 / M2 queueAddPairedSugar 추출 / M3 roundTripWith generic helper / M4 fingerprint 단위 테스트 / M5 formatArrowType enum cover) + Minor m1 (Set.ofList 명료화), m5 (주석 축약). 보류: m2/M3 (todo 권고대로), cycle2 m2/m3/m4/m6/m7/o1 (반론 — 본문 §3.1 참조). **322 통과** (Phase 2 baseline 315 + cycle2 M4/M5 신규 7) |
| Phase 3 (prompt 마이그레이션) | ✅ **완료** — 5 파일 +476/-311. 3.tooling.md 주력 진입점 `apply_model_doc` 교체 + escape hatch 21종 강등. 2.modeling.md §1 매핑표 / 룰 A/B/C / §3.3a / §3.5 / §4.1-§4.5 / §5 self-check 어휘 doc-level 화. 1.entities.md / Prompts/CLAUDE.md / chat-simulation/CLAUDE.md sibling drift 동기화. 자가 검열 sub-agent (general-purpose): Critical 1 + Major 2 + Minor 3 → 5건 적용 (C1 escape hatch 카운트 21=15+6 정정 / M1 chat-simulation drift / m1 JSON object string 통일 / m2 룰 D + sugar 경계 강조 / m3 'mode 인자 없음' 1줄), M2 (refs SSOT vs prompt 차이 — 실 구현 정합 우선) 보류. **코드/테스트 무변경**. §7.3 참조 |
| Phase 4 (UI YAML preview/apply) | ⏳ 대기 (변형 진행 — chat dialog 의 YAML/Mermaid view 만 도입, preview/apply 분기는 미실행) |
| Phase 5 (op-layer 일소 — C+D 15종) | ✅ **완료** — `apply_operations` / `add_*` / `remove_entity` / `rename_entity` 15종 + 관련 `queueBatch` / `dispatchBatchOp` 등 F# helper 일소. doc-level 4 + read 6 = **10종** 으로 응축. `PromakerToolNames.cs:All` sanity 25→10, `BatchTests.fs` 삭제 (15 테스트 감소: 334→319). prompt 5 파일 (`3.tooling.md` escape hatch 절 + `2.modeling.md` 3건 + `1.entities.md` 2건 + Prompts/chat-simulation CLAUDE.md) op-layer 어휘 일소 |
| Phase 6 (read surface GUID-free 정렬) | ✅ **완료 (2026-05-13)** — Phase 5 후 잔존 read 6종 중 4종이 GUID 입출력 → done §6 #5 invariant 충돌 청산. `list_projects` / `list_systems` / `describe_system` / `describe_subtree` 4종을 `export_model_doc(path?, depth?)` 으로 흡수, `find_by_name` 출력 = `[{kind, path}]` 목록, `validate_model` 의 'global' literal + GUID scope 폐기. 풀세트 10 → **6종** (-40%). SSOT = `Apps/Promaker/Docs/done-read-surface-guid-cleanup.md`. 2-단계 commit 완료: #1 SSOT-only (`e85edba`) + #2 코드/prompt/test (`eab4537` + `caabfa7` + `989a47c` + `7eb4dc8` + `7c56cc0` 6 sub-chunk). **330 테스트 통과** (이전 314 + 신규 16). Phase 6 v6 도입: top-level `summary` 키 (절단 metadata `{totalEntities, emitted, budget}`) / `PartialBudget = 500` / `tryPathOf` 안전 API / set equality drift fence. 자가 검열 (Agent 5요소) 발견 이슈 0건 |

**현 working tree 상태**: Phase 3 작업 unstaged (prompt 5 파일). `bKwak` branch, local only (upstream 없음). 자동 생성 untracked = `Apps/Promaker/Promaker.kwak.sln` + `Apps/Promaker/Docs/ds2.log*` (제외). **322 테스트 통과** (Phase 2.5 commit `58574f8` baseline — Phase 3 는 prompt only, 테스트 무영향). 다음 세션 진입 시 *clean working tree* 가정 (Phase 3 commit 후).

**Phase 2 핵심 결정 — SSOT §1.7 항목 추가** (`yaml-protocol-v0.md` 참조):
- **alias→systemName 정정** — Call 참조는 `Call.DevicesAlias` 가 아닌 *Passive system 이름* 으로 emit. ApiCall→ApiDef→ParentSystem chain resolve. 4 fallback case (ApiCalls empty / ApiDefId None / orphan ApiDef / orphan System) 에 logWarn.
- **`custom(Unknown)` fallback** — Passive SystemType=None 시 fail-safe emit + logWarn. round-trip 시 SystemType 이 "Unknown" 으로 굳음 (silent type mutation 위험 주석).
- **`inferOpposing` N=2 정규화 = chain** — apiCount=2 ResetReset=1 시 chain/all-pairs 동값 → chain.
- **YAML plain scalar 안정화** — `^[A-Za-z_][A-Za-z0-9_\-]*$` ASCII identifier 면 Plain emit. reserved 22 token (yaml12Bools 6 / yaml11OnlyBools 16 (y/Y/n/N 포함) / yamlNullTokens 5) quoted 강제.
- **multi-sugar deterministic** — short-form (`device: cylinder`) round-trip 완전 동등 보장. sugar internal Flow 이름 `"{name}_Flow"` deterministic.
- **logger 신규** — `Ds2.LlmAgent.ModelProtocol` log4net logger 도입.

---

## 3. 다음 세션에서 바로 착수 가능한 작업

### 3.0 우선순위 가이드 (Phase 3 이후)

**새 세션 진입자 안내**: 본 §3.0 + §2 표 + §4 "Phase 4" 절을 먼저 읽고 진입. §3.1~§3.4 / §7.X 는 *역사적 참조* — 옛 진입 가이드 (Phase 1/2/2.5/3 시점) 가 ✅ 완료 표기로 보존. 새 작업은 아래 후보 중 선택.

**현 후보 = C > 측정**:

**C. Phase 4 UI YAML preview/apply** (큰 작업, 별개 PR) — §4 "Phase 4 — UI" 참조. Promaker 채팅 패널에 dry-run preview UX 도입. 진입 조건 = Phase 3 commit 완료 + (선택) `apply_model_doc` 의 `dryRun` 인자 도입 결정.

**측정 baseline** — Phase 3 효과 검증은 `Promaker` GUI 빌드/실행 + 동일 사양 (`ds2.log20260512` turn #2 = 3 zone × N cyl + Pusher Punch) 재현이 필요 (사용자 환경). baseline `$0.61 / 62 op / 1 재시도` 대비 기대 `≤ $0.20 / 1 op`.

**측정 결과** (2026-05-12 `ds2.log20260512` 4 turn 비교 / commit `ae8d2e8` 회귀 fix 적용 후):

| Turn | 도구 | duration | cost | output tokens | 재시도 | 결과 |
|---|---|---|---|---|---|---|
| 0 (04:50) | (질문만) | 162.6 s | $0.43 | 2935 | — | op-layer 명확화 turn (3개 질문) |
| **A** (04:55) | `apply_operations` (GUID) | **181.2 s** | **$0.61** | **15013** | 1회 (apiNames type 오류) | op-layer 모델 생성. 62 op (159 planSize) |
| B (11:24) | `apply_model_doc` | 58.4 s | $0.56 | 5474 | 0 | doc-level, 회귀 9 work flat ❌ |
| **C** (12:09) | `apply_model_doc` + fix | **37.2 s** | **$0.45** | **3216** | 0 | doc-level + fix, 3 work + call DAG ✅ |

- **A → C (모델 생성 turn 비교)**: 시간 **4.9 배 빠름**, output tokens **4.7 배 감소**, cost 26 % 절감.
- **누적 (Turn 0 + A → C)**: 시간 **9.2 배 빠름** (343.8 s → 37.2 s), cost **2.3 배 절감** ($1.04 → $0.45).
- **회귀 fix 단독 (B → C)**: 시간 1.57 배 빠름, tokens 41 % 감소, cost 20 % 절감.
- **사용자 체감 (4~5 배 빠름)**: A → C 의 4.9 배와 정확 부합.

**기대치 대비**: $0.45 / 0 재시도 / 단일 doc — 기대 `≤ $0.20 / 1 op` 미충족 (cost 측면). cache_creation_input_tokens 53730 이 절반 차지 — 후속 turn (cache hit) 에선 더 떨어질 가능성. *재측정 trigger*: 동일 session 내 후속 turn / Mermaid view 추가 등 추가 컨텍스트 변동 시.

**가속 요인 분석**:
1. 명확화 turn 제거 (Phase 3 §0.4 default 정책 강화) — 162 s 절약.
2. 재시도 제거 (op-layer 의 op-by-op type 실수 빈번 → doc-level 단일 발행).
3. output tokens 4.7 배 감소 (op-by-op spec → 단일 doc).
4. 회귀 fix 의 부차 효과 — 예시 2 정확화로 LLM reasoning 부담 감소 (+1.57 배 추가 가속).

A (Phase 2.5 refactor backlog), B (Phase 3 prompt 마이그레이션), D (todo organizational cleanup) 모두 완료 — A=§3.1, B=§7.3, D=본 cleanup commit.

**Phase 5/6 완료 이후 후속 cycle 후보**:
- **Phase 6 ✅ 완료 (2026-05-13)** — read surface GUID-free 정렬. §2 phase 표 행 참조. SSOT = `Apps/Promaker/Docs/done-read-surface-guid-cleanup.md`. 2-단계 commit 완료 (`e85edba` SSOT + 6 sub-chunk 코드/prompt/test/SSOT sync/외부 문서 sweep). 330 test 통과. 자가 검열 통과.

**Phase 6 외 신규 후속 cycle 후보**:
- **PathResolver 모듈 SRP split (Phase 6 v6 발견)** — Phase 6 chunk-1c 시점에 `ModelProtocol.fs` 의 `tryPathOf` / `tryFindEntity` / `pathSegments` 와 `ToolOperations.fs` 의 인라인 local resolver (`pathSegmentsForScope` / `trySystemPathLocal` / `tryFlowPathLocal`) 두 곳이 비슷한 로직을 중복 (fsproj 컴파일 순서상 forward-ref 회피 위해 인라인). 단일 PathResolver 모듈로 통합 시 (1) NFC 정규화 / dot-segment 분해 / kind 자동 판별이 한 곳에 모임, (2) Phase 7 이후 path 어휘 확장 (Work / Call segment) 시 sync drift 회피. 본 SRP split 은 아래 `ModelProtocol.fs` SRP split 작업과 묶어 진행 권고 (둘 다 동일 file split 영향).
- **`patch.arrows.remove` 의 Arrow EntityKind 확장** — Phase 1.5 부터 미지원 (entity 단위 cascade 만 가능). Flow 안 *arrow 단발 제거* 시나리오 실 corpus 등장 시 dispatcher 분기 추가.
- **doc-level dispatcher 의 name sanitize 도입** — `ModelTools.cs` 의 op-layer 진입점이 강제하던 `sanitizeName` (control char / RTL override / `@`·`$` prefix 거부) 정책이 `ModelProtocol.fs` dispatcher 에 미적용. systems/flow/work/api 이름 발견 시점에 `ToolOperations.sanitizeName` 호출 추가 권고. 실 corpus 에 비정상 이름 등장 시 보강.
- **doc-level cascade quota 차감 재도입 검토** — Phase 5 cleanup 으로 `RunWithChargedQuota` 제거. `apply_model_doc` 한 호출이 N op 누적해도 quota 1로 카운트 → DoS 표면. 실 corpus 에서 N>2000 발행 시나리오 등장 시 ModelProtocol dispatcher 에 cascade 누적 차감 재도입.
- **`ModelProtocol.fs` 1201 line SRP 모듈 split** — 기존 review M8 권고 (5 module split). Phase 6 진입 전 권장 → Phase 6 commit #2 직전 또는 후 별개 cycle. 우선순위 상승.
- ~~**`find_by_name` long-term clean** — 잠정 (a) ordinal suffix `[N]` → (c) sibling-unique invariant 강제 + 마이그레이션.~~ **[archive — Phase 6 v4]** find_by_name 출력 spec 자체가 `[{kind, path}]` 목록 회신으로 격상되어 unique path 의사결정 항목 자체 소멸. 동명 sibling 도 그대로 N건 노출, 호출자 (LLM) 가 `kind`+`path` 조합으로 다음 단계 결정. sibling-unique invariant 강제는 별도 동기 (예: dotted-path apply 시점 entity 재조회 안정성) 발생 시 재검토.

**기존 보류된 후속 cycle 후보 (corpus / trigger 등장 시)**:
- Phase 1 M2 — `applyPatch` 의 `buildSystems → buildActiveFlows` 사이 diagnostic 게이트 (patch corpus 본격 등장 시)
- Phase 1 외부 review m2 — `processOneArrow` / `processOneFlowArrow` 25줄 중복 (generic 추출 시 가독성 trade-off)
- Phase 1 외부 review m5 — `Queries.allArrowCalls` O(N×M) (실 모델 N≤20, perf 필요 시 캐싱)
- Phase 2.5 M3 — `ApiCalls=[]` fallback 직접 회귀 테스트 (현재 logger forensic 으로 간접 보장)
- Phase 2.5 cycle2 반론 보류 6건 — m2/m3/m4/m6/m7/o1 (사유 §3.1)
- Phase 3 M2 — refs SSOT vs 실 구현 mismatch (`apply_model_doc` 응답에서 GUID 정보 필요 시나리오 등장 시. SSOT = `yaml-protocol-v0.md §4`)
- SSOT 미결정 4건 — Active 의 직접 ApiDef 키 / arrowType Group·Unspecified 빈도 / patch cross-system scope 표기 / yaml_to_json LLM 노출 여부 (SSOT §7)
- review Major 8건 (commit `78a9589` 직후 inspect-diff 결과) — M1 `sanitizeName` 의 `:` 차단 / M2 Passive `device` 키 부재 round-trip 깨짐 / M3 arrow 처리 3중복 (todo cycle2 m2 합의 유지로 보류) / M4 `ModelTools.cs` wrapper boilerplate / M5 `exportToJson` 의 500ms hard-code → `KnownSugars` SSOT 통합 / M6 `inferOpposing` heuristic silent loss (logWarn 추가) / M7 `yaml_to_json` 비대칭 (apply_model_doc 의 auto-detect 검토) / M8 `ModelProtocol.fs` 1201 line SRP — Phase 4 진입 전 5 module split
- review Minor 9건 — m1 suggestion 빈 string / m2 nearestCandidates threshold / m3 parseDuration overflow / m4 flowKeyRegex 숫자 시작 허용 / m5 KnownSugars custom default record 화 / m6 patch.arrows.remove Warning vs Error 카테고리 / m7 tryResolveCallTargetSystem 1:1 invariant guard / m8 queueAddRobot SSOT 절반 적용 / m9 success-string 비대칭
- **capacity cycle 자동 적용 (Turn D 관찰)** — §3.4b "1회 명확화" 룰을 LLM 이 무시하고 자동 capacity > 1 cycle 적용 (Reset + Self Reset). prompt 의 명확화 룰 강제 강화 또는 default = sequential 명시 검토.
- **chat-UI boost (Phase A/B/C) revert 경위 기록** — Phase A (YAML echo prompt 룰 5a) = LLM 이 zone 내부 arrow 를 ellipsis 처리 → 검증 가치 0 + tokens 77% ↑ → revert. Phase B (export_mermaid_diagram MCP tool) = chat 창 미렌더 + Promaker 의 기존 entity tree + canvas editor 와 중복 → revert. Phase C (Markdig.Wpf chat bubble) = Phase A/B 무가치 결정 후 자연 보류. *재방문 trigger*: 사용자가 GUI 사용 중 *발행 doc 검증 단계가 부족* 하다 명시 요청 시. memory `yaml-protocol-phase4-ui-deferred` 결정 유효.
- **Critical fix 자가 검열 권고 Minor 3건** — (1) C1 의 `apply` 외부 throw 시 호출 site (`ModelTools.ApplyModelDoc` 의 `RunMutation` catch 분기) plan rollback 보장 검토; (2) C2 회귀 테스트의 `"PoC 미지원"` substring 의존 fragility → hint 상수 SSOT 추출; (3) todo §3.0 분량 ↑ → Phase 5 진입 시 별개 절로 split.

### 3.1 Phase 2.5 — refactor backlog (✅ 완료, commit `58574f8`)

**결과 요약**: cycle1 (1인 sub-agent) 8건 + cycle2 (5인 외부 review meta) 8건 적용. **322 통과** (Phase 2 baseline 315 + cycle2 M4/M5 신규 7).

| ID | 항목 | 결과 |
|---|---|---|
| **M5** | `Queries.tryResolveCallTargetSystem` helper — Call→ApiCall→ApiDef→ParentSystem chain 통합 | ✅ |
| **M4** | `KnownSugars.fs` 신설 — `KnownSugarSpec` SSOT (cylinder/clamp/robot) + `tryMatchFingerprint`. literal `["ADV";"RET"]` 등 4 곳 제거 | ✅ |
| **m1/m3-m7** | RelaxedShape 이동 / `roundTripRelaxed` helper / null literal·yaml reserved cache 단일화 / callArrows 캐싱 / `formatArrowType` public | ✅ |
| **C1** | `defaultOpposing` 제거 → `spec.DefaultOpposing` 직접 사용 (SSOT 통합 완성) | ✅ |
| **M1** | `KnownSugars.customDefaultApis/Opposing/Duration` 모듈 상수 추출 | ✅ |
| **M2** | `queueAddPairedSugar` helper — cylinder/clamp 25줄 골격 중복 통합 (robot 은 quota check 별개) | ✅ |
| **M3** | `roundTripWith` generic helper + 3 round-trip pattern 치환 | ✅ |
| **M4/M5** | `tryMatchFingerprint` 단위 테스트 6건 / `formatArrowType` enum 전수 cover (신규 7) | ✅ |
| m1/m5 | `Set.ofList` 비교 명료화 / placeholder 주석 축약 | ✅ |
| **m2 (cycle1)** | `processOneArrow` / `processOneFlowArrow` 25줄 중복 | ⏸ 보류 (가독성 trade-off) |
| **M3 (cycle1)** | `ApiCalls=[]` fallback 직접 회귀 테스트 | ⏸ 보류 (logger forensic 간접 보장) |

**반론 / 보류 6건** (cycle2): m2 null 가드 (F# 안전), m3 `failwithf` (stack trace), m4 `formatArrowType` private→public (수용), m6 if/elif→match (의미 차이), m7 `DefaultApis=[]` fence (영향 없음), m8 캐싱 주석 (기존 충분), o1 ModelEquivalence 분리 (over-engineering). 상세는 commit `58574f8` history 참조.

### 3.2 자가 검열 / 외부 review 잔여 우려 (Phase 2 진입 전 인지)

**M2 (deferred, Phase 1 자가 검열)**: `ModelProtocol.fs` 의 `applyPatch` 가 `collectSystems → buildSystems → buildActiveFlows` 를 직렬 호출. **Phase 1.5 commit `0d1dc6f` 에서 부분 보완** (collectSystems 후 diagnostic 게이트 추가). 단 patch 경로의 `buildSystems` 와 `buildActiveFlows` 사이의 게이트는 여전히 없음 — patch corpus 가 본격 들어오는 Phase 후속 cycle 에서 재검토.

**보류된 외부 review Minor 2건**:
- **m2**: `processOneArrow` (work-scope) / `processOneFlowArrow` (flow-scope) 25줄 가량 중복. closure (`resolveCallId` vs `resolveWorkId`) 차이로 추출 시 generic 파라미터 도입 필요 → 가독성 저하 가능. 별도 cycle 검토.
- **m5**: `ModelEquivalence.fs` 의 `Queries.allArrowCalls` 가 work 마다 전체 enumerate (O(N×M)). 테스트 helper + 실 모델 N≤20 라 functional 영향 0. perf 필요 시 helper 내 1회 캐싱.

### 3.3 SSOT (`yaml-protocol-v0.md`) 의 미결정 항목 (Phase 2 진행 중 결정 가능)

PoC 진입 후 실제 사례 만나는 시점에 결정 — *사전 over-design 금지*. trigger = "구현 중 해당 키/형식이 *실제* YAML 입력으로 들어와야 결정":

1. **Active system 의 직접 ApiDef 키** — Active 가 외부 노출 인터페이스 갖는 사양이 PoC corpus 에 등장하면 키 추가.
2. **arrowType `Group` / `Unspecified` 빈도** — PoC 의 corpus 에서 0건이면 v0 schema 에서 제외, 1건 이상이면 유지.
3. **patch 안 cross-system 추가 scope 표기 통일** — SSOT §3.4 가 `systems:` list 와 `in: <path>` 혼용 — PoC 의 patch case 처음 만나는 형태로 고정.
4. **Error message i18n** — 한국어 유지 (기존 `VALIDATION_ERROR` 정책 일치).

### 3.4 SSOT 결정 완료 항목 (Phase 1 모두 적용, 참조용)

결정 본문은 `yaml-protocol-v0.md §1.7` 가 normative SSOT. Phase 1 적용 결과 = §7.1 (commit `ed6c7c3`). 본 절은 결정 8건 요약:

| # | 결정 | 적용 |
|---|---|---|
| C5 | entity 이름 `.` 금지 — path 구분자 충돌 회피 | `sanitizeName` |
| C6 | patch DSL add+remove — *원래 있던* 제거+신규 추가 허용 / *방금 추가한* 제거 불허 (§3.2 M2 deferred 게이팅) | `queueRemoveEntity` invalidOp |
| C7 | `apply_model_doc` project 키 — `mode` 인자 없음, 4 시나리오 (빈 store ±키 / 기존 project ±키) | dispatcher `resolveProjectKey` |
| CR-5 | `workDuration` 키 통일 — Active Work duration override + Passive device workDuration 모두 단일 키. grammar `^(\d+)(ms\|s)$` | dispatcher reject + 친절 에러 |
| R2 | Known device sugar = `cylinder` / `clamp` / `robot` 3종 한정. 그 외 `device: custom(<Type>), apis: [...]` long-form | dispatcher |
| R4 | ApiDef 중복 Call — concurrent (arrow 없음) 자유 / 순차 (arrow chain) 고유 이름 요구 | `queueAddCallAllowDup` + dispatcher useAllowDup 분기 |
| — | `yaml_to_json` LLM 비노출 잠정 — `json_to_yaml` 만 노출, drift sanity = 25 | allowlist 4종 |
| — | device sugar 외 bare literal (`device: pusher` 등) — validate 에러 + 친절 메시지 | `parseDevice` UnknownSugar reject |

---

## 4. Phase 4 이후 (참조용 메모)

> Phase 3 (prompt 마이그레이션) 은 §7.3 으로 흡수. 본 절은 *현 후보 + 장기* 만.

### Phase 4 — UI (현 진입 후보)
- **목표**: Promaker 채팅 패널이 LLM 이 발행한 `apply_model_doc` 응답을 *바로 commit* 하지 않고, **YAML preview + 사용자 confirm + apply 버튼** UX 로 중간 검토 layer 추가.
- **흐름**: LLM tool_use → MCP `apply_model_doc(dryRun=true)` 형식으로 dry-run → JSON 응답을 `json_to_yaml` 통해 YAML 변환 → 패널 렌더 → 사용자 [Apply]/[Reject] → confirm 시 `apply_model_doc(dryRun=false)` 실호출.
  - `apply_model_doc` 현 시그니처는 `(model)` 단일 인자라 `dryRun` 분기 도입 전엔 `validate_model_doc` 로 미리 검증 → preview 후 `apply_model_doc` 호출 2-step 사용 가능 (UI 측에서 wrapping).
- **대상 코드**:
  - `Apps/Promaker/Promaker/Views/` 의 채팅 패널 (LlmAgent UI). 정확한 경로 = `Grep "chat" Apps/Promaker/Promaker/Views/**/*.xaml` 로 확인 필요.
  - YAML rendering: 이미 노출된 `mcp__promaker__json_to_yaml` 활용 가능 (LLM/사용자 양쪽 호출 가능).
- **보조 nice-to-have**: Mermaid 다이어그램 view (SSOT §8.4). `export_model_doc(format='json')` → AST 순회 → mermaid flowchart 텍스트 생성 → 패널 렌더.
- **진입 조건**: Phase 3 commit 완료. UI 작업은 별개 PR 권장 (prompt PR 과 UI PR 분리).

### Phase 5 — Deprecation (장기)
- op-layer 21종 중 PoC 후 사용 빈도 0 인 것부터 점진 정리.
- escape hatch 유력 후보: `add_api_def` / `add_arrow` / `remove_entity` / `rename_entity` (patch DSL 대체 가능).
- 측정 방법: `Apps/Promaker/Promaker/bin/Debug/.../logs/ds2.log*` 의 `ToolCall ...` 라인 grep 으로 도구별 호출 빈도 집계. 일정 기간 (예: 1개월) 0건이면 deprecation 후보.
- 도구 제거 시 `PromakerToolNames.cs:All` + `PromakerToolNamesDriftTests.fs` sanity count 동시 갱신 필수.

---

## 5. 관련 파일 / 경로

### 설계 / 참조
- `Apps/Promaker/Docs/yaml-protocol-v0.md` — **SSOT (먼저 읽기)**
- `Apps/Promaker/Promaker/LlmAgent/Prompts/1.entities.md` — entity 모델 참조
- `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` — 도메인 룰 SSOT
- `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` — 현 op-layer 도구 사용 규약 (Phase 3 에서 교체 대상)

### 코드
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` — 기존 `queueAdd*` / `queueRemoveEntity` / `queueRenameEntity` — 재사용 대상
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` — MCP tool 진입점. 현재 21개 `[McpServerTool]`. Phase 1 에서 신규 `ApplyModelDoc` / `ValidateModelDoc` / `ExportModelDoc` (+ 보조 `YamlToJson` / `JsonToYaml`) 추가. snake_case = `apply_model_doc` 등.
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — `All` allowlist 에 신규 4~5종 추가 (`json_to_yaml` 만 노출 잠정 → 4종, `yaml_to_json` 노출 결정 시 → 5종)
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — `Assert.Equal(21, ..)` 실제 본체는 line 83 (`:77` 은 test 이름 라인). sanity count `21` → `25` (잠정) 또는 `26`
- `Solutions/Directory.Packages.props` — `YamlDotNet` 16.x 등록 (CPM)
- `Solutions/Tests/Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs` — round-trip 의미-동등 helper 신규
- `Solutions/Tests/Ds2.LlmAgent.Tests/` — Phase 1 검증 테스트 위치
  - Fixture: `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` (canonical)
  - 실행: `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj -c Debug --nologo`

### 솔루션
- `Solutions/Ds2.sln`
- `Apps/Promaker/Promaker.sln`

### 로그 / 검증 데이터
- `Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log20260512` — 현 op-layer turn 비용 실측 (turn #2 = $0.61 / 62 op / 1 재시도). Phase 1 후 동일 사양으로 측정해 절감 비교. **OS / 사용자 / Debug 빌드 의존** — Release 또는 다른 사용자 환경에서는 경로 다름.

---

## 6. 주의 사항

1. **--plan 정신 유지**: 사용자 명시적 구현 요청 전까지 코드 변경 금지. Phase 2 착수 시점은 사용자 결정.
2. **기존 op-layer 도구는 유지** — Phase 2~4 동안 escape hatch. Phase 5 에서 점진 deprecation.
3. **GUI canonical 정합 보존** — Passive cascade (Flow + Work×N + ApiDef×N + ResetReset Arrow) 가 `WithCyl.json` 과 형상 일치. 기존 `queueAddCylinder` 등 helper 가 이를 보장하므로 *그 함수들을 그대로 호출*하는 게 안전.
4. **kind 명시 강제** — SSOT §2.7 룰 #1.
5. **GUID 는 LLM 에 절대 노출 금지** — `apply_model_doc` 응답의 `refs` 필드도 *옵션* 으로 유지하되, prompt 에는 *이름만 사용* 명시.
6. **`device: custom(WedgeClamp)`** 표기는 valid YAML scalar + valid JSON string. parser regex (`ModelProtocol.deviceLiteralRegex`) 한 곳에서 처리.
7. **Wire = JSON object, View = YAML** — 두 경로 schema 동형 (`ModelProtocol.Yaml.fs` 의 `yamlToJson` / `jsonElementToYaml` 양방향).
8. **Drift test + Allowlist 동시 갱신** — `PromakerToolNamesDriftTests.fs` 의 sanity count (현 25) + `PromakerToolNames.cs` 의 All 배열. Phase 2 는 신규 도구 추가 *없음* → 25 그대로.
9. **F# compile order** — `Ds2.LlmAgent.fsproj` 에 `ModelProtocol.Yaml.fs` → `ModelProtocol.fs` 순 (Yaml helper 가 ModelProtocol 호출 안 함). 새 파일 추가 시 `StoreSnapshot.fs` *위* 배치.
10. **Phase 3 (prompt 마이그레이션) 은 독립 PR** — `3.tooling.md` (264 라인) + `2.modeling.md` (476 라인) 의 주력 진입점 교체는 50-70% 재작성. Phase 2 commit 과 한 PR 에 묶으면 회귀 추적 불가. *Phase 2 = export 정교화 / Phase 3 = prompt 재작성* 으로 분리.
11. **`hasCallNameClash` 함수 자체 수정 금지** — 신규 entry `queueAddCallAllowDup` 추가 방식 (Phase 1 commit `ed6c7c3` 적용 완료). 기존 op-layer 호출 경로 회귀 차단.
12. **YAML arrow 자연 형태**: `- A -> B : T` 가 YamlDotNet 에 의해 mapping `{"A -> B": "T"}` 로 해석됨. dispatcher 의 `extractArrowString` helper 가 string 과 1-key object 양쪽 정규화. 새 arrow path 추가 시 동일 helper 사용 권장.

---

## 7. 이미 정리된 사안 (참고)

### 7.1 Phase 1 적용 결과 (commit `ed6c7c3`)

**신규 파일**:
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` — yaml↔json (anchor/tag/merge/YAML 1.1 boolean 거부 enforce)
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` — schema v0 dispatcher + 이름 테이블 + Levenshtein 후보 제안 + export
- `Solutions/Tests/Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs` — round-trip 의미-동등 helper. **`FlowCount`/`ApiDefCount` int 필드 포함** (set 만으로 중복 흡수 방지 — M1 fix)
- `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs` — SSOT §3.1/§3.2 round-trip + parser subset 부정 케이스 + concurrent ApiDef + sanitize

**수정 파일**:
- `ToolOperations.fs` — `sanitizeName` 에 `.` 거부 + `queueAddCallAllowDup` 신규 (`queueAddCallCore` 추출 패턴)
- `ModelTools.cs` — 신규 [McpServerTool] 4종 (`ApplyModelDoc` / `ValidateModelDoc` / `ExportModelDoc` / `JsonToYaml`)
- `PromakerToolNames.cs` — allowlist +4
- `PromakerToolNamesDriftTests.fs` — sanity 21 → 25
- `Directory.Packages.props` — YamlDotNet 16.2.0
- `Ds2.LlmAgent.fsproj` — compile order + PackageReference

**자가 검열 결과** (sub-agent general-purpose):
- Critical 0
- Major 3 → 적용 2 (M1 ModelEquivalence count, M3 validate docstring), deferred 1 (M2 applyPatch 게이팅 — §3.2 참조)
- Minor 6 → 적용 1 (m6 suggestion None 처리), 정보성 5

**테스트**: 270/270 통과 (baseline 258 + 신규 12).

### 7.1.5 Phase 1.5 — 1차 외부 review 반영 (commit `0d1dc6f`)

외부 reviewer (별개 sub-agent) 가 `ed6c7c3` 코드 리뷰 결과 식별·반영한 사항. 본 절은 review 문서가 부재한 환경에서도 완전 이해 가능하도록 항목별 풀어서 기술 (CLAUDE.md 룰 — review 처리 기록 시 reviewer 표기 번호만 나열 금지).

**회귀 위험 — 모두 수정 적용**:
- **patch.arrows 분기 누락**: SSOT 의 `patch.arrows.add` / `patch.arrows.remove` 시나리오가 dispatcher 에 없어 silent ignore. `findFlowByPath` helper 추가 + `patch.arrows.add` 풀구현 (Flow path 해석, work 이름 resolve, queueAddArrow 호출). `patch.arrows.remove` 는 `EntityKind` enum 에 Arrow case 부재로 PoC 명시 미지원 (친절 에러 메시지 + 후속 cycle 에 EntityKind 확장 + CascadeRemove 분기 추가 필요 명시).
- **apis 빈 list 명시 시 default 무력화**: 사용자가 `apis: []` 를 명시 입력하면 `Option.defaultValue` 가 `Some []` 를 통과시켜 cylinder 등 sugar 의 default `[ADV;RET]` 가 무력화 → 빈 cascade 생성. `Option.bind (fun l -> if List.isEmpty l then None else Some l)` 로 빈 list 를 None 으로 정규화.
- **중복 flow 키 처리**: 같은 `flow X:` 키가 두 번 등장 시 두 번째도 `queueAddFlow` 호출되어 `sysEntry.FlowIds[X]` 가 두 번째 ID 로 덮어써짐 (첫 번째는 leak). `dedupedFlowKeys` 로 첫 등장만 채택하도록 변경.

**중간 우려 — 모두 수정 적용**:
- **patch.add 의 store-side 충돌 검출 누락**: `collectSystems` 가 `ctx.Systems` (이번 입력의 system 이름 collection) 만 검사. patch 시나리오에서는 store 에 *이미 존재하는* 같은 이름 system 추가 시도 detect 못 함. `findSystemByName` helper 로 store 측도 검사 + diagnostic.
- **exportToJson 의 workDuration / opposing override 직렬화 누락**: Active Work 의 `Duration` 이 default (500ms) 와 다르거나 Passive 의 internal Work duration override / 실제 opposing 형태 (chain/all-pairs/none) 가 device default 와 다르면 emit 되어야 round-trip. `formatDuration` (TimeSpan → "Nms"/"Ns" grammar), `formatArrowType` (enum → SSOT §2.4 string), `inferOpposing` (ResetReset arrow 갯수 → opposing 추정), `defaultOpposing` (cylinder/clamp=chain, 그 외 none) 추가하여 mismatch 시 emit. **별도 발견**: dispatcher 의 Active Work `workDuration` 키 자체를 처리하지 않아 (read 만 하고 plan 에 적용 안 함) override 가 export 에서 보이지 않을 뿐 아니라 *입력 round-trip 도 깨짐*. 동시 수정.
- **parseDuration regex fallback unreachable**: regex 가 `(ms|s)` 만 capture 보장하므로 `match | u -> Error` 분기 도달 불가. 단순 `if/else` 로 정리.
- **테스트 커버리지 보강**: 신규 19 테스트 추가 — YAML custom tag (!tag) 거부, YAML merge key (`<<:`) 거부, YAML duplicate map key 거부, YAML multi-document (`---`) 거부, kind=passive 인데 flow 키 존재 시 에러, kind=active 인데 device 키 존재 시 에러, `apis: []` default 적용 회귀, 중복 flow 키 회귀, ArrowType 6종 InlineData round-trip, §3.4 patch (Zone 4 추가) round-trip, store-side 충돌 patch.add 에러, patch.arrows.remove 미지원 에러, workDuration override export round-trip, opposing override (robot=chain) export round-trip.

**개선 — 적용 4건 / 보류 2건**:
- **deviceDefaults 의 UnknownSugar dead branch**: 호출처에서 사전 분기 처리되므로 본 함수 도달 불가. 시그니처를 `option` 제거 + UnknownSugar 케이스에 `failwithf` (호출 시 invariant 위반 명시).
- **nearestCandidates 키 비대칭**: `resolveCallId` 는 normalized 키, `resolveWorkId` 는 raw 키로 Levenshtein 계산 — 일관성 부족. `resolveWorkId` 도 normalized 통일.
- **Levenshtein F# 관용성**: `for j in 0 .. lb do` (IEnumerable seq) → `for j = 0 to lb do` (primitive loop) — 성능/관용성.
- **suggestion 빈 string 처리**: 후보 없을 때 `Some ""` 가 들어가 ` (제안: )` 빈 괄호 출력. `if candidates.IsEmpty then None` 로 정정 (Phase 1 자가 검열 단계에서 이미 적용 완료).
- **arrow processing 25줄 중복** (보류): work-scope `processOneArrow` 와 flow-scope `processOneFlowArrow` 의 골격 중복. closure (`resolveCallId` vs `resolveWorkId`) 차이로 추출 시 generic 파라미터 도입 필요 — 가독성 저하 가능. 별도 cycle 검토.
- **ModelEquivalence 의 allArrowCalls O(N×M)** (보류): work 마다 전체 ArrowBetweenCalls 순회. 테스트 helper + 실 모델 N≤20 라 functional 영향 0. 필요 시 helper 내 1회 캐싱.

**테스트**: 289/289 통과 (270 baseline + 신규 19).

### 7.1.6 Phase 1.6 — 2차 외부 review 반영 (commit `2b40746`)

`0d1dc6f` 이후 외부 reviewer 의 2차 검토 — Critical / 중간 우려 *없음* 판정 + 정확성 모두 ✅. 개선 항목 4건 적용·반영, 참고 우려 2건 명시 보류.

**개선 — 적용**:
- **flow dedup single-pass 통합**: 이전 구현이 HashSet 을 두 번 만들어 (1: diagnostic, 2: dedup) 의도가 분산. `List.filter` 한 pass 안에서 dedup + diagnostic 동시 수행하도록 통합.
- **Option.map 우회 패턴 정리**: `Passive opposing` 추정 시 `internalFlow |> Option.map (fun _ -> ...)` 가 Option 내부값을 사용하지 않고 단지 `IsSome` 여부만 보는 구조였음. `if internalFlow.IsSome then ... else 0` 로 자연스럽게 변경.
- **ArrowType round-trip 테스트의 `%A` 의존 제거**: 본 변경의 핵심 motivation 이 `%A` 의존 회피였는데 회귀 테스트는 여전히 `Assert.Equal(typeName, sprintf "%A" t)` 로 검증 → 같은 path 를 fence 하지 못함. `exportToJson` 경유 round-trip 으로 변경 — 실제 `formatArrowType` (private) emit path 까지 검증.
- **Passive `firstWorkDur` 가정 주석 명시**: 현재 구현은 internal Flow 의 *첫* Work duration 만 보고 emit 결정. 모든 known sugar (cylinder/clamp/robot/device) 가 동일 duration 으로 모든 Work 를 만든다는 가정에 의존 — 후속 sugar 가 Work 별 다른 duration 을 만드는 케이스 도입 시 본 가정이 깨짐. 주석으로 가정 명시 + 정책 재검토 필요성 표기.

**참고 우려 — 보류 2건 (현 PoC 범위 내 functional OK)**:
- **deviceDefaults 의 failwithf 안전망**: 호출처 사전 분기가 깨질 경우 unhelpful exception. 현 코드 흐름상 안전하나, 본 함수가 *공개 helper 로 진화* 시점에 Option/Result 재도입 권장.
- **workDuration override 검색의 plan 전체 linear scan**: `dispatchWork` 가 work 마다 `ctx.Plan.Operations |> Seq.tryPick` 로 방금 추가한 AddWork 검색 → Work N 개에서 O(N²). PoC 규모 (N ≤ 20) 에서는 무시 가능. Phase 2 의 cascade emit 단계에서 entry 별 `Dictionary<Guid, AddWork>` backref 도입 검토.

**테스트**: 289/289 통과 (변경 무회귀).

- SSOT (`yaml-protocol-v0.md`) 작성 — 549 라인. §2.0 Parser subset / §2.5 flow prefix grammar / §2.3 duration grammar / `protocol: promaker/v0` 키 / device regex ASCII-only / Unspecified 명료화 등 4차 누적 review 반영
- todo (`todo-yaml-protocol-implementation.md`) 작성 — 본 문서
- SSOT §3.2 예시의 `Z1.C1` 형 이름 → `Z1_C1` 등으로 정정

### 7.2 Phase 2 — export_model_doc 정교화 (cycle1+2+3 통합 commit)

Phase 2 §3.1 #1/#3/#4/#5/#6 6항목 적용 + 외부 review 5명 종합 반영. 한 commit 으로 묶음.

**cycle1 — §3.1 #1 + alias 정책 결정**:
- SSOT §1.7 신규 결정 — **Call 참조는 systemName 사용 — alias 무시**. `Call.DevicesAlias` (GUI 부여 약어) doc-level 추상화에서 무시.
- `exportToJson` 의 calls emit: `c.DevicesAlias` → `Call.ApiCalls.tryHead → ApiDefId → Queries.getApiDef → ParentId → Queries.getSystem → s.Name` chain resolve. fallback (None) = `c.DevicesAlias` 유지 + 후속 logWarn.
- `WithCyl.json` round-trip: GUI fixture (alias="cyl" / systemName="NewFlow_cyl") → load → captureRelaxed → exportToJson → apply → captureRelaxed 동등 검증. **3 신규 테스트** (#1 / #1b cylinder sugar / #1c alias-correction lock-in).
- baseline 289 → 292 통과.

**cycle2 — §3.1 #3/#4/#5/#6**:
- **§3.1 #3** multi-sugar (cylinder+clamp+robot+custom+override) short-form round-trip *완전* 동등 + deterministic 2회 apply. **3 신규** (#3 / #3b / #3c chain wiring lock-in).
- **§3.1 #4** YAML plain scalar — `jsonToYamlNode` 의 string 분기에 `plainScalarSafeRegex` (`^[A-Za-z_][A-Za-z0-9_\-]*$`) + reserved (yaml12Bools / yaml11OnlyBools / yamlNullTokens) 체크. ASCII identifier 면 Plain, 아니면 DoubleQuoted. **11 신규** (4 Fact + 8 Theory).
- **§3.1 #5** device fingerprint — `SystemType=None → custom(Unknown) + apis` fail-safe + fingerprint 매칭 주석. **4 신규**.
- **§3.1 #6** drift sanity 25 유지 확인 (변경 없음).
- baseline 292 → 310 통과.

**cycle3 — 외부 review 5명 종합 반영**:
- Reviewer G/L/D/S/T 종합: Critical 0 / Major 5 / Minor 11.
- **M1** SystemType=None 회귀 가드 — logWarn 1줄 (silent type mutation 경고).
- **M2** Call emit fallback logWarn — log4net logger `Ds2.LlmAgent.ModelProtocol` 신규 도입 + 4 fallback case 에 Warn 출력.
- **M3** ApiCalls=[] 직접 회귀 테스트 — *부분 수용*: helper 정상 경로로 시뮬레이션 불가 → logger forensic 으로 *간접* 보장.
- **m1 (외부 m3)** YAML 1.1 단축 boolean `y/Y/n/N` 4 token defensive 추가 + Theory 4 case 신규.
- **m4 (외부)** custom(Unit) round-trip 완전 동등 신규 테스트 (#5b).
- **m6 (외부)** apis 순서 정확 substring 매칭 강화.
- **L4 (외부)** SSOT 에 `inferOpposing N=2 = chain` 명문화.
- **m9 (외부)** SSOT 에 `custom(Unknown)` fallback footnote 추가.
- **m8 (외부)** todo 체크박스 + 상태 표 갱신.
- **반론/보류 (별도 cycle)** — M4 knownSugars 단일 테이블 / M5 Queries.resolveCallTargetSystemName / m1/m2/m5/m7/m10/m11 refactor 7건 → Phase 2.5 backlog (§3.1) 로 분리.
- baseline 310 → 315 통과.

**누적 결과**: 289 → 315 (Phase 2 신규 26 = cycle1 3 + cycle2 18 + cycle3 5).

**변경 파일 (Phase 2 통합 commit)**:
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` — logger 도입 + calls emit alias→systemName 정정 + device fingerprint None fail-safe + 2 logWarn 위치
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` — plainScalarSafeRegex + yamlNullTokens + yaml11OnlyBools 에 y/Y/n/N 추가 + jsonToYamlNode string 분기 plain emit
- `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs` — 26 신규 테스트 (Phase 2 §3.1 #1/#3/#4/#5 패키지)
- `Apps/Promaker/Docs/yaml-protocol-v0.md` — §1.7 결정 표에 3 행 추가 (alias / custom(Unknown) / N=2 정규화)
- `Apps/Promaker/Docs/todo-yaml-protocol-implementation.md` — Phase 2 완료 + Phase 2.5 backlog + Phase 3 시작 가이드

### 7.3 Phase 3 — Prompt 마이그레이션 (commit 예정)

`apply_model_doc` 주력 진입점 교체 + op-layer 21종 escape hatch 강등. 6 파일 (`Apps/Promaker/Promaker/LlmAgent/Prompts/` 5 + Docs/todo 1) +536/-321 (자가 검열 5건 + 5인 외부 review 메타 8건 누적 후).

**변경 파일**:
- `3.tooling.md` (264 → ~280 라인) — *완전 재작성*. 주력 진입점 `apply_model_doc` + schema v0 한 페이지 reference + YAML 예시 4종 (단일 cylinder / multi-zone / concurrent / patch) + 6 핵심 룰 (protocol/v0, kind 명시, alias 무시, sugar 3종, workDuration 통일, GUID 0) + Project 키 4 시나리오 + escape hatch 21종 표.
- `2.modeling.md` (476 → ~480 라인) — 본문 도메인 룰 유지, §1 매핑표 일부 / 룰 A·B·C / §3.3a / §3.5 / §4.1-§4.5 예시 5종 / §5 self-check 어휘만 doc-level 화.
- `1.entities.md` — §4.5 / §4.6 의 helper 호출 어휘 minor 갱신.
- `Prompts/CLAUDE.md` — line 9 한 줄 갱신 (`apply_operations 우선` → `apply_model_doc 주력`).
- `chat-simulation/CLAUDE.md` — line 20 sibling drift 동기화 + §2.2 MCP 호출 예시 + §2.3 GUID 안내 + §3 한 turn 1회 호출 어휘 모두 schema v0 어휘로 갱신.

**자가 검열 결과** (sub-agent general-purpose):
- Critical 1 → 적용 1 (C1: escape hatch 카운트 21=15+6 정정. Read 6 + mutation 15 = 21 명시).
- Major 2 → 적용 1 + 보류 1.
  - M1 (chat-simulation drift): 적용. line 20 + §2.2 예시 + §2.3 안내 동기화.
  - M2 (refs 응답 형식 SSOT vs prompt 차이): 보류. prompt 가 실 구현 (`refs=N` 카운트) 정합 우선 — SSOT (`yaml-protocol-v0.md §4` 의 `→ refs: {<name>: <guid>}` design 형식) 측을 실 구현에 맞추거나 구현 측 풍부화 결정 후속 cycle. trigger = 사용자가 `apply_model_doc` 응답에서 `refs` 의 GUID 정보가 필요한 시나리오 등장 시.
- Minor 3 → 적용 3 (m1: JSON object string 어휘 통일 / m2: 룰 D + cylinder sugar 경계 강조 1줄 / m3: 'mode 인자 없음' 1줄).
- 회귀: 없음 (도메인 룰 본문 보존, canary 4파일 보존, schema v0 정합, cross-reference 정합).

**5인 외부 review 메타 반영** (R0/R1/R2/R3/R4 + Generalist):
- 즉시 정리 적용 8건 (모두 1-2 줄 보강):
  - **C1 (R0 outlier)** flow key JSON wire 순서: schema reference 에 "JSON object insertion-ordered, 다중 flow 결정성은 LLM 발행 순서 의존" 1줄.
  - **MJ1 (R4 outlier)** Path 룰 patch `in:` 컬렉션-key 단말 케이스 ✓/✗ 1쌍.
  - **MJ2 (R1 outlier)** §5 self-check 의 "opposing 임의 동반 금지" 룰에 "sugar Passive ApiDef cascade 는 룰 D 무관" cross-ref.
  - **MJ3 (2/5)** Project 키 표 5번째 행 "patch 단독 doc" 추가 (project 키 무관, 대상 path 존재 여부로 판정).
  - **MN1 (2/5)** chat-simulation §2.2 예시에 yaml-pretty + json-wire 두 fence 페어.
  - **MN2 + m_R0_1 (2/5)** chat-simulation escape hatch 카운트 "mutation 15 + read 6 = 21종" 통일.
  - **m_R0_3 (1/5)** 예시 3 opposing chain 도메인 의미 주석 1줄 (TILT_UP/HOLD/TILT_DOWN chain 시점 가정 + opposing none 대안 안내).
  - **m_R0_2 / m_R1_3 (1/5)** 1.entities §4.5 Call.DevicesAlias 비고에 "schema v0 doc-level 에서는 alias 무시" cross-ref 1줄.
  - **m_R2_1** 3.tooling.md `json_to_yaml` 을 *보조 도구 (변환 only)* 소제목으로 분류.
  - **m_R2_2** 2.modeling.md §4.5 Gate 주석에 "custom default 는 'none' 이라 명시 필수" 1줄.
- **MJ4 / MJ5 / MJ6 검증 통과 (변경 불요)**:
  - MJ4 (refs SSOT mismatch) — todo 가 추적 중. SSOT 파일명 `yaml-protocol-v0.md §4` + trigger 명시 보강 (위 M2 항목).
  - MJ5 (validate_model_doc vs validate_model 구분 약함) — 3.tooling.md line 22 + line 270 + line 359 cross-ref 확인. drift 없음.
  - MJ6 (4.attachments.md 동반 점검 누락 잠재) — Grep 결과 `apply_operations` / `@<ref>` / helper 호출 모두 부재. drift 없음.
- **반론 / 보류 3건**:
  - **m_R4_1** "주력 진입점 / 주력 / doc-level / schema v0" 4 동의어 통일 — 보류. 각 어휘가 자연스러운 layer 다양성 (도구 / 추상화 / spec 버전) 을 가리켜 통일 시 가독성 ↓. 미적용.
  - **m_R4_2** §4 예시 4 YAML 주석 4줄 → 1줄 축약 — 보류. 현 4줄 (Work cascade / 자식 / 관련 Arrow 자동) 이 의도 명시 가독성 우위. 미적용.
  - **m_R4_3** Project 키 표 "있음 (X)" → "있음, 값=X" 명료성 — 보류. 표 헤더가 `project:` 키 값임을 충분히 시사. 미적용.
- 합의 충돌 / 검증 기각 0건. PR 본질 (apply_model_doc 주력 교체 + sibling drift 동기화) 모든 reviewer 통과 판정.

**측정**:
- baseline (op-layer Phase 0 시점): turn #2 = `$0.61 / 62 op / 1 재시도` (ds2.log20260512).
- 기대 (apply_model_doc 단발): `≤ $0.20 / 1 op`.
- 실측: 사용자 환경 (Promaker GUI + Debug build) 재현 필요. LLM 측 자가 측정 불가.

**잔여 우려**:
- M2 (SSOT vs 구현 mismatch) — 후속 cycle 분리.
- chat-simulation 의 `--gr` / `--graph` 플래그 (§6) 가 ASCII 표기 — Phase 4 의 Mermaid view 와 별개 layer 라 영향 없음.

### 7.4 Phase 5 — op-layer 15종 일소 (단일 commit)

`apply_model_doc` 주력 진입 안정화 + patch DSL (SSOT §2.6) 의 모든 mutation cover 검증 완료 후, op-layer (C 11종 + D 4종 = 15종) 를 *단일 commit* 으로 일소. 잔존 도구 풀세트 = doc-level 4 (apply/validate/export/json_to_yaml) + read 6 (list_projects / list_systems / describe_system / describe_subtree / find_by_name / validate_model) = **10종**.

**대상 도구 (15종)**:
- C 11종 — `apply_operations` / `add_project` / `add_active_system` / `add_passive_system` / `add_flow` / `add_work` / `add_call` / `add_cylinder` / `add_clamp` / `add_robot` / `add_device`
- D 4종 — `add_api_def` / `add_arrow` / `remove_entity` / `rename_entity`

**코드 변경**:
- `ModelTools.cs` — `[McpServerTool]` 메서드 15개 본문 + 관련 private helper (`RunWithChargedQuota` / `IsHelperOp` / `CalcCascadeOpCount` / `BuildBatchOpInputs` / `FormatBatchResult` / `PlanHasQueuedAddProject` / `FormatApiDefMapping` / `ToFSharpList` / `ParseDurationMs` / `ParseStringArrayArg` / `RunPairedDeviceCascadeWork` / `RunListDeviceCascadeWork`) 일소. `PlanVisibilityHintLine` 도 dead → 삭제. header doc comment 압축. 965 → 374 line.
- `PromakerToolNames.cs` — `All` 배열 25 → 10 항목.
- `ToolOperations.fs` — `BatchOpInput` / `BatchOpResult` type + `queueBatch` / `dispatchBatchOp` / `validateRefName` / `resolveBatchRef` / `formatApiDefIds` 일소 (252 line 감소). **단 `queueAdd*` helper (Cylinder / Clamp / Robot / Device / Project / ActiveSystem / PassiveSystem / Flow / Work / Call / ApiDef / Arrow / RemoveEntity / RenameEntity) 자체는 *유지*** — `ModelProtocol.fs` 의 doc-level dispatcher 가 그대로 호출.
- `PromakerToolNamesDriftTests.fs` — sanity count 25 → 10 + snake_case 변환 단위 테스트 갱신.
- `BatchTests.fs` — 파일 자체 삭제 (15 테스트 감소). `HelperCascadeTests` / `HelperGuiParityTests` / `RemoveRenameTests` / `ImportPlanApplyApiCallTests` 는 *helper 함수 직접 호출* 경로 검증이라 **유지** (doc-level dispatcher 도 같은 helper 호출 — sugar canonical 회귀 fence).

**prompt 변경 (5 파일)**:
- `3.tooling.md` — Escape hatch 표 (line 382-403, 21줄 + 도구 표 14행) 통째 삭제 → "현 도구 풀세트 = 10종" 4줄로 응축.
- `2.modeling.md` — 3건 (`rename_entity` → `patch.rename` / `add_project` → `apply_model_doc.project` / `add_arrow` 어휘 일반화).
- `1.entities.md` — 2건 (`add_call` reference 제거 + Passive ApiDef 의 op-layer helper 리스트 일소).
- `Prompts/CLAUDE.md` — 3.tooling.md 안내 1줄 갱신 ("op-layer 21종 escape hatch" → "현 도구 풀세트 10종, op-layer 일소").
- `chat-simulation/CLAUDE.md` — 2건 (op-layer 시뮬레이션 형식 + GUID placeholder 안내) 정리.

**테스트 결과**: baseline 334 (Phase 4 변형 + Mermaid 신규 commit `b84556e` 시점) → 319 통과 (BatchTests 15 감소, 회귀 0).

**측정 잠재력** — prompt 토큰 추가 절감 (escape hatch 표 21행 + 도구 description 15개 제거). 다음 모델링 turn 의 input_tokens 감소 폭은 후속 측정.

**잔여 후속 cycle** — `patch.arrows.remove` 의 Arrow EntityKind 확장 (실 corpus 등장 시).

### 7.5 Phase 0 직전 별개 작업 (commit `44cf62f`)

- `chat-samples.txt` — `Pusher → Puncher` 정정 + 1줄 추가
- `ToolOperations.fs` — `tryReadStringArrayProp` helper 추가
- `3.tooling.md` — batch robot/device 예시 + 비대칭 주의 라인

**빌드/테스트 baseline**:
- Phase 0 commit 시점: 258 통과
- Phase 1 commit (`ed6c7c3`): 270 통과 (신규 12)
- Phase 1.5 commit (`0d1dc6f`): 289 통과 (신규 19 — 1차 review 반영)
- Phase 1.6 commit (`2b40746`): **289 통과 유지** (refactor — 무회귀)
- Phase 2 commit (`628f996`): **315 통과** (신규 26 — cycle1 3 + cycle2 18 + cycle3 5)
- Phase 2.5 commit (`58574f8`): **322 통과** (cycle1 8건 무회귀 + cycle2 C1/M1/M2/M3 refactor 무회귀 + cycle2 M4 6 신규 + cycle2 M5 1 신규)
- **Phase 3 commit (예정)**: **322 통과 유지** (prompt only — 코드/테스트 무변경)
- Phase 4 진입 시 *이 baseline 위에서 시작*
