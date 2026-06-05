<#
  gitlab-fetch-upload.ps1  ( /fix skill )
  GitLab issue 첨부(uploads) 이미지를 PAT 로 다운로드해 로컬 절대경로를 stdout 으로 돌려준다.
  메인(orchestrator)이 이 경로를 Read 도구로 열어 이미지를 직접 보고 판단/지시한다.

  핵심(재발 방지): uploads 는 web 경로(/<ns>/<proj>/uploads/..)로 받으면 sign_in 으로
        리다이렉트되어 106 byte HTML("You are being redirected") 만 떨어진다. 반드시
        API 엔드포인트로 받는다:  <GitLabBase>/projects/<enc>/uploads/<secret>/<filename>
        (이 값이 gitlab-issues.ps1 결과의 attachments[].apiUrl 이다.)

  RepoRoot/PAT 도출은 gitlab-issues.ps1 과 동일.
  사용:
    powershell -NoProfile -File gitlab-fetch-upload.ps1 -ApiUrl "<attachments[].apiUrl>"
    powershell -NoProfile -File gitlab-fetch-upload.ps1 -ProjectPath dualsoft/helpds `
      -Secret 80261b3a9abd52947f9c4635a57788e8 -FileName image.png
#>
[CmdletBinding()]
param(
  [string]$ApiUrl      = "",   # gitlab-issues.ps1 의 attachments[].apiUrl 을 그대로 줘도 됨
  [string]$ProjectPath = "",
  [string]$Secret      = "",
  [string]$FileName    = "",
  [string]$OutDir      = "",
  [string]$RepoRoot    = "",
  [string]$PatFile     = "",
  [string]$GitLabBase  = "http://dualsoft.co.kr:8081/api/v4"
)
$ErrorActionPreference = "Stop"

# --- RepoRoot 동적 도출 (.bare 의 부모, 하드코딩 없음) ---
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

# --- URL 결정: ApiUrl 직접 지정 우선, 아니면 ProjectPath+Secret+FileName 로 구성 ---
if ($ApiUrl) {
  $url = $ApiUrl
  if (-not $FileName) { $FileName = Split-Path $ApiUrl -Leaf }
} else {
  if (-not $Secret -or -not $FileName) { Write-Error "ApiUrl 또는 (-Secret 와 -FileName) 이 필요합니다."; exit 4 }
  $statePath = Join-Path $RepoRoot "fix-state.json"
  if (-not $ProjectPath -and (Test-Path $statePath)) {
    $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($state -and $state.issueRepo) { $ProjectPath = $state.issueRepo }
  }
  if (-not $ProjectPath) { $ProjectPath = "dualsoft/helpds" }
  $enc = [uri]::EscapeDataString($ProjectPath)
  $url = "$GitLabBase/projects/$enc/uploads/$Secret/$FileName"
}

# --- 출력 폴더/경로 (파일명 충돌 회피용 secret 접두) ---
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP "fix-uploads" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$prefix = if ($Secret) { $Secret.Substring(0, [Math]::Min(8, $Secret.Length)) }
          elseif ($ApiUrl -match '/uploads/([0-9a-fA-F]{32})/') { $Matches[1].Substring(0, 8) }
          else { "ul" }
$dest = Join-Path $OutDir ("{0}_{1}" -f $prefix, $FileName)

# --- 다운로드 (PAT 는 헤더 임시파일로 -> argv 노출 금지, native curl 비0 exit 는 직접 검사) ---
$hdr = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($hdr, "PRIVATE-TOKEN: $pat")
$hdrArg = "@$hdr"
& curl.exe -s -f --max-time 60 -H $hdrArg -o $dest $url
$rc = $LASTEXITCODE
Remove-Item $hdr -Force -ErrorAction SilentlyContinue
if ($rc -ne 0) { Write-Error "첨부 다운로드 실패 (curl exit=$rc, url=$url)"; exit 3 }

# --- 리다이렉트(HTML) 방어: web 경로/인증 실패 시 'You are being redirected' HTML 이 떨어진다 ---
$head = ''
if (Test-Path $dest) {
  $fs = [System.IO.File]::OpenRead($dest)
  try {
    $buf = New-Object byte[] 256
    $n = $fs.Read($buf, 0, 256)
    $head = [System.Text.Encoding]::ASCII.GetString($buf, 0, $n)
  } finally { $fs.Close() }
}
if ($head -match 'You are being .*redirected' -or $head -match '^\s*<html') {
  Remove-Item $dest -Force -ErrorAction SilentlyContinue
  # 의도적 종료: Write-Error 는 $ErrorActionPreference=Stop 에서 exit 6 도달 전 종료시키므로 직접 stderr+exit.
  # 메시지는 PS5.1 무BOM 한글 깨짐을 피해 ASCII 로 둔다.
  [Console]::Error.WriteLine("ERROR: upload redirected to sign_in (wrong web path or auth). Use API endpoint /api/v4/projects/:id/uploads/:secret/:filename (= attachments[].apiUrl). Check PAT scope=read_api.")
  exit 6
}

# --- 성공: 로컬 절대경로(Windows) 출력 ---
(Resolve-Path $dest).Path
