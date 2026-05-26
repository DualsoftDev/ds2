<#
.SYNOPSIS
  LightHouseService dev install — PFX password 1회 입력으로 generate-dev-cert + install-service 통합 호출.

.DESCRIPTION
  기존 Makefile install 은 (a) generate-dev-cert PFX password + (b) install-service PSK + (c) install-service
  TLS cert password 를 따로 prompt → 사용자가 동일 값을 여러 번 입력해야 하는데 mismatch 가 빈번 → service
  start 실패 (LoadPkcs12FromFile 가 잘못된 password 에서 hang → SCM Error 1053).
  본 wrapper 가 PFX password 를 1회만 prompt → generate-dev-cert / install-service 양쪽 + PSK 까지 동일 값 박제
  → mismatch 자체를 차단. PSK 동일 값 사용은 dev/PoC 한정 단순화 — 운영 배포는 install-service.ps1 의
  -GeneratePsk (RNG 32-byte base64) 직접 호출 권장.
  관리자 권한 PowerShell 필요 (DPAPI LocalMachine + sc.exe).

  또한 generate-dev-cert.ps1 의 "PFX exists → skip" 가드는 stale PFX 에 대해 새 password 가 박제되어
  mismatch 를 silent 로 유발 → 본 wrapper 가 기존 PFX 를 자동 삭제 후 재발급.

.PARAMETER PfxPath
  생성할 PFX 파일의 절대 경로.

.PARAMETER ExePath
  Ds2.LightHouseService.exe 의 절대 경로 (publish output).

.PARAMETER ListenUrl
  HTTPS bind URL. install-service.ps1 default 와 동일.

.EXAMPLE
  # Makefile install 이 호출하는 형태 (관리자 PowerShell)
  .\setup-cert-and-service.ps1 -PfxPath "C:\ProgramData\Dualsoft\LightHouseService\service.pfx" `
                               -ExePath "F:\...\publish\Ds2.LightHouseService.exe"
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PfxPath,
  [Parameter(Mandatory=$true)][string]$ExePath,
  [string]$ListenUrl = "https://127.0.0.1:8443"
)

$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$generateScript = Join-Path $scriptsDir "generate-dev-cert.ps1"
$installScript  = Join-Path $scriptsDir "install-service.ps1"
if (-not (Test-Path $generateScript)) { throw "generate-dev-cert.ps1 not found: $generateScript" }
if (-not (Test-Path $installScript))  { throw "install-service.ps1 not found:  $installScript" }

# PFX exists → 재발급 (skip 가드 우회). 기존 PFX 의 password 는 알 수 없으므로 stale.
if (Test-Path $PfxPath) {
  Write-Host "기존 PFX 감지 -- mismatch 회피를 위해 삭제 후 재발급: $PfxPath"
  Remove-Item $PfxPath -Force
}

# password 1회 prompt — SecureString. BSTR -> plain 은 generate-dev-cert / install-service 의
# -CertPasswordPlain / -PskPlain 인자에 전달. plain 은 함수 scope 만; finally 에서 BSTR ZeroFree.
$sec = Read-Host -Prompt "PFX password (1회 입력 -- generate / install / PSK 동일 박제)" -AsSecureString
if ($sec.Length -eq 0) { throw "password empty -- aborted." }

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
try {
  $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
  try {
    Write-Host ""
    Write-Host "=== generate-dev-cert.ps1 (PFX 재발급) ==="
    & $generateScript -PfxPath $PfxPath -CertPasswordPlain $plain
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
      throw "generate-dev-cert.ps1 failed (exit=$LASTEXITCODE)"
    }

    Write-Host ""
    Write-Host "=== install-service.ps1 (cert pw / PSK 동일 박제) ==="
    & $installScript -ExePath $ExePath -TlsCertPath $PfxPath -ListenUrl $ListenUrl `
                     -CertPasswordPlain $plain -PskPlain $plain
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
      throw "install-service.ps1 failed (exit=$LASTEXITCODE)"
    }
  } finally {
    # plain string 은 .NET string intern 가능성 — 명시 GC 트리거 (best-effort).
    $plain = $null
    [GC]::Collect()
  }
} finally {
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

Write-Host ""
Write-Host "=============================================================="
Write-Host "setup-cert-and-service complete. next:"
Write-Host "  sc.exe start Ds2.LightHouseService"
Write-Host "=============================================================="
