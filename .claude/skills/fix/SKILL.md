---
name: fix
description: Fetch OPEN issues from a GitLab project and auto-resolve each one inside an isolated git worktree of the fixed ds2 code repo. Builds only the project touched by the fix, commits locally (never push/merge), and classifies every issue into fix-state.json as resolved / needs_review / unsolvable. Use when the user runs /fix [gitlab-project-path] [--issue N] [--concurrency N] [--max N]. For periodic runs the user wraps it with /loop <interval> /fix ... (the skill itself stays single-shot).
---

# /fix — GitLab open issue 자동 처리

GitLab 의 **open issue** 를 가져와, ds2 코드 repo 의 **격리된 worktree** 에서 issue 별로 자동 수정·검증·커밋하고, 처리 결과를 `fix-state.json` 에 분류 기록한다.

- **1회 실행 = 신규 open issue 1회 처리(멱등)**. 이미 처리된 issue 는 건드리지 않는다.
- **주기 실행은 이 skill 에 넣지 않는다.** 사용자가 `/loop <interval> /fix ...` 로 감싸면 주기화된다.

## 0. 경로 / 인자

| 항목 | 값 |
|---|---|
| 코드 repo 루트 `<REPO>` | **경로 하드코딩 금지** — skill 실행 위치(auto-fix worktree)에서 `git rev-parse --git-common-dir` 의 절대경로의 **부모**(= `.bare` 의 부모)로 동적 도출. 기준 worktree = `<REPO>/main` |
| 작업 루트 | `<REPO>/auto-fix` (skill·실행 위치) |
| 상태 대장 (SSOT) | `<REPO>/fix-state.json` (모든 worktree 공유, git 비추적) |
| issue worktree | `<REPO>/fix-<iid>` (`.bare` 와 동급, auto-fix worktree 밖) |
| 보조 스크립트 | `<REPO>/auto-fix/.claude/skills/fix/scripts/` |

**인자 파싱** (`$ARGUMENTS` 에서 메인이 분리):
- 위치 인자 = GitLab project path (예: `dualsoft/helpds`). 생략 시 `fix-state.json` 의 `issueRepo`, 그것도 없으면 `dualsoft/helpds`.
- `--issue <iid[,iid...]>` = **특정 issue 만** 처리. → gitlab-issues.ps1 의 `-Iids` 로 전달. 지정 시 전체 스캔 안 함, **assignee·처리 이력 무관 강제 처리**, `--max` 미적용.
- `--concurrency N` = 동시 subagent 수 (기본 **5**).
- `--max N` = 이번 회차 처리 상한 (기본 **20**, 폭주 방지).

모든 경로는 동적 도출한 `<REPO>` 절대값을 앞에 붙여 쓴다. **짧은 상대경로(`fix-<iid>` 단독)를 git 명령 path 인자로 주지 말 것** — `git -C` 가 아니라 프로세스 cwd 기준으로 풀려 엉뚱한 위치에 생성된다.

## 1. PAT 확보 (prerequisite)

보조 스크립트가 다음 순서로 PAT 를 찾는다. **전부 없으면 중단하고 사용자에게 요청**:
1. 환경변수 `GITLAB_TOKEN`
2. 파일 `<REPO>/.pat`
3. 없음 → "PAT 없음: GITLAB_TOKEN 또는 <REPO>/.pat (scope read_api) 필요" 출력 후 종료.

## 2. issue 조회 + 선별

> **GitLab 호스트 / 조회 방법 (반드시 준수)**: GitLab 인스턴스는 자체 호스팅 `http://dualsoft.co.kr:8081/api/v4` 이다. **호스트 값의 SSOT 는 아래 `gitlab-issues.ps1` 의 `-GitLabBase` 기본값**(스크립트 param 절) — 값이 바뀌면 스크립트가 기준이고 본 문서 병기값은 참고용. issue 데이터(title/description/labels/web_url)가 필요하면 **단건 본문 확인이라도 반드시 이 스크립트를 통해** 가져온다. `gitlab.com` 등 **임의 호스트로 직접 REST 호출 금지**(잘못된 호스트로 401). 특정 iid 만 빠르게 보려면 `-Iids <iid[,iid...]>` 를 쓰되, **PowerShell 5.1 에서는 호출 방식별 콤마 해석 차이를 피하기 위해 따옴표를 권장**: `-Iids "154,149"`.

기본(전체 스캔):
```
powershell -NoProfile -File .claude/skills/fix/scripts/gitlab-issues.ps1 -ProjectPath <path>
```
특정 issue 지정(`--issue` 가 있을 때):
```
powershell -NoProfile -File .claude/skills/fix/scripts/gitlab-issues.ps1 -ProjectPath <path> -Iids <iid[,iid...]>
```
- **기본 모드**: open issue 중 ① 이미 처리된(`resolved`/`unsolvable`/`needs_review`) iid, ② **assignee 가 할당된 issue** 를 제외한 신규만 반환.
- **특정 모드(`-Iids`)**: 지정 iid 만. assignee·처리 이력 무관 강제 처리(닫힌 closed issue 포함).
- 결과 JSON: `{ projectPath, mode, total, newCount, issues:[{ iid, title, description, labels, issue_type, web_url, notes:[{author,created_at,body}], attachments:[{secret,filename,markdownPath,apiUrl}] }] }`
- `newCount == 0` → "처리할 issue 없음" 출력 후 종료.
- 스크립트는 `--max` 를 적용하지 않는다(전량 반환). 상한 적용은 메인의 책임(3절).

> **본문(description) 없음 ≠ 정보 없음 (필수 — #191 류 재발 방지)**: GitLab issue 는 본문이 비어도 **댓글(notes)·첨부 이미지**에 핵심 요구·합의가 있는 경우가 많다(실제 #191 "속성버튼 중복" 은 본문 null, 댓글+이미지에서 작업 대상·합의가 결정됨). 따라서:
> - `gitlab-issues.ps1` 은 위 notes/attachments 를 함께 반환한다(특정 모드 항상 / 전체 모드는 `-IncludeNotes` 또는 본문 빈 issue 자동). **description 만 보고 `unsolvable`/`needs_review` 판정 금지** — notes 와 첨부 이미지를 먼저 확인한다.
> - **첨부 이미지는 메인이 직접 본다**: 각 `attachments[].apiUrl` 을 아래 스크립트로 받아 로컬 경로를 얻고 **Read 도구로 열어** 화면/대상을 눈으로 확인한 뒤 판단·지시한다.
>   ```
>   powershell -NoProfile -File .claude/skills/fix/scripts/gitlab-fetch-upload.ps1 -ApiUrl "<attachments[].apiUrl>"
>   ```
>   ⚠️ uploads 를 **web 경로**(`http://<host>/<ns>/<proj>/uploads/..`)로 받으면 `sign_in` 으로 리다이렉트되어 106 byte HTML 만 떨어진다. 반드시 **API 엔드포인트**(`/api/v4/projects/:id/uploads/:secret/:filename` = `apiUrl`)로 받을 것. 위 스크립트가 이 함정과 PAT/리다이렉트 검증을 캡슐화한다.

## 3. 각 issue 병렬 처리 (최대 N개 subagent)

**메인(orchestrator)이 회차 시작 시 1회 수행:**
1. `<REPO>` 도출(0절). skill 실행 위치에서 `git rev-parse --git-common-dir` 의 절대경로의 부모.
2. **fetch 1회**: `git -C <REPO> fetch origin` (subagent 가 각자 fetch 하지 않게 메인이 한 번만 — `.bare` 공유 ref 경합 방지).
3. 2절 신규 목록을 iid 오름차순 정렬 → **기본 모드는 `--max` 개까지만** 슬라이싱(잘린 건수는 5절에 명시, 조용히 누락 금지). 특정 모드는 전량.

> **중단 복구**: 회차가 중간에 죽으면 종결 기록되지 않은 iid 는 fix-state.json 에 남지 않으므로, **다음 회차에서 신규로 자동 재픽업**된다(아래 1번 선정리가 잔존 worktree 충돌을 흡수). `/loop` 는 직전 실행이 끝난 뒤 다음 실행이라 동시 처리가 없어 별도 in-progress 락이 불필요하다.

그 다음 issue 들을 **최대 N(기본 5)개씩 `Agent` 도구(subagent_type=general-purpose)** 로 병렬 디스패치한다. worktree 가 물리적으로 분리되어 파일 충돌이 없다. 각 subagent 에 **`<REPO>` 값**과 issue 데이터(iid/title/description/labels/web_url)에 더해, **notes(댓글) 요약과 메인이 첨부 이미지를 Read 로 확인해 정리한 "작업 대상·합의 내용"** 을 함께 전달한다(본문이 비어도 notes/이미지에 요구가 있으므로 — 2절). 아래 지침을 전달한다:

> 너는 GitLab issue **하나**를 처리한다. 입력: `<REPO>`(절대경로), iid, title/description/labels, **notes 요약·이미지 분석으로 정리된 작업 대상**.
>
> 0. 주의: git(worktree/commit)은 진행 메시지를 **stderr 로** 출력한다 — stderr 존재를 실패로 보지 말고 **exit code 로만** 판정하라. 모든 git path 인자는 **`<REPO>/...` 절대경로**로 준다.
> 1. 전용 worktree 준비 (**멱등 — 잔존 정리 후 생성**):
>    - 잔존 제거(없으면 에러 무시): `git -C <REPO> worktree remove <REPO>/fix-<iid> --force` ; `git -C <REPO> branch -D fix-<iid>`
>      (resolved 의 커밋 SHA 는 fix-state.json 에 이미 보존돼 있으니 worktree/branch 제거는 안전)
>    - 생성: `git -C <REPO> worktree add <REPO>/fix-<iid> -b fix-<iid> origin/main`
>      lock 경합으로 실패하면 2~3초 후 1회 재시도.
> 2. **해결 가능성 판단**(아래 "판단 기준"). 자동 수정 부적절 → worktree 제거(`git -C <REPO> worktree remove <REPO>/fix-<iid> --force`; `git -C <REPO> branch -D fix-<iid>`) 후 `status=unsolvable`(애매하면 `needs_review`) + reason 반환.
> 3. 해결 시도: **`<REPO>/fix-<iid>` 안에서만** 코드 수정.
> 4. **빌드(검증)** — 수정 파일이 속한 프로젝트만(전체 sln 금지):
>    - 파일→프로젝트: 수정한 각 파일의 디렉토리에서 **위로 올라가며 첫 `.csproj`/`.fsproj`** 를 찾는다. 후보가 여럿이면 그 파일을 실제 컴파일에 포함하는 proj 를 택한다.
>    - `dotnet build <그_proj>` (worktree 내 경로). 성공/실패는 exit code 로.
>    - **빌드 대상이 없는 변경**(순수 web 자산 `.js`/`.html`/`.css`, 문서 등 .NET proj 밖만 수정): 빌드로 검증 불가 → **자동 commit 하지 말고** `status=needs_review`, reason="빌드 대상 없음 — 사람 검토 필요".
> 5. 빌드 **성공** → 그 worktree 에서 commit(push/merge 안 함):
>    `git -C <REPO>/fix-<iid> add -A`
>    commit 전 `git -C <REPO>/fix-<iid> status --short` 로 의도한 소스만 staged 인지 확인(`bin/`·`obj/` 는 `.gitignore` 제외). 무관 파일 staged 면 제외 후:
>    `git -C <REPO>/fix-<iid> commit -m "fix(#<iid>): <한줄 요약>"`
>    → `status=resolved`, commit SHA 기록.
>    빌드 **실패**/막힘 → commit 하지 말고 `status=needs_review` + 실패 로그 요약.
> 6. 반환(JSON 한 줄): `{ iid, status, title, branch:"fix-<iid>", worktree, commit, touchedProjects, reason, summary }`
>
> 금지: base(`main`) worktree 직접 수정 / 다른 fix-* worktree 침범 / push / merge / PAT 값 출력·커밋.

## 4. 결과 집계 → fix-state.json 갱신 (메인 단독, JSON 경유)

title/reason 에 따옴표·`$`·줄바꿈이 섞여도 안전하도록 **argv 가 아닌 JSON 파일**로 넘긴다:
1. 메인이 임시파일에 `{ iid, status, title, branch, worktree:"<REPO>/fix-<iid>", commit, reason, summary, touchedProjects, projectPath }` 를 UTF-8 로 저장.
2. `powershell -NoProfile -File .claude/skills/fix/scripts/update-state.ps1 -InputJson <임시파일>`
3. 임시파일 삭제.

- **메인이 순차로** 호출한다(동시 쓰기 race 방지 — subagent 가 직접 쓰지 않는다).
- `branch`/`worktree` 의 SSOT 는 메인이 `fix-<iid>` / `<REPO>/fix-<iid>` 로 고정(subagent 반환값은 참고용).
- commit 이 복수면 콤마로 결합한 문자열로 기록.

## 5. 요약 리포트

회차 종료 시 출력:
- `resolved` / `needs_review` / `unsolvable` / `skipped(기존)` 건수
- resolved 각각: `#iid  fix-<iid>  commit  한줄요약`
- needs_review / unsolvable 각각: `#iid  reason`
- `--max` 로 잘린 잔여 건수(있으면)
- 안내: "검토 후 사람이 직접 merge/push. resolved 브랜치는 `fix-<iid>`(worktree `<REPO>/fix-<iid>`)."

## 판단 기준 — 해결 가능 / 불가

**unsolvable (코드 자동해결 부적절 → 사람 판단):**
- 기능 제안·건의("~했으면 좋겠습니다", "~기능 추가") 로 설계 의사결정이 필요한 것
- **notes(댓글)·첨부 이미지까지 확인한 뒤에도** 재현 절차·로그·대상 파일이 불명확한 것 (본문만 비었다고 곧장 unsolvable 금지 — 2절)
- 외부 시스템/장비(PLC, XG5000, 서버 설치, 하드웨어) 의존
- UX/디자인 주관 판단이 필요한 것
- 사양이 모호하거나 변경 범위가 과도하게 큰 것

**해결 시도 가능:**
- 명확한 버그(재현·원인·기대동작이 **본문 또는 notes/이미지에** 있음 — 예: #191)
- 국소적·소규모 수정(특정 화면 텍스트, 명백한 로직 오류, null/예외 처리 등)
- 빌드로 검증 가능한 변경

**애매하면 `needs_review`** — 자동 수정하지 않고 사람에게 넘긴다. 잘못된 자동 수정보다 보류가 낫다.

## 안전장치 (불변)

- base(`main`) worktree 직접 수정 금지 — 반드시 `<REPO>/fix-<iid>` worktree 에서만.
- 모든 git path 인자는 `<REPO>/...` 절대경로(cwd 의존 상대경로 금지).
- 빌드 실패·빌드 대상 없음 시 자동 commit 금지.
- **push / merge 금지** (사람 몫).
- 한 회차 처리 상한 `--max`(기본 20). 초과분은 다음 회차로, 잘린 건수 명시.
- PAT 값을 출력하거나 commit 에 포함하지 말 것.
