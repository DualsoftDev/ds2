<#
.SYNOPSIS
  개발/PoC 용 self-signed TLS 인증서 생성 — Cert:\LocalMachine\My 발급 → service.pfx export.

.DESCRIPTION
  todo-lighthouse-kb-server.md §3.7 / §6 m1 / §4.3 미확정 표 (s1-r0 결정 = self-signed PoC).
  운영 배포는 사내 CA 발급 권장. 본 스크립트는 dev/PoC 환경 setup 단계용.

.PARAMETER PfxPath
  생성할 PFX 파일의 절대 경로. config.json.template 의 tlsCertPath 와 동일해야 함.

.PARAMETER DnsName
  인증서의 Subject CN / SAN. default `localhost`.

.PARAMETER ValidityYears
  유효 기간 (년). default 2.

.EXAMPLE
  # 관리자 PowerShell
  .\generate-dev-cert.ps1
  # → C:\ProgramData\Dualsoft\LightHouseService\service.pfx 생성 + PFX password 대화형 입력

  .\generate-dev-cert.ps1 -DnsName "service.company.local"
  # → 사내 hostname 으로 CN
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
  [string]$PfxPath = "C:\ProgramData\Dualsoft\LightHouseService\service.pfx",
  [string]$DnsName = "localhost",
  [int]$ValidityYears = 2
)

$ErrorActionPreference = 'Stop'

$dir = Split-Path $PfxPath -Parent
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Write-Host "  -> Created directory: $dir"
}

if (Test-Path $PfxPath) {
    Write-Host "  -> PFX already exists: $PfxPath  (skip — delete the file to regenerate)"
    return
}

$pwd = Read-Host -AsSecureString -Prompt "PFX password (will be DPAPI-encrypted by install-service.ps1)"
if ($pwd.Length -eq 0) {
    throw "PFX password 빈 값 금지."
}

$cert = New-SelfSignedCertificate `
    -DnsName $DnsName `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears($ValidityYears)

Export-PfxCertificate `
    -Cert ("Cert:\LocalMachine\My\" + $cert.Thumbprint) `
    -FilePath $PfxPath `
    -Password $pwd | Out-Null

Write-Host ""
Write-Host "  -> PFX created : $PfxPath"
Write-Host "  -> Thumbprint  : $($cert.Thumbprint)"
Write-Host "  -> DnsName     : $DnsName"
Write-Host "  -> Expires     : $((Get-Date).AddYears($ValidityYears).ToString('yyyy-MM-dd'))"
Write-Host ""
Write-Host "  Next step: install-service.ps1 (sc.exe register + config.json + DPAPI encrypt)"
Write-Host ""
Write-Host "  Optional — to silence cert trust warning in client / browser:"
Write-Host "    Export-Certificate -Cert 'Cert:\LocalMachine\My\$($cert.Thumbprint)' -FilePath dev-cert.cer"
Write-Host "    Import-Certificate -FilePath dev-cert.cer -CertStoreLocation Cert:\LocalMachine\Root"
