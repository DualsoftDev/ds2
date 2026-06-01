---
name: indexer
description: "LightHouse Service 에 폴더를 collection 으로 색인/업로드 (lighthouse-cli wrapping). Trigger: /indexer"
trigger: /indexer
---

# /indexer

light-house repo 의 `Ds2.LightHouse.Cli` (`lighthouse-cli`) 를 호출하여 폴더를 LightHouse Service 의 collection 으로 색인 + 업로드.

본 skill 은 CLI wrapper 일 뿐, 색인 로직 자체는 `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` 의 SSOT 를 따른다. protocol contract 는 `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` 와 동일 (POST `/collections` multipart + `Authorization: Bearer <PSK>` + `X-User-Identity`).

## Usage

```
/indexer                                           # 현재 working directory 색인 (default URL = localhost service)
/indexer <folder>                                  # 지정 폴더 색인
/indexer <folder> --title <name>                   # collection 표시 이름 override (생략 시 폴더명)
/indexer <folder> --url <baseUrl>                  # URL 명시 override (LIGHTHOUSE_URL env / default 모두 무시)
```

추가 CLI subcommand (2026-05-28 fix — skill 자동 dispatch / 사용자 수동 진단용):

```
lighthouse-cli probe-indexer-version <folder>      # stdout JSON: {"status":"ok|key-missing|open-failed|db-missing", "version":..., "reason":...}
lighthouse-cli export-image-cache <folder>          # caption 박제 ImageCache row JSON stdout (wipe 전 backup)
lighthouse-cli import-image-cache <folder> <json>   # backup JSON → 신규 DB 의 caption 4 컬럼 UPDATE (idempotent)
```

## 사전 조건

### Service URL 결정 우선순위 (SSOT)

| 우선순위 | source | 값 |
|---|---|---|
| 1 | 명령행 `--url <baseUrl>` | 사용자가 명시한 값 |
| 2 | env var `LIGHTHOUSE_URL` | shell 환경에 박제된 값 |
| 3 | **default (fallback)** | **`https://127.0.0.1:8443`** — local host 에 구동 중인 LightHouse Service 의 loopback listen URL (`install-service.ps1` / `config.json.template` SSOT). self-signed cert → `--allow-invalid-certs` 자동 박제 |

→ **3가지 모두 미박제 시 사용자에게 묻지 않고 default 로 진행**. 단 default 진입 전 healthz probe 1회 수행 (아래 참조).

### env var
- `LIGHTHOUSE_URL` — service base URL (선택). 명령행 `--url` 가 우선, 둘 다 미박제 시 default `https://127.0.0.1:8443`.
- `LIGHTHOUSE_PSK` — 평문 PSK (**필수**). CLI 가 직접 읽음. 미박제 시 사용자에게 일반 텍스트로 물어보고 응답값을 그 turn 의 `$env:LIGHTHOUSE_PSK` 로 박제하여 호출.

### local default 진입 시 healthz probe

URL 결정이 우선순위 3 (default) 인 경우 다음을 1회 수행:

```powershell
& curl.exe -k -s -o NUL -w "%{http_code}" --max-time 3 https://127.0.0.1:8443/healthz
```

- `200` 외 응답 시 사용자에게 `로컬 LightHouse Service 미가동 (https://127.0.0.1:8443/healthz != 200). 서비스 기동 후 재시도 또는 --url 로 명시.` 노출 후 abort.
- 우선순위 1·2 (사용자 명시 URL) 인 경우 probe skip — 사용자의 명시 의도 존중.

### Ollama (embedding backend)
- `ollama serve` 가동 + `bge-m3` 모델 pull 됨 (`ollama pull bge-m3`).
- **NaN 방지 (bge-m3 known issue)**: `OLLAMA_FLASH_ATTENTION=false` 환경에서 `ollama serve` 가동 필수. 미박제 시
  특정 입력 길이부터 Flash Attention long-context FP16 결함으로 embedding 결과에 NaN 발생 → ollama 가
  `"failed to encode response: json: unsupported value: NaN"` 로 500 반환 → CLI abort.
- search skill 의 동일 안내와 정합 (`.claude/skills/search/SKILL.md` §외부 의존).
- `--no-embedding` flag 박제 시 본 의존 무관 (BM25-only 색인).

### CLI binary
repo local build 결과를 직접 사용. `dotnet publish` 또는 PATH 등록 불필요.

## CLI 위치 도출 절차

1. `git rev-parse --show-toplevel` 으로 repo root 도출.
2. 다음 두 경로 순서대로 탐색 (Release 우선):
   - `<root>/Solutions/Tools/Ds2.LightHouse.Cli/bin/Release/net9.0/lighthouse-cli.exe`
   - `<root>/Solutions/Tools/Ds2.LightHouse.Cli/bin/Debug/net9.0/lighthouse-cli.exe`
3. 둘 다 미존재 시 즉시 에러 종료. 사용자에게 다음 메시지 노출:
   ```
   lighthouse-cli build 안 됨.
   다음 명령 실행 후 재시도:
     dotnet build -c Release <root>/Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj
   ```
   (자동 build 안 함 — 사용자가 명시적으로 빌드.)

## 진입 분기 결정 (SSOT)

skill 진입 즉시 다음 순서로 분기를 박제. **모델은 image 수 추측 / 폴더 종류 추론 / 파일 종류 추론 등 어떤 자체 추론으로도 분기하지 않는다** — 아래 환경 / flag 단일 기준만 따른다.

| 조건 | 진입 path |
|---|---|
| 사용자가 명령행에 `--force-without-image-caption` 명시 | §"단일 호출 path" (caption 명시 skip) |
| `$env:LIGHTHOUSE_VLM_API_KEY` 박제됨 | §"단일 호출 path" (CLI 가 Anthropic API direct 로 caption) |
| 위 둘 다 미해당 (**Claude Code 사용자의 default**) | §"Step 1/2/3 흐름" (subagent caption + summary path) |

→ image 0건 폴더에서도 Step 1/2/3 path 안전 — Step 1-b / Step 2 가 empty batch exit 0 no-op. 즉 "이 폴더는 image 없을 것 같으니까 단일 호출 path" 식의 분기 금지.

### 사용자 질의 시 결정 고수

진입 분기 결정 후 사용자가 "왜 X 명령?" / "왜 --skip-upload?" 등을 물으면 **선택된 path 의 mechanism 을 설명할 뿐, 자체 판단으로 path 를 변경하지 않는다.** 분기 정정은 다음 두 경우에만 허용:
- 사용자가 명시적으로 다른 path 를 지시 (e.g. "단일 호출로 진행해줘", "`--force-without-image-caption` 박제해줘").
- 사용자가 환경을 바꾸고 재시도 (e.g. `LIGHTHOUSE_VLM_API_KEY` 박제).

Step 1-a 의 `--skip-upload` 는 *의도된 시작점* — 표면적으로 "업로드 안 함" 처럼 읽혀 사용자가 의문을 표할 수 있지만, Step 3 의 `--upload` 가 caption 박제 이후 별도 진입하는 분리 흐름의 의도된 1단계.

## 단일 호출 path

**진입 조건**: §"진입 분기 결정 (SSOT)" 표의 상위 2개 행 (사용자 명시 flag OR `LIGHTHOUSE_VLM_API_KEY` 박제). 그 외 환경에서는 §"Step 1/2/3 흐름" 을 사용.

```powershell
& "<cli-path>" index "<folder>" --upload "<resolved-url>" --title "<derived-title>"
```

- `<resolved-url>` 은 위 §"Service URL 결정 우선순위" 의 1→2→3 순으로 도출. 우선순위 3 (default `https://127.0.0.1:8443`) 진입 전 healthz probe 통과 필수.
- PSK 는 env var `LIGHTHOUSE_PSK` 로 CLI 가 자동 흡수 (명령행 노출 안 함).
- `--title` 미지정 시 CLI 가 자동으로 폴더명 사용 (skill 에서 굳이 derive 안 함).
- stderr 의 진행률 (`[N%] x/y — file`) 그대로 사용자에게 전달.
- self-signed cert 환경 (default loopback URL 포함) 이면 `--allow-invalid-certs` 자동 박제 (dev only). 우선순위 1·2 의 외부 URL 이 정식 cert 인 경우 사용자가 `--url` 뒤 별도 인자 없이도 그대로 통과 (CLI 가 SSL 검증 정상 수행).

## Step 1/2/3 흐름 (subagent caption + summary path)

**진입 조건**: §"진입 분기 결정 (SSOT)" 표의 3번째 행 — `LIGHTHOUSE_VLM_API_KEY` 미박제 + 사용자가 `--force-without-image-caption` 미명시. **Claude Code 사용자의 default path** (대다수 환경이 이에 해당).

VLM caption 을 Anthropic API 직접 (`LIGHTHOUSE_VLM_API_KEY`) 대신 Claude Code subagent 로 위임하여 비용을 Claude Code subscription 으로 통합. `Apps/Promaker/Docs/done-lighthouse-indexer-claude-caption.md` 의 채택안 (옵션 B, deferred 2-step + parallel subagent) 박제. todo §2 #3 의 lower=1 결정에 따라 **image 1장이라도** 있으면 subagent path 의무 — image 0건 폴더에서도 안전 (Step 1-b / Step 2 가 empty batch exit 0 no-op).

**design (r5+)**: doc-level summary 도 subagent batch 박제 (Step 1 의 sub-step). PR-H1 zero-cost fallback (firstSentence)
폐기 — PDF 표제지 stale 박제 결함이 design 의도 ("LLM 호출 없이 박제된 summary 박제") 를 fail 시킴. SummaryText NULL
상태는 `SummaryBuilder.PendingPlaceholder` 박제로 명시.

### 결정 박제 (todo §2)

| # | 값 | 설명 |
|---|---|---|
| 1 | K=4 | subagent 1개당 image 개수 (tier 1 ITPM 안전 마진) |
| 2 | P=4 | parallel spawn 수 |
| 3 | lower=1 | N ≥ 1 이면 항상 subagent path dispatch. 종전 lower=20 (skip 분기) 은 부조리 — Claude Code 사용자 대부분이 `LIGHTHOUSE_VLM_API_KEY` 미박제 → caption 미생성으로 종결되는 결과. batch 는 속도 향상 도구일 뿐, image 수 적다고 처리 자체 skip 하지 않음 |
| 4 | upper=120 | N>120 시 사용자 confirm prompt |
| 5 | prompt SSOT | `lighthouse-cli print-caption-prompt` 매 진입 시 fetch — 사본 박제 없음 |
| 6 | pending SSOT | `lighthouse-cli list-pending-captions <folder>` (SQLite query, manifest 파일 박제 없음) |
| 7 | subagent_type | general-purpose (1차) |
| 9 | CaptionModel | `claude-{model}-via-subagent` (env `$CLAUDE_MODEL` 기반, 미박제 시 `claude-via-subagent`) |
| 10 | retry | per-image max 2 attempts |
| 11 | image 전달 | file path 만 prompt 에 박제, subagent 가 Read 도구로 read |
| 12 | dedup | hash 당 1 row (`listPendingCaptions` 가 자연 박제) |
| 14 | spot-check | 샘플 5장 중 ≥ 4장 의미 동등 |
| 16 | empty batch | exit 0 (no-op) |

### Step 1 — index + summary-fill (CLI + skill orchestration)

**Step 1-a — CLI 색인**:

```powershell
& "<cli-path>" index "<folder>" --skip-upload
```

- `--skip-upload` 박제 시 caption 자동 skip (`--force-without-image-caption` 자동 박제). 색인 자체는 정상 — `ImageCache` row + blob file 모두 박제.
- 본 step 종결 후 `<folder>/.lighthouse-kb/{index.db,blobs/images/<hash>.<ext>,summary.md}` 박제됨.
- 이 시점 `summary.md` = SummaryText NULL doc 들이 `PendingPlaceholder` ("(pending — summary-fill 미진행)") 로 박제. Step 1-b 가 박제된 summary 로 regenerate.

**Step 1-b — summary-fill (skill orchestration)**:

caption-fill 보다 비용 가벼움 (doc 단위 N 작음, text dump ≤ 512KB) → Step 1 안에서 자동 진행. caption-fill 은 별도 Step 2 로 분리 (N 큼).

1. **summary-pending fetch**:
   ```powershell
   & "<cli-path>" list-pending-summaries "<folder>"
   ```
   stdout JSON array (각 row = `{docId, originalPath, textDumpPath}` camelCase).

2. **threshold 분기** (N = array length):
   - **N = 0**: empty batch — Step 1-b skip (모든 doc 의 summary 박제됨).
   - **1 ≤ N ≤ 60**: 자동 dispatch. 잠정 K=2, P=4.
   - **N > 60**: confirm prompt — `Doc {N}개 summary. 예상 wall-clock {N/(K*P)*추정시간}분. 진행?`

3. **summary-prompt fetch** (1회):
   ```powershell
   & "<cli-path>" print-summary-prompt
   ```

4. **subagent dispatch** — pending array 를 K=2 단위로 chunking, P=4 parallel `Agent` 호출:
   - `subagent_type = "general-purpose"`.
   - 각 agent prompt 안 명시:
     - text dump file path = `<folder>/.lighthouse-kb/<textDumpPath>` — **Read 도구로 read 후 처리** (전문 본문 흡수).
     - summary prompt = Step 1-b-3 의 fetch 결과 그대로 복사.
     - 출력 형식: "summary 한 줄 (한국어 80~500자, 본문 분량 / 중요도에 비례) 만. 본문 echo 금지. 줄바꿈 금지. **마지막 줄은 단일 JSON line**: `{"docId":<int>,"summary":"..."}`".
   - `max_output_tokens=800` 가드 (한국어 500자 + JSON wrapper 여유). parse 실패 시 per-doc max 2 attempts retry.

5. **batch JSON 박제** — 모든 round 의 successful row 를 단일 array 로 모아 임시 파일 (e.g. `<folder>/.lighthouse-kb/summary-batch.json`).

6. **summary-update**:
   ```powershell
   & "<cli-path>" summary-update "<folder>" "<summary-batch.json>"
   ```
   단일 transaction 안 N 회 UPDATE → atomic commit + summary.md regenerate. empty batch 시 exit 0 no-op.

7. **잔여 확인** — `list-pending-summaries` 재호출이 빈 array 반환할 때만 Step 2 진입.

### Step 2 — caption-fill (skill orchestration)

1. **caption-pending fetch**:
   ```powershell
   & "<cli-path>" list-pending-captions "<folder>"
   ```
   stdout JSON array (각 row = `{hash, ext, refLocator, docPath}` camelCase).

2. **threshold 분기** (N = array length):
   - **N = 0**: empty batch — Step 2 자체 skip (이미 모든 image 의 caption 박제됨). Step 3 진행.
   - **1 ≤ N ≤ 120**: 자동 dispatch. N 이 작으면 (e.g. 1~K) subagent 1개로 충분 — parallel slot 미충족이라도 caption 생성 자체는 진행. dispatch 단위는 자연스럽게 `ceil(N/K)` subagents, P 까지 동시 spawn.
   - **N > 120**: confirm prompt — `이미지 {N}장. 예상 wall-clock {ceil(N/(K*P))*추정시간}분. 진행?`

3. **caption-prompt fetch** (1회):
   ```powershell
   & "<cli-path>" print-caption-prompt
   ```

4. **subagent dispatch** — pending array 를 K=4 단위로 chunking, P=4 parallel `Agent` 호출:
   - `subagent_type = "general-purpose"`.
   - 각 agent prompt 안 명시:
     - image file path = `<folder>/.lighthouse-kb/blobs/images/<hash>.<ext>` — **Read 도구로 read 후 처리** (base64 첨부 금지).
     - caption prompt = Step 2-3 의 fetch 결과 그대로 복사.
     - 출력 형식: "caption text 1~2 문장만. image 데이터 echo 금지. **마지막 줄은 단일 JSON line**:"
       ```json
       {"hash":"<sha256>","captionText":"...","captionModel":"claude-{model}-via-subagent"}
       ```
   - `max_output_tokens=300` 가드.
   - parse 실패 / 결함 row 는 재 enumerate (multi-round retry, per-image max 2 attempts).

5. **batch JSON 박제** — 모든 round 의 successful row 를 단일 array 로 모아 임시 파일 (e.g. `<folder>/.lighthouse-kb/caption-batch.json`).

6. **caption-update**:
   ```powershell
   & "<cli-path>" caption-update "<folder>" "<batch.json>"
   ```
   단일 transaction 안 N 회 UPDATE → atomic commit. empty batch 시 exit 0 no-op.

7. **잔여 확인** — `list-pending-captions` 재호출이 빈 array 반환할 때만 Step 3 진입 허용 (서버 측 caption-only patch endpoint 부재).

### Step 3 — upload (CLI)

```powershell
& "<cli-path>" index "<folder>" --upload "$env:LIGHTHOUSE_URL" --reuse-kb --title "<derived-title>"
```

- **`--reuse-kb` 박제 의무** — Step 1-a 가 생성한 `<folder>/.lighthouse-kb/` 산출물 (DB + caption + summary + text dump) 을 *wipe 없이* 그대로 zip + POST. 박제 시 `runUpload` 가 `resetKbDir` + `runIngest` 건너뛰고 곧장 `summarizeReuse` + `writeMeta` + `createZip` 진입.
- `--reuse-kb` 박제 시 captionGen / embedder 의존성 자체 dead — `LIGHTHOUSE_VLM_API_KEY` 미박제도 OK, `--force-without-image-caption` 자동 박제 의무 없음, `OLLAMA_FLASH_ATTENTION` precheck 도 skip.
- `--title` 미지정 시 server 가 `ArgumentException: title 필수` 로 거부 (exit 99). CLI 의 default fallback 은 multipart 단계까지 도달 못 함 — skill 측에서 폴더명을 명시 박제 의무.
- self-signed cert (default loopback URL 포함) 환경은 `--allow-invalid-certs` 자동 박제.

#### 이전 결함 박제 (2026-05-27 fix)

종전 본 step 호출은 `--reuse-kb` 부재로 `runUpload` 가 `Packager.resetKbDir` → `runIngest` 재수행 → DB 의 caption / summary 모두 wipe → 색인 결과의 text dump 가 `(caption 미생성)` + summary placeholder 로 박제됨 → server 측 collection 도 동일 상태 박제 결함. `--reuse-kb` 신설 (Program.fs `FlagReuseKb`, Packager.fs `summarizeReuse`) 로 wipe-free upload path 박제 + 본 SKILL Step 3 호출 갱신.

### concurrent SQLite writer 회피

subagent 는 SQLite 직접 접근 0. caption text + model 만 main (skill) 으로 return → `caption-update` 단일 process 가 `SqliteStore.openConnection` 단일 진입점 + BEGIN/COMMIT atomic.

### CaptionModel 식별자

subagent path 결과는 `ImageCache.CaptionModel = "claude-{model}-via-subagent"` (e.g. `claude-opus-4-7-via-subagent`). Anthropic direct path 의 model literal (`claude-sonnet-4-6` 등) 과 구분되어 동일 image 의 두 path caption 박제 시 source 식별 가능 (todo §2 #9).

## 산출물 보관 정책 (s6-r55+, 2026-05-21 정책 재정의)

CLI 가 **in-place 색인** — 색인 산출물은 `<folder>/.lighthouse-kb/` 폴더 1개에 보관:

```
<folder>/
  (사용자 원 파일들 …)
  .lighthouse-kb/        ← idempotent 재활용 (hash 기반) — `--skip-upload` 경로는 wipe 안 함
    meta.json
    index.db
    blobs/images/…
```

- **재활용 우선 (`--skip-upload` 경로는 시작 시 wipe 안 함)** — `.lighthouse-kb/` 를 *그대로 두고* idempotent 색인.
  - **파일 수준 fast-skip**: mtime/size match → hash 재계산 skip + 기존 docId 재활용 (`Indexer.fastSkipMatched`).
  - **hash 수준 skip**: 동일 hash 의 Document 이미 있으면 skip (`findDocumentByHash`).
  - **image caption 재활용**: `ImageCache.ImageHash` PK + `upsertImageCache` 가 `INSERT OR IGNORE` → 같은 hash 의
    image 가 다시 들어와도 기존 `CaptionText` / `CaptionModel` 절대 덮어쓰지 않음. caption 비용 재투입 0.
- **`--upload` 경로 wipe** — `Packager.resetKbDir` 가 marker / index.db 존재 시에만 wipe (사용자의 다른
  용도 동명 폴더 보호). zip 산출물의 결정성 보장 우선.
- **`--upload --reuse-kb` 경로는 wipe 안 함** — `<folder>/.lighthouse-kb/` 의 기존 산출물 (DB + caption + summary + text dump)
  을 그대로 zip + POST. /indexer skill 의 Step 3 default path 박제 — Step 1-b / Step 2 박제분 server 까지 전달 보장.
- **self-ingest 방지**: `Indexer.enumerateFiles` 가 `.lighthouse-kb/` 안 파일 (DB / blob / dump 등) 을 `GetFullPath`
  normalize 비교로 제외. 추가로 source top-level 의 caption cache (`.lighthouse-caption-cache.json`, `CaptionCache.CacheFileName`
  SSOT) 도 파일명 기준 제외 — `.lighthouse-kb/` 밖 sibling 이라 미제외 시 base64 caption dump 가 collection content 로 색인됨.
- **upload zip**: temp 위치에 만들고 업로드 완료/실패 시 즉시 정리. `.lighthouse-kb/` 는 source 안에 유지.
- **source write 권한 필수** — 부재 시 exit 11.
- **권장**: `<folder>` 가 git tree 안이면 `.gitignore` 에 `.lighthouse-kb/` 추가.

### 명시 wipe 가 필요한 경우

source 폴더에서 *파일이 삭제* 되었거나, 강제 재색인이 필요하면 사용자가 명시적으로 `<folder>/.lighthouse-kb/` 를
지우고 재실행. CLI 가 자동으로 stale row 청소는 안 함 (현 phase 한정 — 추후 selective cleanup 도입 가능).

### build-drift / stale 색인본 자동 복구 path (2026-05-28 fix)

CLI/lib build 가 갱신되어 기존 `.lighthouse-kb/` 의 schema/extension 정합이 무너진 경우 (`Meta.indexer_version`
키 부재 / 호환 range 밖 / sqlite-vec extension drift 로 open 실패), 종전 `--reuse-kb` upload 는 server 의
IndexerVersion gate (§3.12) 에서 거부. 본 phase 의 자동 복구 path:

1. **stale 감지** — `lighthouse-cli probe-indexer-version <folder>` 호출. stdout JSON `{"status":"ok|key-missing|open-failed|db-missing","version":"...","reason":"..."}`.
   - `ok` = 정상, Step 3 `--reuse-kb` 진입 가능.
   - `key-missing` / `open-failed` = stale, 아래 (2)~(5) 로 자동 복구.
   - `db-missing` = Step 1-a 미수행. `--skip-upload` 색인부터.
2. **caption 보존 export** — `lighthouse-cli export-image-cache <folder> > <temp>/caption-backup.json`.
   `key-missing` 경로는 DB open 정상 → caption 박제분 dump 가능. `open-failed` 경로는 dump 불가 → caption 비용 신규 발생 (불가피).
3. **wipe** — `<folder>/.lighthouse-kb/` 삭제 (사용자 명시 동의 후).
4. **재색인** — `lighthouse-cli index <folder> --skip-upload`. 새 build 의 `IndexerVersion.Current` stamp.
5. **caption 복원** — `lighthouse-cli import-image-cache <folder> <temp>/caption-backup.json`. 재색인이 새로 박제한
   ImageCache row 의 caption 4 컬럼만 UPDATE (`WHERE ImageHash = $hash AND CaptionText IS NULL` 정합 — 이미 caption
   박제된 row 는 보존). hash 매치 row 만 갱신, 미매치 hash 는 silent skip + 보고.
6. **upload** — `lighthouse-cli index <folder> --upload <url> --reuse-kb --title <name>`.

skill 측 자동 dispatch 박제 권장 — Step 3 진입 전 `probe-indexer-version` 호출, `key-missing`/`open-failed` 인 경우
사용자에게 확인 후 (2)~(5) 자동 실행. `open-failed` 의 경우 caption 비용 발생 사전 안내.

#### server 측 정합 응답 (2026-05-28 fix ①)

종전 server 는 stale 시 `{"error":"indexerVersion 미존재 — index.db 의 Meta.indexer_version 키 미존재"}` 로
원인 (open 실패 / 키 부재 / DB 부재) 을 압축 응답 → 운영자 진단 불가. 본 fix 이후 분리:
- HTTP 400 `indexerVersion key missing` — caption 보존 자동 path 안내 (suggestedAction).
- HTTP 400 `indexerVersion db missing` — zip 결함 / Step 1-a 미수행 안내.
- HTTP 400 `indexerVersion db open failed` — schema/extension drift, wipe + 신규 색인 안내.

## Exit code 해석 (사용자 친화 메시지)

CLI 의 exit code SSOT (`Program.fs` D-S6-4 박제) 를 다음 메시지로 변환하여 사용자에게 보고.

| code | 의미 | 사용자 메시지 |
|---|---|---|
| 0 | ok | "업로드 완료 — collectionId=…" (CLI stdout 그대로) |
| 1 | 401/403 | "PSK 검증 실패. `LIGHTHOUSE_PSK` 재확인." |
| 2 | 415 IndexerVersion mismatch | "IndexerVersion mismatch. CLI / Service 빌드 정합 확인 필요." |
| 3 | 413 zip size 초과 | "zip size 초과. 폴더 분할 또는 server 의 size 한도 확인." |
| 10 | 인자 오류 | "명령행 인자 오류. usage 재확인." |
| 11 | 폴더 미존재 | "폴더 미존재 — <folder>" |
| 12 | ingested=0 | "색인 대상 0건 (빈 폴더 또는 모두 unsupported extension)." |
| 13 | VLM API key 미박제 | "LIGHTHOUSE_VLM_API_KEY 박제 또는 `--force-without-image-caption` flag 추가." |
| 14 | OLLAMA_FLASH_ATTENTION 박제 미달 | "setx OLLAMA_FLASH_ATTENTION false 후 Ollama 재시작. 또는 `--no-embedding` 으로 BM25-only 색인." |
| 99 | 기타 | CLI stderr 마지막 줄 그대로 노출. |

## 사용 예

```
/indexer F:/Git/ds2/light-house/Apps/Promaker/Docs              # default URL = https://127.0.0.1:8443 (local service)
/indexer ./Apps/Promaker/Docs --title "Promaker Docs"           # 동일 default URL + title override
/indexer ./Apps/Promaker/Docs --url https://service.local:8443  # 외부 service 명시
```

## 비포함 (follow-up)

- `/indexer list` (`GET /collections`)
- `/indexer delete <id>` (`DELETE /collections/{id}`)
- `/indexer session` (`POST /sessions`)

본 phase 는 `index/upload` 만 박제. 위 sub-action 은 필요해질 때 별도 추가.
