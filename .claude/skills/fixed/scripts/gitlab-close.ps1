<#
  gitlab-close.ps1  ( /fixed skill )
  단일 GitLab issue 에 comment(note) 를 추가한 뒤 issue 를 close 한다.
  close 시 AddLabels(기본 'ByLLM') 를 add_labels 로 함께 붙인다(기존 label 보존·추가).
  -NoClose: close 하지 않고 comment + label 만 추가한다(/fix 의 unsolvable 보고용 —
            issue 는 사람이 봐야 하므로 open 유지. 예: -NoClose -AddLabels "SkippedByLLM").
  comment/close/label 은 write 작업이므로 PAT 는 반드시 'api' scope 여야 한다(read_api 면 403).

  RepoRoot: 비우면 git common-dir(.bare) 의 부모로 자동 도출 (경로 하드코딩 없음).
  PAT 우선순위: env GITLAB_TOKEN -> file <RepoRoot>/.pat -> 없으면 exit 2.
  ProjectPath: 비우면 fix-state.json 의 issueRepo -> 그것도 없으면 dualsoft/helpds.
  사용:
    powershell -NoProfile -File gitlab-close.ps1 -ProjectPath dualsoft/helpds -Iid 154 -BodyFile note.md
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][int]$Iid,
  [Parameter(Mandatory=$true)][string]$BodyFile,
  [string]$ProjectPath = "",
  [string]$RepoRoot    = "",
  [string]$PatFile     = "",
  [string]$GitLabBase  = "http://dualsoft.co.kr:8081/api/v4",
  [string]$AddLabels   = "ByLLM",  # 콤마 구분. 빈 문자열이면 label 추가 안 함
  [switch]$NoClose                 # close 생략 — comment + label 만 (open 유지)
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

# --- ProjectPath 기본값: fix-state.json 의 issueRepo ---
if (-not $ProjectPath) {
  $statePath = Join-Path $RepoRoot "fix-state.json"
  if (Test-Path $statePath) {
    $st = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($st.issueRepo) { $ProjectPath = [string]$st.issueRepo }
  }
}
if (-not $ProjectPath) { $ProjectPath = "dualsoft/helpds" }

# --- PAT (env -> file -> 실패) ---
$pat = $env:GITLAB_TOKEN
if (-not $pat -and (Test-Path $PatFile)) { $pat = (Get-Content $PatFile -Raw).Trim() }
if (-not $pat) { Write-Error "No PAT: set env GITLAB_TOKEN or file $PatFile (scope api, write 필요)"; exit 2 }

if (-not (Test-Path $BodyFile)) { Write-Error "BodyFile 없음: $BodyFile"; exit 6 }
# 주의: Get-Content -Raw 는 ETS NoteProperty(PSPath/PSProvider/ReadCount 등)가 부착된 문자열을
#       돌려주고, 그걸 @{body=...} | ConvertTo-Json 하면 그 속성 그래프까지 직렬화되어 body 가
#       거대 객체로 변질된다(GitLab 이 "body is invalid" 로 거부). 순수 string 을 얻기 위해
#       .NET ReadAllText 를 쓴다(BOM 자동 감지·제거).
$bodyText = [System.IO.File]::ReadAllText($BodyFile, [System.Text.Encoding]::UTF8)

$enc = [uri]::EscapeDataString($ProjectPath)

# --- GitLab write 호출 헬퍼 (curl) ---
# 주의 1: PAT 를 argv 로 노출하지 않도록 헤더는 임시파일(-H @file, curl 7.55+)로 전달한다.
# 주의 2: 본문은 임시파일에 UTF-8(no BOM)로 저장 후 --data-binary @file 로 보낸다(개행 보존).
# 주의 3: curl(native) 비0 exit 는 throw 되지 않으므로 $LASTEXITCODE 를 직접 검사하고,
#         HTTP 상태는 -w "%{http_code}" 로 받아 2xx 여부로 판정한다(-f 는 에러 body 를
#         버려 진단이 어려우므로 쓰지 않는다 — 403/404 응답 본문을 보존해야 한다).
function Invoke-GitLabWrite {
  param([string]$Method, [string]$Url, [string]$JsonBody)
  $tmpOut  = [System.IO.Path]::GetTempFileName()
  $hdr     = [System.IO.Path]::GetTempFileName()
  $bodyTmp = [System.IO.Path]::GetTempFileName()
  try {
    [System.IO.File]::WriteAllText($hdr, "PRIVATE-TOKEN: $pat")
    [System.IO.File]::WriteAllText($bodyTmp, $JsonBody, (New-Object System.Text.UTF8Encoding($false)))
    $hdrArg = "@$hdr"; $bodyArg = "@$bodyTmp"
    # Content-Type 은 비밀이 아니므로 별도 -H 로 직접 전달한다(헤더파일에는 PAT 한 줄만 두어
    # gitlab-issues.ps1 의 검증된 단일헤더 패턴과 동일하게 유지 — 멀티라인 -H @file 미전송 회피).
    $httpCode = & curl.exe -s --max-time 30 -X $Method -H $hdrArg -H "Content-Type: application/json" --data-binary $bodyArg -o $tmpOut -w "%{http_code}" $Url
    $rc = $LASTEXITCODE
  } finally {
    # 토큰이 평문으로 담긴 헤더 임시파일은 예외 발생 시에도 반드시 삭제
    Remove-Item $hdr, $bodyTmp -Force -ErrorAction SilentlyContinue
  }
  $resp = ""
  if (Test-Path $tmpOut) {
    $resp = [System.IO.File]::ReadAllText($tmpOut, [System.Text.Encoding]::UTF8)
    Remove-Item $tmpOut -Force -ErrorAction SilentlyContinue
  }
  $code = 0; [void][int]::TryParse(($httpCode | Select-Object -Last 1), [ref]$code)
  return [pscustomobject]@{ rc = $rc; http = $code; body = $resp }
}

function Test-Ok { param($R) return ($R.rc -eq 0 -and $R.http -ge 200 -and $R.http -lt 300) }

# 1) comment(note) 추가
$noteJson = @{ body = $bodyText } | ConvertTo-Json -Depth 4 -Compress
$r1 = Invoke-GitLabWrite -Method POST -Url "$GitLabBase/projects/$enc/issues/$Iid/notes" -JsonBody $noteJson
if (-not (Test-Ok $r1)) {
  Write-Error "comment 추가 실패 (curl=$($r1.rc) http=$($r1.http) iid=$Iid): $($r1.body)"; exit 3
}

# 2) issue update: close(+ label), -NoClose 면 label 만 — add_labels 는 기존 label 을 보존하며
#    추가, 미존재 label 은 자동 생성(Reporter 이상)되므로 별도 호출 없이 한 번의 PUT 으로 처리한다.
$updateBody = @{}
if (-not $NoClose) { $updateBody.state_event = "close" }
if ($AddLabels)    { $updateBody.add_labels  = $AddLabels }

$state = ""; $labels = @()
if ($updateBody.Count -gt 0) {
  $updateJson = $updateBody | ConvertTo-Json -Compress
  $r2 = Invoke-GitLabWrite -Method PUT -Url "$GitLabBase/projects/$enc/issues/$Iid" -JsonBody $updateJson
  if (-not (Test-Ok $r2)) {
    Write-Error "issue update(close/label) 실패 (curl=$($r2.rc) http=$($r2.http) iid=$Iid): $($r2.body)"; exit 4
  }
  try {
    $resObj = $r2.body | ConvertFrom-Json
    $state  = $resObj.state
    if ($resObj.labels) { $labels = @($resObj.labels) }
  } catch {}
}

[pscustomobject]@{
  iid           = $Iid
  projectPath   = $ProjectPath
  commentPosted = $true
  closed        = ($state -eq "closed")
  state         = $state
  labels        = ($labels -join ",")
} | ConvertTo-Json -Compress
