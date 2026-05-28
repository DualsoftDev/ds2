# ============================================================
# check-paired-release.ps1 — paired-release drift detector
# ============================================================
# SSOT: Apps/Promaker/Docs/done-lighthouse-kb-server.md s5d-r0
# (s1-r0 박제 정정 — AssemblyVersion 비교 → IndexerVersion.Current literal 비교)
# (s6-r3 박제 보강 — s5d-M1 4-part version normalize / s5d-m1 Fail-Drift helper / s5d-m3 Resolve-Path 가드)
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
#   단 [System.Version] 은 1-4 part 모두 받지만 비교 시 short 쪽이 0 으로
#   padding 됨 ("1.0.0" < "1.0.0.1" — 4-part 가 더 큼). F# 측은 short 쪽
#   missing component 를 0 처리 (동일). 따라서 의미론 정합.
#   s5d-M1 보강: pre-release 등 4-part 외 SemVer 입력 시 명시 reject.
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
#
# 본 ps1 검증의 한계 (2026-05-28 fix ④ 박제):
#   본 script 는 *신규 build 의 lib + service config 정합*만 검증. 운영 환경의
#   *기존 collection*의 stale 여부 (예전 build 산출의 indexer_version 키 부재 / 범위 밖) 는
#   service Program.fs 의 startup sweep (2026-05-28 추가) 이 별도 검증 + Warn 로그 박제.
#   운영자가 service 기동 후 Logs/service-*.log 에서 'startup sweep:' 패턴 grep 하여
#   stale collection 인지. stale 인 경우 /indexer skill 의 caption 보존 자동 path 활용
#   (lighthouse-cli export-image-cache → wipe → 재색인 → import-image-cache → upload --reuse-kb).
# ============================================================

[CmdletBinding()]
param(
    [string]$RepoRoot = $null
)

$ErrorActionPreference = 'Stop'

# s5d-m1: Write-Error + exit 1 중복 (6회) 패턴 추출.
# Write-Error 가 -ErrorAction Stop 없이도 본 script 의 $ErrorActionPreference='Stop' 으로
# terminating error 가 되어 exit 1 가 실행 안 될 위험 — 명시 exit 으로 강제.
function Fail-Drift {
    param([Parameter(Mandatory=$true)][string]$Message)
    Write-Error "[paired-release] $Message" -ErrorAction Continue
    exit 1
}

# s5d-M1: SemVer pre-release / build metadata (1.0.0-beta, 1.0.0+build) 거부.
# [System.Version]::Parse 는 hyphen/plus 만나면 ArgumentException — 단 에러 메시지가 불친절.
# 명시 가드로 진단 가독성 향상.
function Assert-NumericVersion {
    param([Parameter(Mandatory=$true)][string]$Value, [Parameter(Mandatory=$true)][string]$Label)
    if ($Value -notmatch '^\d+(\.\d+){0,3}$') {
        Fail-Drift "$Label='$Value' 가 1-4 part numeric version 형식 아님 (SemVer pre-release / build metadata 등 미지원)."
    }
}

# 1) RepoRoot 결정 — 미지정 시 본 스크립트 (= Apps/Promaker/scripts/) 의
#    상위 3 단계가 repo root.
# s5d-m3 보강: Resolve-Path 가 path 미존재 시 throw 라 진단 메시지 빈약 →
# Test-Path 선검사 후 명시 메시지.
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $candidate = Join-Path $PSScriptRoot '..\..\..'
    if (-not (Test-Path -LiteralPath $candidate)) {
        Fail-Drift "RepoRoot 자동 추정 실패 — candidate path 부재: $candidate (스크립트 위치 = $PSScriptRoot)"
    }
    $RepoRoot = (Resolve-Path -LiteralPath $candidate).Path
} else {
    if (-not (Test-Path -LiteralPath $RepoRoot)) {
        Fail-Drift "지정된 -RepoRoot path 부재: $RepoRoot"
    }
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

$libSourcePath = Join-Path $RepoRoot 'Solutions\Core\Ds2.LightHouse\SqliteStore.fs'
$configTemplatePath = Join-Path $RepoRoot 'Solutions\Tools\Ds2.LightHouseService\scripts\config.json.template'

# 2) 존재 확인
if (-not (Test-Path -LiteralPath $libSourcePath)) {
    Fail-Drift "lib source not found: $libSourcePath"
}
if (-not (Test-Path -LiteralPath $configTemplatePath)) {
    Fail-Drift "service config template not found: $configTemplatePath"
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
    Fail-Drift "failed to extract IndexerVersion.Current from $libSourcePath (regex: $libRegex)"
}
$currentVer = $libMatch.Groups[1].Value.Trim()

# 4) config.json.template 의 indexerVersionRange 추출
try {
    $configJson = [System.IO.File]::ReadAllText($configTemplatePath) | ConvertFrom-Json
} catch {
    Fail-Drift "failed to parse JSON: $configTemplatePath — $($_.Exception.Message)"
}
if ($null -eq $configJson.indexerVersionRange) {
    Fail-Drift "indexerVersionRange missing in $configTemplatePath"
}
$minVer = $configJson.indexerVersionRange.min
$maxVer = $configJson.indexerVersionRange.max
if ([string]::IsNullOrWhiteSpace($minVer) -or [string]::IsNullOrWhiteSpace($maxVer)) {
    Fail-Drift "indexerVersionRange.min or .max missing/empty in $configTemplatePath"
}

# 5) [System.Version] 비교 — component-wise int compare 의미론 (F# ZipImport.compareVersion 정합)
# s5d-M1: 입력 형식 가드 후 parse.
Assert-NumericVersion -Value $currentVer -Label 'IndexerVersion.Current (lib)'
Assert-NumericVersion -Value $minVer     -Label 'indexerVersionRange.min (service config)'
Assert-NumericVersion -Value $maxVer     -Label 'indexerVersionRange.max (service config)'
try {
    $cur = [System.Version]::Parse($currentVer)
    $lo  = [System.Version]::Parse($minVer)
    $hi  = [System.Version]::Parse($maxVer)
} catch {
    Fail-Drift "version parse failed (current=$currentVer min=$minVer max=$maxVer): $($_.Exception.Message)"
}

# 6) 범위 검증 — `-ge`/`-le` 가 5.1 의 일부 환경에서 좌변 type 추론 quirk 로 false 반환 회귀
# (CompareTo 명시로 우회). [System.Version].CompareTo 는 component-wise int compare 보장.
$inRange = ($cur.CompareTo($lo) -ge 0) -and ($cur.CompareTo($hi) -le 0)
Write-Host "[paired-release] IndexerVersion.Current = $currentVer  vs  indexerVersionRange = [$minVer, $maxVer]"
if ($inRange) {
    Write-Host "[paired-release] OK — Promaker 가 동봉하는 lib 가 service 의 호환 범위 안에 있음."
    exit 0
} else {
    Fail-Drift "DRIFT — IndexerVersion.Current ($currentVer) 가 service config 범위 [$minVer, $maxVer] 밖. dist 진행 시 KB upload 가 415 (gate fail) 로 차단됨. 둘 중 하나 조치:`n  (a) lib 의 IndexerVersion.Current 를 범위 안으로 조정 (SqliteStore.fs)`n  (b) service config 의 indexerVersionRange 를 확장 (config.json.template 및 운영 머신의 config.json)"
}
