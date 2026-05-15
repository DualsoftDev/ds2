---
name: dist
description: Promaker installer 배포 자동화 — 빌드 + ReleaseNote 누적 + commit + zip + scp + tag + bump + push 를 한 번에. `make dist` 의 상위 워크플로.
---

# Promaker `/dist` skill

Promaker installer 의 정식 배포 진입점. `make dist` (scp + bump 만) 에
**ReleaseNote 누적 + commit + zip 패키징 + tag + remote push** 를 얹은
Claude 전용 워크플로. `scripts/dist-common.mk` 의 공용 mk 자체는 손대지 않고
이 skill 안에서만 처리한다.

배포 산출물은 **zip** 이다 (MS Edge 등 브라우저의 `.exe` 다운로드 차단 회피).
zip 안에는 installer `.exe` + `ReleaseNote.txt` 가 들어간다.

**ReleaseNote 는 누적 관리**: `installer/Apps/Promaker/ReleaseNote.txt` 가
git 추적 대상이며, 매 `/dist` 마다 파일 맨 위에 이번 배포 entry 가
**prepend** 된다. 과거 entry 는 그대로 보존되어 전체 릴리즈 히스토리가
한 파일에 누적된다. zip 에는 이 누적 파일 전체가 복사된다.

## 관련 파일 / 경로

- Version file: `installer/Apps/Promaker/BuildVersion.txt`
- ReleaseNote (git 관리): `installer/Apps/Promaker/ReleaseNote.txt` — 헤더 `# Promaker Release Notes` + 누적 entry
- Installer 빌드: `make -C installer/Apps/Promaker dist-installer` (bump 없이 `.exe` 만 생성, MODE=sc default)
- Installer 산출물: `installer/Apps/Promaker/Installer/Output/Promaker_Setup_<VER>_sc.exe`
- ReleaseNote 업데이트: `installer/Apps/Promaker/scripts/update-releasenote.sh <CUR_VER> <COMMIT_MSG_FILE>` — commit **전** 호출
- Zip 패키징: `installer/Apps/Promaker/scripts/package-release.sh <CUR_VER> <exe>` — commit **후** 호출, stdout 마지막 줄에 zip 경로
- Zip 산출물: `installer/Apps/Promaker/Installer/Output/Promaker_Setup_<VER>_sc.zip`
- SCP: `installer/Apps/Promaker/scripts/scp-installer.sh <zip> <DEST>` (zip 만 업로드, exe 는 로컬 보존)
- Bump: `installer/Apps/Promaker/scripts/bump-buildversion.sh <BuildVersion.txt>` — 마지막 슬롯 +1
- SCP 기본 대상: `download@dualsoft.co.kr:/media/download/Dualsoft Software/Setup ProMaker(DS2_ProMaker) - Windows x64`

## 수행 순서 (`/dist`) — A안: commit 을 scp 보다 먼저

1. **CUR_VER 저장**: `CUR_VER=$(tr -d '[:space:]' < installer/Apps/Promaker/BuildVersion.txt)` — bump 이전 버전 값.
2. **Tag 사전 점검** — 다음 둘 중 하나라도 `vPromaker_$CUR_VER` 존재 시 에러 출력 후 **전체 중단**:
   - 우선 `git fetch --tags --prune origin` 으로 remote tag 캐시 갱신 (다른 머신에서 push 된 tag 가 stale 로 누락되는 것 방지).
   - Local: `git rev-parse --verify refs/tags/vPromaker_$CUR_VER` 성공
   - Remote: `git ls-remote --tags origin "refs/tags/vPromaker_$CUR_VER"` non-empty
3. **빌드**: `make -C installer/Apps/Promaker dist-installer` — Promaker installer `.exe` 생성. 실패 시 중단.
4. **Pull**: `git pull --ff-only` — upstream 존재 시 (`git rev-parse --abbrev-ref --symbolic-full-name @{u}` 로 판정). 실패 시 중단.
5. **배포 대상 변경 유무 사전 검사** — ReleaseNote prepend **이전** 에 수행 (검사를 prepend 뒤에 두면 ReleaseNote 변경 자체가 항상 non-empty 로 잡혀 검사가 사문화됨):
   - `PREV_TAG=$(git describe --tags --match 'vPromaker_*' --abbrev=0 HEAD 2>/dev/null || true)`
   - (a) tracked working tree 수정 유무 (BuildVersion.txt 제외 — bump 후 남은 잔재이므로 제외):
     `git diff --name-only -- . ':(exclude)installer/Apps/Promaker/BuildVersion.txt'` 결과가 비어있으면 a=0
   - (b) `PREV_TAG` 이후 새 commit 유무:
     `PREV_TAG` 존재 시 `git rev-list "${PREV_TAG}..HEAD" --count`, 없으면 initial release 로 간주해 통과
   - (a) 와 (b) 모두 0 이면 "dist 대상 변경사항 없음 (이전 tag <PREV_TAG> 이후 tracked 변경 없음)" 에러 출력 후 **종료**.
6. **Commit message 준비**: Claude 가 이번 배포 commit msg 전문을 작성하여 임시 파일에 저장. 이 파일이 **commit msg + ReleaseNote entry 본문** 양쪽에 그대로 투입되므로, **사용자 관점에서 의미 있는 변경만** 선별해 itemize 해야 한다.
   - `MSGFILE=$(mktemp -t dist-msg-XXXXXX)` — **이 파일 내용 작성은 반드시 Bash heredoc (`cat > "$MSGFILE" <<'MSGEOF' ... MSGEOF`) 으로 할 것**. Claude Code `Write` 도구는 Windows 에서 `/tmp/...` 경로를 Git Bash tmp 와 다른 위치로 해석해 0 바이트 파일이 생성될 수 있음.
   - 참조 소스: `git diff --stat` (working tree 변경), `git log <PREV_TAG>..HEAD --oneline` (이전 배포 이후 커밋 전체)
   - 권장 Summary: `Promaker v<CUR_VER> 배포`
   - **포함 대상 (user-facing)**: 새로운 기능 / UI 또는 동작 변경 / 버그 수정 (사용자가 체감하는 것) / 설정 / 파일 경로 / 호환성 파괴 / 설치 절차 변경 등
   - **제외 대상 (구현 세부사항)**: 내부 리팩토링 / 변수 rename / 주석·docstring / 테스트 추가/수정 / 빌드 스크립트 / CI / typo / 로그 포맷 조정 / 함수 시그니처 정리 등 — 여러 commit 이 같은 사용자 대상 개선이면 **한 줄로 merge**
   - **정렬 원칙**: 시간 순이 아니라 **카테고리(= 기능 영역) 별로 그룹핑**. 동일 카테고리(e.g. UI / LLM Chat / Convert / Runtime / 3D View / installer / SDF 파일 / Dock layout 등) 의 user-facing 변경이 **2건 이상** 이면 해당 카테고리를 상위 bullet 로 두고 변경사항을 sub-bullet (2-space indent) 으로 nest. 1건뿐인 카테고리는 nest 없이 flat bullet.
   - 구조:
     ```
     <Summary>                              ← 1줄
                                            ← blank
     - <카테고리 A>                          ← 2건 이상이면 상위 bullet
       - <change A-1>                       ← 2-space indent sub-bullet
       - <change A-2>
     - <single-change category>             ← 1건이면 flat bullet
     - <카테고리 B>
       - <change B-1>
       - <change B-2>
     ```
   - 카테고리 분류는 **사용자 관점** 기준 — 동일 GUI 화면 / 동일 기능 영역을 한 그룹으로. 내부 디렉터리 구조를 그대로 옮기지 말 것.
   - 선별 결과가 사소한 것 1~2 줄 뿐이어도 OK. 반대로 여러 commit 이 큰 기능 하나로 묶이면 itemize 1 줄로 압축.
   - Co-Authored-By 미기입 (글로벌 `--git-commit` 규칙 상속).
7. **ReleaseNote prepend**: `bash installer/Apps/Promaker/scripts/update-releasenote.sh "$CUR_VER" "$MSGFILE"`
   - 스크립트 내부: `git describe --tags --match 'vPromaker_*' --abbrev=0 HEAD` 로 PREV_TAG 탐색 → 새 entry = [구분선 + `[v<CUR_VER>] <timestamp>` + `Previous: <PREV_TAG>` + **MSGFILE 내용 전문**] → 파일 맨 위(헤더 바로 아래)에 prepend.
   - 스크립트는 `git log` 를 **자동 첨부하지 않는다** — 선별 책임은 Step 6 의 Claude.
   - 실패 시 중단. 복구는 `git checkout -- installer/Apps/Promaker/ReleaseNote.txt` 로 충분.
8. **Staging**:
   - `git add -u` — tracked 수정분만 (untracked 제외)
   - `git add installer/Apps/Promaker/BuildVersion.txt` — bump 전 상태 명시 스테이징
   - `git add installer/Apps/Promaker/ReleaseNote.txt` — 방금 prepend 한 변경
9. **Commit**: `git commit -F "$MSGFILE"` — step 6 에서 만든 msg 파일을 그대로 사용 → commit msg 와 ReleaseNote entry 본문이 동기화. 이 commit 에 이후 `vPromaker_$CUR_VER` tag 가 붙는다.
10. **Zip 패키징**: `ZIP=$(bash installer/Apps/Promaker/scripts/package-release.sh "$CUR_VER" "<installer.exe>")`
    - 스크립트는 이미 업데이트된 `ReleaseNote.txt` 와 `installer.exe` 를 `Promaker_Setup_<CUR_VER>_sc.zip` 으로 묶음.
    - zip 툴: `zip` 우선, 없으면 PowerShell `Compress-Archive` fallback.
    - 실패 시 롤백 (아래 롤백 표 참조).
11. **SCP**: `bash installer/Apps/Promaker/scripts/scp-installer.sh "$ZIP" "<DEST>"` — zip 업로드 (exe 아님).
    - 실패 시 롤백 필수. tag 는 아직 없으므로 추가 정리 불필요.
12. **Tag**: `git tag vPromaker_$CUR_VER HEAD` — lightweight tag. 강제 덮어쓰기(`-f`) 금지.
13. **Bump**: `bash installer/Apps/Promaker/scripts/bump-buildversion.sh installer/Apps/Promaker/BuildVersion.txt` — 마지막 슬롯 +1. bump 후 working tree 는 uncommitted 상태로 남으며 다음 `/dist` 의 commit 에 자연스럽게 흡수된다.
14. **Push** — upstream 존재 시에만:
    - `git push` — 실패 시 경고만 출력하고 계속
    - `git push origin "vPromaker_$CUR_VER"` — 실패 시 경고만
15. **정리**: `rm -f "$MSGFILE"`

## Dry run (`/dist dry`)

Destructive 스텝(빌드 / ReleaseNote 파일 수정 / commit / zip / scp / tag / bump / push)을 **전부 스킵**하고 프리뷰만 출력:
- 현재 `CUR_VER` 와 bump 후 예상 버전 (마지막 슬롯 +1 계산)
- 예상 tag 이름 (`vPromaker_$CUR_VER`) + local/remote 충돌 사전 점검 결과
- **배포 대상 변경 유무 사전 검사 결과**: Step 5 의 판정 (tracked 수정 / PREV_TAG 이후 새 commit 유무) 을 그대로 보고
- `git status -s` / `git diff --stat` 로 이번 commit 예정 파일 목록
- 예상 commit message 초안 (실제 포맷 그대로)
- 예상 zip 파일명 (`Promaker_Setup_<CUR_VER>_sc.zip`)
- **ReleaseNote entry 프리뷰**: Claude 가 `PREV_TAG..HEAD` 의 log 를 검토해 **사용자 관점 선별 후** 작성한 예상 commit msg 를 그대로 표시. 추가로 참고용으로 **선별 전 전체 git log** 도 별도 섹션으로 출력.
- 실행될 명령 리스트 — 실행 없음 명시

## 롤백 시나리오 요약

**중요 원칙**: `git reset --mixed HEAD^` 는 index 만 리셋하고 working tree 는 유지하므로, ReleaseNote.txt 의 prepend 된 entry 가 남아있다. 재시도 시 Step 7 (update-releasenote.sh) 가 **같은 entry 를 또 prepend → 중복 누적**. 따라서 commit 을 롤백할 때는 `--mixed HEAD^` 와 **반드시 함께** `git checkout -- installer/Apps/Promaker/ReleaseNote.txt` 를 실행해 working tree 의 ReleaseNote 도 이전 상태로 되돌려야 한다.

| 실패 시점 | 남은 상태 | 복구 절차 |
|---|---|---|
| 1~6 (commit msg 준비까지) | 변경 없음 | 재시도 |
| 7 ReleaseNote prepend 실패 | ReleaseNote.txt 만 부분 수정 가능 | `git checkout -- installer/Apps/Promaker/ReleaseNote.txt` → 재시도 |
| 8 staging 실패 | Staging 일부 존재 + ReleaseNote prepend 됨 | `git reset HEAD`<br>`git checkout -- installer/Apps/Promaker/ReleaseNote.txt` → 재시도 |
| 9 commit 실패 | Staging 존재, commit 없음, ReleaseNote prepend 됨 | `git reset HEAD`<br>`git checkout -- installer/Apps/Promaker/ReleaseNote.txt` → 재시도 |
| 10 zip 생성 실패 | commit 남음 + ReleaseNote 파일 변경 포함된 상태 | `git reset --mixed HEAD^`<br>`git checkout -- installer/Apps/Promaker/ReleaseNote.txt` → 원인 수정 → 재시도 |
| 11 scp 실패 | commit + 로컬 zip + ReleaseNote 변경 | `git reset --mixed HEAD^`<br>`git checkout -- installer/Apps/Promaker/ReleaseNote.txt` → 재시도 (로컬 zip 은 다음 실행에서 덮어써짐) |
| 12 tag 실패 | commit + zip 업로드 완료 | tag 만 수동 생성 후 13~14 이어서 진행 (여기부터는 롤백 금지 — 이미 scp 됨) |
| 13 bump 실패 | 이례적 — 파일 쓰기 실패 | 디스크/권한 확인 후 수동 bump |
| 14 push 실패 | commit + tag 로컬에만 존재 | 경고 메시지대로 무시하거나 수동 재 push |

## 제약 / 주의

- **`make dist` 는 notice-only 로 비활성화** — 우발적 배포 방지를 위해 Makefile 이 notice 메시지만 출력하고 종료한다. 강제로 legacy 동작 (scp + bump, ReleaseNote/commit/tag/push 없음) 이 필요하면 `make dist-force` 사용. 정식 배포는 반드시 본 `/dist` skill 사용.
- `/dist` 는 **Promaker 단독 배포 전용**. ds2 의 다른 컴포넌트(DSPilot 등) 가 추후 같은 구조를 도입하더라도 각자의 `/dist` 를 갖는다.
- **fd 모드는 `/dist` 자동 배포 대상이 아님** — 필요 시 `MODE=fd make -C installer/Apps/Promaker dist-installer` 로 수동 빌드만 한다 (산출물은 로컬 보존, scp 대상은 sc).
- Tag 충돌 시 강제 덮어쓰기(`git tag -f`, `git push --force`) 금지 — 히스토리 훼손 방지.
- `git add -A` 금지 — 빌드 산출물 / 로그 유입 위험. `-u` 고정 (+ BuildVersion.txt / ReleaseNote.txt 명시).
- 글로벌 `--git-commit` 규칙 상속: `git pull --ff-only` 선행, upstream 있을 때만 push, Co-Authored-By 미기입, commit 이전 pull 실패 시 중단.
- `ReleaseNote.txt` 는 **UTF-8 (no BOM)** 로 관리. `update-releasenote.sh` 는 Bash heredoc/cat 으로 작성하여 기본 UTF-8 유지.
- `update-releasenote.sh` 는 `git describe ... HEAD` (HEAD 기준) 를, `package-release.sh` 는 HEAD 기준 파일만 묶음 — 순서 역전 금지. 반드시 **5(pre-check) → 6(msg) → 7(update-releasenote) → 8~9(add/commit) → 10(package) → 11(scp)** 순서.
- commit msg 파일(`$MSGFILE`) 과 ReleaseNote entry 본문은 **같은 텍스트** 여야 함 (단계 6 에서 만든 파일이 단계 7 과 9 에 모두 투입). Claude 가 중간에 수정하지 말 것.
- **MSGFILE 등 `/dist` 임시 파일(`/tmp/...`) 작성은 반드시 Bash heredoc 으로 할 것** — Claude Code 의 `Write` 도구는 Windows 환경에서 `/tmp/...` 경로를 Git Bash 의 tmp (`C:\Users\<user>\AppData\Local\Temp\`) 와 **다른 위치**(드라이브 루트의 `\tmp\` 등) 로 해석하는 경우가 있어, `Write` 로 만들면 후속 Bash 스크립트가 0 바이트 파일을 읽게 됨. `cat > "$MSGFILE" <<'MSGEOF' ... MSGEOF` 패턴 사용.
- **롤백 시 반드시 `git checkout -- installer/Apps/Promaker/ReleaseNote.txt` 병행** — `git reset --mixed HEAD^` 는 working tree 를 건드리지 않으므로 ReleaseNote 의 prepend 된 entry 가 남아 재시도 시 중복 누적된다.
