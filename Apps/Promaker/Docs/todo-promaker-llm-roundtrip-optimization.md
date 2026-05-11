# Promaker LLM chat — round-trip 최소화 (delta-only snapshot)

> v4 — snapshot 포맷 확정 / cache_control provider 분기 / transaction 경계 선결 확인 / token 측정 procedure / E2E 테스트 시나리오 추가. 변경 이력은 문서 끝 §Revision History.

## 작업 목표

Promaker LLM chat 의 단순 모델링 요청 1건 처리 시 round-trip 수를 **4 → 1 RT** 로 축소.

핵심:
1. **store snapshot delta-only** — 변경 시점에만 자동 첨부
2. **MCP tool schema preload — provider-specific**
3. **명시 cache_control breakpoint — provider-specific**

## 배경 / 맥락

현재 1 요청당 RT 분해: `ToolSearch` (R1) → `list_projects` (R2) → `ToolSearch` (R3) → `apply_operations` (R4) = 4. snapshot 자동 첨부로 R2 제거, schema preload 로 R1/R3 제거, R4 만 남기는 것이 목표.

## 결정된 설계

### 1. Store.Revision — atomicity / transaction / 중앙 hook

**Store 측 변경**
```
Store.Revision : int  (monotonic, runtime-only — 디스크 저장하지 않음)
Store.RenderSnapshot() : string
```

**중앙 hook (UI 분산 금지)**

| 동작 | 파일:라인 |
|---|---|
| Editor transaction commit 성공 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:27` |
| Undo / Redo 성공 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:161` |
| Store load / replace / import / new / close | `Solutions/Core/Ds2.Core/Store/DsStore.fs:98` |

**금지**: `ClearHistory()`, 파일 저장, in-flight 외부 노출.

**규칙**
- **Atomicity**: `Interlocked.Increment` 또는 store-level lock
- **Transaction boundary**: 단일 transaction (=1 mutation 또는 `apply_operations` 의 N op batch) 당 **`Revision++` 1회**. 중간 상태 외부 노출 금지
- **`RenderSnapshot()`**: commit 직후 일관 상태에서만 호출

### 1.0. 선결 확인 task — apply_operations 의 transaction 경계 (Step 0)

`Authoring.fs:27` 의 transaction commit 단위가 `apply_operations` 의 batch 단위와 **자동 일치하는지** 코드 확인:

- **Case A**: `apply_operations` 가 내부적으로 단일 `withTransaction` 으로 N op 묶음 → batch 1회 = `Revision++` 1회 자동 보장. 추가 작업 없음.
- **Case B**: op 마다 별도 transaction → batch N op 마다 N회 `++` 발생, 외부 reader 가 중간 상태 관찰 가능. **반드시 wrapper 추가 필요**:
  - `apply_operations` 진입 시 outer transaction 시작
  - 모든 op 적용 후 outer commit 1회 → 이 시점에 `Revision++`
  - inner transaction 들은 nested 처리 (커밋해도 outer 가 열려 있으면 ++ 보류)

**확인 절차** (구현자 첫 작업):
1. `apply_operations` handler 코드 찾기 (`Apps/Promaker/Promaker/LlmAgent/` 또는 MCP server 측)
2. handler 가 op 들을 어떻게 transaction 으로 묶는지 확인
3. Case A 면 §1 hook 으로 충분. Case B 면 outer transaction wrapper 도입

### 2. Invalidation trigger (모두 §1 hook 으로 자동 처리)

위 3 지점 hook 이 다음 모두를 trigger:
- 파일 open / close / new
- `apply_operations` commit 성공 (batch 1회)
- GUI 직접 편집 (rename, delete, drag-drop)
- Undo / Redo
- 외부 import / merge

**별도 처리**:
- 파일 외부 수정 감지: 파일 open 또는 윈도우 포커스 복귀 시 mtime 비교 → reload + `Revision++`

### 3. 송신 파이프라인 — anchor / retry 안전

**Anchor**: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:477`

**snapshot 삽입 순서 (cache-friendly fixed prefix 강제)**
```
[1] system block (tools schema 포함)              ← cache breakpoint #1
[2] <store-snapshot revision="N"> ... </store-snapshot>  ← cache breakpoint #2
[3] editor_changes (변동)
[4] closed-project hints (변동)
[5] text attachments (변동)
[6] user message body
```

**송신 의사코드 (성공 후 commit)**
```fsharp
let snapshotToAttach, revisionAtSend =
    if session.LastSentRevision <> Some store.Revision then
        Some (renderSnapshot store), store.Revision
    else
        None, store.Revision

let result = sendToLlm(messages, snapshotToAttach)
match result with
| Ok _ -> session.LastSentRevision <- Some revisionAtSend  // 응답 streaming 완료 후
| Error _ -> ()  // 재시도 시 재첨부
```

**`LastSentRevision` reset 위치**

| 시점 | 파일:라인 |
|---|---|
| `Reset()` | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:620` |
| `UpdateStore()` | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:700` |
| Provider switch | provider 변경 핸들러 |
| message edit / regenerate | regenerate 진입점 |

**Scope**: in-memory only. app restart / chat history 복원 시 첫 송신에 자동 재첨부 (None 상태로 시작).

### 4. Snapshot wire format

**첨부되는 turn**
```xml
<store-snapshot revision="42">
... 직렬화 본문 ...
</store-snapshot>
```

**미첨부되는 turn**: 완전 침묵 (marker 없음).

### 4.1. Snapshot 직렬화 포맷 (확정 명세)

**원칙**
- 한 entity 또는 한 관계를 한 줄로
- indent 2 spaces, 트리 구조
- 정렬: entity 종류 안에서 이름 오름차순
- UUID 제외 (이름만)
- arrow type 약자: `S`=Start, `R`=Reset, `SR`=StartReset, `RR`=ResetReset, `G`=Group, `U`=Unspecified
- Work 안 Call DAG: `A → B → C`. 다중 source 는 `(A,B) → C`. 분기는 `A → (B,C)`

**Grammar (informal EBNF)**
```
snapshot      = "projects:" (empty | project+)
empty         = "(empty)"
project       = INDENT projectName ":" NEWLINE
                INDENT2 "systems:" NEWLINE system+
                INDENT2 "flows:" NEWLINE flow*

system        = INDENT3 systemName " (" role kind? ")" apiDefs? NEWLINE
                children*   // nested sub-system 은 한 단계 더 indent
role          = "active" | "passive"
kind          = "/cylinder" | "/clamp" | "/robot" | "/device"
apiDefs       = " {" apiDefName ("," apiDefName)* "}"

flow          = INDENT3 flowName " @" ownerSystemName ":" NEWLINE
                INDENT4 "works:" NEWLINE work+
                (INDENT4 "work-arrows:" NEWLINE arrowList)?

work          = INDENT5 workName " [" callDag "]" NEWLINE
callDag       = callExpr (" → " callExpr)*
callExpr      = device "." apiDef | "(" callExpr ("," callExpr)+ ")"

arrowList     = INDENT5 source " →" arrowType " " target ("," ...)
```

**예시 1 — 빈 store**
```
projects: (empty)
```

**예시 2 — 단순 (현재 두 cylinder 모델)**
```xml
<store-snapshot revision="1">
projects:
  Project1:
    systems:
      Controller (active)
      Cyl1 (passive/cylinder) {Adv, Ret}
      Cyl2 (passive/cylinder) {Adv, Ret}
    flows:
      Run @Controller:
        works:
          Advance [Cyl1.Adv → Cyl2.Adv]
          Retract [Cyl1.Ret → Cyl2.Ret]
        work-arrows:
          Advance →S Retract
</store-snapshot>
```

**예시 3 — 다중 system + nested + 분기**
```xml
<store-snapshot revision="17">
projects:
  AutoLine:
    systems:
      MainCtrl (active)
        Robot1 (active/robot) {Home, Pick, Place}
        Conveyor (passive/device) {Run, Stop}
      Vision (active)
    flows:
      Inspect @Vision:
        works:
          Scan [Vision.Capture]
      MainFlow @MainCtrl:
        works:
          Load   [Conveyor.Run]
          Pickup [Robot1.Pick → Robot1.Place]
        work-arrows:
          Load →S Pickup
          Pickup →SR Load
</store-snapshot>
```

**포함 / 제외 결정**

| 포함 | 제외 |
|---|---|
| Project 이름 | UUID 모든 entity |
| System 이름 + role + kind | Device 의 IO 주소 / pin / 네트워크 설정 |
| Device 의 ApiDef 이름 (호출 가능 API 식별 필수) | ApiDef 파라미터 시그니처 |
| Flow 이름 + 소유 system | HwButton 등 외부 IO 의 물리 매핑 |
| Work 이름 + Call DAG | Call 의 IO 주소 |
| Work-arrows + arrow type | Call-arrows 안의 detail (Work 안 DAG 로 표현됨) |

**Size 추정**: 10 system, 30 work, 100 call 규모도 1500~2500 token 수준 — cache breakpoint 효과 대비 경제적.

### 5. Cache 친화 배치 — provider 분기

provider 마다 cache 메커니즘이 다르므로 **단일 정책 불가**. 각각 별도 구현.

#### 5.1. Anthropic (Claude) — 명시 `cache_control: ephemeral`

Anthropic API 의 `messages.create` 호출 시 각 content block 에 `cache_control` 객체 부여 가능. 최대 4개 breakpoint, TTL 5분.

**부착 위치**:

| # | 위치 | 효과 |
|---|---|---|
| 1 | system block 끝 (tools schema 포함) | snapshot 변경 turn 에도 system+schema 까지 hit |
| 2 | snapshot block 끝 | revision 미변경 turn 에 snapshot 까지 포함 prefix hit |
| 3 (선택) | 직전 user turn 끝 | 긴 대화에서 incremental |

**JSON 예시** (Anthropic API 본문):
```json
{
  "system": [
    { "type": "text", "text": "...prompt...",
      "cache_control": { "type": "ephemeral" } }
  ],
  "messages": [
    { "role": "user", "content": [
        { "type": "text",
          "text": "<store-snapshot revision=\"42\">...</store-snapshot>",
          "cache_control": { "type": "ephemeral" } },
        { "type": "text", "text": "editor_changes..." },
        { "type": "text", "text": "user query..." }
      ] }
  ]
}
```

**구현 hook**:
- Claude provider 의 message assembly 부 (`LlmChatViewModel.cs:277` 부근의 tool allowlist 결합 직후 + snapshot 삽입 시점)
- 사용 SDK 의 `CacheControl` 객체 또는 raw JSON 주입 위치 확인 필요

#### 5.2. API providers (OpenAI 호환)

OpenAI 는 **자동 prompt cache** (1024 token 이상의 stable prefix 자동 hit, TTL 5~10분). 명시 breakpoint 없음.

**구현 작업**:
- snapshot 을 §3 fixed prefix 순서로만 두면 자동 hit
- 명시 `cache_control` API 호출 불필요
- `usage.prompt_tokens_details.cached_tokens` 로 적중률 모니터링

**구현 hook**: `ApiChatProvider.cs:129` 부근 — 추가 코드 거의 없음, snapshot 삽입 순서 강제만.

#### 5.3. Codex

현재 코드에 동등 tool exposure path 없음. **조사 대상**.

- Codex 가 Anthropic 호환인지 OpenAI 호환인지 (또는 자체 프로토콜) 확인
- 그 결과에 따라 §5.1 또는 §5.2 정책 재사용 또는 별도 처리
- 조사 task 를 Step 5 안에 명시

### 6. Prompt 룰 — `3.tooling.md` 에 추가

**중요**: `Apps/Promaker/Promaker/Promaker.csproj:62` 가 `*.md` 만 embed. `chat-samples.txt` 는 embed 대상 아님.

**6.1. 신규 룰 추가** (`3.tooling.md` 의 새 섹션, `# Greenfield` 앞)

```
# Store snapshot (자동 첨부)
  - <store-snapshot revision="N"> block 이 보이면 그것을 현재 store 상태로 신뢰.
  - block 이 보이지 않는 turn 에서는 직전 transcript 의 마지막 snapshot 을 그대로 사용.
  - 사용자가 명시적으로 현재 상태 확인을 요청하지 않는 한 list_projects / list_systems 호출 금지.
  - helper add_* 도구는 ToolSearch 로 발견·로드 가능, 또는 apply_operations 의 op 필드로 직접 호출.
```

**6.2. 기존 greenfield 룰 수정** (`3.tooling.md:103`~111)

변경 전:
> "`list_projects` 가 비어 있으면 같은 turn 에 add_project 1회 호출 허용"

변경 후:
> "**snapshot 의 `projects` 가 비어 있으면** 같은 turn 에 add_project 1회 호출 허용. snapshot 이 첨부되지 않은 turn 에서는 직전 transcript 의 snapshot 을 참조. snapshot 도 없는 첫 turn 에 한해 `list_projects` 1회 호출 허용 (cold start)."

→ 룰 충돌 시 LLM 이 보수적으로 `list_projects` 호출 → R2 제거 의미 상실.

### 7. MCP 도구 schema preload — provider-specific

| Provider | 현재 코드 | 정책 |
|---|---|---|
| API providers | `ApiChatProvider.cs:129` 에서 `ListToolsAsync()` 결과 전부 전달 | 이미 eager. 그대로 |
| Claude (Anthropic SDK) | `LlmChatViewModel.cs:277` 에서 21개 allowlist | allowlist = exposure ≠ schema eager-load. API 요청 본문 검증 후 미포함이면 포함 처리 |
| Codex | 동등 path 없음 | 신규 작업 — §5.3 조사와 통합 |

### 7.1. Token 측정 procedure (Step 5 선행)

**측정 항목**
1. `apply_operations` schema 의 token 수
2. 후보 always-load 8개 schema 총합
3. ToolSearch 1 RT 의 실측 latency
4. cached vs 비-cached input token 단가 차이

**측정 방법**

| 항목 | Anthropic | OpenAI 호환 |
|---|---|---|
| Schema token | `POST /v1/messages/count_tokens` 에 schema 만 채워 호출 | tiktoken (cl100k_base) 로직으로 schema JSON 계측 |
| RT latency | Anthropic API 호출 wall-clock | API 호출 wall-clock |
| Cached ratio | response `usage.cache_read_input_tokens / (input_tokens + cache_read_input_tokens)` | response `usage.prompt_tokens_details.cached_tokens / prompt_tokens` |

**측정 script 권장 위치**: `Apps/Promaker/Promaker/Tools/measure-tool-tokens/` 또는 단발 F# script.

**의사결정 기준**
- always-load total ≤ ~3K token 이면 eager 채택 (1 ToolSearch RT ≈ 500ms~1s 의 본전 회수 충분)
- ≥ 5K token 이면 deferred 유지 + ToolSearch
- 3~5K 사이는 cache 적중률 시뮬레이션 후 결정

## 적용 시 round 수 효과

| 조합 | RT |
|---|---|
| 현재 | 4 |
| §6 prompt 룰만 | 3 |
| §7 schema preload만 | 3 |
| §1+§3+§6+§7 | **1** |
| §1+§3+§6 만 | 2 |

## 구현 순서

### Step 0 — 선결 확인 (조사만, 코드 변경 없음)
1. **`apply_operations` transaction 경계** 확인 — Case A/B 판정 (§1.0)
2. **Claude provider 실 tool 노출** 검증 — `LlmChatViewModel.cs:277` allowlist 가 Anthropic API `tools` 배열에 실제 포함되는지 (§7)
3. **Codex provider 프로토콜** 조사 (§5.3, §7)

### Step 1 — Store 측 기반
1. `DsStore.Revision : int` 도입 (atomic, runtime-only)
2. `Store.RenderSnapshot()` 구현 (§4.1 grammar)
3. Revision bump hook 부착 3 지점:
   - `Authoring.fs:27` (transaction commit 성공)
   - `Authoring.fs:161` (undo/redo 성공)
   - `DsStore.fs:98` (load/replace/import/new/close)
4. (Case B 이면) `apply_operations` outer transaction wrapper 추가
5. 파일 외부 수정 감지 (mtime) → reload + `Revision++`

### Step 2 — Session / 송신
6. `LlmChatViewModel.LastSentRevision` 필드 추가
7. Reset 처리:
   - `Reset() :620`
   - `UpdateStore() :700`
   - provider switch
   - message edit / regenerate
8. 송신 어셈블러 (`:477`) — retry-safe (성공 후 commit), snapshot 을 [system → snapshot → editor_changes → hints → attachments → body] 순서로 삽입

### Step 3 — Prompt 룰
9. `3.tooling.md` 에 §6.1 snapshot 룰 신설
10. `3.tooling.md:103` 의 list_projects 권장 문구를 §6.2 안으로 교체

### Step 4 — Cache control (provider 분기)
11. Anthropic provider: `cache_control: ephemeral` breakpoint 부여 (§5.1, system 끝 + snapshot 끝)
12. API providers: snapshot 위치만 stable prefix 강제 (§5.2, 추가 코드 거의 없음)
13. Codex: Step 0.3 조사 결과 기반 처리

### Step 5 — Provider tool exposure (측정 선행)
14. Token 측정 (§7.1) — Anthropic / OpenAI 양쪽
15. Claude tool 실 노출 검증 결과 반영 (Step 0.2)
16. 측정 결과 기반 always-load vs deferred 정책 확정

### Step 6 — 운영 안전망
17. Compaction keepalive: 기본 ON, N=20 turn
18. Cache 적중률 모니터링: `usage.cache_read_input_tokens` (Anthropic) / `usage.prompt_tokens_details.cached_tokens` (OpenAI) 로깅

### Step 7 — E2E 검증
19. 통합 테스트 시나리오 (§E2E) 실행 및 측정

## E2E 테스트 시나리오

**시나리오 1 — 1 RT 달성 (핵심 목표 검증)**
1. 새 chat session 시작 (LastSentRevision = None)
2. store 비어 있는 상태 (snapshot = `projects: (empty)`)
3. 사용자 입력: "새 프로젝트 생성해서 두 cylinder 가 순차로 전진하고, 다시 두 cylinder 가 순차로 후진"
4. **기대**: 단일 turn 안에 `apply_operations` 1회 호출 + 응답 종료. 중간에 `list_projects`, 추가 `ToolSearch` 호출 없음.
5. **측정**: HTTP 요청 횟수, 첫 token latency, 총 turn 수

**시나리오 2 — Delta-only 검증**
1. 시나리오 1 완료 후 사용자 입력: "Cyl3 추가해서 마지막에 전진"
2. **기대**: 첨부 snapshot 은 revision 1 (Cyl1, Cyl2 이미 있는 상태). LLM 이 add_cylinder + add_call + add_arrow 발행.
3. 다음 turn 사용자 입력: "ok 잘 됐어"
4. **기대**: snapshot 미첨부 (revision 무변경 가정). marker 도 없음. LLM 은 직전 snapshot 사용.

**시나리오 3 — Cache 적중률**
1. 동일 session 에서 10회 query (mutation 없음, 단순 질의)
2. **기대**: 적중률 ≥ 90% (`usage.cache_read_input_tokens / total_input_tokens`)

**시나리오 4 — Retry 안전성**
1. 송신 중 네트워크 차단 시뮬레이션 → 첫 send 실패
2. 재시도 → 동일 snapshot 재첨부 확인 (LastSentRevision 미갱신)

**시나리오 5 — Session 분기**
1. 사용자 message edit → 이전 send 의 LastSentRevision 무효화
2. 새 send 에 snapshot 재첨부 확인

**시나리오 6 — 외부 편집**
1. LLM 으로 모델 작업 후 별도 에디터로 파일 수정 → Promaker 윈도우 포커스 복귀
2. **기대**: mtime 비교 트리거 → store reload + `Revision++` → 다음 send 에 갱신 snapshot 첨부

## 관련 파일 / 라인 정리

| 용도 | 경로 |
|---|---|
| Editor transaction commit | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:27` |
| Undo / Redo | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:161` |
| Store load / replace | `Solutions/Core/Ds2.Core/Store/DsStore.fs:98` |
| Chat send anchor | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:477` |
| Reset() | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:620` |
| UpdateStore() | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:700` |
| Claude tool allowlist | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:277` |
| API providers tool exposure | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:129` |
| Prompt embed (md only) | `Apps/Promaker/Promaker/Promaker.csproj:62` |
| Greenfield 룰 (수정 대상) | `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md:103` |
| Snapshot 룰 신설 위치 | `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` (`# Greenfield` 앞) |

## 주의 사항 / Edge cases

| 상황 | 처리 |
|---|---|
| 새 chat session | `LastSentRevision = None` → 첫 메시지 무조건 첨부 |
| API 실패 / 재시도 | 성공 응답 확인 후에만 commit (§3) |
| message edit / regenerate | reset |
| Provider switch | reset |
| Anthropic context compaction | snapshot 을 stable prefix + cache_control 로 보호 + keepalive 기본 ON (N=20) |
| LLM mutation → 같은 turn 내 다음 read | 기존 룰 유지. 다음 user turn 에서 갱신 snapshot 자동 첨부 |
| GUI 와 LLM 동시 mutation | Revision atomicity + transaction layer 일원화 |
| 외부 에디터 파일 직접 변조 | mtime 체크 → reload + `Revision++` |
| Stale snapshot 의심 시 | system prompt 룰 + 첫 turn 한정 `list_projects` cold-start 허용 (§6.2) |
| snapshot token 비용 | 중규모 (10 system 30 work) 도 1.5~2.5K token. cache breakpoint 효과 대비 경제적 |

## Revision History

- **v1** — 초기 작성
- **v2** — Critical/Major 5 + 부가 2 반영 (atomicity, retry-safe, marker 폐기, cache_control 도입, 측정 선행, keepalive ON, mtime 체크)
- **v3** — 코드 사실 기반 리뷰 6건 반영 (hook 위치 확정, 송신 anchor 확정, reset 위치 확정, `3.tooling.md` 룰 위치 확정, greenfield 룰 교체, provider-specific 분리, 구현 순서 재정렬)
- **v4** — 새 세션 즉시 착수 가능 수준으로 보강
  - §1.0 `apply_operations` transaction 경계 선결 확인 task 신설
  - §4.1 Snapshot 직렬화 grammar + 예시 3종 (빈 / 단순 / 다중) 확정
  - §5 cache_control 을 provider 분기 (Anthropic / OpenAI / Codex) 로 재작성, JSON 예시 포함
  - §7.1 Token 측정 procedure (count_tokens / tiktoken / usage 필드) 명세
  - §E2E 통합 테스트 시나리오 6종 추가
  - Step 0 (선결 확인) 신설, 구현 순서 7 step 으로 재정렬

## 참고

- Anthropic prompt cache TTL: 5분 (cache_control ephemeral). 최대 4 breakpoint.
- OpenAI 자동 cache: 1024 token 이상 stable prefix, TTL 5~10분.
- "1 파일 = 1 project" 룰.
- 본 최적화는 user-perceived latency (enter → 첫 token) 를 직접 축소.
