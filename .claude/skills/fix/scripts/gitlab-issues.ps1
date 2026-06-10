<#
  gitlab-issues.ps1  ( /fix skill )
  GitLab issue 를 조회해 처리 대상만 JSON 으로 stdout 출력한다.

  - 기본(전체 스캔): open issue 중 이미 처리된 iid / assignee 할당 / ExcludeLabels(기본
    'Human,SkippedByLLM') label 이 붙은 issue 를 제외한 신규만. merged 이력 issue 가 다시 open
    으로 보이면(= close 후 reopen) 재픽업하되 reopen 사유 파악을 위해 notes 를 항상 포함한다.
  - 특정 지정(-Iids): 지정한 iid 만 반환(assignee·처리 이력·label 무관, 강제).

  RepoRoot: 비우면 git common-dir(.bare) 의 부모로 자동 도출 (경로 하드코딩 없음).
  PAT 우선순위: env GITLAB_TOKEN -> file <RepoRoot>/.pat -> 없으면 exit 2.
  사용:
    powershell -NoProfile -File gitlab-issues.ps1 -ProjectPath dualsoft/helpds
    powershell -NoProfile -File gitlab-issues.ps1 -ProjectPath dualsoft/helpds -Iids 182,177

  notes/첨부 (본문 없음 != 정보 없음 — #191 류 재발 방지):
    각 issue 에 notes(댓글)·attachments(첨부 uploads) 를 포함한다.
    - 특정 모드(-Iids): 항상 포함.
    - 전체 모드: -IncludeNotes 스위치 ON 또는 description 이 비어있는 issue 만 자동 포함.
    - attachments[].apiUrl 은 uploads "API" 엔드포인트. web 경로(/<ns>/<proj>/uploads/..)로
      받으면 sign_in 리다이렉트(HTML)되니 첨부는 반드시 이 apiUrl 로 받을 것.
#>
[CmdletBinding()]
param(
  [string]$ProjectPath = "",
  [string]$Iids        = "",
  [string]$RepoRoot    = "",
  [string]$PatFile     = "",
  [string]$GitLabBase  = "http://dualsoft.co.kr:8081/api/v4",
  [string]$ExcludeLabels = "Human,SkippedByLLM",   # 콤마 구분(공백 포함 label 가능). 기본 모드에서 제외:
                                      # Human=사람 전담, SkippedByLLM=unsolvable 보고됨(state 유실 시 중복 comment 2차 방어).
                                      # 비활성: 공백 " " 전달 (powershell -File 은 빈 문자열 인자를 소실시킴)
  [switch]$IncludeNotes
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
$skip = @("resolved","unsolvable","needs_review")
$done = @{}
$mergedIids = @{}   # merged 이력 — open 으로 다시 보이면 reopen 된 것: 재픽업 대상이되 notes 필수
if ($state -and $state.issues) {
  foreach ($p in $state.issues.PSObject.Properties) {
    if ($skip -contains $p.Value.status)  { $done[[int]$p.Name] = $true }
    if ($p.Value.status -eq "merged")     { $mergedIids[[int]$p.Name] = $true }
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

# --- 특정 issue 의 notes(댓글) 조회 (Get-Issues 와 동일한 curl/UTF-8/exit 검사 규약) ---
function Get-Notes {
  param([int]$Iid)
  $acc  = New-Object System.Collections.ArrayList
  $page = 1
  do {
    $url = "$GitLabBase/projects/$enc/issues/$Iid/notes?per_page=100&page=$page&sort=asc"
    $tmp = [System.IO.Path]::GetTempFileName()
    $hdr = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($hdr, "PRIVATE-TOKEN: $pat")
    $hdrArg = "@$hdr"
    & curl.exe -s -f --max-time 30 -H $hdrArg -o $tmp $url
    $rc = $LASTEXITCODE
    Remove-Item $hdr -Force -ErrorAction SilentlyContinue
    if ($rc -ne 0) {
      Remove-Item $tmp -Force -ErrorAction SilentlyContinue
      Write-Error "GitLab notes 조회 실패 (curl exit=$rc, iid=$Iid, page=$page)."
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

# --- 텍스트(description/note body) 에서 첨부 uploads 추출 -> apiUrl 동반 ---
# GitLab 마크다운의 /uploads/<32hex secret>/<filename>. web 경로가 아닌 API 엔드포인트로 변환.
function Get-UploadsFromText {
  param([string]$Text)
  $res = New-Object System.Collections.ArrayList
  if (-not $Text) { return ,$res }
  $rx = [regex]'/uploads/([0-9a-fA-F]{32})/([^\s)\]"<>]+)'
  foreach ($m in $rx.Matches($Text)) {
    $secret = $m.Groups[1].Value
    $file   = $m.Groups[2].Value -replace '[.,;:!?]+$', ''   # prose 내 delimiter 없는 bare 경로의 후행 구두점 흡수 방지
    [void]$res.Add([pscustomobject]@{
      secret       = $secret
      filename     = $file
      markdownPath = $m.Value
      apiUrl       = "$GitLabBase/projects/$enc/uploads/$secret/$file"
    })
  }
  return ,$res
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
  # 기본: open 전체 중 처리이력 + assignee 할당 + 제외 label(기본 Human,SkippedByLLM) 제외
  $mode = "all"
  # 콤마 전용 split + Trim: 공백 포함 label(예: 'Needs Triage') 지정 가능, " " 비활성 트릭 유지
  $exclude = @($ExcludeLabels -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  $all  = Get-Issues -Query "state=opened"
  $sel  = @($all) |
    Where-Object { -not $done.ContainsKey([int]$_.iid) } |
    Where-Object { @($_.assignees | Where-Object { $_ }).Count -eq 0 } |
    Where-Object { -not (@($_.labels) | Where-Object { $exclude -contains $_ }) } |
    Sort-Object { [int]$_.iid }
}

# notes 포함 조건: 특정 모드는 항상 / 전체 모드는 -IncludeNotes 또는 본문 빈 issue (정보 누락 방지)
#                 / merged 이력 issue (= close 후 reopen — 본문이 있어도 reopen 사유는 notes 에 있다)
$out = @($sel) | ForEach-Object {
  $desc      = $_.description
  $wantNotes = ($mode -eq "specific") -or $IncludeNotes -or [string]::IsNullOrWhiteSpace($desc) -or $mergedIids.ContainsKey([int]$_.iid)
  $notesOut  = @()
  if ($wantNotes) {
    $rawNotes = Get-Notes -Iid ([int]$_.iid)   # Get-Issues 와 동일하게 변수로 받아 ,$acc 언랩
    $notesOut = @(@($rawNotes) |
      Where-Object { -not $_.system } |
      ForEach-Object { [pscustomobject]@{ author = $_.author.name; created_at = $_.created_at; body = $_.body } })
  }
  # description + 모든 note body 에서 첨부 수집(중복 제거)
  $atts = New-Object System.Collections.ArrayList
  $seen = @{}
  foreach ($t in (@($desc) + @($notesOut | ForEach-Object { $_.body }))) {
    foreach ($u in (Get-UploadsFromText -Text $t)) {
      $key = "$($u.secret)/$($u.filename)"
      if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; [void]$atts.Add($u) }
    }
  }
  [pscustomobject]@{
    iid         = $_.iid
    title       = $_.title
    description = $desc
    labels      = @($_.labels)
    issue_type  = $_.issue_type
    web_url     = $_.web_url
    notes       = @($notesOut)
    attachments = @($atts)
  }
}

[pscustomobject]@{
  projectPath = $ProjectPath
  mode        = $mode
  total       = @($all).Count
  newCount    = @($out).Count
  issues      = @($out)
} | ConvertTo-Json -Depth 8
