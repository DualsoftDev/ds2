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
  # default SAN — `localhost` (DNS) + `127.0.0.1` (IP) 모두 포함.
  # 인자 자체는 후방호환 위해 `-DnsName` name 유지 — 실제 박제는 본 스크립트가 IPv4/IPv6 regex 판정 후
  # dNSName 과 iPAddress 로 분리 (아래 BuildSubjectAltNameExt 참조). 호출자는 그냥 host string 만 박제.
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

# ─── SAN OID 2.5.29.17 직접 박제 (Subject Alternative Name) ──────────────
# **2026-05-27 사고 박제** — `New-SelfSignedCertificate -DnsName` 는 모든 인자값을 dNSName type
# 으로만 박제 (IP literal 도 string match — IP 자동 감지 안 함). 결과 cert SAN 은 `DNS:127.0.0.1`
# 가 되어 Node OpenSSL (Claude CLI 포함) 의 RFC 6125 strict 검증에서 `https://127.0.0.1` 접속 시
# iPAddress 매칭 0건 → TLS handshake fail. .NET SChannel (HttpClient) 은 관대해서 dNSName 의 IP
# string 도 매칭 — Promaker LightHouseClient 의 `/sessions` 호출은 통과 → Node 측만 fail 발견 지연.
# **fix**: `-DnsName` 사용 폐기, `-TextExtension "2.5.29.17={text}DNS=...&IPAddress=..."` 로
# SAN 본문 직접 박제. IPv4 / IPv6 regex 판정 후 자동 분리.
# **메타리뷰 m6 (2026-05-27)** — IPv6 regex 강화. 기존 `^[0-9a-fA-F:]+:[0-9a-fA-F:]*$` 는 `:::` 같은 비정상도 통과.
# `[System.Net.IPAddress]::TryParse` 가 정공이지만 본 스크립트의 인자 단순 분류만 필요 → form check 강화.
$ipv4Regex = '^(\d{1,3}\.){3}\d{1,3}$'
$ipv6Regex = '^([0-9a-fA-F]{1,4}:){1,7}[0-9a-fA-F]{1,4}$|^::1$|^::$'
$sanParts = @()
foreach ($h in $DnsName) {
    if ($h -match $ipv4Regex -or $h -match $ipv6Regex) {
        $sanParts += "IPAddress=$h"
    } else {
        $sanParts += "DNS=$h"
    }
}
$sanText = "2.5.29.17={text}" + ($sanParts -join '&')

# **2026-05-27 사고 박제 (2차)** — Basic Constraints (OID 2.5.29.19) CA:TRUE 박제.
# `New-SelfSignedCertificate` 의 default 는 leaf cert (Basic Constraints 자체 미포함 = CA:FALSE) →
# Node.js OpenSSL 의 RFC 5280 strict chain validation 에서 "self-signed leaf 는 trusted root 될 수 없음"
# → `DEPTH_ZERO_SELF_SIGNED_CERT` 거부 → Claude CLI mcp lighthouse "failed" 사고.
# .NET SChannel (Windows HttpClient / Promaker LightHouseClient) 는 관대 — leaf 도 root 인정 → 사고 발견 지연.
# fix: Basic Constraints CA:TRUE 박제 + Trusted Root 에 import (기존 path 그대로) → self-signed cert 가
# 본인 chain 의 root CA 역할 → Node OpenSSL 통과. dev/PoC 환경 한정, 운영은 사내 CA 발급 권장 그대로.
$caText = "2.5.29.19={text}CA=TRUE"

# Subject CN — 첫 번째 dNSName entry 사용. 전부 IP 면 첫 IP literal 그대로 (X.500 CN 은 free string 허용).
$cnHost = ($DnsName | Where-Object { -not ($_ -match $ipv4Regex -or $_ -match $ipv6Regex) } | Select-Object -First 1)
if ([string]::IsNullOrEmpty($cnHost)) { $cnHost = $DnsName[0] }

$cert = New-SelfSignedCertificate `
    -Subject "CN=$cnHost" `
    -TextExtension @($sanText, $caText) `
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
Write-Host "  -> Subject CN  : $cnHost"
Write-Host "  -> SAN 박제    : $($sanParts -join ', ')   # iPAddress vs dNSName 분리 확인 — 회귀 가드"
Write-Host "  -> Basic Constraints: CA=TRUE   # Node OpenSSL self-signed root 신뢰 의무 — 회귀 가드"
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
