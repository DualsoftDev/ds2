# /indexer skill — VLM caption 을 Claude Code subagent 로 위임

## 0. 목적

`/indexer` skill 의 VLM caption 생성을 Anthropic API 직접 호출 (`Vlm.buildCaptionGen`, env `LIGHTHOUSE_VLM_API_KEY`) 대신 Claude Code session 의 subagent 로 위임. **skill path 한정** 으로 API key 박제 불필요 + 비용을 Claude Code subscription 으로 통합. server / Promaker / CI 등 unattended caller 의 default path 는 그대로 Anthropic direct 유지.

## 1. 채택안 — 옵션 B (deferred 2-step) + parallel subagent

본 단원의 "Step 1/2/3" 은 **skill sub-flow** 단위. parent `done-lighthouse-kb-index.md` §3.15.5 의 "Phase 1/2/3" (MVP lib / image+VLM / OCR) 와 무관하므로 혼동 회피 위해 **Step** 으로 명명한다.

- **Step 1 (index-only)**: CLI 가 `caption=NULL` 로 색인만 (`ImageCache` row 박제 + `ImageReferences` join). image bytes 는 이미 `<folder>/.lighthouse-kb/blobs/images/<sha256>.<ext>` 에 file 로 박제됨 (`ImageStore.saveBlob`, 변경 0).
- **Step 2 (caption-fill)**: skill 이 SQLite query 로 caption-pending row 를 fetch → subagent 병렬 dispatch → caption text + model 회수 → CLI batch UPDATE 호출.
- **Step 3 (upload)**: CLI 가 zip + POST upload.

### 1.1 현 통합 path 와의 delta

현재 `Program.runIndex` (Program.fs:151~) 와 `runUpload` (Program.fs:196~) 는 *진입 시점에* `Vlm.buildCaptionGen` 호출 → `LIGHTHOUSE_VLM_API_KEY` 미박제 + `forceWithoutCaption=false` 이면 fail-fast (exit 13). 본 채택안에서 skill path 는 **항상 `--force-without-image-caption` 박제** 로 captionGen=`CaptionGenerator.noop` 진입한다. 즉 lib / Vlm.fs / Program.fs 의 fail-fast 분기는 변경 0 이며 *flag 박제만* 으로 noop path 가 자연 활성화된다 (`Vlm.fs:42` 의 기존 분기 재사용).

## 2. 핵심 설계 결정 (사용자 confirm 의무)

본 표는 parent §3.15.5 의 10건 default (s6-r7 확정) 위에 **skill subagent path 한정** 으로 신설된다. confirm 완료 시 각 행에 `✓ s{n}-r{m} (YYYY-MM-DD)` stamp 를 추가하고 SKILL.md 의 "실행 절차" 단원에 박제한다 (parent §3.15.5 패턴 정합).

| # | 항목 | 잠정값 | 비고 |
|---|---|---|---|
| 1 | subagent batch K (image 개수 / agent) | **4** | image 1장 ≈ 1K vision token + prompt+system ≈ 3K → K=4 시 ≈ 7K input/agent. K=8 은 Anthropic tier 1 ITPM (40K) 위험. tier 2+ 확정 시 K=8 재상향 가능 |
| 2 | parallel P (동시 spawn 수) | **4** | Claude Code Agent 동시 dispatch 상한 documented 부재 — 보수 추정. K×P=16 image/round |
| 3 | image volume threshold (lower) | **1** (2026-05-21 정정) | N ≥ 1 이면 항상 subagent path dispatch. 종전 **20** 박제는 부조리 — Claude Code 사용자 대부분이 `LIGHTHOUSE_VLM_API_KEY` 미박제 → caption 미생성으로 종결되는 결과. batch (K×P=16) 는 *parallel 효율* 단위일 뿐, image 수 적다고 caption 처리 자체 skip 하지 않음. 작은 N (예: 4) 은 subagent 1개로 충분 — dispatch 단위는 자연스럽게 `ceil(N/K)` |
| 4 | image volume threshold (upper) | **120** | N > 120 (≈ K×P×7.5 round, wall-clock 5분+) 시 사용자 confirm prompt. 측정 단위 = 폴더 전체 `ImageCache.CaptionText IS NULL` row 수 |
| 5 | caption-prompt SSOT 위치 | **lib `CaptionGenerator.CaptionPrompt` (Literal) 단일 유지** | skill 은 CLI `lighthouse-cli print-caption-prompt` 로 매번 fetch — 사본 박제 없음, drift 원천 차단 |
| 6 | caption-pending SSOT 위치 | **SQLite (`ImageCache.CaptionText IS NULL` query)** | 별 manifest 파일 박제 폐기. CLI `lighthouse-cli list-pending-captions <folder>` 가 매 호출 시 JSON stream |
| 7 | subagent_type | **general-purpose** (1차) → `image-captioner` specialized agent (2차 확장) | 1차는 정의 의무 회피, 2차는 system prompt 절감 ROI |
| 8 | CLI sub-command 분리 범위 | **`index --skip-upload` flag + `caption-update <folder> <batch.json>` sub-command + 기존 `index --upload` 유지** | 신규 sub-command 1개 + flag 1개. SRP 측면 응집도 우수 |
| 9 | subagent caption 의 `CaptionModel` 식별자 | **`claude-{model}-via-subagent`** (e.g. `claude-opus-4-7-via-subagent`) | Anthropic direct path 의 model literal 과 구분 → MR3 cache invalidation key 정합 + 진단 가능 |
| 10 | 부분 실패 retry 정책 | **per-image max 2 attempts** | retry 마다 base64 재인코딩 token 비용 0 아님 (blob file 박제는 disk IO 만 절감) |
| 11 | subagent → image bytes 전달 방식 | **file path 전달 후 subagent 가 Read 도구로 read** | Claude Code Agent 표준 패턴. harness 가 Read 시점에 자동 base64 화 → subagent context 안에서만 처리, main 컨텍스트 token 회수 회피. caption text (1~2 문장) 만 return → max_output_tokens=300 가드와 정합 |
| 12 | 동일 hash 의 multiple ref 처리 | **hash 당 1 row 반환 (대표 ref = MIN(refLocator) 또는 첫 ref)** | caption 은 image 자체에 1회만 박제. per-collection dedup (MR2) 정합. `listPendingCaptions` SQL 에 `GROUP BY ImageHash` + `MIN(...)` 박제 |
| 13 | `CaptionModel` 의 `{model}` 추출 | **env `$CLAUDE_MODEL` 확인 → 미박제 시 `claude-via-subagent` 폴백** | skill 진입 시 1회 read. session model 식별 가능하면 정확 박제, 아니면 일반 literal |
| 14 | 결과 동등성 spot-check 합격 기준 | **샘플 5장 중 ≥ 4장 의미 동등 (사람 판정)** | 미만 시 §2 #5 SSOT (lib `CaptionPrompt` literal) 재검토. 즉 spot-check 실패 = caption-prompt 정책 점검 trigger |
| 15 | Anthropic tier 식별 안내 | **사용자가 `console.anthropic.com → Settings → Limits` 확인 후 §2 #1/#2 조정** | 미확인 시 K=4/P=4 default (tier 1 안전 마진). tier 2+ 확정 시 K=8/P=8 까지 상향 검토 |
| 16 | `caption-update` 빈 batch 입력 | **exit 0 (no-op)** | manifest empty + retry 시 idempotent. crash recovery 시 자연 진입 path |
| 17 | ~~lower-bound (N<20) fallback 시 env 검사~~ | **폐기 (2026-05-21)** | §2 #3 lower=1 정정으로 fallback path 자체 폐기. LIGHTHOUSE_VLM_API_KEY 검사는 별 entry (사용자가 명시적으로 Anthropic direct path 선택 시) 에만 의미 |

## 3. 변경 범위

### CLI (`Solutions/Tools/Ds2.LightHouse.Cli/`)

- **`Program.fs`** — entry 추가:
  - `index --skip-upload` flag (기존 `runIndex` 에 분기 추가, force-without-caption 자동 박제).
  - `list-pending-captions <folder>` — SQLite query → stdout JSON stream. lib helper `ImageStore.listPendingCaptions conn` 호출.
  - `caption-update <folder> <batch.json>` — `runCaptionUpdate` entry. 입력 JSON parse → 단일 SQLite transaction 안 N 회 `ImageStore.updateCaption` 호출 → commit.
  - `print-caption-prompt` — lib `CaptionGenerator.CaptionPrompt` literal stdout. skill 의 prompt fetch SSOT.
- **`Vlm.fs`** — 변경 0. 기존 force-flag 분기 (`buildCaptionGen` 의 `forceWithoutCaption=true` → `CaptionGenerator.noop` 반환 path) 그대로 skill path 흡수. *line number 는 drift 가능 → 함수명 anchor 우선.*
- **`Packager.fs`** — 변경 0. Step 1 종결 후 manifest 박제 의무 없음 (SQLite query 가 SSOT).

### lib (`Solutions/Core/Ds2.LightHouse/`)

- **`ImageStore.fs`** — 신규 1 record + 1 함수:
  ```fsharp
  type CaptionPendingRecord = {
      Hash: string
      Ext: string
      RefLocator: string
      DocPath: string
  }

  // ImageCache.CaptionText IS NULL AND ImageCache row 존재 (icon-skip 자연 제외).
  // ImageReferences join + GROUP BY ImageHash + MIN(RefLocator) 로 hash 당 1 row (§2 #12).
  // PRAGMA / connection lifecycle 은 SqliteStore.openConnection 단일 진입점 의무 (parent §3.17).
  val listPendingCaptions: SqliteConnection -> CaptionPendingRecord seq
  ```
  - 기존 `updateCaption` 시그니처 (`hash, text, model`) 변경 0 — `caption-update` 가 batch loop 안 호출. 동일 hash 가 batch 안 중복 시 last-wins (transaction 안 sequential UPDATE).
- **`CaptionGenerator.fs`** — 변경 0. `CaptionPrompt` literal (line 44~45) 가 SSOT, CLI 가 그대로 노출.
- **`Indexer.fs`** — 변경 0. `captionGen` DI surface (line 92, 193, 296, 301, 326, 364) 가 이미 noop 주입 가능.

### skill (`.claude/skills/indexer/SKILL.md`)

- Step 1/2/3 흐름 박제. SKILL.md 의 현 "## 사용법" 단원 직후에 **"## Step 1/2/3 흐름 (subagent caption path)"** 단원으로 신설. 기존 단순 CLI wrapper 안내 단원은 default path 안내로 그대로 유지.
- subagent dispatch template — 다음 JSON contract 강제:

  **manifest (caption-pending input)** — `lighthouse-cli list-pending-captions <folder>` 의 stdout:
  ```json
  [
    { "hash": "<sha256>", "ext": "png", "refLocator": "doc:0:3", "docPath": "Foo.pptx" }
  ]
  ```

  **batch (caption-update input)** — skill 이 subagent result 를 모아서 박제, CLI `caption-update <folder> <batch.json>` 입력:
  ```json
  [
    { "hash": "<sha256>", "captionText": "...", "captionModel": "claude-opus-4-7-via-subagent" }
  ]
  ```

  **subagent return contract** — Agent tool 응답의 마지막 줄이 단일 JSON line. parse 실패 시 해당 batch row 는 manifest 잔존 (자연 재시도).
- threshold 분기:
  - N < 20 (lower) → subagent dispatch skip. skill 이 `lighthouse-cli index --upload <url>` (force-without-caption 미박제) 안내 → Anthropic direct path 로 fallback.
  - 20 ≤ N ≤ 120 → 자동 dispatch.
  - N > 120 → 사용자 confirm prompt ("이미지 {N}장. 예상 wall-clock {ceil(N/(K*P))*소요시간}분. 진행?").
- caption-prompt fetch — 진입 시 `lighthouse-cli print-caption-prompt` 1회 호출 → subagent prompt template 에 박제.
- subagent prompt 안에 다음 명시:
  - image file path = `<folder>/.lighthouse-kb/blobs/images/<hash>.<ext>` — subagent 가 **Read 도구로 read** (§2 #11). prompt 안 직접 base64 첨부 금지.
  - "caption text 1~2 문장만 출력. image 데이터 echo 금지. 마지막 줄에 JSON line 박제."
- Agent tool 호출 시 max_output_tokens=300 가드.

## 4. 주의 / risk

- **concurrent SQLite writer 회피** — subagent 가 직접 SQLite 접근 0. caption text + model 만 main (skill) 으로 return → `lighthouse-cli caption-update` 단일 process 가 `SqliteStore.openConnection` 단일 진입점 (parent §3.17 invariant) 으로 BEGIN TRANSACTION → N 회 UPDATE → COMMIT atomic.
- **partial success contract** — `caption-update` 의 transaction 은 (UPDATE 다수) atomic. crash 시 in-memory caption text 는 소실되나 SQLite 는 일관. retry 시 `list-pending-captions` 가 다시 NULL row 만 enumerate → 자연 idempotent. per-image max 2 attempts cap (§2 #10) 으로 무한 retry 차단.
- **token cost ≈ Anthropic direct** — subagent path 가 system prompt + skill instruction 만큼 ≈ image 1장당 +500~1500 input token 가산. wall-clock 만 P 배 단축. retry 마다 base64 재인코딩 token 비용 재발생.
- **subagent caption-prompt drift 차단** — §2 #5 결정으로 SSOT 단일 (lib `CaptionGenerator.CaptionPrompt`). skill 은 매 진입 시 `lighthouse-cli print-caption-prompt` 로 fetch → 사본 박제 없음. lib 의 prompt 변경 = 자동 전파. (장기적으로 lib build 와 skill runtime 의 binary 버전 차이 risk 잔존 — 동일 repo checkout 가정 필요).
- **양 path 결과 동등성 진단** — `ImageCache.CaptionModel` literal 이 path 별로 다름 (§2 #9: `claude-opus-4-7-via-subagent` vs `claude-sonnet-4-6` direct). 동일 image 에 두 path 가 caption 박제 시 model literal 로 source 구분 가능. MR3 cache invalidation key 자연 정합.
- **`upload` 된 collection 의 caption patch API 부재** — server-side caption-only patch endpoint 없음. Step 3 진입 = Step 2 완전 종결 invariant 의무. `caption-update` 후 `list-pending-captions` 가 빈 결과 반환할 때만 `index --upload` 또는 별 `upload-only` path 진입 허용. skill 측 가드.
- **manifest 가 zip 안 동봉되는 risk 자연 해소** — 별 manifest 파일 박제 폐기 (§2 #6) 로 `Packager.createZip` 의 enumeration 에 진단 파일 진입 0.
- **caption=NULL row race** — Step 1 ~ Step 2 사이에 다른 caller (Promaker / server) 가 동일 폴더 색인 진입 = parent §3.15.4 의 in-place 색인 가정 (`wipe .lighthouse-kb/`) 으로 통상 차단. lock 의무는 별도 결정 필요 시 본 단원 갱신.
- **Anthropic tier ITPM 검증** — §2 #1 K=4 / #2 P=4 는 tier 1 (40K ITPM) 안전 마진. tier 2+ 환경에서 K=8 / P=8 까지 실측 후 상향.
- **자가 검열 trigger** — 본 transfer 구현 phase 진입 시 trigger ② (`listPendingCaptions` + `runCaptionUpdate` + `print-caption-prompt` entry = 3 신규) + ③ (Program.fs 다회 변경 + ImageStore.fs lib 변경 = 2 파일) + ⑤ (CLI public sub-command 신설 = SSOT 갱신) 동시 충족. `Agent` (general-purpose 또는 code-review skill) 호출 의무.

## 4.1 코딩 / 빌드 정합

- **logging** — 신규 CLI entry 는 cli 의 기존 logger 사용 (e.g. `Log.lighthouse.Debug/Info/Warn/Error` — 동일 패턴 grep 으로 spot check 후 재사용). lib 안 신규 함수는 `logDebug/logInfo/logWarn/logError` 사전 정의 함수 (CLAUDE.md 정책).
- **build** — `dotnet build Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj` (CLI) + `dotnet build Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj` (lib). 통합은 동일 solution (`Solutions/Ds2.sln` 또는 LightHouse 전용 sln — 진입 시 repo 최상위에서 `*.sln` glob 으로 확인).
- **JSON** — Newtonsoft.Json 사용 (CLAUDE.md default). `CaptionPendingRecord` / batch record 는 record 필드명 그대로 PascalCase 직렬화 — JSON contract (§3 의 manifest/batch fenced block) 는 camelCase 이므로 `JsonSerializerSettings { ContractResolver = CamelCasePropertyNamesContractResolver() }` 박제 의무 (CLI entry 시점).
- **line ending** — `.fs` LF only (CLAUDE.md 정책).

## 5. 진입 순서 (구현 시점)

1. 사용자 confirm — §2 의 1~10 결정. confirm 완료 시 행에 `✓` stamp.
2. lib 변경 — `ImageStore.listPendingCaptions` 신설 (단일 함수 30 line 내외, lib 변경 최소).
3. CLI 변경 — `Program.fs` 에 4 entry (`--skip-upload` flag / `list-pending-captions` / `caption-update` / `print-caption-prompt`) 추가.
4. SKILL.md 갱신 — Step 1/2/3 흐름 + threshold 분기 + subagent prompt template + JSON contract 박제.
5. 소형 폴더 (image 5~10장) 로 lower-bound fallback path 검증 → 30~50장 폴더로 자동 dispatch path 검증 → 200장 폴더로 upper-bound prompt 검증.
6. 결과 동등성 spot-check — 동일 image 를 두 path (direct vs subagent) 로 caption 한 결과의 의미 동등성 샘플 비교 (수치 metric 강제 아님, 사람 검토).
7. 자가 검열 sub-agent 호출 → review 결과 반영 → commit.

## 6. 관련 파일

- `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` — `runIndex` / `runUpload` 분기 (기존) + `--skip-upload` flag + `list-pending-captions` / `caption-update` / `print-caption-prompt` sub-command (신설).
- `Solutions/Tools/Ds2.LightHouse.Cli/Vlm.fs` — `buildCaptionGen` 의 force-flag 분기 (변경 0). Anthropic direct path 의 builder.
- `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` — Step 1 종결 후 manifest 박제 의무 없음 (변경 0).
- `Solutions/Core/Ds2.LightHouse/CaptionGenerator.fs` — Anthropic direct path 의 실 HTTP call (`callAnthropic`) + `CaptionPrompt` literal SSOT (§2 #5).
- `Solutions/Core/Ds2.LightHouse/ImageStore.fs` — `saveBlob` (변경 0) + `updateCaption` (변경 0) + 신규 `listPendingCaptions` 1 함수.
- `Solutions/Core/Ds2.LightHouse/Indexer.fs` — `captionGen` DI surface (4 호출점). caller 가 `CaptionGenerator.noop` 주입만으로 Step 1 noop path 활성 (변경 0).
- `.claude/skills/indexer/SKILL.md` — Step 1/2/3 흐름 + subagent dispatch template 박제 (신설 단원).
- `Apps/Promaker/Docs/done-lighthouse-kb-index.md` — parent SSOT. §3.15.5 MR1 (blob 경로) / MR2 (per-collection dedup) / MR3 (CaptionModel tier invalidation) 정합 + §3.17 (SqliteStore.openConnection 단일 진입점 + PRAGMA WAL/synchronous/busy_timeout 자동 박제) invariant 정합.
- `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` — `openConnection` 가 모든 PRAGMA 박제 진입점. caption-update entry 도 단순 `openConnection <folder>` 호출만으로 sufficient (별도 PRAGMA 박제 의무 0).
