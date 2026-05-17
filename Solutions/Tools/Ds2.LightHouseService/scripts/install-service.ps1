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
  [string]$ListenUrl = "https://0.0.0.0:8443",
  [string]$ServiceName = "Ds2.LightHouseService",
  [string]$DisplayName = "Ds2 LightHouse KB Service"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath))      { throw "ExePath 미존재 — $ExePath" }
if (-not (Test-Path $TlsCertPath))  { throw "TlsCertPath 미존재 — $TlsCertPath" }
if ($ListenUrl -notmatch '^https://') { throw "listenUrl 은 https:// 만 허용 — $ListenUrl" }

# PSK / TLS cert password 평문 입력 → DPAPI(LocalMachine) 암호화
Add-Type -AssemblyName System.Security

$psk = Read-Host -Prompt "Pre-Shared Key (PSK)" -AsSecureString
$pskPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
  [Runtime.InteropServices.Marshal]::SecureStringToBSTR($psk))

$certPwd = Read-Host -Prompt "TLS 인증서 (.pfx) 비밀번호" -AsSecureString
$certPwdPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
  [Runtime.InteropServices.Marshal]::SecureStringToBSTR($certPwd))

function Protect-DpapiLocalMachine([string]$plain) {
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
  $cipher = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
  return [System.Convert]::ToBase64String($cipher)
}

$pskEnc = Protect-DpapiLocalMachine $pskPlain
$certPwdEnc = Protect-DpapiLocalMachine $certPwdPlain

# config.json 저장 — %PROGRAMDATA%\Dualsoft\LightHouseService\
$configDir = Join-Path $env:PROGRAMDATA "Dualsoft\LightHouseService"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$configPath = Join-Path $configDir "config.json"

$config = @{
  schemaVersion = 1
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
  indexerVersionRange = @{ min = "1.0.0"; max = "1.99.99" }
}

$config | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8
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
