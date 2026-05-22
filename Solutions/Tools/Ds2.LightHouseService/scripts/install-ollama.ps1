<#
.SYNOPSIS
  Ollama (embedding backend) 설치 + 모델 pull.

.DESCRIPTION
  LightHouse Service 의 indexer 는 embedding backend 로 Ollama 를 사용한다
  (default: http://localhost:11434, model=bge-m3, dim=1024 — Ds2.LightHouse.Cli SSOT).
  본 스크립트는 dev/PoC 환경 setup 의 사전 단계로 호출된다.

  절차:
    1) ollama.exe 검출 — PATH 또는 %LOCALAPPDATA%\Programs\Ollama\ollama.exe.
    2) 미설치 시 winget 으로 silent install (Ollama.Ollama).
    3) 지정 모델 pull (이미 존재하면 fast no-op).

  winget 미가용 환경에서는 https://ollama.com/download/OllamaSetup.exe 수동 설치 후 재실행.

.PARAMETER Model
  pull 할 embedding 모델 이름. default `bge-m3` (Ds2.LightHouse.Cli SSOT 와 정합).

.EXAMPLE
  .\install-ollama.ps1
  # → winget install Ollama.Ollama (필요 시) + ollama pull bge-m3
#>

[CmdletBinding()]
param(
    [string]$Model = 'bge-m3'
)

$ErrorActionPreference = 'Stop'

function Resolve-OllamaExe {
    $cmd = Get-Command ollama -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $local = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'
    if (Test-Path $local) { return $local }
    return $null
}

Write-Host '=== Ollama 설치 확인 ==='
$ollamaExe = Resolve-OllamaExe

if ($null -eq $ollamaExe) {
    Write-Host 'Ollama 미설치 — winget 으로 설치 시도.'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        Write-Error @'
winget 미가용. 다음 중 하나로 수동 설치 후 재실행:
  - https://ollama.com/download/OllamaSetup.exe
  - Microsoft Store 에서 winget (App Installer) 설치 후 재시도
'@
        exit 1
    }
    & winget install --id Ollama.Ollama -e --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Error "winget install Ollama.Ollama failed (exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
    $ollamaExe = Resolve-OllamaExe
    if ($null -eq $ollamaExe) {
        Write-Error 'winget 설치는 성공했으나 ollama.exe 검출 실패. 새 셸에서 재시도 권장.'
        exit 1
    }
    Write-Host "Ollama 설치 완료 — $ollamaExe"
} else {
    Write-Host "Ollama 이미 설치됨 — $ollamaExe"
}

Write-Host ''
Write-Host "=== 모델 pull: $Model ==="
& $ollamaExe pull $Model
if ($LASTEXITCODE -ne 0) {
    Write-Error "ollama pull $Model failed (exit $LASTEXITCODE). Ollama 서비스 가동 여부 확인."
    exit $LASTEXITCODE
}

# bge-m3 known issue — Flash Attention long-context FP16 정밀도 결함이 embedding 결과에 NaN 생성 →
# ollama 가 "failed to encode response: json: unsupported value: NaN" 으로 HTTP 500 반환 → 색인 abort.
# 회피책 = OLLAMA_FLASH_ATTENTION=false 환경변수 박제 후 ollama 재시작. search / indexer skill SKILL.md 와 정합.
Write-Host ''
Write-Host '=== OLLAMA_FLASH_ATTENTION=false 박제 (bge-m3 NaN 방지) ==='
# Machine scope 필수 — Ds2.LightHouseService 는 통상 LocalSystem 계정으로 등록되며 LocalSystem 은 install 실행 사용자
# 의 User scope env 를 받지 못함. User scope fallback 으로 박제하면 "박제 성공" 메시지가 false-positive (Service
# 입장에서 무효) → 진단 불가 상태로 회귀 (자가 검열 M-2 정합).
try {
    [Environment]::SetEnvironmentVariable('OLLAMA_FLASH_ATTENTION', 'false', 'Machine')
    Write-Host '  Machine scope 박제 성공 — system-wide 적용.'
} catch {
    Write-Warning "  Machine scope 박제 실패 — 관리자 권한이 필요합니다. ($($_.Exception.Message))"
    Write-Warning '  Ds2.LightHouseService (LocalSystem) 는 User scope env 를 받지 못하므로 fallback 박제하지 않습니다.'
    Write-Warning '  관리자 PowerShell 에서 본 스크립트를 재실행하세요.'
    exit 1
}
$env:OLLAMA_FLASH_ATTENTION = 'false'  # 현 process 에도 즉시 반영
Write-Host '  적용 시점: Ollama Desktop / Service 재시작 후. tray 종료 → 재실행 또는 "sc restart Ds2.LightHouseService" 권장.'

Write-Host ''
Write-Host "=== 완료 — model=$Model ready ==="
