<#
.SYNOPSIS
  Promaker UI "로컬 LightHouse 서비스 활성화" 트리거용 wrapper.
  4단계 sequential 실행 — Ollama / cert / install-service / firewall + service start.

.DESCRIPTION
  Promaker WPF 의 LightHouseLocalInstaller 가 UAC elevation (Verb=runas) 으로 호출.
  caller 는 ExePath / PskOutputPath / LogPath 를 명시 전달. cert password 와 PSK 는 본 스크립트가
  RNG 로 자동 생성 — PSK 평문 1회만 PskOutputPath 에 박제 (caller 가 read → LlmConfig DPAPI 박제
  → 즉시 wipe).

.PARAMETER ExePath
  Ds2.LightHouseService.exe 의 절대 경로. 운영 = {app}\LightHouseService\Ds2.LightHouseService.exe,
  개발 = Solutions/Tools/Ds2.LightHouseService/bin/Release/net9.0/publish/Ds2.LightHouseService.exe.

.PARAMETER PskOutputPath
  PSK base64 평문 1줄 박제 대상 (caller 가 즉시 read + wipe).

.PARAMETER LogPath
  Start-Transcript 출력 — 진단용. caller 가 사용자에게 표시 후 정리.
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$PskOutputPath,
  [Parameter(Mandatory=$true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'

$scriptsDir = $PSScriptRoot
$pfxPath    = Join-Path $env:PROGRAMDATA 'Dualsoft\LightHouseService\service.pfx'

Start-Transcript -Path $LogPath -Force | Out-Null

try {
    Write-Host "===== Promaker AI 활성화 시작 ====="
    Write-Host "  ExePath  : $ExePath"
    Write-Host "  PfxPath  : $pfxPath"
    Write-Host ""

    if (-not (Test-Path $ExePath)) {
        throw "ExePath 미존재 — $ExePath (installer 의 'AI' 컴포넌트 체크 후 재설치 또는 'make publish-lighthouse' 필요)"
    }

    # ─── [1/4] Ollama + bge-m3 ──────────────────────────────
    Write-Host "===== [1/4] Ollama + bge-m3 ====="
    & (Join-Path $scriptsDir 'install-ollama.ps1')
    if ($LASTEXITCODE -ne 0) { throw "install-ollama.ps1 실패 (exit $LASTEXITCODE)" }

    # ─── [2/4] self-signed cert ─────────────────────────────
    Write-Host ""
    Write-Host "===== [2/4] self-signed TLS cert ====="
    # cert password — RNG 18 byte → base64 24 char. PFX 비번 (DPAPI 박제 대상).
    $certRng = New-Object byte[] 18
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($certRng)
    $certPwd = [System.Convert]::ToBase64String($certRng)
    [Array]::Clear($certRng, 0, $certRng.Length)
    # 기존 PFX 가 있어도 매번 새로 생성 — install-service.ps1 가 매번 새 RNG password 를 DPAPI 박제하므로
    # 옛 PFX 의 password 와 mismatch 되어 service 가 PKCS12 load 시 unhandled exception → 1053 crash.
    # generate-dev-cert.ps1 의 skip 로직은 대화형 path 호환 — wrapper 가 강제 fresh start.
    if (Test-Path $pfxPath) {
        Remove-Item -Path $pfxPath -Force
        Write-Host "  기존 PFX 삭제 (password 정합 위해 재생성) — $pfxPath"
    }
    & (Join-Path $scriptsDir 'generate-dev-cert.ps1') -PfxPath $pfxPath -CertPasswordPlain $certPwd
    if ($LASTEXITCODE -ne 0) { throw "generate-dev-cert.ps1 실패 (exit $LASTEXITCODE)" }

    # ─── [3/4] install-service (sc create + DPAPI PSK) ──────
    Write-Host ""
    Write-Host "===== [3/4] LightHouseService 등록 (sc create + DPAPI PSK) ====="
    $installSvc = Join-Path $scriptsDir 'install-service.ps1'
    # -GeneratePsk + -PskOutputPath → install-service.ps1 가 RNG 32-byte → base64 → 파일에 직접 박제.
    # stdout/transcript 전혀 미경유 (PSK 평문 surface 최소화 — 자가검열 C1+C2 정합).
    & $installSvc -ExePath $ExePath -TlsCertPath $pfxPath -CertPasswordPlain $certPwd `
                  -GeneratePsk -PskOutputPath $PskOutputPath
    if ($LASTEXITCODE -ne 0) { throw "install-service.ps1 실패 (exit $LASTEXITCODE)" }

    # cert password 평문 즉시 비움 (managed string 잔존은 GC 한계).
    $certPwd = $null

    if (-not (Test-Path $PskOutputPath)) {
        throw "install-service.ps1 가 PSK output 파일을 박제하지 못함: $PskOutputPath"
    }
    Write-Host "  PSK output 박제 완료: $PskOutputPath"

    # ─── [4/4] firewall rule + service start ────────────────
    Write-Host ""
    Write-Host "===== [4/4] firewall rule + service start ====="
    # default listen = https://127.0.0.1:8443 (loopback). remoteip=127.0.0.1 로 한정 — 외부 노출 위험 0.
    & netsh advfirewall firewall delete rule name="Ds2 LightHouse Service" 2>&1 | Out-Null
    & netsh advfirewall firewall add rule name="Ds2 LightHouse Service" `
            dir=in action=allow protocol=tcp localport=8443 remoteip=127.0.0.1 profile=any | Out-Null
    Write-Host "  firewall rule 추가 — Ds2 LightHouse Service (8443/tcp, remoteip=127.0.0.1)"

    & sc.exe start Ds2.LightHouseService | Out-Null
    $startExit = $LASTEXITCODE
    if ($startExit -ne 0) {
        Write-Warning "sc.exe start 실패 (exit $startExit) — 'sc query Ds2.LightHouseService' 확인."
    } else {
        Write-Host "  Ds2.LightHouseService start OK."
    }

    # healthcheck — self-signed cert 신뢰 우회 (-k), /healthz GET 200 확인.
    # **curl.exe 사용 이유** (실측, 2026-05-26): Kestrel 가 TLS handshake 후 renegotiation 을 요청한다.
    # PowerShell 5.1 의 Invoke-WebRequest (.NET Framework HttpWebRequest) 는 default 로 client renegotiation
    # 차단 → "기본 연결이 닫혔습니다" fail. curl (schannel) 은 자동 처리 → 200 OK.
    # curl.exe 는 Windows 10 1803+ / 11 / Server 2019+ default 박제 — 본 코드 운영 환경 보장.
    # service crash (1053 / STOPPED) 면 polling 무의미 → 즉시 break + EventLog 진단 박제.
    Write-Host "  healthcheck polling (curl.exe) — 최대 30s (service STOPPED 감지 시 즉시 중단)..."
    $health = $false
    $abortedByCrash = $false
    $probeStart = Get-Date
    $lastErr = $null
    $script:stoppedStreak = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        # service state 확인 — STOPPED 상태가 2회 연속이면 부팅 실패로 판정 (start 직후 transient 회피).
        $q = & sc.exe query Ds2.LightHouseService 2>&1
        # KR Windows 의 sc.exe 는 한글 localized 출력 ("상태  : 1  STOPPED"). 영어/한글 무관 — `1\s+STOPPED` 패턴만.
        $isStopped = ($q -match ':\s*1\s+STOPPED')
        if ($isStopped) {
            if ($script:stoppedStreak) { $abortedByCrash = $true; break }
            $script:stoppedStreak = $true
        } else { $script:stoppedStreak = $false }
        # curl -k -s -o NUL -w "%{http_code}" — body 미사용, status code 만 출력. timeout 3s.
        # **NUL 박제 의무** (실측, 2026-05-26): PowerShell `$null` 인자 박제 시 빈 문자열로 변환되어
        # mingw curl (Git Bash 의 C:\Program Files\Git\mingw64\bin\curl.exe — Windows PATH 에서 자주 우선)
        # 이 `-o ""` 처리하면서 write-out 표현식 무시 + 'ok' 출력. Windows 의 'NUL' device 박제 의무.
        $code = & curl.exe -k -s -o NUL -w "%{http_code}" --max-time 3 https://127.0.0.1:8443/healthz 2>&1
        if ($LASTEXITCODE -eq 0 -and $code -eq '200') { $health = $true; break }
        $lastErr = "curl exit=$LASTEXITCODE code=$code"
    }
    $elapsed = [int]((Get-Date) - $probeStart).TotalSeconds
    if ($health)             { Write-Host    ("  healthcheck OK — elapsed " + $elapsed + "s") }
    elseif ($abortedByCrash) { Write-Warning ("service 부팅 실패 감지 (STOPPED, elapsed " + $elapsed + "s) — polling 중단") }
    else                     { Write-Warning ("healthcheck 실패 (" + $elapsed + "s) — last: " + $lastErr) }

    # 진단 보조 — sc query + EventLog tail transcript 박제. log 에서 STATE / stack trace 확인 가능.
    Write-Host ""
    Write-Host "----- sc query Ds2.LightHouseService -----"
    & sc.exe query Ds2.LightHouseService 2>&1 | ForEach-Object { Write-Host "  $_" }
    Write-Host "------------------------------------------"

    if (-not $health) {
        Write-Host ""
        Write-Host "----- Application EventLog tail (Ds2.LightHouseService / .NET Runtime) -----"
        try {
            $evs = Get-EventLog -LogName Application -Source 'Ds2.LightHouseService','.NET Runtime','Application Error' -Newest 5 -ErrorAction Stop
            foreach ($ev in $evs) {
                Write-Host ("  [" + $ev.TimeGenerated.ToString('HH:mm:ss') + "] " + $ev.Source + " / " + $ev.EntryType)
                ($ev.Message -split "`r?`n") | Select-Object -First 4 | ForEach-Object { Write-Host ("    " + $_) }
            }
        } catch {
            Write-Host ("  EventLog 조회 실패 — " + $_.Exception.Message)
        }
        Write-Host "---------------------------------------------------------------------------"
    }

    Write-Host ("START_RESULT=exit={0} health={1} elapsed={2}s crashed={3}" -f $startExit, $health, $elapsed, $abortedByCrash)

    Write-Host ""
    Write-Host "===== Promaker AI 활성화 완료 ====="
} finally {
    Stop-Transcript | Out-Null
}
