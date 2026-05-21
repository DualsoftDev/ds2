<#
.SYNOPSIS
  Ds2.LightHouseService 설치 — PSK 평문 입력 → DPAPI(LocalMachine) 암호화 → config.json 저장 → sc.exe 등록.

.DESCRIPTION
  todo-lighthouse-kb-server.md Phase S1 / CR4 SSOT.
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
  [string]$DisplayName = "Ds2 LightHouse KB Service"
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

$pskBytes = Read-SecretAsBytes "Pre-Shared Key (PSK)"
$certPwdBytes = Read-SecretAsBytes "TLS 인증서 (.pfx) 비밀번호"

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
$binPath = "`"$ExePath`""
$scOut = & sc.exe create $ServiceName binPath= $binPath DisplayName= $DisplayName start= auto obj= LocalSystem 2>&1
if ($LASTEXITCODE -ne 0) {
  Write-Warning "sc.exe create 실패: $scOut"
  Write-Warning "기존 service 가 등록되어 있을 수 있음 — 'sc.exe delete $ServiceName' 후 재시도."
  exit 1
}

& sc.exe description $ServiceName "Ds2 LightHouse KB — central index + search host" | Out-Null
Write-Host "Service 등록 완료: $ServiceName"
Write-Host "시작: sc.exe start $ServiceName"
