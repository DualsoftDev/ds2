<#
.SYNOPSIS
  Ds2.LightHouseService 설치 — PSK 평문 입력 → DPAPI(LocalMachine) 암호화 → config.json 저장 → sc.exe 등록.

.DESCRIPTION
  done-lighthouse-kb-server.md Phase S1 / CR4 SSOT.
  관리자 권한 필요 (DPAPI LocalMachine scope + sc.exe).

.PARAMETER ExePath
  Ds2.LightHouseService.exe 의 절대 경로 (publish output).

.PARAMETER TlsCertPath
  TLS 인증서 (.pfx) 절대 경로.

.PARAMETER ListenUrl
  HTTPS bind URL. default `https://0.0.0.0:8443`. plain HTTP 거부.

.EXAMPLE
  .\install-service.ps1 -ExePath "C:\Program Files\Dualsoft\LightHouseService\Ds2.LightHouseService.exe" `
                        -TlsCertPath "C:\ProgramData\Dualsoft\LightHouseService\service.pfx"
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$TlsCertPath,
  # review IM-10 (4/7 reviewer): default = loopback. 사내 LAN bind 의도 시 명시 지정 (`-ListenUrl https://<lan-ip>:8443`).
  # 0.0.0.0 default 는 의도치 않은 외부 노출 위험 — 운영자가 명시적으로 LAN IP 선택.
  [string]$ListenUrl = "https://127.0.0.1:8443",
  [string]$ServiceName = "Ds2.LightHouseService",
  [string]$DisplayName = "Ds2 LightHouse KB Service",
  # Promaker UI 자동 활성화 path (LightHouseLocalInstaller) + setup-cert-and-service.ps1 wrapper 의 1회 입력 path 양쪽 공통.
  # 둘 다 비면 기존 대화형 prompt (Read-Host SecureString) 유지.
  # **RNG path 폐기 (사용자 결정 2026-05-27)** — PSK 는 항상 사용자 입력. -GeneratePsk / -PskOutputPath 인자 삭제.
  [string]$PskPlain,
  [string]$CertPasswordPlain
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath))      { throw "ExePath 미존재 — $ExePath" }
if (-not (Test-Path $TlsCertPath))  { throw "TlsCertPath 미존재 — $TlsCertPath" }
if ($ListenUrl -notmatch '^https://') { throw "listenUrl 은 https:// 만 허용 — $ListenUrl" }

# PSK / TLS cert password 평문 입력 → DPAPI(LocalMachine) 암호화
# review IM-10 (4/7 reviewer): BSTR 변환 후 ZeroFreeBSTR 의무 — 평문이 unmanaged memory 에 잔존하면 dump 위험.
# **B17 (s6-r85, 15-reviewer Major)** — PtrToStringAuto 반환 managed string 평문 잔존 차단. BSTR (UTF-16LE)
# 을 char[] → UTF-8 byte[] 직접 변환 후 byte clear. managed string 단계 우회.
Add-Type -AssemblyName System.Security

function Read-SecretAsBytes([string]$prompt) {
  $sec = Read-Host -Prompt $prompt -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try {
    # BSTR 의 char count = `Marshal.ReadInt32(bstr, -4) / 2` (BSTR length prefix = byte 수).
    $len = [Runtime.InteropServices.Marshal]::ReadInt32($bstr, -4) / 2
    $chars = [char[]]::new($len)
    for ($i = 0; $i -lt $len; $i++) {
      $chars[$i] = [char][Runtime.InteropServices.Marshal]::ReadInt16($bstr, $i * 2)
    }
    try {
      $bytes = [System.Text.Encoding]::UTF8.GetBytes($chars)
      return ,$bytes  # unary `,` = single-element array unwrap 회피.
    } finally {
      [Array]::Clear($chars, 0, $chars.Length)
    }
  } finally {
    # BSTR 의 unmanaged memory 즉시 zero + free.
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
  }
}

function Protect-DpapiLocalMachineBytes([byte[]]$plain) {
  try {
    $cipher = [System.Security.Cryptography.ProtectedData]::Protect(
      $plain, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [System.Convert]::ToBase64String($cipher)
  } finally {
    # 평문 byte buffer 즉시 zero (B17 정합 — caller 단계 잔재 차단)
    [Array]::Clear($plain, 0, $plain.Length)
  }
}

# Promaker UI 자동 path: 인자로 들어온 평문을 그대로 사용 (byte 변환 후 즉시 zero).
# 대화형 path: 기존 Read-SecretAsBytes 로 SecureString prompt.
function ConvertTo-Utf8BytesAndClear([string]$plain) {
  if ([string]::IsNullOrEmpty($plain)) { throw "ConvertTo-Utf8BytesAndClear: empty plain text." }
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
  return ,$bytes
}

if (-not [string]::IsNullOrEmpty($PskPlain)) {
  $pskBytes = ConvertTo-Utf8BytesAndClear $PskPlain
} else {
  $pskBytes = Read-SecretAsBytes "Pre-Shared Key (PSK)"
}

if (-not [string]::IsNullOrEmpty($CertPasswordPlain)) {
  $certPwdBytes = ConvertTo-Utf8BytesAndClear $CertPasswordPlain
} else {
  $certPwdBytes = Read-SecretAsBytes "TLS 인증서 (.pfx) 비밀번호"
}

$pskEnc = Protect-DpapiLocalMachineBytes $pskBytes
$certPwdEnc = Protect-DpapiLocalMachineBytes $certPwdBytes

# config.json 저장 — %PROGRAMDATA%\Dualsoft\LightHouseService\
$configDir = Join-Path $env:PROGRAMDATA "Dualsoft\LightHouseService"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$configPath = Join-Path $configDir "config.json"

# s6-r54 M10 (보안 sweep) — %PROGRAMDATA%\Dualsoft\LightHouseService\ ACL 강화.
# default %PROGRAMDATA% ACL = Authenticated Users read + Users read. PSK / TLS cert pw / registry.json 보관
# 디렉토리이므로 Authenticated Users / Users read 제거. Administrators+SYSTEM 전용 (LocalSystem 으로 service
# 실행 → SYSTEM read/write 의무).
# /inheritance:r = parent (%PROGRAMDATA%) ACL 상속 차단 + 명시 grant 만 박제.
$icaclsOut = & icacls $configDir /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" 2>&1
if ($LASTEXITCODE -ne 0) {
  Write-Warning "icacls 실패 (ACL 미강화 — registry.json / PSK 평문 노출 risk): $icaclsOut"
} else {
  Write-Host "ACL 강화 완료: $configDir (Administrators+SYSTEM only)"
}

# s6-r66 D-S7-4: schemaVersion 4 + multiTenant 섹션 박제 (default mode="T1" flat — 현행 동작 유지).
# in-place migration chain 제거 (s6-r66 scope 확장) — schemaVersion ≠ 4 의 stale config 는 reinstall 의무.
$config = @{
  schemaVersion = 4
  listenUrl = $ListenUrl
  tlsCertPath = $TlsCertPath
  tlsCertPasswordEncrypted = $certPwdEnc
  preSharedKeyEncrypted = $pskEnc
  storageRoot = "$env:PROGRAMDATA\Dualsoft\LightHouseService"
  maxUploadBytes = 10737418240
  zipBombRatioLimit = 50
  sessionIdleTtlMinutes = 240
  stagingSweepIntervalMinutes = 10
  logRetentionDays = 30
  logMaxSizeMB = 100
  auditRetentionDays = 365
  indexerVersionRange = @{ min = "1.0.0"; max = "2.99.99" }
  embedding = @{
    enabled = $false
    baseUrl = "http://localhost:11434"
    model = "bge-m3"
    dimension = 1024
  }
  mtls = @{
    mode = "off"
    allowedThumbprints = @()
  }
  multiTenant = @{
    mode = "T1"
  }
}

# review IM-10 (4/7 reviewer): PowerShell 5.1 의 Set-Content -Encoding UTF8 은 BOM 동봉 → System.Text.Json
# 은 BOM tolerant 하지만 외부 reader (jq / 일부 editor) 가 깨질 수 있음. WriteAllText 의 default UTF8Encoding(false) 사용.
$configJson = $config | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($configPath, $configJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "config.json 저장: $configPath"

# EventLog Source 등록 (log4net EventLogAppender 의 첫 호출 권한 회피, review M2)
if (-not [System.Diagnostics.EventLog]::SourceExists("Ds2.LightHouseService")) {
  New-EventLog -LogName Application -Source "Ds2.LightHouseService"
  Write-Host "EventLog Source 등록: Ds2.LightHouseService"
}

# sc.exe create — Windows Service 등록. obj= LocalSystem 명시 (review M1, default 와 동일 + drift 차단)
# 기존 등록 service 가 있으면 자동 cleanup (Promaker UI 재활성화 path 정합 — 동일 service 재등록은 'already exists' 로 fail).
$existing = & sc.exe query $ServiceName 2>&1
if ($LASTEXITCODE -eq 0) {
  Write-Host "기존 service 감지 — stop + delete polling 후 재등록."
  & sc.exe stop $ServiceName 2>&1 | Out-Null
  # STOPPED 까지 polling — Kestrel + SQLite 자원 해제 시간 보장. 최대 30s
  # (10s → 30s 강화 — 2026-05-27 사고 D1 backlog 흡수: bge-m3 캐시 / SQLite WAL flush 가 느릴 때 fail).
  for ($i = 0; $i -lt 60; $i++) {
    $q = & sc.exe query $ServiceName 2>&1
    if ($q -match 'STATE\s*:\s*1\s+STOPPED') { break }
    Start-Sleep -Milliseconds 500
  }
  & sc.exe delete $ServiceName 2>&1 | Out-Null
  # SCM 에서 service 가 사라질 때까지 polling — 'marked for deletion' 잔존 회피. 최대 30s.
  # `sc query` 의 LASTEXITCODE 가 1060 (service does not exist) 으로 바뀔 때까지 polling.
  for ($i = 0; $i -lt 60; $i++) {
    & sc.exe query $ServiceName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { break }
    Start-Sleep -Milliseconds 500
  }
}

# ─── sc.exe create + 1072 retry loop ──────────────────────────────────
# **2026-05-27 사고 박제 (done-lighthouse-next-session.md D1)** — sc delete 후 polling 까지 통과해도
# `sc create` 시 1072 (ERROR_SERVICE_MARKED_FOR_DELETE) 빈발. 사유: SCM 내부의 service handle
# refcount 가 polling 의 `sc query` (handle open → close) 자체로 다시 1 이 되어 잔존 race,
# 또는 외부 process (services.msc / Process Explorer / 보안 SW agent / Promaker WPF 의 LightHouseClient
# HttpClient connection pool) 의 handle 잠금이 늦게 풀림.
# **fix**: 1072 감지 시 5s 간격 × 최대 6회 (30s) 재시도. SCM 의 internal cleanup 대기.
# 다른 에러 (binPath 오류 / 권한 부족 등) 는 즉시 fail (retry 불의미).
$binPath = "`"$ExePath`""
$scOut = $null
$createOk = $false
$maxCreateAttempts = 6
$createDelayMs = 5000
for ($i = 1; $i -le $maxCreateAttempts; $i++) {
  $scOut = & sc.exe create $ServiceName binPath= $binPath DisplayName= $DisplayName start= auto obj= LocalSystem 2>&1
  if ($LASTEXITCODE -eq 0) {
    $createOk = $true
    if ($i -gt 1) { Write-Host "  -> sc create 성공 (attempt $i/$maxCreateAttempts)" }
    break
  }
  $outText = ($scOut | Out-String).Trim()
  # 1072 (marked-for-deletion) 만 retry. 한글 환경 '지정된 서비스가 지워진 것으로 표시되었습니다' / 영문 'marked for deletion' 둘 다 검출.
  $isMarked = ($outText -match '1072') -or ($outText -match 'marked for deletion') -or ($outText -match '지워진 것으로 표시')
  if (-not $isMarked) {
    Write-Warning "sc.exe create 실패 (non-retryable): $outText"
    exit 1
  }
  if ($i -lt $maxCreateAttempts) {
    Write-Host "  -> sc create attempt $i/$maxCreateAttempts — 1072 (marked-for-deletion) 잔존, $($createDelayMs)ms 후 재시도..."
    Start-Sleep -Milliseconds $createDelayMs
  }
}
if (-not $createOk) {
  Write-Warning ""
  Write-Warning "═══════════════════════════════════════════════════════════════════"
  Write-Warning "sc.exe create 실패 — 1072 (ERROR_SERVICE_MARKED_FOR_DELETE) 지속."
  Write-Warning "$maxCreateAttempts 회 × $($createDelayMs / 1000)s 재시도 ($($maxCreateAttempts * $createDelayMs / 1000)s) 후에도 SCM 의 marked-for-deletion 상태가 해소되지 않았습니다."
  Write-Warning ""
  Write-Warning "원인 후보 (service handle 을 붙들고 있는 외부 process):"
  Write-Warning "  1. services.msc — 열려 있으면 닫기"
  Write-Warning "  2. Process Explorer / Process Hacker / Sysinternals 등 service 감시 도구 — 종료"
  Write-Warning "  3. 보안 SW (CrowdStrike / Defender ATP / EDR agent) — service hook 잠금 가능"
  Write-Warning "  4. Promaker WPF 가 LightHouseClient HttpClient connection pool 을 닫지 않음"
  Write-Warning ""
  Write-Warning "해결 단계:"
  Write-Warning "  a) 위 도구들 닫기 + Promaker 종료"
  Write-Warning "  b) 1분 대기 (SCM cleanup) 후 'Local 서비스 설치/재설치' 버튼 재시도"
  Write-Warning "  c) 그래도 실패하면 PC 재부팅 후 재시도 (SCM service entry 전면 cleanup)"
  Write-Warning ""
  Write-Warning "마지막 sc.exe stdout: $($scOut | Out-String)"
  Write-Warning "═══════════════════════════════════════════════════════════════════"
  exit 1
}

& sc.exe description $ServiceName "Ds2 LightHouse KB — central index + search host" | Out-Null
Write-Host "Service 등록 완료: $ServiceName"
Write-Host "시작: sc.exe start $ServiceName"
