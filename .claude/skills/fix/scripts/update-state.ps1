<#
  update-state.ps1  ( /fix skill )
  fix-state.json 에 issue 1건의 처리 결과를 반영한다.
  병렬 충돌 방지를 위해 메인(orchestrator)에서만 순차 호출한다.

  입력 (셸 인용 안전을 위해 -InputJson 권장):
    1) -InputJson <file> : { iid,status,title,branch,worktree,commit,reason,
                             summary,touchedProjects,projectPath } JSON 파일.
                           title/reason 에 따옴표·$·줄바꿈이 있어도 안전.
    2) 개별 인자 (-Iid -Status ...) : 특수문자 없을 때만 안전(보조용).
  RepoRoot: 비우면 git common-dir(.bare) 의 부모로 자동 도출 (경로 하드코딩 없음).
#>
[CmdletBinding()]
param(
  [string]$InputJson       = "",
  [int]$Iid                = 0,
  [string]$Status          = "",
  [string]$Title           = "",
  [string]$Branch          = "",
  [string]$Worktree        = "",
  [string]$Commit          = "",
  [string]$Reason          = "",
  [string]$Summary         = "",
  [string]$TouchedProjects = "",
  [string]$ProjectPath     = "",
  [string]$RepoRoot        = ""
)
$ErrorActionPreference = "Stop"

# --- InputJson 우선 (argv 셸 인용 회피) ---
if ($InputJson) {
  if (-not (Test-Path $InputJson)) { Write-Error "InputJson 파일 없음: $InputJson"; exit 6 }
  $in = Get-Content $InputJson -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($null -ne $in.iid)             { $Iid = [int]$in.iid }
  if ($in.status)                    { $Status = [string]$in.status }
  if ($null -ne $in.title)           { $Title = [string]$in.title }
  if ($null -ne $in.branch)          { $Branch = [string]$in.branch }
  if ($null -ne $in.worktree)        { $Worktree = [string]$in.worktree }
  if ($null -ne $in.commit)          { $Commit = [string]$in.commit }
  if ($null -ne $in.reason)          { $Reason = [string]$in.reason }
  if ($null -ne $in.summary)         { $Summary = [string]$in.summary }
  if ($null -ne $in.touchedProjects) { $TouchedProjects = (@($in.touchedProjects) -join ', ') }
  if ($in.projectPath)               { $ProjectPath = [string]$in.projectPath }
}
if ($Iid -le 0)   { Write-Error "Iid 필요 (-InputJson 또는 -Iid)"; exit 6 }
if (-not $Status) { Write-Error "Status 필요 (-InputJson 또는 -Status)"; exit 6 }

# --- RepoRoot 동적 도출: .bare 의 부모 (모든 worktree 공통, 하드코딩 없음) ---
if (-not $RepoRoot) {
  $gcd = & git -C $PSScriptRoot rev-parse --git-common-dir
  if ($LASTEXITCODE -ne 0 -or -not $gcd) { Write-Error "git repo 를 찾지 못함 ($PSScriptRoot)"; exit 5 }
  if (-not [System.IO.Path]::IsPathRooted($gcd)) { $gcd = Join-Path $PSScriptRoot $gcd }
  $RepoRoot = (Split-Path (Resolve-Path $gcd).Path -Parent) -replace '\\','/'
}
$statePath = Join-Path $RepoRoot "fix-state.json"

if (Test-Path $statePath) {
  $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
  $state = [pscustomobject]@{
    issueRepo   = $ProjectPath
    projectRepo = $RepoRoot
    baseBranch  = "main"
    lastChecked = ""
    issues      = [pscustomobject]@{}
  }
}
if ($ProjectPath) { $state.issueRepo = $ProjectPath }
if (-not $state.issues) {
  $state | Add-Member -NotePropertyName issues -NotePropertyValue ([pscustomobject]@{}) -Force
}

$now   = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
$entry = [pscustomobject]@{
  title           = $Title
  status          = $Status
  branch          = $Branch
  worktree        = $Worktree
  commit          = $Commit
  reason          = $Reason
  summary         = $Summary
  touchedProjects = $TouchedProjects
  updatedAt       = $now
}
# 주의: -NotePropertyName "$Iid" 는 iid 가 PSMemberTypes 열거형 underlying 값(예: "1"=AliasProperty)
#       으로 변환 가능할 때 파라미터 검증에서 거부된다. 일반 string 파라미터인 -Name/-Value 로 우회한다.
$state.issues | Add-Member -MemberType NoteProperty -Name "$Iid" -Value $entry -Force
$state.lastChecked = $now

# BOM 없는 UTF-8 로 저장
$json = $state | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($statePath, $json, (New-Object System.Text.UTF8Encoding($false)))
"updated #$Iid -> $Status"
