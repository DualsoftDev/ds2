# /indexer skill — VLM caption 을 Claude Code subagent 로 위임

## 0. 목적

`/indexer` skill 의 VLM caption 생성을 Anthropic API 직접 호출 (`Vlm.buildCaptionGen`, env `LIGHTHOUSE_VLM_API_KEY`) 대신 Claude Code session 의 subagent 로 위임. API key 관리 불필요 + 비용 Claude Code subscription 으로 통합.

## 1. 채택안 — 옵션 B (deferred 2-phase) + parallel subagent

- **Phase 1**: CLI 가 caption=NULL 로 색인만. image bytes 는 이미 `<folder>/.lighthouse-kb/blobs/images/<sha256>.<ext>` 에 file 로 박제됨 (`ImageStore.saveBlob`, 변경 0).
- **Phase 2**: skill 이 caption-pending manifest 기반 subagent 병렬 dispatch → caption text collect.
- **Phase 3**: CLI 가 zip + upload.

Anthropic direct path 는 default 로 유지 — server / Promaker / CI 등 unattended caller 무영향. skill 한정 alternate.

## 2. 핵심 설계 결정 (사용자 confirm 의무)

| # | 항목 | 잠정값 | 비고 |
|---|---|---|---|
| 1 | subagent batch K (image 개수 / agent) | 8 | spawn overhead vs subagent context 부담 trade-off |
| 2 | parallel P (동시 spawn 수) | 5 | Anthropic 429 회피 + Claude Code dispatch 상한 |
| 3 | image volume threshold | 30 | 초과 시 사용자 confirm prompt |
| 4 | caption-prompt SSOT 위치 | `.claude/skills/indexer/caption-prompt.md` 분리 | parent §3.15.5 MR1 정합 |
| 5 | subagent_type | general-purpose vs 신규 `image-captioner` agent | 후자 = 깨끗하나 정의 의무 |
| 6 | CLI sub-command 분리 범위 | `index --skip-upload` flag 1개 (최소) vs 3 sub-command (`index-only` / `caption-update --batch` / `upload`) | 후자 권장 — 구조 명확 |

## 3. 변경 범위

### CLI (`Solutions/Tools/Ds2.LightHouse.Cli/`)
- `Program.fs` — phase 분리 sub-command 추가 (또는 `--skip-upload` flag).
- 신규 `runCaptionUpdate` — `<batch.json>` 입력 → SQLite `ImageCache.CaptionText` UPDATE + `captions-pending.json` 갱신.
- `Packager.fs` — phase 1 종결 시 `<folder>/.lighthouse-kb/captions-pending.json` 박제 (caption=NULL row 만, `[{hash, ext, refLocator, docPath}, ...]`).

### lib (`Solutions/Core/Ds2.LightHouse/`)
- **변경 0**. `ImageStore.saveBlob` + `ImageCache.CaptionText NULL` 박제 그대로 사용. captionGen surface 도 noop 으로 호출 (phase 1).

### skill (`.claude/skills/indexer/SKILL.md`)
- Phase 1/2/3 흐름 박제.
- caption-pending.json Read → K 단위 chunk → `Agent` 도구 multiple call (parallel P) → result collect → `caption-update --batch` 호출 → upload.
- volume threshold prompt.
- caption-prompt 별 파일 참조.

## 4. 주의 / risk

- **concurrent SQLite writer 회피** — subagent 들이 직접 UPDATE 안 함. caption text 만 main 으로 return → main 이 batch UPDATE 1회.
- **token cost = Anthropic direct 와 동등** — parallel 은 wall-clock 만 단축. 비용 절감 아님.
- **subagent caption-prompt drift** — lib default Vlm.fs 의 system prompt 와 동일 의미 박제 의무. 단순 "이미지 설명" 는 quality 저하.
- **부분 실패 idempotent** — manifest 잔존 row 만 재시도. NULL row retry 자연 흡수.
- **upload 이미 된 collection 의 caption 패치** — server-side caption-only patch API 부재. 처음부터 caption 완료 후 1회 upload 가 정합 (재upload 회피).
- **자가 검열 trigger** — CLI sub-command 분리 + Packager helper 신설 = trigger ② / ③ 충족. 구현 phase 진입 시 sub-agent 검열 의무.

## 5. 진입 순서 (구현 시점)

1. 사용자 confirm — §2 의 1~6 결정.
2. CLI 변경 (sub-command 분리 + caption-pending manifest 박제 + caption-update).
3. caption-prompt SSOT 작성 (별 파일).
4. SKILL.md 갱신 (phase 1/2/3 흐름).
5. 소형 폴더 (image 5~10장) 로 end-to-end 검증.
6. 자가 검열 sub-agent + commit.

## 6. 관련 파일

- `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` — `runIndex` / `runUpload` 분기.
- `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` — phase 1 종결 + manifest 박제.
- `Solutions/Tools/Ds2.LightHouse.Cli/Vlm.fs` — Anthropic direct path (default 유지).
- `Solutions/Core/Ds2.LightHouse/ImageStore.fs` — blob 저장 경로 SSOT (`blobs/images/<sha256>.<ext>`).
- `Solutions/Core/Ds2.LightHouse/Indexer.fs` — `captionGen` caller 주입 surface.
- `.claude/skills/indexer/SKILL.md` — phase 흐름 박제.
- `Apps/Promaker/Docs/todo-lighthouse-kb-index.md` — parent SSOT (§3.15.5 MR1/MR2 정합).
