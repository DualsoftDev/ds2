---
name: fixed
description: Finalize resolved /fix issues by rebasing fix worktrees onto main, fast-forward merging, pushing main, commenting on GitLab issues, closing them, and updating fix-state.json. Use when the user runs /fixed with one or more issue iids after human approval.
---

# /fixed — resolved issue 를 main 에 반영하고 GitLab 에서 close

`/fix` 가 격리 worktree 에 commit 까지 끝낸 **resolved** issue 를, 사람 검토 후 `main` 에 **rebase → fast-forward merge → push** 하고, GitLab issue 에 **작업 comment 를 남긴 뒤 close** 한다.

- **`/fix` 와 짝**: `/fix` = 자동 수정·빌드검증·커밋(push/merge 안 함), **`/fixed` = 사람 OK 후 merge·push·close**.
- **1회 실행 = 인자로 준 iid(들)만 처리**. 멱등 — 이미 `merged` 인 iid 는 skip.
- **부분 실패 = 그 iid 만 skip, 나머지 진행**. 단 `main` dirty / `pull --ff-only` 실패 / `push` 실패는 전체 중단(아무 issue 도 close 하지 않음).

## 0. 인자 / 경로

| 항목 | 값 |
|---|---|
| iid 목록 | 위치 인자 1개 이상. `/fixed 154` 또는 `/fixed 154 149 176`. **필수** |
| 코드 repo 루트 `<REPO>` | **경로 하드코딩 금지** — skill 실행 위치에서 `git rev-parse --git-common-dir` 의 절대경로의 **부모**(= `.bare` 의 부모). 기준 worktree = `<REPO>/main` |
| 상태 대장 (SSOT) | `<REPO>/fix-state.json` (`/fix` 와 공유, git 비추적) |
| issue worktree | `<REPO>/fix-<iid>` |
| ProjectPath | `fix-state.json` 의 `issueRepo` (없으면 `dualsoft/helpds`) |
| 보조 스크립트 | `<REPO>/auto-fix/.claude/skills/fixed/scripts/gitlab-close.ps1` (write), 상태갱신은 `/fix` 의 `.../fix/scripts/update-state.ps1` **재사용** |

모든 git path 인자는 동적 도출한 `<REPO>` 절대값을 앞에 붙여 쓴다(`fix-<iid>` 단독 상대경로 금지 — cwd 기준으로 풀려 엉뚱한 위치를 가리킨다). git 은 진행 메시지를 **stderr** 로 내므로 stderr 존재를 실패로 보지 말고 **exit code 로만** 판정한다.

## 1. PAT 확보 (prerequisite) — **write 필요**

comment 추가·issue close 는 **`api` scope (write)** PAT 가 필요하다. `read_api` 만 있으면 **403** 으로 실패한다(`/fix` 의 읽기 전용과 다른 점).
1. 환경변수 `GITLAB_TOKEN`
2. 파일 `<REPO>/.pat`
3. 없음 → "PAT 없음: GITLAB_TOKEN 또는 <REPO>/.pat (scope **api**, write) 필요" 출력 후 종료.

## 2. 처리 시퀀스 (메인 orchestrator 가 **순차** 수행)

issue 수가 적고 `main` 이 순차로 전진(뒤 iid 는 앞 iid 가 반영된 `main` 위로 rebase)하므로 subagent 병렬 없이 메인이 직접 처리한다.

### Phase 0 — 검증
1. `<REPO>` 도출.
2. PAT 존재 확인(1절). 없으면 종료.
3. **`main` worktree clean(tracked-only)**:
   `git -C <REPO>/main status --porcelain --untracked-files=no`
   출력이 있으면(M/D/A 존재) → **전체 중단**. dirty 파일 목록을 보여주고 "main 의 미커밋 변경을 먼저 정리하라" 안내. (untracked `??` 는 무시)
4. 각 iid 에 대해(아래 중 하나라도 걸리면 그 iid 만 **skip**, 사유 기록):
   - `fix-state.json` 에 entry 존재 & `status == "resolved"` (아니면 skip: "status=<X>"). 이미 `merged` 면 skip("이미 merged").
   - worktree 디렉토리 `<REPO>/fix-<iid>` 존재(없으면 skip: "worktree 없음 — 이미 정리/수동처리").
   - branch 존재: `git -C <REPO> branch --list fix-<iid>` 비어있지 않음(아니면 skip: "branch 없음").
   - **worktree clean(tracked-only)**: `git -C <REPO>/fix-<iid> status --porcelain --untracked-files=no` 비어야 함(dirty 면 skip: "worktree dirty").
   - 통과한 iid 만 처리 대상에 넣는다(iid 오름차순 정렬).

### Phase 1 — main 최신화 (1회)
- `git -C <REPO> fetch origin`
- `git -C <REPO>/main pull --ff-only origin main`
  - ff 불가(로컬 `main` 이 origin 과 갈라짐) → **전체 중단**(사람 개입 필요). 이후 단계 진행 안 함.

### Phase 2 — 각 iid 순차 rebase + ff merge (iid 오름차순, 로컬)
처리 대상 iid 마다:
1. `before = git -C <REPO>/main rev-parse HEAD`
2. **rebase**: `git -C <REPO>/fix-<iid> rebase main`
   - 충돌/실패(exit≠0) → `git -C <REPO>/fix-<iid> rebase --abort` 후 그 iid **skip**("rebase 충돌"), 다음 iid 계속.
3. **ff merge**: `git -C <REPO>/main merge --ff-only fix-<iid>`
   - 실패(exit≠0) → 그 iid **skip**("ff merge 실패"), 다음 iid 계속.
4. `after = git -C <REPO>/main rev-parse HEAD`
5. 이 iid 가 `main` 에 올린 **최종 트리상의 commit** 수집:
   `git -C <REPO>/main log --format="%H%x09%s" <before>..<after>`
   → `(hash, subject)` 목록을 그 iid 에 보관(rebase 로 hash 가 바뀌므로 **이 시점 hash 가 최종**, comment 에 이 값을 쓴다).
   - **빈 목록(`before == after`)이면** 그 iid 의 변경이 **이미 `main` 에 반영돼 있다**는 뜻이다(rebase 가 중복 커밋을 drop). 이 경우:
     - hash 목록을 fix-state.json 의 **기존 `commit` 값으로 대체**하고 "이미 반영됨" 으로 표시한다.
     - 이후 Phase 4 의 status 갱신에서 **`commit` 필드를 빈 값으로 덮지 말 것**(기존 hash 보존). comment 에는 "이미 main 에 반영된 변경(기존 커밋)" 으로 적고 close 만 수행한다.

> 각 iid 의 hash 는 그 iid 가 merge 되는 순간 확정되고, 뒤 iid 가 `main` 을 더 전진시켜도 변하지 않는다 → push 될 `main` 의 실제 hash 와 일치한다.

### Phase 3 — push (1회)
- 처리(merge)된 iid 가 1개 이상이면: `git -C <REPO>/main push origin main`
  - 실패 → **전체 중단**. **issue close 진행 안 함**(미반영 상태로 close 금지). 로컬 merge 는 남으므로 push 만 사람이 재시도하면 된다. 보고.
- 처리된 iid 가 0개면 push 생략(요약만 출력).

### Phase 4 — 각 처리 iid: comment + close + 상태 갱신
push 성공 후, 처리된 iid 마다:
1. **comment 본문**을 임시 `.md` 파일에 UTF-8 로 작성:
   ```
   ## 자동 처리 완료 (/fixed)

   <fix-state.json 의 summary>

   **변경 프로젝트**: <touchedProjects>

   **반영 커밋** (main):
   - `<hash>` <subject>
   - ...
   ```
2. `powershell -NoProfile -File <REPO>/auto-fix/.claude/skills/fixed/scripts/gitlab-close.ps1 -ProjectPath <pp> -Iid <iid> -BodyFile <md>`
   - 성공 → 다음 3번.
   - 실패(예: 403 scope 부족) → 그 iid 는 **"merged&pushed 됐으나 close 실패"** 로 보고하고 수동 close 안내. **merge/push 는 이미 반영됨**(되돌리지 않음). gitlab-close.ps1 은 comment 추가 **후** close 하므로, **close 만 실패한 경우 `/fixed` 를 재실행하지 말 것**(comment 가 중복으로 또 달린다). PAT scope(`api`) 를 고친 뒤 close 만 수동으로 처리한다.
3. **상태 갱신** — `/fix` 의 `update-state.ps1` 재사용. 기존 entry 를 읽어 `status="merged"`, `commit=<이 iid 의 최종 hash 들 콤마결합>`(Phase 2-5 에서 빈 목록이었으면 **기존 commit 값 유지** — 빈 값으로 덮지 말 것), `branch=""`, `worktree=""`(이미 정리됐으므로 비움)로 바꾸고 나머지(title/reason/summary/touchedProjects)는 보존하여 InputJson 작성:
   `powershell -NoProfile -File <REPO>/auto-fix/.claude/skills/fix/scripts/update-state.ps1 -InputJson <임시json>`
4. **사후 정리**(close 성공 iid): `git -C <REPO> worktree remove <REPO>/fix-<iid> --force` ; `git -C <REPO> branch -d fix-<iid>` (이미 merge 됐으므로 `-d` 안전).
5. 임시 `.md`/`.json` 삭제.

### Phase 5 — 요약 리포트
- `merged`(처리완료): `#iid  <hash들>  <한줄요약>`
- `skipped`: `#iid  <사유>`
- push 결과(성공/생략/중단)
- close 실패 iid: 수동 close 안내(있으면)

## 안전장치 (불변)
- `main` worktree 와 `fix-<iid>` worktree 가 **tracked-clean(M/D/A 없음)** 이 아니면 진행 안 함.
- **resolved 만** 대상(`needs_review`/`unsolvable`/`merged` 거부).
- `pull --ff-only` 실패 시 전체 중단(강제 merge/rebase 금지).
- **push 성공 전에는 issue close 하지 않는다.**
- 멱등: 이미 `merged` 인 iid 는 skip.
- 모든 git path 는 `<REPO>/...` 절대경로.
- PAT 값을 출력·commit 하지 말 것. write scope 필요.
