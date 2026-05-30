<#
.SYNOPSIS
  LightHouseService 설치 — 빌드 산출물을 *설치본* 폴더로 복사 + PFX password 1회 입력으로
  generate-dev-cert + install-service 통합 호출 (설치본 exe 를 service binPath 로 등록).

.DESCRIPTION
  기존 Makefile install 은 (a) generate-dev-cert PFX password + (b) install-service PSK + (c) install-service
  TLS cert password 를 따로 prompt → 사용자가 동일 값을 여러 번 입력해야 하는데 mismatch 가 빈번 → service
  start 실패 (LoadPkcs12FromFile 가 잘못된 password 에서 hang → SCM Error 1053).
  본 wrapper 가 PFX password 를 1회만 prompt → generate-dev-cert / install-service 양쪽 + PSK 까지 동일 값 박제
  → mismatch 자체를 차단. PSK 동일 값 사용은 dev/PoC 한정 단순화 — 운영 배포는 PSK 와 PFX password 를 분리하여
  install-service.ps1 의 `-PskPlain <...>` / `-CertPasswordPlain <...>` 인자로 직접 호출 권장
  (RNG path 는 2026-05-27 폐기 — 사용자 입력만).
  관리자 권한 PowerShell 필요 (DPAPI LocalMachine + sc.exe).

  **설치본 기준 고정 (2026-05-30 사용자 결정)** — Promaker GUI 경로(LightHouseLocalInstaller.ResolveDeployment)
  와 동일하게 dev repo 빌드 출력을 binPath 로 쓰지 않는다 (clean 시 소멸 / 버전 불일치). 대신 빌드 산출물
  ($PublishDir) 을 설치본 폴더({InstallLocation}\LightHouseService) 로 복사한 뒤, 복사된 exe 를 binPath 로 등록.
  InstallLocation 조회는 LightHouseLocalInstaller.ResolveInstalledPromakerRoot 와 동일 레지스트리/GUID(Setup.iss AppId).

  또한 generate-dev-cert.ps1 의 "PFX exists → skip" 가드는 stale PFX 에 대해 새 password 가 박제되어
  mismatch 를 silent 로 유발 → 본 wrapper 가 기존 PFX 를 자동 삭제 후 재발급.

.PARAMETER PfxPath
  생성할 PFX 파일의 절대 경로.

.PARAMETER PublishDir
  dotnet publish 산출물 폴더 (dev repo). 본 스크립트가 설치본 폴더로 복사한 뒤 복사된 exe 를
  service binPath 로 등록한다. Promaker installer ('AI 기능 활성화' 컴포넌트) 미설치 시 throw.

.PARAMETER ListenUrl
  HTTPS bind URL. install-service.ps1 default 와 동일.

.EXAMPLE
  # Makefile install 이 호출하는 형태 (관리자 PowerShell)
  .\setup-cert-and-service.ps1 -PfxPath "C:\ProgramData\Dualsoft\LightHouseService\service.pfx" `
                               -PublishDir "F:\...\bin\Release\net9.0\publish"
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PfxPath,
  [Parameter(Mandatory=$true)][string]$PublishDir,
  [string]$ListenUrl = "https://127.0.0.1:8443"
)

$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$generateScript = Join-Path $scriptsDir "generate-dev-cert.ps1"
$installScript  = Join-Path $scriptsDir "install-service.ps1"
if (-not (Test-Path $generateScript)) { throw "generate-dev-cert.ps1 not found: $generateScript" }
if (-not (Test-Path $installScript))  { throw "install-service.ps1 not found:  $installScript" }

# ── 설치본 LightHouseService 폴더 확정 + 빌드 산출물 복사 (2026-05-30 사용자 결정) ──────
# `make install` 도 GUI 경로(LightHouseLocalInstaller.ResolveDeployment)와 동일하게 *설치본 기준*으로
# service 를 등록한다. 흐름: 빌드 산출물($PublishDir) → 설치본 폴더({InstallLocation}\LightHouseService)
# 복사 → 복사된 exe 를 binPath. dev repo 빌드 출력을 직접 binPath 로 쓰지 않음 (clean 시 소멸 / 버전 불일치).
if (-not (Test-Path $PublishDir)) { throw "PublishDir 미존재 — $PublishDir (먼저 dotnet publish 필요)." }

# InstallLocation 조회 — LightHouseLocalInstaller.ResolveInstalledPromakerRoot 와 동일 레지스트리/GUID.
# Setup.iss: AppId={{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31} → uninstall 키 "<GUID>_is1".
# 64-bit 모드 설치(ArchitecturesInstallIn64BitMode)이므로 64-bit view HKLM 만 조회. 미설치 / 값 없음 시 null.
function Resolve-InstalledServiceDir {
  $subKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31}_is1'
  $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key = $hklm.OpenSubKey($subKey)
    if ($null -eq $key) { return $null }
    try {
      $loc = $key.GetValue('InstallLocation')
      if ([string]::IsNullOrWhiteSpace($loc)) { return $null }
      return (Join-Path ($loc.TrimEnd('\', '/')) 'LightHouseService')
    } finally { $key.Dispose() }
  } finally { $hklm.Dispose() }
}

$installServiceDir = Resolve-InstalledServiceDir
if ($null -eq $installServiceDir) {
  throw "설치된 Promaker 를 찾지 못했습니다. Promaker installer 로 'AI 기능 활성화' 컴포넌트를 포함해 설치한 뒤 다시 시도하십시오 (make install 은 설치본 기준으로만 service 를 등록합니다)."
}
if (-not (Test-Path $installServiceDir)) {
  throw "설치 폴더에 LightHouseService 가 없습니다 — $installServiceDir. Promaker installer 의 'AI 기능 활성화' 컴포넌트를 포함해 재설치 후 재시도하십시오."
}

# 복사 전 service stop — 설치본 exe 가 실행 중이면 파일 잠김으로 복사 실패. install-service.ps1 이 이후
# stop+delete+create 를 다시 수행하므로 여기서는 파일 잠금 해제 목적의 stop 만 (delete 는 install-service 가).
$svc = Get-Service -Name 'Ds2.LightHouseService' -ErrorAction SilentlyContinue
if ($null -ne $svc -and $svc.Status -ne 'Stopped') {
  Write-Host "기존 service 실행 중 — 파일 복사를 위해 정지: Ds2.LightHouseService"
  Stop-Service -Name 'Ds2.LightHouseService' -Force
  $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# 빌드 산출물 → 설치본 폴더 overlay 복사 (installer 가 배치한 scripts\ 등은 $PublishDir 에 없으므로 보존).
Write-Host "빌드 산출물 복사: $PublishDir -> $installServiceDir"
Copy-Item -Path (Join-Path $PublishDir '*') -Destination $installServiceDir -Recurse -Force

# 이후 모든 단계는 설치본 exe 기준 — install-service.ps1 이 이 경로를 sc.exe binPath 로 등록.
$ExePath = Join-Path $installServiceDir 'Ds2.LightHouseService.exe'
if (-not (Test-Path $ExePath)) { throw "복사 후에도 exe 미발견 — $ExePath ($PublishDir 에 Ds2.LightHouseService.exe 없음)." }
Write-Host "service binPath 기준 exe (설치본): $ExePath"

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

    # ─── .cer (PEM) export — 일반 https client 호환용 archive ──────
    # **2026-05-27 박제** — enable-ai.ps1 (Promaker UI path) 는 .cer export 박제했으나 본 wrapper
    # (`make install` path) 가 누락 → PFX 재발급 시 .cer 가 옛 thumbprint 잔존 → 진단 어려움.
    # PEM 형식 의무 — Plan B 폐기 후에도 archive 용도 + 일반 https client 호환 위해 유지.
    # **메타리뷰 m8 (2026-05-27)** — enable-ai.ps1 의 동일 블록과 동기화 의무. helper module 분리는 별 phase.
    $cerPath = [System.IO.Path]::ChangeExtension($PfxPath, '.cer')
    $certForExport = $null
    try {
        $certForExport = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $plain)
        $b64 = [System.Convert]::ToBase64String($certForExport.RawData, [System.Base64FormattingOptions]::InsertLineBreaks)
        $pem = "-----BEGIN CERTIFICATE-----`n" + $b64 + "`n-----END CERTIFICATE-----`n"
        [System.IO.File]::WriteAllText($cerPath, $pem, [System.Text.UTF8Encoding]::new($false))
        Write-Host "  .cer (PEM) export 완료 — $cerPath (thumbprint=$($certForExport.Thumbprint))"
    } finally {
        if ($null -ne $certForExport) { $certForExport.Dispose() }
    }
    # icacls — Users 그룹 read (NODE_EXTRA_CA_CERTS 가 일반 사용자 권한에서 read 가능). .cer 은 public.
    & icacls $cerPath /grant "*S-1-5-32-545:R" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "icacls .cer Users:R 부여 실패 — Node client 가 .cer read 거부될 수 있음." }
    else { Write-Host "  icacls .cer Users:R 부여 완료." }
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
