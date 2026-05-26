<#
.SYNOPSIS
  개발/PoC 용 self-signed TLS 인증서 생성 — Cert:\LocalMachine\My 발급 → service.pfx export.

.DESCRIPTION
  done-lighthouse-kb-server.md §3.7 / §6 m1 / §4.3 미확정 표 (s1-r0 결정 = self-signed PoC).
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
  # default SAN — `localhost` 와 `127.0.0.1` 모두 포함. .NET HttpClient 의 cert validation 은 SAN dNSName /
  # iPAddress 둘 다 매칭 필요 — IP literal 로 접속 (`https://127.0.0.1:8443`) 시 dNSName 만 박제된 cert 는 fail.
  # New-SelfSignedCertificate 가 `-DnsName "127.0.0.1"` 인자에서 IP 패턴을 자동 감지해 SAN.iPAddress 박제.
  [string[]]$DnsName = @('localhost','127.0.0.1'),
  [int]$ValidityYears = 2,
  # Promaker UI 자동 path: 평문 password 인자 — 비면 대화형 prompt.
  [string]$CertPasswordPlain
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

if (-not [string]::IsNullOrEmpty($CertPasswordPlain)) {
    $pwd = ConvertTo-SecureString -String $CertPasswordPlain -AsPlainText -Force
} else {
    $pwd = Read-Host -AsSecureString -Prompt "PFX password (will be DPAPI-encrypted by install-service.ps1)"
    if ($pwd.Length -eq 0) {
        throw "PFX password 빈 값 금지."
    }
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
Write-Host "  -> DnsName/SAN : $($DnsName -join ', ')"
Write-Host "  -> Expires     : $((Get-Date).AddYears($ValidityYears).ToString('yyyy-MM-dd'))"

# self-signed cert 를 LocalMachine\Root 에 자동 import — .NET HttpClient 의 cert validation 통과 정합.
# 미import 시 client 측 AuthenticationException ("원격 인증서가 잘못되었습니다") 으로 SSL handshake fail.
# 같은 thumbprint 가 이미 Root 에 있으면 Import-Certificate 가 no-op (정합).
$tmpCer = Join-Path $env:TEMP ("promaker-devcert-" + $cert.Thumbprint + ".cer")
try {
    Export-Certificate -Cert ("Cert:\LocalMachine\My\" + $cert.Thumbprint) -FilePath $tmpCer | Out-Null
    Import-Certificate -FilePath $tmpCer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Write-Host "  -> Trusted Root import OK — LocalMachine\\Root 에 등록 완료 ($($cert.Thumbprint))"
} finally {
    if (Test-Path $tmpCer) { Remove-Item $tmpCer -Force }
}

Write-Host ""
Write-Host "  Next step: install-service.ps1 (sc.exe register + config.json + DPAPI encrypt)"
Write-Host ""
Write-Host "  Optional — to silence cert trust warning in client / browser:"
Write-Host "    Export-Certificate -Cert 'Cert:\LocalMachine\My\$($cert.Thumbprint)' -FilePath dev-cert.cer"
Write-Host "    Import-Certificate -FilePath dev-cert.cer -CertStoreLocation Cert:\LocalMachine\Root"
