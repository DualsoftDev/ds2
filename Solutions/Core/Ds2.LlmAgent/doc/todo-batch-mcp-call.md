# MCP 호출 batch 화 — round-trip 압축 (todo)

## 작업 목표

LLM agent 가 한 turn 안에 여러 mutation 을 발생시킬 때 N 번의 tool_use ↔ tool_result round-trip 을 1 번으로 줄여 응답 지연을 단축한다. 모델 정확도와 mutation 안전성 (1 turn = 1 undo, F# editor 무수정) 은 그대로 유지.

- **PoC 결과 (Pass 0 완료, 2026-05-07)** — 우선순위 재정렬:
  - **(a) parallel tool_use 안 = 즉시 적용**. PoC 2 에서 SystemPrompt 힌트 없이도 자연 발생 (단, 자발 표본 2 케이스). SystemPrompt 명시 힌트로 묶음 비율 추가 향상 가능, 비용 0
  - **(c) Variable binding 안 = 잠정 우선** (Pass 2 spike 결과로 final). PoC 1 의 LLM 비중 ≥99% + 묶임 boundary = ID 의존 가설 검증 → 변수 바인딩이 boundary 를 직접 끊는 가장 큰 ROI 추정. 단 schema 깔끔성 vs 작업 면적 trade-off 가 있어 "최우선" 표현 약화
  - **(b) batch tool 안 = Appendix A 로 격리**. (c) 폐기 시 fallback

#### 채택안 3축 비교 (review C3·M-4·M-12 반영 — 정량 보강)

| 축 | (a) parallel tool_use | (c) Variable binding | (b) Batch tool (Appendix) |
|---|---|---|---|
| 신규 코드 | SystemPrompt 1 문단 | F# `sanitizeVarName` + `resolveGuidOrVar` + `registerVar` (각 ~10줄) + `LlmTurnContext` SemaphoreSlim/`ConcurrentDictionary`/`SignalCascadeFailure` (~30줄) | 신규 tool `apply_operations` 1 + F# `queueBatch` + `BatchOpInput`/`BatchOpResult` type + `TruncateTo` API |
| 기존 코드 수정 | 0 | `ModelTools` mutation tool 7개의 GUID 인자 description 갱신 + `assignVar` 인자 추가 (각 1-2줄) | `ModelTools` 인자 schema 변경 0 |
| Schema 추가 token (LLM prompt cost) | 0 | mutation tool 7개 × `assignVar` description (~30 token × 7 = ~210 token) | 신규 tool 1개 description + 예시 (~300 token) |
| ID chain 압축 가능 | ❌ | ✅ | ✅ |
| 독립 op 압축 가능 | ✅ (자발) | ✅ (자발 + `assignVar` 미사용) | ✅ |
| Race / 동시성 처리 부담 | 없음 (호출 간 무상태) | 있음 (VarCache + cascadeFailureFlag — Critical-1 SemaphoreSlim 으로 흡수) | 없음 (1 tool call 안에서 단일 thread) |
| 사용자 철학 ("재활용 90점 / 신규 10점") 정합 | 최상 | **중-상** (mutation tool 7개 수정 — 단 description / 인자 추가 수준이고 핵심 로직은 F# 재활용) | **중** (신규 tool/type 다수 — schema bloat ~300 token, 하지만 기존 코드 수정 0 — M-12) |

> **재활용 vs 신규** 관점 (사용자 철학 가중 시): (a) → (b) → (c) 순. 단 (b) 는 schema bloat 가 LLM prompt 안에 들어가 **모든 turn 비용** 에 누적, (c) 의 description 갱신은 분산된 ~210 token 으로 대등 또는 약간 우월.

→ Pass 1 (a) 적용 후 **Pass 1.5 측정 gate** 에서 (a) 단독 ROI 측정 (예: 평균 turn 수 ≤ 6 AND 평균 시간 ≤ 25초이면 (c) 진행 보류 결정). 측정 결과로만 (c) vs (b) vs neither 결정.

---

## PoC 결과 (Pass 0 — 2026-05-07 완료)

### PoC 1 — round-trip 시간 분해 (단일 세션, n=12 LLM gap)

| 항목 | 값 |
|---|---|
| 총 LLM turn-around | 43,028 ms (n=12, avg 3,586 ms, max 10,423 ms, max/avg=2.9 — 분산 큼) |
| 총 server-side ToolCall elapsedMs | 7 ms (n=10, avg 1 ms, max 2 ms) |
| LLM + 비측정 IPC 비중 | **≥99.98%** (Round 표시상 100.0%, 통계적 독립 trial 0회 — 단일 세션) |

→ "round-trip 압축 = LLM turn-around 압축" 가설은 방향성으로 확정되나, 정량 수치 (예: "12 turn → 4~5 turn") 는 **단일 측정의 산출** 이므로 Pass 1.5 / Pass 5 에서 5+회 반복 측정으로 보강 필수.

### PoC 2 — parallel tool_use 검출 (message id 그룹핑)

stream-json 은 단일 message 를 여러 라인으로 incremental delta emit. message id 그룹핑이 SSOT — 스크립트 line 88-126 의 `Group-Object MsgId` 가 자동 산출 (M-1 review 반영, 이전 수동 분석 대체):

| message | tool_use 수 | 묶인 tool 들 | 자발 vs prompt-induced |
|---|---|---|---|
| `msg_01B71pw3` | 3 | add_system + add_work + add_work | 자발 (실린더 시나리오) |
| `msg_01GAnpnS` | 5 | add_api_def×2 + add_call×2 + add_arrow | 자발 (실린더 시나리오) |
| `msg_0196WFxS` | 3 | add_system × 3 | prompt-induced (PoC 2 prompt 직접 응답, 자발성 근거에서 제외 — M5) |

→ Claude 가 SystemPrompt 힌트 없이도 **multi tool_use 를 묶을 수 있음** (자발 표본 2 케이스). 묶임 boundary = ID chain 이 끊기는 지점 (= 직전 message 의 tool_result 를 봐야 다음 op 의 ID 인자를 알 수 있는 곳).

### 결정 결과 (잠정)

- (a) 안의 전제 (parallel tool_use 동작) ✅ 검증
- (c) 안의 ROI 가설 (boundary = ID 의존) ✅ 검증 — 변수 바인딩이 boundary 를 직접 압축
- (b) 안의 입지 약화 — (c) 가 schema 깔끔함과 동등 효과 모두 달성

→ **(a) 즉시 적용, (c) 잠정 우선, (b) fallback 으로 격리** 로 우선순위 재정렬.

> "(c) 최우선" 이 아니라 **잠정 우선** — Pass 2 의 McpHostService 동시성 spike 결과가 (c) 진행 가능성의 final gate.

### PoC 신뢰성 caveat (review M1·M5·C6 반영)

- **표본 부족 (M1)**: PoC 1 n=12 LLM gap, avg 3,586ms / max 10,423ms / min 1ms 분산이 큼 (max/avg=2.9). 현 PoC 결과는 "방향성 확정" 수준이며 정량 결정 (예: "12 turn → 4~5 turn") 의 근거로는 **5+회 반복 + 3-5 시나리오 paired test** 가 추가로 필요. → Pass 1.5 측정 gate 에서 보강.
- **PoC 2 자발성 표본 (M5)**: msg_0196WFxS 는 PoC 2 prompt 의 직접 응답 (prompt-induced) 라 "자발적 묶음" 근거에서 제외. 자발 묶음 표본은 msg_01B71pw3 (3 tool_use) + msg_01GAnpnS (5 tool_use) **2 케이스만**. SystemPrompt 가이드 추가 후 표본 확장 필요.
- **측정 정의 결함 (C6)**: `poc-roundtrip-analysis.ps1` 의 "user→assistant" gap 안에 RawStream log4net flush + Kestrel SSE buffer + WPF Dispatcher Background hop + RebuildAll 큐 대기 등 server-side 비측정 IPC 가 포함됨. ToolCall elapsedMs (`ModelTools.cs:62-63` Stopwatch) 는 dispatcher work 만. 따라서 결론은 **"LLM + 비측정 IPC ≥ 99%"** 로 표현 약화. 향후 측정에서 `RunMutation` 의 enqueueTs / workStartTs 분리 + `ClaudeCliProvider` stdout 라인 수신 직후 `StreamRecv` 로거 추가로 정확도 강화.

### 분석 도구 / 원본 위치

- 스크립트: `doc/poc-roundtrip-analysis.ps1`
- 원본 로그: `Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log20260507`
- PowerShell 5.1 console 한글 깨짐 회피: `chcp 65001` + `[Console]::OutputEncoding = [Text.UTF8Encoding]::new()` (스크립트 안에 이미 후자 포함, console code page 는 별도)

---

## 배경 / 현황

### 관측된 병목 (2026-05-07 sample 세션, PoC 1 로 정량화 완료)

`실린더 전진/후퇴 시스템 만들어줘` 한 마디에 9 mutation + 1 read = 10 round-trip 직렬. 각 round-trip ≒ (LLM token streaming + MCP IPC + UI dispatcher hop). 사용자 체감 지연의 대부분이 이 **직렬화 자체** 에서 발생.

샘플 chain:
```
add_system Cyl              → id=3cf5c5fe...
add_work   Adv (flow=...)   → id=a04e200f...
add_work   Ret (flow=...)   → id=42bb46d9...
add_api_def ADV (sys=Cyl)
add_api_def RET (sys=Cyl)
add_call   Cyl.ADV (work=Adv id)
add_call   Cyl.RET (work=Ret id)
add_arrow  Adv → Ret
validate_model
```

### 직렬화의 근본 원인 = ID 의존 chain

`add_call(workId=...)` 가 **직전 `add_work` 가 반환한 Guid** 를 받아야 한다. 따라서 단순한 "LLM 이 동시에 호출하라" 류의 솔루션은 **ID 의존이 없는 op 들** (예: `add_system Cyl` + `add_work Adv` + `add_api_def ADV`) 에만 효과 있고, 의존 chain 은 여전히 직렬.

> 단, ID 의존이 없는 독립 op 묶음만으로도 sample 은 9 → ~5 round-trip 압축 가능 (보조안 (a) 의 효과 범위).

### 기존 자산 — 이미 있는 것 / 없는 것

이미 있음:
- `ImportPlanBuilder` (`Solutions/Core/Ds2.LlmAgent/ImportPlanBuilder.fs`): turn 동안 op 누적 → turn end 시 단일 `ApplyImportPlan` → 1 undo step. **batch tool 1번 호출이 N op 을 누적해도 그대로 동작**.
- `ToolOperations` (F#): `queueAddSystem` / `queueAddFlow` / `queueAddWork` / `queueAddCall` / `queueAddApiDef` / `queueAddArrow` / `queueRemoveEntity` / `queueRenameEntity`. 인자가 모두 `(plan, store, ...)` 형태로 통일 — batch dispatcher 가 그대로 dispatch 가능.
- `ModelTools.RunMutation` 헬퍼: `Dispatcher.InvokeAsync` + 로그 + 카운터 + try/catch. batch 도 한 번의 `RunMutation` 호출 안에서 N 회 enqueue 만 하면 됨.

없음:
- LocalRef → Guid 해소 메커니즘. (현재는 LLM 이 매 호출마다 직전 반환 Guid 를 다음 인자로 그대로 복사)
- batch 입력 schema. 단일 tool 이 union 형태 array 를 받는 패턴은 처음.

---

## (b) Batch tool 설계

### Tool 시그니처

```
[McpServerTool]
ApplyOperations(
    LlmTurnContextProvider turnProvider,
    string operations  // JSON array — 아래 schema 참조
) → string             // per-op 결과 + 누적 plan size 요약
```

이름 후보: `apply_operations` (1순위) / `batch` / `compose`. fully-qualified = `mcp__promaker__apply_operations`.

> JSON 을 string 으로 받는 것은 ModelContextProtocol.AspNetCore 1.2.0 의 schema 생성기가 union/discriminated 타입을 잘 못 다루기 때문. tool 내부에서 `JsonDocument` 파싱. (검증 필요 — Pass 시작 전 schema 자동 생성 결과 sanity check)

### 입력 schema (JSON)

```json
[
  { "op": "add_system",  "ref": "Cyl",  "args": { "name": "Cyl", "isActive": false } },
  { "op": "add_api_def", "ref": "ADV",  "args": { "name": "ADV",   "systemId": "@Cyl" } },
  { "op": "add_api_def", "ref": "RET",  "args": { "name": "RET",   "systemId": "@Cyl" } },
  { "op": "add_work",    "ref": "Adv",  "args": { "localName": "Adv", "flowId": "4f757f9c-..." } },
  { "op": "add_work",    "ref": "Ret",  "args": { "localName": "Ret", "flowId": "4f757f9c-..." } },
  { "op": "add_call",    "ref": "AdvCall", "args": { "devicesAlias": "Cyl", "apiName": "ADV", "workId": "@Adv" } },
  { "op": "add_call",    "ref": "RetCall", "args": { "devicesAlias": "Cyl", "apiName": "RET", "workId": "@Ret" } },
  { "op": "add_arrow",   "args": { "sourceId": "@Adv", "targetId": "@Ret", "arrowType": "Start" } }
]
```

규칙:
- `op`: 필수. `add_system | add_flow | add_work | add_call | add_api_def | add_arrow | remove_entity | rename_entity` (기존 mutation tool 7+1종 1:1 대응).
- `ref`: optional. 같은 batch 안 후속 op 가 이 op 의 결과 Guid 를 참조할 때 부여하는 **logical name**. 영문/숫자/밑줄, 1-32자, batch 안 unique.
- `args`: 해당 op 의 기존 tool 인자와 동일 key. **Guid 자리에 `"@<ref>"` 사용 가능** — batch 처리 시 resolver 가 실제 Guid 로 치환.
- 일반 Guid 문자열도 그대로 허용 (기존 store 의 Guid 와 mix 가능).

### LocalRef resolver

batch 처리 시 `Dictionary<string, Guid> refMap` 을 들고 순서대로 dispatch:
1. op 의 `args` 안 모든 string 필드를 스캔 → `@<name>` 패턴이면 `refMap[name]` 으로 치환 (없으면 `VALIDATION_ERROR`).
2. 기존 `ToolOperations.queueXxx` 호출 → 반환 Guid 를 `refMap[op.ref]` 에 등록 (op.ref 있을 때만).
3. 다음 op 로 진행.

> resolver 는 **batch 입력 array 순서에 의존**. 위상정렬 자동화 X — LLM 이 의존 순서대로 적도록 SystemPrompt 에 가이드. 이유: schema 단순화 + LLM 도 자연어로 step 순서를 기술하는 데 익숙.

### 에러 처리 정책

`fail-fast`:
- 첫 실패 시점에 batch 중단, **이미 enqueue 된 op 도 plan 에서 rollback** (batch 진입 시점의 plan size 기억 → 실패 시 그 size 까지 잘라냄).
- 반환 메시지 = `BATCH_ERROR: op[3] add_call 실패: VALIDATION_ERROR ... (rollback applied, 0 ops queued)`.

이유: partial batch 가 ImportPlan 에 남으면 turn end 의 `ApplyImportPlan` 이 의도와 다른 모델을 만들어 1 undo 의 의미가 깨짐. LLM 은 next turn 에 재시도하는 편이 안전.

### 반환 형태

```
[batch] 8 op(s) queued (planSize 0 → 8):
  [0] add_system  Cyl       → id=3cf5c5fe-... (ref=@Cyl)
  [1] add_api_def ADV       → id=96e68b7a-... (ref=@ADV)
  ...
  [7] add_arrow   Adv→Ret   → id=c774c4d4-...
```

LLM 이 이 응답에서 후속 turn 에 쓸 Guid 를 추출할 수 있도록 ref 와 id 를 함께 표기. 기존 단일 tool 의 `[plan] add_xxx queued: ... id=...` 와 시각적 일관성 유지.

### 한계

- 1 batch 안에서 **방금 queue 된 entity 의 child 를 add 하려면 ref 사용 필수**. ref 없이 GUID 자리비움은 불가 (LLM 이 단일 호출 시 직전 반환 Guid 를 복사하던 흐름과 다름 — SystemPrompt 에서 명시적으로 가르쳐야 함).
- `validate_model` 같은 read tool 은 batch 에 포함 X. read 는 turn 안의 plan 누적이 보이지 않으므로 배치해도 의미가 없음. (현재 sample 에서 `EmptyFlow` 경고가 나는 이유와 동일.)
- 구조적 의존이 없는 read 후 mutation 흐름 (`list_systems → add_system on first project`) 은 여전히 2 round-trip — read 결과를 LLM 이 봐야만 mutation 가능하므로 batch 화 불가.

---

## (a) Parallel tool_use 보조안 — PoC 로 동작 ✅

### 현황
PoC 2 에서 Claude 가 **SystemPrompt 힌트 없이도** 자발적으로 묶음 emit. 최대 5 tool_use / 1 message 관측. Claude CLI subscription 의 stream-json 출력이 Anthropic API 의 multi tool_use 를 그대로 통과시킴이 확인됨.

### 적용 (M-3 review: 현재 SystemPrompt 의 batch 가이드는 read tool 한정 — mutation 가이드는 **신설**)

`SystemPrompt.cs` 의 line 21 / 85 의 batch 가이드는 모두 "Read tools — prefer batch" / "Batch reads first" 로 read 한정. mutation 묶기 가이드 신설:

> **# Mutation tools — batch via parallel tool_use**
>
> 같은 turn 안에서 다음 조건을 만족하는 mutation tool 호출은 한 assistant 메시지 안에 여러 tool_use block 으로 묶어라:
> - (조건 1) 서로 결과 Guid 를 참조하지 않거나
> - (조건 2) (c) 활성 시 — `assignVar` / `$<varname>` 으로 미래 Guid 를 참조하는 chain 으로 묶여 있고, **선행 op (assignVar 등록) 가 array 의 앞쪽** 에 위치
>
> Anthropic API 는 한 메시지 안 tool_use 들을 1 round-trip 으로 처리하지만, **dispatch 는 array 순서대로 직렬 실행됨** — 따라서 의존 chain 이 있으면 의존 순서대로 array 에 적어야 한다.

### Pass 1 단독 적용의 risk (M-9 review)

(a) 가이드 추가 시점에 (c) 가 미구현이면 LLM 이 다음을 시도할 수 있음:
- 한 메시지에 `add_system + add_flow` 묶기 → `add_flow` 가 *직전 메시지의* `add_system` Guid 를 알아야 하는데 미래 Guid 라 인자가 비거나 placeholder → `VALIDATION_ERROR` 폭증

대응:
- Pass 1 가이드 본문을 **(a) 단독 안전 케이스만 명시**: "결과 Guid 를 다른 op 가 참조 *안 하는* 묶음만". `assignVar` / `$<var>` 언급은 (c) 적용 후 Pass 4 에서 동시 추가
- 즉 Pass 1 에서는 PoC 에서 이미 자발적으로 묶이던 패턴 (msg_01GAnpnS = 5 op) 을 **명시적으로 허용** 하는 수준만. 새로운 chain pattern 을 LLM 에게 가르치지 않음

### 효과
- 비용 0, 적용 즉시
- (c) variable binding 이 적용되기 전에도 단독으로 round-trip 일부 압축
- (c) 와 결합 시 ID chain 까지 묶음 → 12 turn → 4~5 turn 추정 (단일 측정 추정)

### 한계
- ID chain boundary 는 (a) 만으로는 못 끊음 — (c) 가 필요
- `read-after-mutation` (read tool 결과를 보고 다음 mutation 결정) 패턴은 (c) 로도 안 풀림 — 진짜 ceiling. Pass 5 lower bound 분석 시 별도 확인

---

## (c) Variable binding 안 — 최우선 (신규 채택)

### 핵심 아이디어

LLM 이 mutation tool 호출 시 **출력 변수명** (`assignVar`) 을 부여하고, 같은 message 안 후속 tool_use 의 Guid 인자 자리에 `$<varname>` 사용. MCP server 가 turn-scoped Dictionary 에 `varname → 새 Guid` 를 저장하고, 후속 호출에서 `$<varname>` 을 만나면 cache lookup 으로 해소.

(a) 가 이미 multi tool_use 를 1 round-trip 에 dispatch 하지만 ID chain 이 있는 op 는 묶을 수 없는 한계를, **변수 바인딩으로 미래 Guid 를 가리키게 만들어** boundary 를 직접 끊음.

### Tool 시그니처 변화 (M-6: queueXxx 반환 비균질성 반영)

기존 mutation tool 의 모든 Guid 인자 (`systemId` / `flowId` / `workId` / `sourceId` / `targetId` / `entityId`) 가 다음 둘 다 허용:

- 정식 GUID 문자열 (기존 그대로)
- `$<varname>` (1-32자 영숫자/밑줄, turn-scoped cache lookup)

`assignVar: string?` 인자 — 반환 Guid 를 turn-scoped cache 에 등록. 적용 가능 tool 한정:

| Tool | queueXxx 반환 | assignVar 가능 |
|---|---|---|
| `add_system / add_flow / add_work / add_call / add_api_def` | `Guid` | ✅ |
| `add_arrow` | `Guid * string` (kind 표시명) | ✅ (Guid 부분만 등록) |
| `remove_entity` | `EntityKind` (새 Guid 없음) | ❌ — schema 에서 인자 자체 제외 |
| `rename_entity` | `EntityKind` | ❌ — 위와 동일 |

> 신규 tool 0개. 기존 tool 인자 schema 만 확장. (b) batch tool 처럼 union 입력이 없어 schema 깔끔.

### 사용 예 (LLM 측)

한 assistant message 의 multi tool_use 로:

```
add_system(name="Cyl", isActive=false, assignVar="cyl")
add_api_def(name="ADV", systemId="$cyl", assignVar="apiAdv")
add_api_def(name="RET", systemId="$cyl", assignVar="apiRet")
add_work(localName="Adv", flowId="<known>", assignVar="advWork")
add_work(localName="Ret", flowId="<known>", assignVar="retWork")
add_call(devicesAlias="Cyl", apiName="ADV", workId="$advWork")
add_call(devicesAlias="Cyl", apiName="RET", workId="$retWork")
add_arrow(sourceId="$advWork", targetId="$retWork", arrowType="Start")
```

→ 8 op 이 1 round-trip. PoC 1 의 12 turn 시나리오가 4~5 turn 으로 압축 (read 호출은 여전히 별 turn).

### 핵심 결정 포인트 — 실행 순서 보장 (Critical-1: 4명 합의)

`McpHostService` (= `ModelContextProtocol.AspNetCore` 1.2.0 + Kestrel) 는 ASP.NET Core 표준 위에 구축되어 **request 별 thread pool 위 concurrent dispatch 가 default** 임이 거의 확정적 (R3·R4). SSE / streamable HTTP transport 위에서 multi tool_use 가 별 HTTP request 또는 별 JSON-RPC message 로 분기되면 (c) 의 가정이 race 로 깨짐. 따라서 race 처리는 **spike 의 미지수가 아닌 확정 위험** 으로 다룬다.

#### Default 채택 (보완안 2 — semaphore 직렬화) — 명문화

```csharp
public sealed class LlmTurnContext : IAsyncDisposable {
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ConcurrentDictionary<string, Guid> VarCache { get; } = new();   // C1-2: ConcurrentDictionary 강제
    private volatile bool _cascadeFailureFlag;                              // C1-3: volatile bool
    public bool CascadeFailureFlag => _cascadeFailureFlag;
    internal void SignalCascadeFailure() {
        _cascadeFailureFlag = true;
        Plan.Clear();   // C2: set 시점에 plan.Clear() — fail-fast 통일
    }

    public async Task<T> RunSerialized<T>(Func<Task<T>> work) {
        await _gate.WaitAsync();
        try { return await work(); } finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() {
        VarCache.Clear();         // M-14: explicit cleanup
        _gate.Dispose();
    }
}
```

`RunMutation` 진입 시 `await ctx.RunSerialized(...)` wrap → multi tool_use 가 array 순서대로 직렬 실행. ToolCall elapsedMs 측정상 server-side 1-2ms 라 직렬화 overhead 무시 수준.

#### 보완안 1 (lookup miss 시 timeout-wait) — **명시적 reject** (Critical-1 R3)

- 이유: race 의존 자체 / UI 응답성 위협 / 디버깅 난이도 ↑ / deadlock 회피 코스트 (보완안 2 보다 항상 비싸거나 위험)
- 본 todo 어떤 분기에서도 채택하지 않음

#### Pass 2 spike 목표 재정의 (Critical-1 R4)

기존 "직렬 vs concurrent 판정" 만으로는 부족. 다음을 함께 측정:
1. SDK (`ModelContextProtocol.AspNetCore` 1.2.0) 가 **message-boundary 를 어디서 노출하는지** — tool 호출 시점이 array 순서를 보존하는 hook 이 있는지
2. 같은 client connection 의 multi tool_use 의 **동시 진입 가능 thread 수**
3. SDK upgrade 시 동작 변경 가능성 — version pin 정책 결정

spike 결과로 gate 유지/제거/SDK 의 직렬화 hook 활용 셋 중 결정.

### 부분 실패 처리 — fail-fast 통일 (Critical-2: 4명 합의)

`Authoring.fs:43-49 withTransaction` 의 catch 블록이 명시적 `for i ... -1 do records.[i].Undo()` + `reraise()` 함 — silent 가 아니라 explicit rollback + 예외 전파. 단 partial plan 이 turn end 까지 누적된 후 ApplyImportPlan 시점에 throw 시 사용자에게는 **"BATCH_ABORTED chat 알림은 보였는데 모델은 안 변함"** 이라는 UX 불일치가 발생. 따라서 (b) 와 의미적으로 동등한 fail-fast 로 통일:

- `LlmTurnContext.SignalCascadeFailure()` (위 코드 참조): `_cascadeFailureFlag = true` + **즉시 `Plan.Clear()`** (C2 핵심)
- 호출 트리거: mutation tool 의 `VALIDATION_ERROR` / `$<varname>` lookup miss / sanitize 실패 / `assignVar` 중복 / cap 초과
- 같은 turn 의 후속 mutation tool 은 진입 즉시 `BATCH_ABORTED: prior op failed (op[N] msg=...)` 로 단락. plan 변경 X (이미 clear 되어 있음)
- **turn end 의 `ApplyImportPlan` 호출 자체 skip** (Plan 이 비어 있어도 호출 없음 더 안전) + AssistantDelta 마지막에 `[skipped] turn 안 mutation 이 실패하여 변경 없음.` 메시지 emit
- LLM 은 다음 turn 에 read tool 로 현재 상태를 재확인 후 보완 (= 사용자 글로벌 규칙 "간단한 fail-safe 우선" 과 정합)

> (b) appendix 의 fail-fast rollback 과 의미적으로 동일. all-or-nothing 통일. 결정 7 (d) "1 turn = 1 undo" 의 spirit 과도 정합 — 0-step 도 undo 의미 일관 (UX 측에서는 "변경 없음").

### ImportPlanBuilder API 사전 조건 (Critical-3 검증)

검증 결과 `ImportPlanBuilder.fs:16-34` 는 `Add / Build / Operations / Count / IsEmpty / Clear` 만. `RemoveAt / TruncateTo` 부재.

- (c) 정책 = `Plan.Clear()` 만 사용 → **현행 API 로 충분, 신규 API 불필요**
- (b) appendix 의 `while plan.Count > snapshotSize do plan.RemoveAt(...)` 는 **존재하지 않는 API**. (b) 활성 시점에 `member _.TruncateTo(n: int) = ops.RemoveRange(n, ops.Count - n)` 1줄 추가가 사전 조건으로 등록

### Variable cache 의 scope

- **turn 단위** — F# `ToolOperations` 모듈의 `VarCache` 또는 `ImportPlanBuilder` 안 (C4 review — F# 흡수)
- turn 종료 시 자동 폐기. session 단위 확장 X — 사용자가 GUI 로 같은 entity 를 수정/삭제했을 때 stale Guid 위험
- **cap = 50** (M7 review — `MutationQuota` 와 동일 SSOT). 초과 시 `BATCH_ABORTED: 변수 capacity 초과 (50)` + cascadeFailureFlag set
- `LlmTurnContext` 는 `IAsyncDisposable` 화. Dispose 시 `VarCache.Clear()` + `_gate.Dispose()` (M7 review)

### 변수명 rule (M-13 review: register 측 대칭성)

- 1-32 자, `[a-zA-Z_][a-zA-Z0-9_]*`
- 같은 turn 안 unique. 중복 `assignVar` 시 `VALIDATION_ERROR` → `SignalCascadeFailure()`
- **resolve / register 양방향 모두 동일 sanitize 적용** (M-13 핵심): `$<varname>` 참조 시점뿐 아니라 `assignVar` 등록 시점에도 동일 정규식 + Cc/Cf 검사. 비대칭이면 register 가 통과한 잘못된 이름이 후속 resolve 에서만 거절되는 비일관성
- `$<varname>` 참조 시 cache miss → `VALIDATION_ERROR: 변수 '$xxx' 미정의` (`SignalCascadeFailure()`)
- 검증은 F# `ToolOperations.sanitizeVarName` (`sanitizeName` 의 형제 — Cc/Cf/길이 검사 SSOT 재활용 + `[a-zA-Z_][a-zA-Z0-9_]*` regex 추가) 단일 진입점 (C4 review)
- **error 메시지의 사용자 입력 echo 회피** (C4 review): 잘못된 varname 의 codepoint 만 노출, raw value 는 echo X — RLO/Cf 오염 가능성 차단

### 메시지 prefix 의미 분리 (M-15 review)

| Prefix | 의미 | 발생 위치 |
|---|---|---|
| `VALIDATION_ERROR` | 단일 op 자체 검증 실패 (이름 형식 / GUID parse / sanitize). cascade 미관여 | 기존 single tool path |
| `BATCH_ABORTED` | `cascadeFailureFlag` set 후 같은 turn 의 후속 mutation tool 진입 단락 | (c) 활성 시 |
| `INTERNAL_ERROR` | 예기치 않은 exception (dispatcher / store 자체 오류). bug 후보 | RunRead / RunMutation catch |
| `QUOTA_EXCEEDED` | turn 당 mutation 호출 수 / VarCache cap 초과 | `IncrementMutationCount` / `RegisterVar` |

이중 prefix 회피 — `BATCH_ABORTED` 메시지 안에 `prior op msg=VALIDATION_ERROR: ...` 같이 원본 메시지를 그대로 포함시키지 않고, op index 와 카테고리만 단축 표기. 디버깅 필요 시 ToolCall 로거 참조.

### 입력 sanitize 강화 — `@` / `$` prefix 거부 (C5·M10 review)

- F# `sanitizeName` 에 1줄 추가: `name`, `localName`, `devicesAlias`, `apiName`, `newName` 등 entity name 인자가 `@` 또는 `$` 로 시작하면 reject
- 이유:
  - C5: name="@malicious" 가 entity name 으로 등록 → read tool 출력에 노출 → 다음 turn LLM 자기 혼동 (self-loop)
  - M10: 사용자 자연어 안 `@Cyl` 토큰의 echo loop
- 구현 위치: `ToolOperations.fs:33-49 sanitizeName` 함수 본문 시작부에 4-5줄
- 회귀 unit test 1개로 prefix reject 동작 보장

### 구현 task 분해 (Pass 1 ~ Pass 3 재구성)

#### Task C-1 — F# 측 변수 해소 + cache (C4 review — F# 흡수)

`ToolOperations.fs` 에 다음 추가 (기존 `sanitizeName` / `requireFromStoreOrPlan` 와 같은 layer):

```fsharp
/// 변수명 sanitize. 빈 string = valid, 메시지 = error. sanitizeName 의 sibling.
let sanitizeVarName (value: string) : string = ...

/// "$xxx" 또는 GUID 문자열을 Guid 로 해소. cache miss 는 invalidOp.
/// VarCache 는 ImportPlanBuilder 의 멤버로 흡수.
let resolveGuidOrVar (plan: ImportPlanBuilder) (value: string) (field: string) : Guid = ...

/// queueAddSystem/Flow/Work/Call/ApiDef 등의 반환 Guid 를 plan.VarCache 에 등록.
/// assignVar = None 이면 no-op. 중복 / cap 초과 시 invalidOp.
let registerVar (plan: ImportPlanBuilder) (assignVar: string option) (id: Guid) : unit = ...
```

- `VarCache` 는 `ImportPlanBuilder` 의 internal `Dictionary<string, Guid>` 멤버
- `LlmTurnContext` 는 `Plan` 만 들고 `VarCache` 별도 보관 X (SSOT 유지)
- C# `ModelTools` 측은 string 값을 그대로 F# 에 통과 — 해소 / 등록 모두 F# 안에서 (C4 권장)
- `RunMutation` 의 `Dispatcher.InvokeAsync` 안쪽에서 호출되므로 cache lookup race-free 보장

`LlmTurnContext` 에 `bool CascadeFailureFlag { get; set; }` + `SemaphoreSlim Gate { get; }` 추가 (C1 default). `IAsyncDisposable` 화 (M7).

#### Task C-2 — `ModelTools` 의 모든 mutation method 에 `assignVar` 인자 추가

기존 `ParseGuid` 호출을 `ParseGuidOrVar` 로 교체 + 메서드 끝에 cache 등록:

```csharp
// 예: AddSystem
public static Task<string> AddSystem(
    LlmTurnContextProvider turnProvider,
    [Description("System 이름...")] string name,
    [Description("Active 여부.")] bool isActive = true,
    [Description("같은 turn 의 후속 호출이 '$<varname>' 으로 이 system 의 GUID 를 참조하려면 변수명 부여 (1-32자 영숫자/밑줄). 미사용 시 null.")] string? assignVar = null)
{
    // ... 기존 sanitize / cascadeFailureFlag 검사 ...
    return RunMutation(turnProvider, "add_system", ctx => {
        var sysId = ToolOperations.queueAddSystem(ctx.Plan, ctx.Store, name, isActive);
        RegisterVar(ctx, assignVar, sysId);  // 신규 헬퍼
        return $"[plan] add_system queued: ... id={sysId:D}{VarSuffix(assignVar)}";
    });
}
```

`RunMutation` 진입 시 `if (ctx.CascadeFailureFlag) return "BATCH_ABORTED: ..."` 단락 추가.

#### Task C-3 — McpHostService 동시성 동작 spike

별도 spike (1~2 시간):
- 단일 client connection 에 multi tool_use 가 들어오는 trace 로그 추가
- 실제 dispatch 가 직렬인지 concurrent 인지 확인
- 결과에 따라 보완안 1/2/3 중 선택

#### Task C-4 — SystemPrompt 갱신

`SystemPrompt.cs` 의 batch 가이드 절에:
- 변수 바인딩 사용 예 (실린더 8 op 압축형 1개)
- `assignVar` / `$<var>` rule 1 줄
- (a) 의 묶음 힌트와 함께 명시: "한 message 안에 여러 mutation 을 묶고, ID 의존이 있으면 assignVar 로 변수명을 부여하라"

#### Task C-5 — Negative test

- `$<var>` 미정의 → `VALIDATION_ERROR`
- 잘못된 변수명 형식 → `VALIDATION_ERROR`
- 중복 `assignVar` → `VALIDATION_ERROR`
- 중간 op 실패 → 후속 op `BATCH_ABORTED`
- turn end 에 부분 plan 이 1 undo step 으로 적용됨 검증

#### Task C-6 — sample 세션 재측정

PoC 1 의 실린더 prompt 동일 — 12 turn / 43초 → 목표 4~5 turn / ~15초.

---

## Appendix A — (b) Batch tool 안 (fallback only)

> Pass 1.5 측정에서 (a) 만으로 부족하고 + Pass 2 spike 에서 (c) 의 race 회피 비용이 과다로 판명될 때만 활성. (c) 가 Pass 5 까지 정상 진행되면 (b) 폐기.
>
> (b) 안의 array placeholder namespace = `@<ref>` (batch 안 self-contained), (c) 의 turn-scoped 변수 = `$<varname>` — 의도된 분리 (M2 review).

### 구현 task 분해 (legacy — (b) 안 기준)

> 아래는 (b) batch tool 안의 task 분해. (c) 가 진행되면 무관. (c) 폐기 시 fallback 으로 참조.

### Task 1 — F# `ToolOperations.queueBatch` (신규)

위치: `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` 끝.

시그니처:
```fsharp
/// batch op 1개의 입력 형태. args 는 JsonElement (Newtonsoft 가 아닌 System.Text.Json — McpServer 가 사용 중인 직렬화기와 일치).
type BatchOpInput = { Op: string; Ref: string option; Args: System.Text.Json.JsonElement }
type BatchOpResult = { Index: int; Op: string; Ref: string option; Id: Guid; Display: string }

/// batch op 배열을 plan 에 누적. fail-fast — 실패 시 진입 시점의 planSize 까지 rollback.
/// returns: Ok results | Error (failureIndex, opName, message)
val queueBatch : ImportPlanBuilder -> DsStore -> BatchOpInput[] -> Result<BatchOpResult[], int * string * string>
```

내부 구현:
- 진입 시 `let snapshotSize = plan.Count`.
- `let refMap = Dictionary<string, Guid>()`.
- 순회하며 op.Op 에 따라 dispatch (기존 `queueAddSystem` 등 호출 전에 `args` 의 Guid 자리를 `resolveGuid` 로 치환).
- `resolveGuid (s: string) : Guid` = `s.StartsWith("@")` → `refMap.[s.Substring(1)]` else `Guid.Parse(s)`.
- 실패 시 `while plan.Count > snapshotSize do plan.RemoveAt(plan.Count - 1)` 후 Error 반환.

> 기존 `queueAddXxx` 함수의 로직 재사용 — batch 전용 경로를 따로 만들지 않는다 (90점 = 재활용).

### Task 2 — C# `ModelTools.ApplyOperations` (신규 메서드)

위치: `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` 끝.

```csharp
[McpServerTool, Description(@"여러 mutation 을 1 round-trip 으로 누적합니다. 같은 batch 안 후속 op 가 직전 op 결과 Guid 를 참조하려면 'ref' 를 부여하고 args 의 Guid 자리에 '@<ref>' 사용. 의존 순서대로 array 에 적을 것. fail-fast — 실패 시 batch 전체 rollback. read tool (list_*, describe_*, validate_model) 은 포함 불가.")]
public static Task<string> ApplyOperations(
    LlmTurnContextProvider turnProvider,
    [Description("Op 객체 JSON array. 각 객체: { op: \"add_system|add_flow|...|rename_entity\", ref?: \"<localName>\", args: {...} }.")] string operations)
{
    // 1. JsonDocument.Parse → BatchOpInput[] 변환
    // 2. RunMutation(turnProvider, "apply_operations", ctx => { 
    //      var r = ToolOperations.queueBatch(ctx.Plan, ctx.Store, ops);
    //      // Result 매칭 → 성공 시 fancy display, 실패 시 BATCH_ERROR
    //    });
}
```

호출 카운터: `ctx.IncrementMutationCount()` 를 batch op 개수만큼 증가 (단일 호출이 아니라 누적된 op 수가 진실).

### Task 3 — `PromakerToolNames` 에 추가

위치: `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs`.

```csharp
public const string ApplyOperations = "mcp__promaker__apply_operations";
```

allowlist (`AllToolNames` 배열) 에 추가 — 누락 시 LLM 측 tool 차단으로 회귀.

### Task 4 — SystemPrompt 갱신

위치: `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs`.

추가 내용 (개략):
1. batch 가이드 절에 `apply_operations` 사용 예 (위 schema 의 실린더 예시 그대로) 1개 + when-to-use 룰:
   - 같은 turn 에 2개 이상 mutation 이 필요하고, 그 중 하나라도 다른 op 의 결과 Guid 를 참조하면 → `apply_operations`
   - 독립 호출만 있을 때 → 단일 tool 을 한 메시지 안에 여러 tool_use block 으로 (= (a) 보조안)
2. ref 명명 규칙 (영문/숫자/밑줄, 1-32자, batch 안 unique).
3. fail-fast 정책 안내.

> 길이 부담: 현 SystemPrompt 가 이미 phase 1d 풀세트로 큰 편. 새 절은 200~300자 안으로 압축. 예시는 단일 압축형 1개.

### Task 5 — Negative test

기존 1d-6 negative test (PromakerToolNames drift 검출) 와 같은 라인에 추가:
- `apply_operations` schema parse 실패 → `BATCH_ERROR` 메시지 포맷 검증
- ref unknown 참조 → `VALIDATION_ERROR: ref '@xxx' 미정의` 검증
- mid-batch 실패 → planSize 가 진입 시점으로 rollback 됨을 검증

위치: 기존 LlmAgent unit test project 에 case 3개 추가 (해당 project 위치 확인 필요).

### Task 6 — sample 세션 재측정

기존 "실린더 전진/후퇴" prompt 로 batch tool 적용 전/후 round-trip 수와 사용자 체감 시간 비교. 9 → 1 (mutation 만) + 1 (validate read) = 2 round-trip 도달이 목표.

> 측정은 `Promaker.LlmAgent.ToolCall` 로거의 elapsedMs + StreamJson `assistant` 이벤트 timestamp 로 산출.

---

## 영향받는 파일

| 파일 | 변경 |
|---|---|
| `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` | `queueBatch` + `BatchOpInput` / `BatchOpResult` 추가 |
| `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` | `ApplyOperations` 메서드 추가 |
| `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` | 상수 + allowlist 추가 |
| `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs` | batch 가이드 + parallel tool_use 힌트 추가 |
| (test project) | negative test case 3종 |

---

## 결정 포인트 / 리스크

1. **JSON array 를 string 인자로 받을지 vs JsonElement 직접**
   - string 인자: schema 단순, ModelContextProtocol 1.2.0 schema 생성기 호환성 안전
   - JsonElement: 1단계 파싱 절약, 단 schema 표현이 모호 (`"object"` 로 표시될 가능성)
   - 1차 안 = string. Pass 1 에서 schema 결과 sanity check 후 변경 가능.

2. **resolver 가 `args` 안 어느 필드까지 스캔할지**
   - 모든 string 필드의 `@<name>` 패턴을 검사하는 광역 스캔 vs op 별 명시 필드 (`systemId` / `flowId` / `workId` / `sourceId` / `targetId` / `entityId`) 만 검사하는 화이트리스트
   - 1차 안 = 화이트리스트. 광역은 `name="@TODO"` 같은 placeholder 가 오인될 위험.

3. **read tool 의 batch 포함 여부**
   - 1차 안 = mutation only. read 는 plan 미반영 상태에서 응답하므로 batch 안에 끼워봐야 정보가 새것이 아님.
   - 이후 read-only batch (e.g. `describe_subtree` 여러 개) 가 정말 병목이 되면 별 tool `read_batch` 로 분리.

4. **(a) parallel tool_use 의 실제 동작 여부**
   - Claude CLI subscription 의 stream-json output 이 다중 tool_use block 을 1 assistant 이벤트로 emit 하는지 unverified.
   - Pass 0 에서 30 분 spike — sample prompt 1개로 확인. 미동작 시 (a) 폐기, (b) 만 진행.

5. **LLM 이 ref 를 정확히 사용할지**
   - 모델 정확도에 의존. SystemPrompt 의 single 압축 예시만으로 부족하면 multi-shot 예시 추가.
   - 회귀 시: ref 미사용 (= 기존 단일 tool 흐름) 은 항상 fallback 가능하므로 안전망 존재.

---

## 작업 순서 권장

1. ~~**Pass 0 — PoC 1 + 2**~~ ✅ **완료 (2026-05-07)**. 결과는 본 문서 상단 "PoC 결과" 절 참조.
2. ~~**Pass 1 — (a) SystemPrompt 가이드 신설**~~ ✅ **완료 (2026-05-07)**. `Apps/Promaker/Promaker/LlmAgent/Prompts/1.SystemPrompt.md` 의 mutation tools 절과 Arrow semantics 절 사이에 `# Mutation batching — parallel tool_use (independent ops only)` 절 신설. 단독 안전 케이스 4가지 (independent System / 기존 System 의 ApiDef / 기존 endpoint 의 Arrow / 같은 Flow 의 Work) + 금지 케이스 (sibling 반환 Guid 의존) + dispatch=array order 명시. `assignVar` / chain pattern 은 Pass 4 에서 통합. 빌드 통과.
3. **Pass 1.5 — (a) 단독 효과 측정 gate (M-4·M-1·C-4 review, ~1-2시간)**: 아래 "Pass 1.5 측정 Spec" 절 참조.
4. **Pass 2 — McpHostService 동시성 spike (Task C-3, ~1-2시간, Critical-1 R4)**: 단일 client connection 의 multi tool_use 가 직렬 vs concurrent dispatch 인지 + SDK message-boundary 노출 위치 trace. **(c) 진행 가능 여부의 final gate**. SemaphoreSlim gate 유지/제거/SDK hook 활용 셋 중 결정.
5. **Pass 3 — (c) Task C-1 + C-2 구현**: F# `VarCache (ConcurrentDictionary)` + `resolveGuidOrVar` + `registerVar` + `sanitizeVarName` + `@`/`$` prefix reject + `LlmTurnContext.SignalCascadeFailure (volatile + Plan.Clear())`. 모든 mutation tool 의 `assignVar` 인자 (add_* 한정).
6. **Pass 4 — Task C-4 + Task C-5**:
   - (c) chain pattern 가이드를 (a) 가이드와 합쳐 SystemPrompt 갱신 (Pass 1 의 단독 안전 케이스 → 풀세트로 확장)
   - prompt cache breakpoint 영향 확인 (M-12) — `cache_creation_input_tokens` / `cache_read_input_tokens` metadata 비교
   - Negative test: `@`/`$` injection / 변수명 형식 / 중복 assignVar / cap 초과 / cascade abort
7. **Pass 5 — Task C-6 (재측정, 정량 success gate)** + done 문서 결과 정리.
   - 정량 gate (C-4 review): **최소 5회 측정 + 평균 turn ≤ 5 AND 평균 시간 ≤ 18초** 면 success. ttft / total token / output token 분리 측정 (M-13)
   - gate 미달 시 본 todo 보류 + 원인 분석 → (b) appendix 활성 또는 다른 접근 검토

각 Pass 끝에 `dotnet build` 통과 확인 + commit. Pass 5 끝에 `doc/done-promaker-llm-agent.md` 에 결과 절 추가.

> **결합 가능성 (M-9)**: Pass 1.5 결과가 명확히 "ID chain 잔존" 이면 Pass 1 의 (a) 가이드와 Pass 4 의 (c) 가이드를 별도 SystemPrompt 갱신으로 분리하지 않고, **Pass 1 을 skip 하고 Pass 4 에서 통합 갱신** 하는 흐름도 가능. 이유: Pass 1 단독 가이드 → Pass 4 풀세트 가이드 이행이 prompt cache breakpoint 를 두 번 깨뜨려 비용 ↑.

> Pass 1.5 / Pass 2 가 (c) 진행을 막으면: (a) 만 유지 + Appendix A (b) 검토. (a) 는 어느 분기든 살아남음.

---

## Pass 1.5 측정 Spec

### 측정 형태 — paired baseline vs Pass 1

같은 prompt 세트를 두 코드 상태에서 각 5회 반복:
- **Baseline (B)**: Pass 1 commit 직전 SystemPrompt (mutation batching 절 없음). `git stash` 또는 한 commit 뒤 checkout.
- **Treatment (T)**: Pass 1 commit 적용 상태.

각 trial 사이 새 chat session (panel 닫고 다시 열기) — turn-scoped state 누적 회피. Promaker 자체는 trial 마다 재시작 권장 (RawStream / ToolCall 로그 분리 + dispatcher state 초기화). 매 trial 후 `Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log<날짜>` 백업 권장 (덮어쓰기 X — 같은 날짜 파일에 append 됨).

### 시나리오 5종

| ID | 명칭 | Prompt | 예상 mutation 수 | 압축 sweet spot |
|---|---|---|---|---|
| **S1** | 실린더 chain (PoC 1 재현) | `/f/Git/kwak/kwak/DsConcepts/*.md 숙지. 실린더 하나 전진 후퇴하는 시스템 만들어줘.` | 9 (chain heavy) | (a) 부분 — ID 의존 boundary 가 다수 |
| **S2** | 독립 System 다발 | `Sys1, Sys2, Sys3, Sys4 4 개 system 을 한 번에 추가해줘. 서로 의존 없음.` | 4 (전부 독립) | (a) full — 1 message 에 4 묶음 가능 |
| **S3** | 기존 System 의 ApiDef 다발 | (S2 응답 후 같은 chat) `Sys1 에 ADV, RET, IDLE, ERROR 4 개 ApiDef 추가해줘.` | 4 (systemId 이미 known) | (a) full — 4 묶음 가능 |
| **S4** | 실린더 2개 (혼합) | `실린더 두 개 (Cyl1, Cyl2) 만들어줘. 각각 전진/후퇴 work 와 arrow.` | ~16 (chain × 2 + 일부 독립) | (a) 부분 — System / ApiDef 단계는 묶임, Work/Call/Arrow 는 chain |
| **S5** | read-after-mutation (ceiling) | `현재 모델에 어떤 system 이 있는지 보고, 그 중 하나에 'Cleanup' 이라는 work 와 RESET api 를 추가해줘.` | 1 read + 2 mutation | (a)/(c) 모두 효과 미미 — 진짜 ceiling. lower bound 측정용 |

> S2 / S3 는 같은 session 에서 순차 실행 — S3 가 S2 의 결과를 활용. 별 trial 로 분리 측정 시에는 baseline session 에 미리 Sys1 을 GUI 로 추가.

### 측정 항목 (per-trial)

`pwsh -File doc/poc-roundtrip-analysis.ps1 -RecentMinutes 5` 출력에서 추출:

1. **TurnCount** — `user→다음 assistant` gap 의 개수 (= LLM round-trip 수)
2. **TotalLlmGap** — 위 gap 들의 합 (ms)
3. **MaxLlmGap** / **MinLlmGap** — 분산 확인 (PoC 1 의 max/avg=2.9 대비 변화)
4. **MultiToolUseMessages** — 단일 message id 안 tool_use ≥ 2 인 메시지 수
5. **MaxToolUsePerMessage** — 단일 message 의 최대 tool_use block 수
6. **TotalToolElapsedMs** — `Promaker.LlmAgent.ToolCall` 의 elapsedMs 합 (server-side dispatcher work)
7. **WallClock** — 사용자 prompt 입력 → 마지막 assistant delta 까지 stopwatch (외부에서 수동)

### 결과 기록 표 양식 (시나리오별 별 표 5개)

```
S1 — 실린더 chain
| trial | TurnCount | TotalLlmGap(ms) | MultiTool# | MaxToolUse | WallClock(s) |
|-------|-----------|-----------------|------------|------------|--------------|
| B-1   |           |                 |            |            |              |
| B-2   |           |                 |            |            |              |
| B-3   |           |                 |            |            |              |
| B-4   |           |                 |            |            |              |
| B-5   |           |                 |            |            |              |
| T-1   |           |                 |            |            |              |
| T-2   |           |                 |            |            |              |
| T-3   |           |                 |            |            |              |
| T-4   |           |                 |            |            |              |
| T-5   |           |                 |            |            |              |
| **B avg** |       |                 |            |            |              |
| **T avg** |       |                 |            |            |              |
| **Δ%**    |       |                 |            |            |              |
```

paired test = wilcoxon signed-rank 권장이지만 n=5 라 **단순 평균 비교 + 부호 일치 (5/5 trial 에서 T < B 인지)** 로 약식 통과.

### 분기 결정 매트릭스

| S1 (chain heavy) Δ% | S2/S3 (독립) Δ% | S4 (혼합) Δ% | 결정 |
|---|---|---|---|
| TurnCount 감소 ≥ 30% AND 시간 ≤ 25s | 감소 ≥ 50% | 감소 ≥ 30% | **(a) 만 유지, 본 todo close** (ID chain 압축까지 (a) 가 어느 정도 흡수했다는 강한 신호) |
| TurnCount 감소 < 15% (S1) | 감소 ≥ 50% | 감소 < 20% | **(c) 진행 → Pass 2** (ID chain boundary 가 (a) 의 ceiling) |
| TurnCount 감소 < 10% AND parallel multi-tool 발생 < 30% | 마찬가지 < 10% | < 10% | **가이드 강화 후 재측정** — LLM 이 가이드를 잘 안 따름. 예시 추가 / 강한 prompt |
| 어떤 시나리오든 wall-clock 증가 | — | — | **regression** — Pass 1 revert 후 원인 분석 |

S5 는 결정에 사용 X — lower bound 참조용. S5 의 turn 수가 baseline 과 같아야 정상 (read 후 mutation 흐름은 어느 안으로도 압축 불가).

### 측정 절차 (체크리스트)

1. Baseline 측정
   - `git log --oneline -2` 로 Pass 1 commit hash 확인 (= H_T)
   - `git stash push doc/poc-roundtrip-analysis.ps1 Solutions/Core/Ds2.LlmAgent/doc/` 후 Pass 1 commit revert 또는 한 단계 전 checkout
   - `dotnet build ../../../Apps/Promaker/Promaker.sln`
   - Promaker 실행 → S1 → 종료 → 로그 백업 (`cp ds2.log<날짜> ds2.log.B-S1-1`)
   - 5회 반복. S2~S5 각 5회 — total 25 trial
2. Treatment 측정
   - Pass 1 commit checkout (`git checkout H_T`)
   - 동일 절차 25 trial
3. 분석
   - `pwsh -File doc/poc-roundtrip-analysis.ps1 -LogPath ds2.log.B-S1-1` 등 50회 호출 (트라이얼별 분리)
   - 표 채우기 → 분기 결정

### Pass 1.5 결과 보고 위치

본 todo 의 별도 절 "## Pass 1.5 결과 (YYYY-MM-DD)" 신설 + 결정 결론을 작업 순서 절에도 반영. 결과에 따라 `done-promaker-llm-agent.md` 에는 옮기지 않음 — Pass 5 까지 통합 정리.

### 다른 todo 와의 cross-reference (M9 review)

- 본 todo 는 phase 2 후속 작업으로 `doc/todo-promaker-llm-agent.md` 의 "다음 작업 진입 권장 순서" 에서 참조 추가 필요
- Pass 1 / 5 진행 시 `doc/done-promaker-llm-agent.md` 에 결과 절 추가
- `CLAUDE.md` 의 PromakerToolNames "11개" 표기는 stale (실제 14개) — 본 todo 와 별개로 다른 작업에서 update 필요 (M6 review)

---

## Review 응답 — 2차 (2026-05-07, 10명 reviewer 정리 결과 반영)

검증 통과 (사실 확정 9건):

| 검증 항목 | 결과 | 근거 위치 |
|---|---|---|
| ImportPlanBuilder rollback API 부재 | ✅ | `ImportPlanBuilder.fs:16-34` (Add/Build/Operations/Count/IsEmpty/Clear 만) |
| `_validateCache` lock-free 가정 = 단일 field 한정 | ✅ | `LlmTurnContext.cs:33` 주석 |
| poc-roundtrip-analysis.ps1 LogPath default 불일치 | ✅ | line 10 (수정 완료 — glob + 가장 최근 mtime 자동) |
| em-dash hardcoded → hyphen 비호환 | ✅ | line 29 (수정 완료 — `[char]0x2014\-]` 둘 다 매칭) |
| "100%" 는 Round 표시 산출 | ✅ | line 158 `Round(...,1)` — 99.984 → 100.0 |
| message-id 그룹핑 = 수동 분석 | ✅ | 스크립트 update — 자동 산출 |
| SystemPrompt mutation 가이드 부재 | ✅ | `SystemPrompt.cs` line 21·85 = read 한정 |
| ParseGuid / Sanitize / RunMutation 위치 | ✅ | `ModelTools.cs:38, 44, 53` |
| queueXxx 반환 타입 비균질성 | ✅ | `ToolOperations.fs:146/158/169/180/192` (Guid) / `:212/229` (EntityKind) / `:254-256` (Guid*string) |

### Critical 4건 (수용 / 본문 반영)

| # | 항목 | 처리 |
|---|---|---|
| C1 | (c) race 처리 = 확정 위험 (4명 합의) | "(c) 핵심 결정 포인트" 절 — `ConcurrentDictionary<string,Guid>` + `volatile bool` + 보완안 1 명시 reject + Pass 2 spike 목표 재정의 (SDK message-boundary) |
| C2 | (c) partial-plan = (b) 와 fail-fast 통일 (4명 합의) | "(c) 부분 실패 처리" 절 — `SignalCascadeFailure()` set 시점에 `Plan.Clear()` + ApplyImportPlan 자체 skip |
| C3 | ImportPlanBuilder rollback API 부재 | "ImportPlanBuilder API 사전 조건" 절 — (c) 는 `Plar.Clear()` 로 충분, (b) appendix 활성 시만 `TruncateTo` 추가 |
| C4 | PoC 통계적 한계 (단일 세션 + Round 표시) | PoC 1 표 표현 약화 ("≥99.98%") + Pass 5 정량 success gate 추가 (5회 측정 / 평균 turn ≤ 5 / 평균 ≤ 18초) |

### Major (수용 / 본문 반영)

| # | 항목 | 처리 |
|---|---|---|
| M-1 | PoC 결과 "수동 분석" 명시 | 스크립트 update (message-id 그룹핑 자동 산출) — "수동 분석" 문구 제거 |
| M-2 | LogPath default + em-dash | 스크립트 둘 다 수정 |
| M-3 | mutation 가이드 = "기존" 이 아닌 신설 | "(a) 적용" 절 표현 정정 + 신설 절 spec 추가 |
| M-4 | (c) schema bloat 정량 평가 | 3축 비교 표에 token 수 명시 (~210 vs ~300 vs 0) |
| M-6 | queueXxx 반환 비균질 → assignVar 한정 | "(c) Tool 시그니처" 표 — add_* 만 ✅, remove/rename 은 ❌ schema 인자 자체 제외 |
| M-9 | Pass 1 단독 적용 risk | "(a) Pass 1 단독 적용의 risk" 절 신설 + 작업 순서에 Pass 1/Pass 4 결합 가능성 명시 |
| M-10 | array-order dispatch 명시 | (a) 가이드 본문 "dispatch 는 array 순서대로 직렬 실행" 명시 |
| M-12 | "재활용 90점" 비교 누락 | 3축 비교 표 마지막 row + 본문 코멘트 |
| M-13 | register 측 sanitize 대칭 | "(c) 변수명 rule" 절 — resolve / register 양방향 동일 sanitize 명시 |
| M-14 | VarCache 폐기 메커니즘 | `LlmTurnContext.DisposeAsync()` 코드에 `VarCache.Clear()` 명시 |
| M-15 | 메시지 prefix 4종 | "메시지 prefix 의미 분리" 표 신설 |

### 보류 (영향 미미 / 후순위 / 별 작업)

- **M-5**: (b) JSON 파싱 DoS 가드 — Appendix A 활성 시점에만 적용
- **M-7**: ParseGuid 재활용 — F# 흡수 (이전 review C4 권고로 처리됨, 본 todo 의 ParseGuidOrVar 는 F# 측 `resolveGuidOrVar` 로 치환됨)
- **M-8**: legacy details 분리 — Appendix A 격리로 부분 처리
- **M-11**: 메인 todo 진입/탈출 — "다른 todo 와의 cross-reference" 절로 처리
- **Outlier 들** (R6 C-1 / R3 M2·M5 / R5 M2 / R8 m3): 각 phase 진입 시 점검

### 정정 (반론 후 표현 수정)

- **C2 "silent failure" 표현 정정**: 1차 review 시 명시 — `Authoring.fs:43-49 withTransaction` 의 catch 가 `for i ... -1 do records.[i].Undo()` + `reraise()`. silent 가 아닌 explicit rollback. 단 fail-fast 통일 권고는 옳음.
- **R6 C-1 (Result interop)**: F# 측 흡수로 자연 해소 — `Result<...>` 노출 X.

---

## Review 응답 — 1차 (2026-05-07, 5명 reviewer 정리 결과 반영)

### 수용 (문서 반영)

| # | 항목 | 반영 위치 |
|---|---|---|
| C1 | multi tool_use 직렬 보장 미검증 → SemaphoreSlim default + spike 격상 | "(c) 핵심 결정 포인트" / 작업 순서 Pass 2 |
| C2 | partial-plan 정책 통일 (단 표현 정정 — silent fail 아니고 reraise) | "(c) 부분 실패 처리" |
| C3 | (c) vs (b) 우선순위 근거 약함 → 3축 비교 표 | "채택안 3축 비교" |
| C4 | ParseGuidOrVar layer 잘못 → F# 흡수 | "Task C-1" |
| C5/M10 | name="@xxx" / "$xxx" injection self-loop → sanitize 강화 | "(c) 입력 sanitize 강화" |
| C6 | 측정 정의 결함 → "100%" → "≥99%" 표현 약화 | "PoC 신뢰성 caveat" |
| M1 | 표본 부족 → Pass 1.5 gate 에서 5회 반복 | 작업 순서 Pass 1.5 |
| M3 | (b) legacy → Appendix A 격리 | "Appendix A" |
| M4 | Pass 1.5 측정 gate 누락 | 작업 순서 |
| M5 | PoC 2 자발성 표본 약화 (msg_0196WFxS = prompt-induced) | "PoC 신뢰성 caveat" |
| M7 | VarCache cap 부재 + Dispose | "(c) Variable cache scope" |
| M9 | cross-reference 누락 | "다른 todo 와의 cross-reference" |
| M12 | prompt cache breakpoint 영향 | 작업 순서 Pass 4 |
| M13 | ttft vs total 분리 측정 | 작업 순서 Pass 5 |

### 부분 반론 (검증 후 표현 정정)

- **C2 silent failure**: `Authoring.fs:43-49 withTransaction` 의 catch 가 명시적 `for i ... -1 do records.[i].Undo()` + `reraise()`. silent fail 이 아니라 explicit rollback + 예외 전파. 단 "all-or-nothing 통일" 권장 자체는 옳고 사용자 철학 정합 → 정책으로 수용.
- **C5 self-injection**: 외부 권한 상승 아닌 LLM self-loop 수준. 단 sanitize 1줄 비용 0 이라 수용.
- **M5 자발성**: msg_0196WFxS 는 prompt-induced 지만 msg_01B71pw3 / msg_01GAnpnS 두 케이스는 실린더 시나리오라 자발 묶음 근거 유지 (표본만 3 → 2 로 약화).

### 보류 (영향 미미 또는 spike 결과 의존)

- **M2** `@` vs `$` 표기 — 의도된 분리 ((b) batch array 내부 namespace vs (c) turn-scoped 변수). Appendix A 안에 명시.
- **M6** PromakerToolNames "11개" stale — CLAUDE.md 차원의 별 작업 (본 todo 범위 밖이지만 cross-reference 에 기록).
- **M8** (b) rollback 부수효과 (ToolCallLog / IncrementMutationCount 회복) — Appendix A 활성화 시점에만 의미.
- **M11** cascadeFailureFlag 결합도 — Pass 2 spike 결과 "직렬" 이면 자연 해소.
- **Minor 들** — 본문에 직접 반영 또는 논리적 결론 변화 없음.

---

## PoC 실행 절차 (Pass 0 — 완료, 재현용으로 보존)

### 사전 조건

- `log4net.config` 의 root level 이 DEBUG (현행 그대로). 별도 logger 절 추가 불필요 — `Promaker.LlmAgent.RawStream` / `Promaker.LlmAgent.ToolCall` 모두 root 따라 DEBUG 출력.
- Promaker 가 실행 중이면 종료.

### 실행

```powershell
# 1. 빌드 + 실행 (이 디렉토리 = Solutions/Core/Ds2.LlmAgent 기준)
dotnet build ../../../Apps/Promaker/Promaker.sln
dotnet run --project ../../../Apps/Promaker/Promaker
```

Promaker UI 에서:

1. 상단 ribbon "기타" → "유틸" → "LLM Chat (토글)" 으로 chat panel 열기
2. **PoC 1 prompt** 입력 (기존 sample 재현 — round-trip 9 회):
   > /f/Git/kwak/kwak/DsConcepts/*.md 숙지. 실린더 하나 전진 후퇴하는 시스템 만들어줘.
3. 응답 완료 대기 후 새 chat 으로 **PoC 2 prompt** 입력 (parallel tool_use 검증):
   > 새 프로젝트에 SystemA, SystemB, SystemC 세 개를 한 번에 추가해줘. 서로 의존 관계 없으니 한 메시지에 묶어 호출해도 좋아.
4. 응답 완료 후 Promaker 종료 (RollingFile appender flush 보장)

### 분석

```powershell
pwsh -File doc/poc-roundtrip-analysis.ps1
# 또는 다른 위치의 로그라면:
pwsh -File doc/poc-roundtrip-analysis.ps1 -LogPath <ds2.log path>
```

스크립트가 출력하는 항목:
- `Stream events 요약` — system/init / assistant / user / result 라인 수
- `PoC 2: parallel tool_use 검출` — 단일 assistant 라인 안 다중 tool_use block 존재 여부
- `PoC 1: round-trip 분해` — 매 user→다음 assistant 사이 시간 (= LLM turn-around)
- `결론` — LLM 비중 % 와 가설 (≥90% = 확정 / 70~90% = 부분 / <70% = 약화)

### 의사결정 포인트

| PoC 2 결과 | PoC 1 LLM 비중 | 결정 |
|---|---|---|
| ✅ multi tool_use 관측 | ≥90% | (b) batch + (a) parallel 둘 다 진행. 우선순위는 (b) — ID chain 까지 커버 |
| ✅ multi tool_use 관측 | 70~90% | (b) 진행, (a) 보너스. server-side 측에도 일부 최적화 검토 |
| ❌ multi tool_use 미관측 | ≥90% | (a)/(c) 폐기, **(b) batch tool 만** 진행 |
| ❌ multi tool_use 미관측 | <70% | 본 todo 자체 보류 — 다른 병목 (Dispatcher / queueXxx / log4net flush 등) 우선 조사 |

### 주의

- PoC 2 prompt 의 응답이 SystemA/B/C 를 한 메시지에 묶어 호출했는지는 **모델 의지 + SystemPrompt 가이드 유무** 에 좌우. SystemPrompt 의 batch 가이드 절에 parallel tool_use 힌트가 없는 현재 상태에서 ❌ 가 나와도 (a) 안 자체가 무효는 아님 — 힌트 추가 후 재실험 필요. 즉 Pass 0 의 ❌ 는 "현재 상태에서 emit 안 함" 만 결론.
- PoC 2 의 보강 prompt: "한 응답 안에 mcp__promaker__add_system 호출 3개를 동시에 emit 하라" 같은 명시적 지시. 모델이 이마저도 거부하면 진짜 ❌.
