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
/indexer                                           # 현재 working directory 색인
/indexer <folder>                                  # 지정 폴더 색인
/indexer <folder> --title <name>                   # collection 표시 이름 override (생략 시 폴더명)
/indexer <folder> --url <baseUrl>                  # LIGHTHOUSE_URL env var override
```

## 사전 조건

### env var (필수)
- `LIGHTHOUSE_URL` — service base URL (e.g. `https://service.local:8443`). 명령행 `--url` 로 override 가능.
- `LIGHTHOUSE_PSK` — 평문 PSK. CLI 가 직접 읽음.

둘 중 하나라도 미설정 시 사용자에게 일반 텍스트로 물어보고 진행. 응답값을 그 turn 의 `$env:LIGHTHOUSE_URL` / `$env:LIGHTHOUSE_PSK` 로 박제하여 호출.

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

## 실행 절차

```powershell
& "<cli-path>" index "<folder>" --upload "$env:LIGHTHOUSE_URL" --title "<derived-title>"
```

- PSK 는 env var `LIGHTHOUSE_PSK` 로 CLI 가 자동 흡수 (명령행 노출 안 함).
- `--title` 미지정 시 CLI 가 자동으로 폴더명 사용 (skill 에서 굳이 derive 안 함).
- stderr 의 진행률 (`[N%] x/y — file`) 그대로 사용자에게 전달.
- self-signed cert 환경이면 `--allow-invalid-certs` 추가 (dev only).

## Step 1/2/3 흐름 (subagent caption path)

`/indexer` 가 **이미지 다수 포함** 폴더 색인 시 VLM caption 을 Anthropic API 직접 (`LIGHTHOUSE_VLM_API_KEY`) 대신 Claude Code subagent 로 위임하여 비용을 Claude Code subscription 으로 통합하는 path. `Apps/Promaker/Docs/todo-lighthouse-indexer-claude-caption.md` 의 채택안 (옵션 B, deferred 2-step + parallel subagent) 박제.

### 결정 박제 (todo §2)

| # | 값 | 설명 |
|---|---|---|
| 1 | K=4 | subagent 1개당 image 개수 (tier 1 ITPM 안전 마진) |
| 2 | P=4 | parallel spawn 수 |
| 3 | lower=20 | N<20 시 본 path skip → 사용자에게 `LIGHTHOUSE_VLM_API_KEY` 박제 + Anthropic direct fallback 안내 |
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

### Step 1 — index-only (CLI)

```powershell
& "<cli-path>" index "<folder>" --skip-upload
```

- `--skip-upload` 박제 시 caption 자동 skip (`--force-without-image-caption` 자동 박제). 색인 자체는 정상 — `ImageCache` row + blob file 모두 박제.
- 본 step 종결 후 `<folder>/.lighthouse-kb/{index.db,blobs/images/<hash>.<ext>}` 박제됨.

### Step 2 — caption-fill (skill orchestration)

1. **caption-pending fetch**:
   ```powershell
   & "<cli-path>" list-pending-captions "<folder>"
   ```
   stdout JSON array (각 row = `{hash, ext, refLocator, docPath}` camelCase).

2. **threshold 분기** (N = array length):
   - **N < 20**: 본 path skip. 사용자에게 안내:
     ```
     image {N}장 — subagent dispatch overhead 임계 미달. 다음 중 하나 선택:
       (a) LIGHTHOUSE_VLM_API_KEY 박제 후 'lighthouse-cli index <folder> --upload <url>' 으로 Anthropic direct path.
       (b) caption 없이 진행 — Step 3 만 직접 호출 (image 검색 fallback 없음).
     ```
   - **20 ≤ N ≤ 120**: 자동 dispatch.
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
& "<cli-path>" index "<folder>" --upload "$env:LIGHTHOUSE_URL" --title "<derived-title>"
```

- 이 시점 `LIGHTHOUSE_VLM_API_KEY` 미박제여도 caption 이 이미 박제됨 → `--force-without-image-caption` 자동 박제 의무. 또는 별 entry (follow-up) 로 upload-only path 박제.
- **임시**: Step 1 산출물 wipe 회피 위해, `runUpload` 가 in-place 색인을 재수행하지 않도록 별 upload-only entry 가 필요 (follow-up phase). 현재는 Step 3 진입 시 `LIGHTHOUSE_VLM_API_KEY` 가 없으면 `--force-without-image-caption` 으로 호출하여 재색인 시 caption noop → 기존 caption 보존 (DB 이미 박제).

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
- **`--upload` 경로는 여전히 wipe** — `Packager.resetKbDir` 가 marker / index.db 존재 시에만 wipe (사용자의 다른
  용도 동명 폴더 보호). zip 산출물의 결정성 보장 우선.
- **self-ingest 방지**: `Indexer.enumerateFiles` 가 `.lighthouse-kb/` 안 파일 (DB / blob / dump 등) 을 `GetFullPath`
  normalize 비교로 제외.
- **upload zip**: temp 위치에 만들고 업로드 완료/실패 시 즉시 정리. `.lighthouse-kb/` 는 source 안에 유지.
- **source write 권한 필수** — 부재 시 exit 11.
- **권장**: `<folder>` 가 git tree 안이면 `.gitignore` 에 `.lighthouse-kb/` 추가.

### 명시 wipe 가 필요한 경우

source 폴더에서 *파일이 삭제* 되었거나, 강제 재색인이 필요하면 사용자가 명시적으로 `<folder>/.lighthouse-kb/` 를
지우고 재실행. CLI 가 자동으로 stale row 청소는 안 함 (현 phase 한정 — 추후 selective cleanup 도입 가능).

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
/indexer F:/Git/ds2/light-house/Apps/Promaker/Docs
/indexer ./Apps/Promaker/Docs --title "Promaker Docs"
/indexer ./Apps/Promaker/Docs --url https://service.local:8443
```

## 비포함 (follow-up)

- `/indexer list` (`GET /collections`)
- `/indexer delete <id>` (`DELETE /collections/{id}`)
- `/indexer session` (`POST /sessions`)

본 phase 는 `index/upload` 만 박제. 위 sub-action 은 필요해질 때 별도 추가.
