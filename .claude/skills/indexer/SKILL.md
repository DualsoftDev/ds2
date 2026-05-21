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

## 산출물 보관 정책 (s6-r55+)

CLI 가 **in-place 색인** — 색인 산출물은 `<folder>/.lighthouse-kb/` 폴더 1개에 보관:

```
<folder>/
  (사용자 원 파일들 …)
  .lighthouse-kb/        ← 색인 시작 전 wipe + 색인 후 보관 (관리 단위 = 이 폴더)
    meta.json
    index.db
```

- **시작 전**: 이전 색인의 `<folder>/.lighthouse-kb/` 통째 wipe → 새 색인 박제.
- **색인 후**: 보관. 다음 `/indexer` 호출 시점에 다시 wipe.
- **upload zip**: temp 위치에 만들고 업로드 완료/실패 시 즉시 정리. `.lighthouse-kb/` 는 source 안에 유지.
- **source write 권한 필수** — 부재 시 exit 11.
- **권장**: `<folder>` 가 git tree 안이면 `.gitignore` 에 `.lighthouse-kb/` 추가.

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
