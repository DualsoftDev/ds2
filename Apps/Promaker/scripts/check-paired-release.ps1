# ============================================================
# check-paired-release.ps1 — paired-release drift detector
# ============================================================
# SSOT: Apps/Promaker/Docs/todo-lighthouse-kb-server.md s5d-r0
# (s1-r0 박제 정정 — AssemblyVersion 비교 → IndexerVersion.Current literal 비교)
#
# 목적:
#   /dist 진입 시점에 Promaker 가 동봉하는 Ds2.LightHouse lib 의
#   IndexerVersion.Current ("1.0.0") 이 LightHouseService 의
#   config.json.template 의 indexerVersionRange [min, max] 범위 안에
#   들어가는지 검증. 범위 밖이면 service 가 415 (gate fail) 를 반환
#   → 사용자의 KB upload 가 전부 실패하는 drift 회귀 차단.
#
# 검증 의미론:
#   F# 측 ZipImport.compareVersion (component-wise int compare) 와
#   정합. PowerShell 의 [System.Version] 비교가 동일 의미.
#
# Source SSOT:
#   - lib: Solutions/Core/Ds2.LightHouse/SqliteStore.fs 의
#     `module IndexerVersion` 안 `let Current = "x.y.z"` literal
#     (F# [<Literal>] 은 compile-time const inline → reflection 불가,
#      따라서 source regex 추출이 유일 SSOT)
#   - service config range: Solutions/Tools/Ds2.LightHouseService/
#     scripts/config.json.template 의 indexerVersionRange.{min,max}
#
# Exit codes:
#   0 — 호환 (IndexerVersion.Current ∈ [min, max])
#   1 — drift (범위 밖 / source 미존재 / regex 미매치 / JSON parse 실패)
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass `
#       -File check-paired-release.ps1 [-RepoRoot <path>]
#
#   RepoRoot 미지정 시 본 스크립트 위치 기준 자동 추정
#   ($PSScriptRoot/../../../ = light-house repo root).
# ============================================================

[CmdletBinding()]
param(
    [string]$RepoRoot = $null
)

$ErrorActionPreference = 'Stop'

# 1) RepoRoot 결정 — 미지정 시 본 스크립트 (= Apps/Promaker/scripts/) 의
#    상위 3 단계가 repo root.
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
}

$libSourcePath = Join-Path $RepoRoot 'Solutions\Core\Ds2.LightHouse\SqliteStore.fs'
$configTemplatePath = Join-Path $RepoRoot 'Solutions\Tools\Ds2.LightHouseService\scripts\config.json.template'

# 2) 존재 확인
if (-not (Test-Path -LiteralPath $libSourcePath)) {
    Write-Error "[paired-release] lib source not found: $libSourcePath"
    exit 1
}
if (-not (Test-Path -LiteralPath $configTemplatePath)) {
    Write-Error "[paired-release] service config template not found: $configTemplatePath"
    exit 1
}

# 3) IndexerVersion.Current literal 추출
#    SqliteStore.fs:14~16 의:
#      module IndexerVersion =
#          [<Literal>]
#          let Current = "1.0.0"
$libSource = [System.IO.File]::ReadAllText($libSourcePath)
$libRegex = 'module\s+IndexerVersion\b[\s\S]*?\[<Literal>\][\s\S]*?let\s+Current\s*=\s*"([^"]+)"'
$libMatch = [regex]::Match($libSource, $libRegex)
if (-not $libMatch.Success) {
    Write-Error "[paired-release] failed to extract IndexerVersion.Current from $libSourcePath (regex: $libRegex)"
    exit 1
}
$currentVer = $libMatch.Groups[1].Value.Trim()

# 4) config.json.template 의 indexerVersionRange 추출
try {
    $configJson = [System.IO.File]::ReadAllText($configTemplatePath) | ConvertFrom-Json
} catch {
    Write-Error "[paired-release] failed to parse JSON: $configTemplatePath — $($_.Exception.Message)"
    exit 1
}
if ($null -eq $configJson.indexerVersionRange) {
    Write-Error "[paired-release] indexerVersionRange missing in $configTemplatePath"
    exit 1
}
$minVer = $configJson.indexerVersionRange.min
$maxVer = $configJson.indexerVersionRange.max
if ([string]::IsNullOrWhiteSpace($minVer) -or [string]::IsNullOrWhiteSpace($maxVer)) {
    Write-Error "[paired-release] indexerVersionRange.min or .max missing/empty in $configTemplatePath"
    exit 1
}

# 5) [System.Version] 비교 — component-wise int compare 의미론 (F# ZipImport.compareVersion 정합)
try {
    $cur = [System.Version]::Parse($currentVer)
    $lo  = [System.Version]::Parse($minVer)
    $hi  = [System.Version]::Parse($maxVer)
} catch {
    Write-Error "[paired-release] version parse failed (current=$currentVer min=$minVer max=$maxVer): $($_.Exception.Message)"
    exit 1
}

# 6) 범위 검증
$inRange = ($cur -ge $lo) -and ($cur -le $hi)
Write-Host "[paired-release] IndexerVersion.Current = $currentVer  vs  indexerVersionRange = [$minVer, $maxVer]"
if ($inRange) {
    Write-Host "[paired-release] OK — Promaker 가 동봉하는 lib 가 service 의 호환 범위 안에 있음."
    exit 0
} else {
    Write-Error "[paired-release] DRIFT — IndexerVersion.Current ($currentVer) 가 service config 범위 [$minVer, $maxVer] 밖. dist 진행 시 KB upload 가 415 (gate fail) 로 차단됨. 둘 중 하나 조치:`n  (a) lib 의 IndexerVersion.Current 를 범위 안으로 조정 (SqliteStore.fs)`n  (b) service config 의 indexerVersionRange 를 확장 (config.json.template 및 운영 머신의 config.json)"
    exit 1
}
