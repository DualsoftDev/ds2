# todo-hotfix-llm

> **상태**: F-1 spike (todo-free-llm-providers.md) 진행 중 발견된 **provider/모델 무관 (architectural)** hotfix 후보 정리. 코드 변경 없음. 다른 세션에서 이어받아 진행 가능.

> **revision history**
> - rev 1 (2026-05-09): F-1 spike (Groq + Llama 4 Scout 17B) 1회 manual smoke test 결과로 발견된 2건 분리. Groq free tier TPM 한도 / 모델 ID prefix 등 model-specific 발견은 본 todo 제외 (todo-free-llm-providers.md 본문에 기록).
> - rev 2 (2026-05-09): F-1 spike 2회차 manual smoke (Groq + Llama 4 Scout 17B + `system NewSystem2 생성` prompt) 에서 **NewSystem2 ×2 중복 생성** 사례 발현. Issue 1 의 architectural 가설을 trace 데이터로 입증 + LLM 자기 첫 mutation 결과 (id=ab18a871) context 누락하고 두 번째 (id=fb88e232) 만 보고 관찰. **단순 stale snapshot 표시 에러 수준이 아닌 사용자 데이터 오염 위험** 으로 격상. Issue 1 영향 범위 표 갱신 + 검증 prompt 추가.
> - rev 3 (2026-05-09): hotfix 적용 — Issue 2 (`ApiChatProvider.cs` JsonSerializerOptions UnsafeRelaxedJsonEscaping + static readonly 캐싱), Issue 1 옵션 B (`ModelTools.cs` 14곳 mutation wording 에 `PlanVisibilityHint` const suffix), Issue 1 옵션 A (`3.tooling.md` 운영 규칙 1번 항목을 visibility invariant 강화 형태로 재작성). CLI provider 2종 path 검증 결과: `StreamJsonParser.fs:100,108` 의 `JsonElement.GetString()` 자동 unescape 라 한글 정상 표시 → 수정 불필요. `CodexStreamJsonParser.fs:50` 은 tool_result 패킷 production 통합 미시점이라 영향 범위 외. `Ds2.LlmAgent.Tests` 측 wording fixture 회귀 0건 (rev 2 시점의 우려는 stale 정보였음 — Tests 는 wording string 을 expected 로 사용하지 않음). 빌드: csc 컴파일 0 error (MSB3021/26/27 file lock 오류만 — Promaker.exe + VS 잠금, 본 변경과 무관). 잔여: **작은 모델 회귀 manual smoke** (Llama 4 Scout 17B + `system NewSystem2 생성` prompt 로 NewSystem2 ×1 보장 확인).
> - **rev 4 (2026-05-09)**: rev 3 직후 `--review` 4건 반영. (1) `PlanVisibilityHint.TrimStart()` 가독성 → `PlanVisibilityHintLine` 별도 const 분리 + batch 분기는 `sb.Append(PlanVisibilityHintLine)` 으로 의도 명시. (2) drift 위험 → `PlanVisibilityHint` 정의부 주석에 wording sync 3 항목 (양 const 본문 동일 / RemoveEntity 별도 wording / 3.tooling.md 운영 규칙 1번 sync) 추가. (3) `Apps/Promaker/Promaker/LlmAgent/Prompts/facts.txt` EOF newline 추가 (기존 CRLF 일관성 유지). (4) todo cross-reference 의 commit 시점 add 권고는 `--git-commit` 명시 시 진행 — 본 rev 에서는 review 결과만 반영.

## 작업 목표

LLM provider / 모델 선택과 무관하게 **모든 provider (CLI 2 + API 3 = 5종, Phase 2 기준)** 에서 발생하는 architectural / UI 이슈 2건의 hotfix.

본 todo 는 `todo-free-llm-providers.md` 의 F-7 (Cerebras / OpenRouter / SambaNova / Gemini / DeepSeek / Z.AI) 추가 시점에도 그대로 발현되므로, **F-7 진입 전 또는 병행** 처리 권장.

## 발견 경위

`todo-free-llm-providers.md` F-1 spike 실행 (Groq + Llama 4 Scout 17B + `프로젝트 "TestGroq" 만들어줘` prompt) 시점에 관찰된 행적 (`session=5163ceb0...`):

```
turn 1 trace:
  add_project        → [plan] add_project queued: name="TestGroq", id=e8818730-...
  list_projects      → 기존 NewProject 만 (TestGroq 미반영)
  describe_subtree   → NOT_FOUND: rootId=e8818730-... 가 Project/System/Flow/Work 어디에도 ...
  AssistantDelta     → "프로젝트 ID 인식 안 됨, 다시 시도하겠다"
  list_projects      → (재시도, 동일 결과)
  describe_subtree   → 기존 NewProject ID 로 회복 호출
```

이 trace 안에 2 종류의 issue 가 동시 발현:

## Issue 1 — ImportPlan queued vs LLM 동기 검증 mismatch (architectural)

### 현상

Mutation tool (`add_project` / `add_active_system` / `add_passive_system` / `add_flow` / `add_work` / `add_call` / `add_api_def` / `add_arrow` / `add_cylinder` / `add_clamp` / `add_robot` / `add_device` / `remove_entity` / `rename_entity` / `apply_operations`) 호출 시 `tool_result` 가:

```
[plan] add_project queued: name="TestGroq", id=e8818730-...
```

같은 wording 으로 응답. 그러나 LLM 이 같은 turn 안에서 read tool (`list_projects` / `list_systems` / `describe_system` / `describe_subtree` / `find_by_name` / `validate_model_by_guid`) 호출 시 *queued 항목이 보이지 않음* (store snapshot 이 turn 시작 시점 그대로).

### 원인

`Solutions/Core/Ds2.LlmAgent/doc/todo-promaker-llm-agent.md` 의 **결정 7 (d)** = 1 LLM turn = 1 undo step. Mutation tool handler 는 `Solutions/Core/Ds2.LlmAgent/ImportPlanBuilder.fs` 의 `plan.Add` 로 `ImportPlanOperation` 만 누적. 실 store 변경은 turn 종료 시점의 `LlmChatViewModel.ApplyTurnPlanAsync` → `store.ApplyImportPlan(label, plan)` (`Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:48-52` 의 `applyWithUndo` 단일 호출).

→ **같은 turn 안의 read tool 은 항상 turn 시작 snapshot 만 본다** (architectural invariant).

### 현상 보강 (rev 2 — Llama 4 Scout 17B 입증 사례)

`session=...` (Groq + `meta-llama/llama-4-scout-17b-16e-instruct`) 에서 `system NewSystem2 생성` prompt 1 turn trace:

```
1. add_active_system  → [plan] queued: name="NewSystem2", id=ab18a871-..., planSize=2
2. apply_operations   → [batch] 1 op queued (planSize=4): [0] add_active_system name="NewSystem2"
                                                                  id=fb88e232-... (ref=@newsystem2)
3. list_systems       → NewSystem (id=a0637db2-...) only  (turn 시작 snapshot — NewSystem2 미반영)
4. AssistantDelta     → "The provided system 'NewSystem2' was not listed. However, there is a system
                        named 'NewSystem' with the id 'a0637db2-...'. To confirm, you have successfully
                        created a system named 'NewSystem2' with the id 'fb88e232-...'."
```

**관찰 결과** (Promaker UI Explorer):
- `NewSystem` (기존)
- `NewSystem2` (id=ab18a871-..., step 1 결과) ← 첫 add
- `NewSystem2` (id=fb88e232-..., step 2 결과) ← **중복 add!**

**핵심 발현**:
1. `planSize=2 → planSize=4` 증가 = 같은 turn 안에서 NewSystem2 add 가 2회 queue 됨
2. step 3 의 `list_systems` 가 turn 시작 snapshot (NewSystem 만) 반환 → LLM 이 "NewSystem2 not listed" 로 잘못 추론
3. LLM 의 응답이 **자기 첫 add (step 1, id=ab18a871) 를 잊고 두 번째 (step 2, id=fb88e232) 만 인식** = 모델 quality + architectural 결합 영향
4. ImportPlan 이 turn end 에 그대로 apply → NewSystem2 가 실제로 ×2 생성 = **사용자 데이터 오염**

→ Issue 1 의 architectural 가설이 trace 로 입증. **작은 모델일수록 빈도 ↑ + 결과 = 단순 표시 에러를 넘어선 사용자 데이터 오염 위험**.

### 영향 범위

| Provider | 영향 |
|---|---|
| Claude CLI / Codex CLI / Anthropic API (Claude 4.6) | 큰 모델은 system prompt 학습 정도 + 추론 능력으로 같은 turn 자발적 read 자제 → **실용상 문제 미발생** |
| OpenAI API (GPT-4o) | 위와 동일, 자발적 read 자제 |
| Ollama (small local) | small Llama / phi 사용 시 **발현 가능** (모델 추론 부족 → 자발적 검증 시도) |
| **Groq + `meta-llama/llama-4-scout-17b-16e-instruct`** | **rev 2 입증** — NewSystem2 ×2 중복 생성 (id=ab18a871 + id=fb88e232), 자기 첫 mutation 결과 context 누락 + 두 번째만 보고. **사용자 데이터 오염 위험** (단순 표시 에러 아님) |
| Groq / Cerebras / OpenRouter / SambaNova / Gemini / DeepSeek / Z.AI (free tier 작은 모델) | **발현 보장** (Llama 4 Scout 17B class 검증 후 inferred — F-7 진입 시 모든 free tier provider 영향) |

→ 본 phase 의 **F-7 추가 작업 시 모든 free-tier provider 에서 발현 가능성 큼**. 작은 모델일수록 빈도 증가.

### 대응 방향 (3 옵션 — 사용자 합의 필요)

- **옵션 A (system prompt 보강)**: `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` 의 운영 규칙 절에 다음 항목 추가.

  ```
  ## Mutation 결과 가시성 invariant
  - mutation tool (add_*, remove_*, rename_*, apply_operations) 의 [plan] queued 결과는
    **같은 turn 의 read tool (list_*, describe_*, find_*, validate_*) 에 반영되지 않습니다.**
  - 모든 mutation 은 turn 종료 시점에 단일 transaction 으로 일괄 apply 됩니다 (1 turn = 1 undo step).
  - 같은 turn 안에서 *방금 queue 한 entity* 의 존재 / 자식 / 속성을 *재확인하지 마십시오*.
    queue 결과의 id / planSize 만 신뢰하고 다음 mutation 으로 넘어가십시오.
  ```

  - 장점: 코드 변경 X, 모든 provider 자동 적용
  - 단점: 작은 모델은 system prompt 무시 가능. token 추가 = TPM 한도 빡빡한 free tier 에 부담 (Groq 처럼 single request 한도 초과 위험 — todo-free-llm-providers.md F-1 발견의 TPM 12K 이슈와 충돌)

- **옵션 B (tool_result wording 변경)**: `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` 의 mutation tool helper (현 `[plan] add_project queued: name="...", id=..., planSize=...`) 를 다음으로 변경.

  ```
  [plan] add_project queued — id=... (visible after this turn ends; do NOT re-query in same turn)
  ```

  - 장점: tool result 자체가 self-explanatory, system prompt 무관
  - 단점: 모든 mutation tool helper 의 wording 통일 + 사용자 친화 reduce 가능. 한국어 fixture 회귀 영향 (단어 변경 → `Ds2.LlmAgent.Tests` 의 expected string 갱신)

- **옵션 C (read tool 이 plan 내용도 합쳐서 응답)**: `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` 의 read tool (`listProjects` / `describeSubtree` 등) 에 `LlmTurnContext.plan` 을 in-memory merge 하여 응답.

  - 장점: LLM 입장에서 "queued 도 보임" → 자발적 검증이 자연 작동
  - 단점: 결정 7 (d) 의 architectural simplicity 훼손. plan rollback 시 read tool 이 추측 정보 노출. read tool 구현 복잡도 증가 (DU pattern matching 으로 plan ↔ store 합성). 회귀 영향 큼.

**권장**: 옵션 A + B 결합. system prompt 1 항목 추가 (옵션 A) + tool_result wording 1 줄 보강 (옵션 B). 옵션 C 는 architectural 비용 대비 효익 부족.

### 검증 방법

수정 후 작은 모델 (Llama 4 Scout 17B / Llama 3.1 8B Instant / phi-3) 로 다음 prompt smoke:

- `프로젝트 "Test1" 만들고 그 안에 시스템 "Sys1" 추가해줘` (mutation 2회 chain)
- `프로젝트 "Test2" 만들어줘` 직후 같은 turn 안에서 LLM 이 자발적 list_projects 호출 시도하지 않는지
- **`system NewSystem2 생성`** (rev 2 입증 prompt — 현 미수정 상태에서 NewSystem2 ×2 중복 생성 100% 발현) → 수정 후 NewSystem2 가 정확히 1개만 생성되는지 확인

**통과 기준**:
- 자발적 read 회수 = 0 또는 < 1회 / 5 turn
- **중복 entity 생성 0회 / 5 turn** (rev 2 입증 prompt 포함)
- LLM 응답이 자기 호출 행적을 정확히 trace (`첫 add` 의 ID 를 누락하지 않음)

### 영향 phase / 결정

- 결정 7 (d) **유지** (architectural 변경 없음)
- F-2-infra (회귀 측정 metric) 의 **"clarification 비율"** metric 에 `unnecessary self-verification` sub-category 추가 검토 (`todo-free-llm-providers.md` §4 의 "clarification 비율" 갱신 trigger)

## Issue 2 — UI 한글 unicode escape 미해석 (Promaker LLM Chat panel)

### 현상

`tool_result` 의 한국어 메시지가 LLM Chat panel 표시 시 raw `\uXXXX` escape 형태로 노출:

```
NOT_FOUND: rootId=e8818730-... 가 Project/System/Flow/Work 어디에도 없습다.
```

(원문: "... 가 Project/System/Flow/Work 어디에도 없습니다.")

### 원인 (추정)

`Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` 의 `validateModel` / `describeSubtree` 등 한국어 메시지 → `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` 의 `RunRead` / `RunMutation` helper → MCP HTTP tool result → `ApiChatProvider.ExtractToolResult` (`Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:170-182`) 의 `JsonSerializer.Serialize(result.Result)` 가 default 옵션으로 직렬화 → ASCII-safe escape 적용.

`System.Text.Json` default 의 `JsonSerializerOptions.Encoder` 는 `JavaScriptEncoder.Default` (=ASCII-safe). 한국어 / 일본어 / 중국어 / emoji 등 non-ASCII 문자는 모두 `\uXXXX` 로 escape.

### 영향 범위

API provider 3종 (Anthropic / OpenAI / Ollama) + F-7 추가 provider (Groq / Cerebras / OpenRouter / SambaNova / Gemini / DeepSeek / Z.AI) 모두 동일 path → **모든 API provider 에서 발현**. CLI provider 2종 (Claude / Codex) 은 stream-json parser 가 별도라 검증 필요 (현 `StreamJsonParser.fs` / `CodexStreamJsonParser.fs` 는 raw text 통과 가능성 높음 — 별도 확인).

### 대응 방향

`Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:174` 의

```csharp
return (false, JsonSerializer.Serialize(result.Result));
```

→

```csharp
return (false, JsonSerializer.Serialize(result.Result, new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));
```

`System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 적용 시 한국어 그대로 유지 (HTML inject 위험은 LLM Chat panel 이 plain text TextBlock 표시이므로 무관).

또는 더 안전하게 `JavaScriptEncoder.Create(UnicodeRanges.All)` 명시.

`JsonSerializerOptions` 인스턴스는 `static readonly` 로 cache (`System.Text.Json` 권장 — 매 호출 생성 시 reflection cost).

### 영향 / 회귀

- 단일 위치 변경 (1 line — `ApiChatProvider.cs:174`)
- 회귀 위험: tool_result 가 LLM 입력으로 다시 들어갈 때 escape 풀린 한국어가 모델 token 수 변화 (UTF-8 multi-byte = 1 char ≈ 3 byte ≈ 1.5 token vs `\uXXXX` = 6 char = 6 token) → **token 사용량 reduction**, free tier TPM 한도 완화에도 유리.
- CLI provider 2종 (Claude / Codex) 은 별도 path. CLI 측 한글 raw 표시 여부는 `Solutions/Core/Ds2.LlmAgent/StreamJsonParser.fs` + `CodexStreamJsonParser.fs` 의 `JsonDocument` 추출 path 검증 필요 — `JsonElement.GetString()` 은 자동 unescape 라 raw 표시 가능성 높음 (확인 필요).

### 검증 방법

수정 후 한국어 prompt 로 manual smoke:
- `존재하지 않는 ID 로 describe_subtree 호출 유도` → tool_result 의 한국어 그대로 표시 확인
- `validate_model_by_guid` 의 placeholder 검사 결과 (한국어 카테고리명 포함) 표시 확인

## 잔여 우려 / 후속 사항

- **CLI provider 측 잠재 risk (rev 3 reviewer 관찰)**: `Solutions/Core/Ds2.LlmAgent/StreamJsonParser.fs:110,112` 의 `parseToolResultContent` 안 `item.GetRawText()` fallback 분기는 raw JSON text 를 반환하므로 escape (`\uXXXX`) 가 보존된다. 현 Claude CLI 의 tool_result 는 `type: "text"` 단일 형태 (`item.GetString()` path → 자동 unescape) 라 실측 영향 없으나, 향후 CLI 가 mixed content / object content 형태로 한국어를 emit 하면 동일 escape 표시 증상 재현 가능. 본 hotfix 의 범위 외 — todo 백로그로 기록.
- ~~**PlanVisibilityHint leading-space convention** (rev 3 reviewer M1 관찰): 14곳 single-mutation 분기는 `{...,planSize=N{PlanVisibilityHint}}` 인라인 보간 (leading space 1개 = separator) 이고 batch 분기 (`ModelTools.cs:275`) 만 `sb.Append(PlanVisibilityHint.TrimStart())` 형태. 동작상 문제 없으나 cosmetic 비대칭 — 향후 wording refactor 시 통일 권장.~~ → **rev 4 (2026-05-09) 해결**: `PlanVisibilityHint` (인라인 suffix, leading space 1개) + `PlanVisibilityHintLine` (batch 마지막 줄, leading space 없음) 별도 const 분리. batch 분기는 `sb.Append(PlanVisibilityHintLine)` 으로 변경 — 의도가 소스에 직접 드러남. 두 const 의 본문은 동일하므로 `PlanVisibilityHint` 정의 주석에 sync 유지 항목 추가.

## 진행 상태

| 단계 | 상태 |
|---|---|
| Issue 1 — system prompt 보강 (옵션 A) | ✅ rev 3 — `3.tooling.md` 운영 규칙 1번 항목을 자가 검증 금지 + read 도구 4종 명시 + 중복 생성 위험 경고 형태로 강화 |
| Issue 1 — tool_result wording 변경 (옵션 B) | ✅ rev 3 — `ModelTools.cs` 의 `PlanVisibilityHint` const + 14곳 mutation wording 에 suffix 추가 (정보 보존 + visibility 경고) |
| Issue 2 — `ApiChatProvider.cs:174` JsonSerializerOptions 수정 | ✅ rev 3 — `ToolResultJsonOptions` static readonly + `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` |
| Issue 2 — CLI provider 2종 path 한글 표시 검증 | ✅ rev 3 — Claude `StreamJsonParser.GetString()` 자동 unescape 정상 / Codex 는 tool_result production 미통합 |
| 작은 모델 회귀 smoke (옵션 A+B 후) | ❌ 미진입 — Promaker.exe 종료 + 재빌드 후 사용자 manual smoke 필요 (검증 prompt 는 본문 §검증 방법 참조) |

## 다음 작업 진입 권장 순서

1. **Issue 2 먼저** (가장 짧음, 단일 line 변경, 회귀 위험 거의 없음 + token 사용량 reduce 효과 = TPM 한도 빡빡한 free tier 에 즉시 도움). CLI provider 2종 검증을 병행.
2. **Issue 1 옵션 B** (tool_result wording 변경) — `ToolOperations.fs` 의 mutation helper 통일. `Ds2.LlmAgent.Tests` 의 expected string fixture 갱신 동반.
3. **Issue 1 옵션 A** (system prompt 보강) — `3.tooling.md` 1 항목 추가. token 부담은 free tier TPM 영향이라 옵션 B 적용 후 효과 측정 후 반영 여부 결정.
4. 작은 모델 (Llama 4 Scout 17B 등) 로 회귀 smoke.

## 관련 파일 / 경로

| 파일:line | 역할 |
|---|---|
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:170-182` | `ExtractToolResult` — Issue 2 의 JsonSerializer 수정 위치 |
| `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` | mutation tool helper (`queueAddProject` 등) — Issue 1 옵션 B 의 wording 변경 위치 |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` | Issue 1 옵션 A 의 system prompt 보강 위치 |
| `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:48-52` | 결정 7 (d) 의 `applyWithUndo` (architectural 근거 — 변경 X) |
| `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` | turn-scoped plan 보유 (Issue 1 옵션 C 검토 시 진입점, 본 todo 권장 X) |
| `Solutions/Core/Ds2.LlmAgent/StreamJsonParser.fs` / `CodexStreamJsonParser.fs` | CLI provider stream-json parsing — Issue 2 CLI 측 path 검증 위치 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/` | 옵션 B 의 fixture 회귀 갱신 대상 |

## 주의 사항

- **결정 7 (d) 유지**: 1 turn = 1 undo step 의 architectural invariant 는 변경하지 않는다. Issue 1 의 옵션 C 는 이 invariant 를 흐리므로 권장하지 않음.
- **TPM 한도 영향 (free tier)**: Issue 2 수정은 token 사용량 *reduction* (escape 풀림 → byte 절감) 라 TPM 한도 빡빡한 free tier (Groq llama-3.3-70b-versatile = 12K TPM 등) 에 즉시 도움. Issue 1 옵션 A (system prompt token 추가) 는 반대 방향이라 옵션 B 적용 후 별도 평가.
- **회귀 fixture**: Issue 1 옵션 B 의 wording 변경 시 `Ds2.LlmAgent.Tests/Fixtures/` 의 expected string 일괄 갱신 필요.
- **CLI provider 2종 별도 검증**: Issue 2 의 `\uXXXX` 표시 여부는 CLI 측에서 다른 path. 별도 manual smoke 후 fix 위치 결정.
- **본 todo 와 todo-free-llm-providers.md 의 관계**: 본 todo 는 model/provider 무관 hotfix. F-7 (Groq / Cerebras / 등) 진입 *전* 또는 *병행* 처리 권장. 본 todo 완료 = `todo-free-llm-providers.md` 의 "검증된 사실 표" 에 1 entry 추가 후보 (`ApiChatProvider.ExtractToolResult` 한글 정상 표시).

## 참조

- `todo-free-llm-providers.md` (rev 5) — F-1 spike 진입 trigger 본문
- `todo-promaker-llm-agent.md` — 결정 7 (d) ImportPlan 정의
- `done-extend-mcp.md` — 21 tool SSOT 확정 시점 historical record
