<#
  gitlab-issues.ps1  ( /fix skill )
  GitLab issue 를 조회해 처리 대상만 JSON 으로 stdout 출력한다.

  - 기본(전체 스캔): open issue 중 이미 처리된 iid 와 assignee 가 할당된 issue 를 제외한 신규만.
  - 특정 지정(-Iids): 지정한 iid 만 반환(assignee·처리 이력 무관, 강제).

  RepoRoot: 비우면 git common-dir(.bare) 의 부모로 자동 도출 (경로 하드코딩 없음).
  PAT 우선순위: env GITLAB_TOKEN -> file <RepoRoot>/.pat -> 없으면 exit 2.
  사용:
    powershell -NoProfile -File gitlab-issues.ps1 -ProjectPath dualsoft/helpds
    powershell -NoProfile -File gitlab-issues.ps1 -ProjectPath dualsoft/helpds -Iids 182,177
#>
[CmdletBinding()]
param(
  [string]$ProjectPath = "",
  [string]$Iids        = "",
  [string]$RepoRoot    = "",
  [string]$PatFile     = "",
  [string]$GitLabBase  = "http://dualsoft.co.kr:8081/api/v4"
)
$ErrorActionPreference = "Stop"

# --- RepoRoot 동적 도출: .bare 의 부모 (모든 worktree 공통, 하드코딩 없음) ---
if (-not $RepoRoot) {
  $gcd = & git -C $PSScriptRoot rev-parse --git-common-dir
  if ($LASTEXITCODE -ne 0 -or -not $gcd) { Write-Error "git repo 를 찾지 못함 ($PSScriptRoot)"; exit 5 }
  if (-not [System.IO.Path]::IsPathRooted($gcd)) { $gcd = Join-Path $PSScriptRoot $gcd }
  $RepoRoot = (Split-Path (Resolve-Path $gcd).Path -Parent) -replace '\\','/'
}
if (-not $PatFile) { $PatFile = "$RepoRoot/.pat" }

# --- PAT (env -> file -> 실패) ---
$pat = $env:GITLAB_TOKEN
if (-not $pat -and (Test-Path $PatFile)) { $pat = (Get-Content $PatFile -Raw).Trim() }
if (-not $pat) { Write-Error "No PAT: set env GITLAB_TOKEN or file $PatFile (scope read_api)"; exit 2 }

# --- fix-state.json 로드 + 이미 처리된 iid 집합 ---
$statePath = Join-Path $RepoRoot "fix-state.json"
$state = $null
if (Test-Path $statePath) { $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json }
if (-not $ProjectPath) {
  $ProjectPath = if ($state -and $state.issueRepo) { $state.issueRepo } else { "dualsoft/helpds" }
}
$skip = @("resolved","unsolvable","in_progress","needs_review")
$done = @{}
if ($state -and $state.issues) {
  foreach ($p in $state.issues.PSObject.Properties) {
    if ($skip -contains $p.Value.status) { $done[[int]$p.Name] = $true }
  }
}

$enc = [uri]::EscapeDataString($ProjectPath)

# --- GitLab issues 조회 헬퍼 (pagination) ---
# 주의 1: PS5.1 Invoke-RestMethod 는 charset 누락 응답에서 UTF-8 을 ISO-8859-1 로
#         오인 디코딩(한글 깨짐) -> curl 로 받아 임시파일에 UTF-8 로 저장 후 읽는다.
# 주의 2: PAT 를 argv 로 노출하지 않도록 헤더는 임시파일(-H @file)로 전달한다.
# 주의 3: curl(native) 비0 exit 는 throw 되지 않으므로 $LASTEXITCODE 를 직접 검사한다
#         (미검사 시 실패가 "0건" 으로 오인되어 issue 가 조용히 누락된다).
function Get-Issues {
  param([string]$Query)
  $acc  = New-Object System.Collections.ArrayList
  $page = 1
  do {
    $url = "$GitLabBase/projects/$enc/issues?$Query&per_page=100&page=$page"
    $tmp = [System.IO.Path]::GetTempFileName()
    $hdr = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($hdr, "PRIVATE-TOKEN: $pat")
    $hdrArg = "@$hdr"
    & curl.exe -s -f --max-time 30 -H $hdrArg -o $tmp $url
    $rc = $LASTEXITCODE
    Remove-Item $hdr -Force -ErrorAction SilentlyContinue
    if ($rc -ne 0) {
      Remove-Item $tmp -Force -ErrorAction SilentlyContinue
      Write-Error "GitLab API 호출 실패 (curl exit=$rc, page=$page, project=$ProjectPath). 부분결과 누락 방지 위해 중단."
      exit 3
    }
    $raw = [System.IO.File]::ReadAllText($tmp, [System.Text.Encoding]::UTF8)
    Remove-Item $tmp -Force
    $batch = $raw | ConvertFrom-Json
    foreach ($it in $batch) { [void]$acc.Add($it) }
    $page++
  } while (@($batch).Count -eq 100)
  return ,$acc
}

# --- 모드 분기 ---
$targets = @()
if ($Iids) { $targets = $Iids -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ } }
if ($Iids -and $targets.Count -eq 0) { Write-Error "유효한 iid 없음: -Iids '$Iids'"; exit 4 }

if ($targets.Count -gt 0) {
  # 특정 지정: 해당 iid 만, assignee/처리이력 무관(강제)
  $mode = "specific"
  $q    = ($targets | ForEach-Object { "iids[]=$_" }) -join '&'
  $all  = Get-Issues -Query $q
  $sel  = @($all) | Sort-Object { [int]$_.iid }
} else {
  # 기본: open 전체 중 처리이력 + assignee 할당 제외
  $mode = "all"
  $all  = Get-Issues -Query "state=opened"
  $sel  = @($all) |
    Where-Object { -not $done.ContainsKey([int]$_.iid) } |
    Where-Object { @($_.assignees | Where-Object { $_ }).Count -eq 0 } |
    Sort-Object { [int]$_.iid }
}

$out = @($sel) | ForEach-Object {
  [pscustomobject]@{
    iid         = $_.iid
    title       = $_.title
    description = $_.description
    labels      = @($_.labels)
    issue_type  = $_.issue_type
    web_url     = $_.web_url
  }
}

[pscustomobject]@{
  projectPath = $ProjectPath
  mode        = $mode
  total       = @($all).Count
  newCount    = @($out).Count
  issues      = @($out)
} | ConvertTo-Json -Depth 6
