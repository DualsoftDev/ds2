---
name: fix
description: Fetch OPEN issues from a GitLab project and auto-resolve each one inside an isolated git worktree of the fixed ds2 code repo. Builds only the project touched by the fix, commits locally (never push/merge), and classifies every issue into fix-state.json as resolved / needs_review / unsolvable. Use when the user runs /fix [gitlab-project-path] [--concurrency N]. For periodic runs the user wraps it with /loop <interval> /fix ... (the skill itself stays single-shot).
---

# /fix — GitLab open issue 자동 처리

GitLab 의 **open issue** 를 가져와, ds2 코드 repo 의 **격리된 worktree** 에서 issue 별로 자동 수정·검증·커밋하고, 처리 결과를 `fix-state.json` 에 분류 기록한다.

- **1회 실행 = 신규 open issue 1회 처리(멱등)**. 이미 처리된 issue 는 건드리지 않는다.
- **주기 실행은 이 skill 에 넣지 않는다.** 사용자가 `/loop <interval> /fix ...` 로 감싸면 주기화된다.

## 0. 고정 경로 / 인자

| 항목 | 값 |
|---|---|
| 코드 repo 루트 `<REPO>` | **경로 하드코딩 금지** — skill 실행 위치(auto-fix worktree)에서 `git rev-parse --git-common-dir` 의 절대경로의 **부모**(= `.bare` 의 부모, ds2 루트)로 동적 도출. `.bare` 공유, 기준 worktree = `<REPO>/main` |
| 작업 루트 (고정) | `<REPO>/auto-fix` |
| 상태 대장 (SSOT) | `<REPO>/fix-state.json` (모든 worktree 공유, git 비추적) |
| issue worktree | `<REPO>/fix-<iid>` (`.bare` 와 동급, auto-fix worktree 밖) |
| 보조 스크립트 | `<REPO>/auto-fix/.claude/skills/fix/scripts/` |

인자(`$ARGUMENTS`):
- 첫 번째 위치 인자 = GitLab project path (예: `dualsoft/helpds`). 생략 시 `fix-state.json` 의 `issueRepo`, 그것도 없으면 `dualsoft/helpds`.
- `--issue <iid[,iid...]>` = **특정 issue 만** 처리(콤마로 복수). 지정 시 전체 스캔하지 않고, **assignee 할당 여부·처리 이력과 무관하게 강제 처리**한다.
- `--concurrency N` = 동시 subagent 수 (기본 **5**).
- `--max N` = 이번 회차 처리 상한 (기본 **20**, 폭주 방지). `--issue` 지정 시에는 적용하지 않는다.

## 1. PAT 확보 (prerequisite)

보조 스크립트가 다음 순서로 PAT 를 찾는다. **전부 없으면 중단하고 사용자에게 요청한다** (가설 추정 경로로 진행 금지):
1. 환경변수 `GITLAB_TOKEN`
2. 파일 `<REPO>/.pat`
3. 없음 → "PAT 없음: GITLAB_TOKEN 또는 <REPO>/.pat (scope read_api) 필요" 출력 후 종료.

## 2. issue 조회 + 선별

기본(전체 스캔):
```
powershell -NoProfile -File .claude/skills/fix/scripts/gitlab-issues.ps1 -ProjectPath <path>
```
특정 issue 지정(`--issue` 인자가 있을 때):
```
powershell -NoProfile -File .claude/skills/fix/scripts/gitlab-issues.ps1 -ProjectPath <path> -Iids <iid[,iid...]>
```
- **기본 모드**: open issue 전체 중 ① 이미 처리된(`resolved`/`unsolvable`/`in_progress`/`needs_review`) iid, ② **assignee 가 할당된 issue** 를 제외한 신규만 반환.
- **특정 모드(`-Iids`)**: 지정한 iid 만 반환. **assignee·처리 이력과 무관**하게 강제 처리하며, 이미 처리된 issue(및 GitLab 에서 닫힌 closed issue)도 다시 처리한다.
- 결과 JSON: `{ projectPath, mode, total, newCount, issues:[{ iid, title, description, labels, issue_type, web_url }] }`
- `newCount == 0` 이면 "처리할 issue 없음" 출력하고 종료.
- (기본 모드) 신규가 `--max` 초과면 iid 오름차순으로 상한까지만 처리하고 **잘린 건수를 로그로 명시**한다(조용히 누락 금지). 특정 모드는 `--max` 미적용.

## 3. 각 issue 병렬 처리 (최대 N개 subagent)

**시작 시 `<REPO>` 도출**(하드코딩 금지): skill 실행 위치(auto-fix worktree)에서 `git rev-parse --git-common-dir` 의 절대경로의 부모가 `<REPO>`(`.bare` 의 부모 = ds2 루트)다. 메인이 1회 도출해 각 subagent 에 전달한다.

신규 issue 를 **최대 N(기본 5)개씩 `Agent` 도구(subagent_type=general-purpose)** 로 병렬 디스패치한다. worktree 가 물리적으로 분리되어 파일 충돌이 없다. 각 subagent 에 해당 issue 데이터(iid/title/description/labels/web_url)와 아래 지침을 전달한다:

> 너는 GitLab issue **하나**를 처리한다. 입력: iid=<iid>, title/description/labels.
>
> 0. 주의: git(fetch/worktree/commit)은 진행 메시지를 **stderr 로** 출력한다 — stderr 존재를 실패로 간주하지 말고 **exit code 로만 성공 판정**하라.
> 1. 최신화: `git -C <REPO> fetch origin`
> 2. 전용 worktree 생성(이미 있으면 재사용):
>    `git -C <REPO> worktree add fix-<iid> -b fix-<iid> origin/main`
>    여러 subagent 동시 생성 중 git lock 경합으로 실패하면 2~3초 후 1회 재시도.
> 3. **해결 가능성 판단** (아래 "판단 기준"). 자동 수정이 부적절하면:
>    - 코드 변경 없이 worktree 제거: `git -C <REPO> worktree remove fix-<iid> --force` 후 branch 삭제 `git -C <REPO> branch -D fix-<iid>`
>    - `status=unsolvable`(또는 애매하면 `needs_review`) + reason 반환.
> 4. 해결 시도: `<REPO>/fix-<iid>` **안에서만** 코드 수정.
> 5. 수정한 파일이 속한 `.csproj`/`.fsproj` 를 찾아 **그 프로젝트만** 빌드(전체 sln 빌드 금지):
>    `dotnet build <touched.csproj/.fsproj>`
> 6. 빌드 **성공** → 그 worktree 에서 commit (push/merge 안 함):
>    `git -C <REPO>/fix-<iid> add -A`
>    commit 전 `git -C <REPO>/fix-<iid> status --short` 로 의도한 소스 파일만 staged 인지 확인(빌드 산출물 `bin/`·`obj/` 는 ds2 `.gitignore` 가 제외함). 무관한 파일이 staged 면 제외 후:
>    `git -C <REPO>/fix-<iid> commit -m "fix(#<iid>): <한줄 요약>"`
>    → `status=resolved`.
>    빌드 **실패** 또는 막힘 → commit 하지 말고 `status=needs_review` + 실패 로그 요약.
> 7. 반환(JSON 한 줄): `{ iid, status, branch, worktree, commit, touchedProjects, reason, summary }`
>
> 금지: base(main) worktree 직접 수정 / 다른 fix-* worktree 침범 / push / merge / PAT 값 출력·커밋.

## 4. 결과 집계 → fix-state.json 갱신 (메인 단독)

각 subagent 반환을 모아 **메인(orchestrator)이 순차로** 상태를 기록한다(동시 쓰기 race 방지):
```
powershell -NoProfile -File .claude/skills/fix/scripts/update-state.ps1 `
  -Iid <iid> -Status <status> -Title "<title>" -Branch fix-<iid> `
  -Worktree fix-<iid> -Commit <sha> -Reason "<reason>" -ProjectPath <path>
```

## 5. 요약 리포트

회차 종료 시 출력:
- `resolved` / `needs_review` / `unsolvable` / `skipped(기존)` 건수
- resolved 각각: `#iid  branch  commit  한줄요약`
- needs_review / unsolvable 각각: `#iid  reason`
- `--max` 로 잘린 잔여 건수(있으면)
- 안내: "검토 후 사람이 직접 merge/push. resolved 브랜치는 `fix-<iid>`."

## 판단 기준 — 해결 가능 / 불가

**unsolvable (코드 자동해결 부적절 → 사람 판단):**
- 기능 제안·건의("~했으면 좋겠습니다", "~기능 추가") 로 설계 의사결정이 필요한 것
- 재현 절차·로그·대상 파일이 불명확한 것
- 외부 시스템/장비(PLC, XG5000, 서버 설치, 하드웨어) 의존
- UX/디자인 주관 판단이 필요한 것
- 사양이 모호하거나 변경 범위가 과도하게 큰 것

**해결 시도 가능:**
- 명확한 버그(재현·원인·기대동작이 본문에 있음)
- 국소적·소규모 수정(특정 화면 텍스트, 명백한 로직 오류, null/예외 처리 등)
- 빌드로 검증 가능한 변경

**애매하면 `needs_review`** — 자동 수정하지 않고 사람에게 넘긴다. 잘못된 자동 수정보다 보류가 낫다.

## 안전장치 (불변)

- base(`main`) worktree 직접 수정 금지 — 반드시 `fix-<iid>` worktree 에서만.
- 빌드 실패 시 자동 commit 금지.
- **push / merge 금지** (사람 몫).
- 한 회차 처리 상한 `--max`(기본 20). 초과분은 다음 회차로, 잘린 건수 명시.
- PAT 값을 출력하거나 commit 에 포함하지 말 것.
