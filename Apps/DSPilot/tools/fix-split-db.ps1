#Requires -Version 5.1
<#
.SYNOPSIS
    DSPilot 의 DB 분열(split-brain) 복구 — 구 DB 삭제 + appsettings 연결 문자열 정정.

.DESCRIPTION
    증상: 설비도 돌고 Agent 도 108개 태그를 정상 수집하는데 DSPilot 화면이 통째로 비어 있고,
          콘솔에 "table dspFlow has no column named FlowId" 가 30회 반복된다.

    원인: DSPilot 이 DB 파일 두 개를 동시에 연다.
      - KpiDb          -> <SharedDir>\dspilot.db          (system / tag / signal 표를 여기 만든다)
      - 그 외 전부      -> Database:ConnectionString 의 폴더 (dspFlow / dspCall / plc 표)
    구버전에서 올라온 현장은 연결 문자열이 %ProgramData%\DualSoft\DSPilot 을 가리키고 있어 둘이 갈라진다.
    그러면 스키마 생성 중 "INSERT INTO system" 이 (그 파일엔 system 표가 없어서) 예외를 던지고,
    바로 아래 있는 EnsureColumn 마이그레이션 블록이 통째로 건너뛰어진다 -> dspFlow.flowId 가 영원히 안 생긴다
    -> 모델 적재 실패 -> 엔진 미초기화 -> Hub 태그 전량 폐기.

    이 스크립트는 연결 문자열을 공유 폴더로 되돌리고, 갈라진 구 DB 를 (백업 후) 지워
    DSPilot 이 정본 파일 하나를 새로 만들게 한다. v68 은 "처음부터 수집" 이라 과거 수집분은 어차피 재생성 대상이다.

.PARAMETER SharedDir
    정본 공유 폴더. 기본값은 환경변수 DUALSOFT_SHARED_DIR, 없으면 %ProgramData%\DualSoft\Shared.

.PARAMETER InstallDir
    DSPilot 설치 폴더. 기본값은 DSPilotService 의 ImagePath 에서 추출, 실패 시 %ProgramFiles%\DualSoft\DSPilot.

.PARAMETER Port
    검증에 쓸 DSPilot 포트. 기본 8080 (appsettings.Hosting.json 의 Urls 가 있으면 그걸 우선).

.PARAMETER DryRun
    아무것도 바꾸지 않고 무엇을 할지만 출력한다.

.PARAMETER Force
    확인 프롬프트 없이 진행한다.

.EXAMPLE
    .\fix-split-db.ps1 -DryRun
    .\fix-split-db.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$SharedDir,
    [string]$InstallDir,
    [int]$Port = 0,
    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'DSPilotService'
$DbFileName  = 'dspilot.db'
# 구 엔진이 쓰던 파일까지 함께 정리한다(LegacyDbPurge 와 같은 목록).
$LegacyNames = @('dspilot.db', 'plc.db', 'oee.db')
# SQLite WAL 모드라 본체만 지우면 -wal/-shm 이 남아 다음 기동에서 되살아난다.
$Suffixes    = @('', '-wal', '-shm')

function Write-Step { param([string]$Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "   [OK] $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "   [!!] $Text" -ForegroundColor Yellow }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-InstallDirFromService {
    try {
        $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $ServiceName
        if (-not (Test-Path $key)) { return $null }
        $image = (Get-ItemProperty -Path $key -Name ImagePath -ErrorAction Stop).ImagePath
        if ([string]::IsNullOrWhiteSpace($image)) { return $null }

        # ImagePath 예: "C:\Program Files\DualSoft\DSPilot\DSPilot.exe"  또는 따옴표 없이 인자가 붙은 형태.
        # ★공백으로 자르면 안 된다 — "C:\Program Files\..." 가 "C:\Program" 이 되어 설치 폴더가 C:\ 로 잡힌다.
        #   .exe 까지를 통째로 집는다.
        $exe = $null
        if ($image -match '^\s*"([^"]+\.exe)"') { $exe = $Matches[1] }
        elseif ($image -match '^\s*(.+?\.exe)(\s|$)') { $exe = $Matches[1] }
        if ([string]::IsNullOrWhiteSpace($exe)) { return $null }
        return (Split-Path -Parent $exe)
    } catch { return $null }
}

<#
.SYNOPSIS
    후보 폴더들에서 appsettings 파일을 찾는다(Production 우선).
#>
function Find-SettingsFile {
    param([string[]]$Dirs)
    foreach ($d in $Dirs) {
        if ([string]::IsNullOrWhiteSpace($d)) { continue }
        foreach ($name in @('appsettings.Production.json', 'appsettings.json')) {
            $p = Join-Path $d $name
            if (Test-Path $p) { return $p }
        }
    }
    return $null
}

# ── 0. 사전 확인 ────────────────────────────────────────────────────────────
if (-not (Test-Admin)) {
    throw '관리자 권한이 필요합니다. PowerShell 을 "관리자로 실행" 후 다시 시도하세요.'
}

if ([string]::IsNullOrWhiteSpace($SharedDir)) {
    if (-not [string]::IsNullOrWhiteSpace($env:DUALSOFT_SHARED_DIR)) { $SharedDir = $env:DUALSOFT_SHARED_DIR.Trim() }
    else { $SharedDir = Join-Path $env:ProgramData 'DualSoft\Shared' }
}
$CanonicalDb = Join-Path $SharedDir $DbFileName
$LegacyDir   = Join-Path $env:ProgramData 'DualSoft\DSPilot'

# ── 1. appsettings 찾기 (= 설치 폴더 확정) ──────────────────────────────────
# 지정값 > 서비스 ImagePath > 표준 설치 위치 순으로 훑는다. 실제로 파일이 있는 폴더를 설치 폴더로 삼는다.
$candidateDirs = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($InstallDir)) { $candidateDirs.Add($InstallDir) }
$fromService = Get-InstallDirFromService
if (-not [string]::IsNullOrWhiteSpace($fromService)) { $candidateDirs.Add($fromService) }
$candidateDirs.Add((Join-Path $env:ProgramFiles 'DualSoft\DSPilot'))
$x86Root = ${env:ProgramFiles(x86)}
if (-not [string]::IsNullOrWhiteSpace($x86Root)) { $candidateDirs.Add((Join-Path $x86Root 'DualSoft\DSPilot')) }

$SettingsFile = Find-SettingsFile -Dirs $candidateDirs
if ($null -eq $SettingsFile) {
    throw ("appsettings 파일을 찾지 못했습니다. 찾아본 폴더: " + ($candidateDirs -join ' | ') +
           "  — -InstallDir 로 DSPilot.exe 가 있는 폴더를 직접 지정하세요.")
}
$InstallDir = Split-Path -Parent $SettingsFile

Write-Step '경로 확인'
Write-Host "   공유 폴더   : $SharedDir"
Write-Host "   설치 폴더   : $InstallDir"
Write-Host "   정본 DB     : $CanonicalDb"

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$settingsText = [System.IO.File]::ReadAllText($SettingsFile)

# "ConnectionString": "Data Source=...;..."  — Data Source 를 담은 첫 항목만 손댄다.
$csRegex = [regex]'("ConnectionString"\s*:\s*")([^"]*Data Source=[^"]*)(")'
$csMatch = $csRegex.Match($settingsText)
$currentCs = ''
if ($csMatch.Success) { $currentCs = $csMatch.Groups[2].Value }

Write-Step 'appsettings 점검'
Write-Host "   파일 : $SettingsFile"
if ($csMatch.Success) { Write-Host "   현재 : $currentCs" } else { Write-Warn 'Database:ConnectionString 을 찾지 못했습니다.' }

# Kpi:DbPath 가 박혀 있으면 KpiDb 가 그쪽을 우선하므로 분열이 그대로 남는다.
if ($settingsText -match '"DbPath"\s*:\s*"[^"]+"') {
    Write-Warn 'appsettings 에 Kpi:DbPath 가 있습니다 — 이 값이 공유 폴더를 덮어씁니다. 수동 확인이 필요합니다.'
}

# JSON 안에서는 역슬래시가 이스케이프된다.
$canonicalJsonPath = $CanonicalDb.Replace('\', '\\')
$newCs = 'Data Source=' + $canonicalJsonPath + ';Version=3;BusyTimeout=20000'
$needsSettingsFix = ($csMatch.Success -and $currentCs -ne $newCs)
if ($needsSettingsFix) { Write-Host "   변경 : $newCs" -ForegroundColor Yellow }
else { Write-Ok '연결 문자열은 이미 정본입니다.' }

# ── 2. 삭제 대상 수집 ───────────────────────────────────────────────────────
$searchDirs = New-Object System.Collections.Generic.List[string]
foreach ($d in @($SharedDir, $LegacyDir)) {
    if (-not [string]::IsNullOrWhiteSpace($d) -and -not $searchDirs.Contains($d)) { $searchDirs.Add($d) }
}
# 현재 연결 문자열이 가리키던 폴더도 포함(위 두 곳이 아닐 수 있다).
if ($currentCs -match 'Data Source=([^;]+)') {
    # 현장 연결 문자열은 %ProgramData%/DualSoft/DSPilot/plc.db 처럼 환경변수 + 슬래시 표기를 쓴다.
    # PowerShell 은 경로 안의 %VAR% 를 스스로 펼치지 않으므로 여기서 펼쳐야 Test-Path 가 걸린다.
    $curPath = [System.Environment]::ExpandEnvironmentVariables($Matches[1].Trim()).Replace('/', '\')
    $curDir  = Split-Path -Parent $curPath
    if (-not [string]::IsNullOrWhiteSpace($curDir) -and -not $searchDirs.Contains($curDir)) { $searchDirs.Add($curDir) }
}

$targets = New-Object System.Collections.Generic.List[string]
foreach ($dir in $searchDirs) {
    if (-not (Test-Path $dir)) { continue }
    foreach ($name in $LegacyNames) {
        foreach ($sfx in $Suffixes) {
            $p = Join-Path $dir ($name + $sfx)
            if (Test-Path $p) { $targets.Add($p) }
        }
    }
}

Write-Step '삭제 대상 DB'
if ($targets.Count -eq 0) { Write-Ok '삭제할 DB 파일이 없습니다.' }
foreach ($t in $targets) {
    $size = '{0:N1} KB' -f ((Get-Item $t).Length / 1KB)
    Write-Host "   - $t  ($size)"
}

if (-not $needsSettingsFix -and $targets.Count -eq 0) {
    Write-Host "`n고칠 것이 없습니다." -ForegroundColor Green
    return
}

# ── 3. 확인 ─────────────────────────────────────────────────────────────────
if ($DryRun) {
    Write-Host "`n[DryRun] 실제로는 아무것도 바꾸지 않았습니다." -ForegroundColor Magenta
    return
}
if (-not $Force) {
    Write-Host ''
    $answer = Read-Host '위 DB 파일을 백업 후 삭제하고 서비스를 재시작합니다. 계속할까요? (y/N)'
    if ($answer -ne 'y' -and $answer -ne 'Y') { Write-Host '취소했습니다.'; return }
}

# ── 4. 서비스 정지 ──────────────────────────────────────────────────────────
Write-Step '서비스 정지'
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $svc) {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', (New-TimeSpan -Seconds 60))
    }
    Write-Ok "$ServiceName 정지됨"
} else {
    Write-Warn "$ServiceName 서비스를 찾지 못했습니다 — 콘솔로 직접 실행 중이라면 먼저 종료하세요."
}

# 서비스를 멈춰도 콘솔로 직접 띄운 인스턴스가 남아 있으면 DB 파일을 잡고 있어 삭제가 실패한다.
# (진단하느라 DSPilot.exe 를 손으로 실행해 둔 상태가 흔하다.)
$stray = @(Get-Process -Name 'DSPilot' -ErrorAction SilentlyContinue)
if ($stray.Count -gt 0) {
    $pids = ($stray | ForEach-Object { $_.Id }) -join ', '
    throw "DSPilot 프로세스가 아직 실행 중입니다 (PID: $pids). 콘솔 창을 닫은 뒤 다시 실행하세요. 변경된 것은 없습니다."
}

# ── 5. 백업 ─────────────────────────────────────────────────────────────────
$stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $env:ProgramData ('DualSoft\backup\dspilot-dbfix-' + $stamp)
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

Write-Step '백업'
Copy-Item -Path $SettingsFile -Destination (Join-Path $backupDir (Split-Path -Leaf $SettingsFile)) -Force
foreach ($t in $targets) {
    # 같은 이름이 여러 폴더에 있으므로 폴더명을 접두어로 붙여 충돌을 막는다.
    $tag  = (Split-Path -Parent $t).Replace(':', '').Replace('\', '_')
    $dest = Join-Path $backupDir ($tag + '__' + (Split-Path -Leaf $t))
    Copy-Item -Path $t -Destination $dest -Force
}
Write-Ok "백업 위치: $backupDir"

# ── 6. appsettings 수정 ─────────────────────────────────────────────────────
if ($needsSettingsFix) {
    Write-Step 'appsettings 수정'
    # .NET Regex 치환에서 특수문자는 $ 뿐이다(역슬래시는 리터럴). 첫 1건만 바꾼다.
    $replacement = '${1}' + $newCs + '${3}'
    $updated = $csRegex.Replace($settingsText, $replacement, 1)
    [System.IO.File]::WriteAllText($SettingsFile, $updated, $Utf8NoBom)
    Write-Ok '연결 문자열을 공유 폴더로 정정했습니다.'
}

# ── 7. 구 DB 삭제 ───────────────────────────────────────────────────────────
Write-Step '구 DB 삭제'
foreach ($t in $targets) {
    Remove-Item -Path $t -Force
    Write-Host "   삭제: $t"
}
if (-not (Test-Path $SharedDir)) { New-Item -ItemType Directory -Path $SharedDir -Force | Out-Null }
Write-Ok '삭제 완료 — DSPilot 이 기동하면서 정본 DB 를 새로 만듭니다.'

# ── 8. 서비스 시작 ──────────────────────────────────────────────────────────
Write-Step '서비스 시작'
if ($null -ne $svc) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', (New-TimeSpan -Seconds 60))
    Write-Ok "$ServiceName 시작됨"
} else {
    Write-Warn 'DSPilot 을 직접 다시 실행하세요.'
}

# ── 9. 검증 ─────────────────────────────────────────────────────────────────
if ($Port -le 0) {
    $Port = 8080
    $hosting = Join-Path $InstallDir 'appsettings.Hosting.json'
    if (Test-Path $hosting) {
        $hostingText = [System.IO.File]::ReadAllText($hosting)
        if ($hostingText -match ':(\d{2,5})') { $Port = [int]$Matches[1] }
    }
}

Write-Step "검증 (http://localhost:$Port)"
Write-Host '   모델 주소가 잡히는지 최대 90초 확인합니다...'
$deadline = (Get-Date).AddSeconds(90)
$ok = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-RestMethod -Uri ("http://localhost:$Port/api/nav/summary") -TimeoutSec 10
        if ($r.agent.addrExpected -gt 0) {
            Write-Ok ("모델 주소 {0}개 인식, 수신 {1}개 · receivingData={2}" -f $r.agent.addrExpected, $r.agent.addrSeen, $r.receivingData)
            $ok = $true
            break
        }
    } catch { }
    Start-Sleep -Seconds 5
}

if ($ok) {
    Write-Host "`n복구 완료. 대시보드에서 신호가 들어오는지 확인하세요." -ForegroundColor Green
} else {
    Write-Warn '90초 안에 주소가 잡히지 않았습니다. 아래를 확인하세요.'
    Write-Host "   1) 서비스를 멈추고 콘솔로 직접 실행: `"$InstallDir\DSPilot.exe`""
    Write-Host '   2) 시작 로그의 "[Startup] KPI schema" / "[Startup] Eager schema creation" 두 줄의 경로가 같은지'
    Write-Host "   3) 백업은 $backupDir 에 있습니다."
}
