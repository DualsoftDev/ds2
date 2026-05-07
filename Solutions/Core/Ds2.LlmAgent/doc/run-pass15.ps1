# Pass 1.5 측정 helper
#
# 사용법:
#   pwsh -File doc/run-pass15.ps1 -Mode baseline
#   pwsh -File doc/run-pass15.ps1 -Mode treatment
#   pwsh -File doc/run-pass15.ps1 -Analyze
#
# Mode 'baseline' / 'treatment':
#   - 사용자에게 시나리오/trial 안내 console 출력
#   - 각 trial 시작/종료 timestamp 를 marker 파일에 기록
#   - 사용자는 chat 에 prompt 1개 입력 + 응답 완료 후 Enter
#   - 25 trial (5 시나리오 × 5 회) 또는 10 trial (-Smoke)
#   - 진입 전 git working tree 가 해당 mode 에 맞게 세팅되어 있는지 안내만 (자동 checkout X)
#
# Mode 'Analyze':
#   - marker 파일 + ds2.log 를 cross-reference 해 trial 별 통계 산출
#   - 결과 표를 doc/pass15-results-<timestamp>.md 로 저장

[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)]
    [ValidateSet('baseline', 'treatment')]
    [string]$Mode,

    [Parameter(ParameterSetName = 'Run')]
    [switch]$Smoke,                        # n=1 smoke (시나리오당 1 회) — Phase A 용

    [Parameter(ParameterSetName = 'Analyze', Mandatory = $true)]
    [switch]$Analyze,

    [string]$MarkerPath = "$PSScriptRoot/pass15-markers.tsv",
    [string]$LogGlob = "$PSScriptRoot/../../../../Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log*"
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$Scenarios = @(
    @{ Id = 'S1'; Name = '실린더 chain (PoC 1 재현)'; Prompt = '/f/Git/kwak/kwak/DsConcepts/*.md 숙지. 실린더 하나 전진 후퇴하는 시스템 만들어줘.' }
    @{ Id = 'S2'; Name = '독립 System 다발';          Prompt = 'Sys1, Sys2, Sys3, Sys4 4 개 system 을 한 번에 추가해줘. 서로 의존 없음.' }
    @{ Id = 'S3'; Name = '기존 System 의 ApiDef 다발 (S2 후 같은 chat)'; Prompt = 'Sys1 에 ADV, RET, IDLE, ERROR 4 개 ApiDef 추가해줘.' }
    @{ Id = 'S4'; Name = '실린더 2개 혼합';           Prompt = '실린더 두 개 (Cyl1, Cyl2) 만들어줘. 각각 전진/후퇴 work 와 arrow.' }
    @{ Id = 'S5'; Name = 'read-after-mutation (ceiling)'; Prompt = '현재 모델에 어떤 system 이 있는지 보고, 그 중 하나에 ''Cleanup'' 이라는 work 와 RESET api 를 추가해줘.' }
)

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host ('=' * 60) -ForegroundColor DarkCyan
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('=' * 60) -ForegroundColor DarkCyan
}

function Append-Marker {
    param([string]$Mode, [string]$Scenario, [int]$Trial, [string]$Phase)
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    $tab = [char]9
    $line = $ts + $tab + $Mode + $tab + $Scenario + $tab + $Trial + $tab + $Phase
    Add-Content -Path $MarkerPath -Value $line -Encoding UTF8
}

# === Run mode ===
if ($PSCmdlet.ParameterSetName -eq 'Run') {
    $trialPerScenario = if ($Smoke) { 1 } else { 5 }
    $totalTrial = $Scenarios.Count * $trialPerScenario

    Write-Section "Pass 1.5 — Mode = $Mode  (trial/scenario = $trialPerScenario, total = $totalTrial)"
    Write-Host ''
    Write-Host '사전 확인:' -ForegroundColor Yellow
    Write-Host '  1) Git working tree 가 해당 mode 의 코드 상태인지 확인하세요.'
    Write-Host '       baseline  = Pass 1 커밋 직전 (1.SystemPrompt.md 에 batching 절 없음)'
    Write-Host '       treatment = Pass 1 커밋 적용 (batching 절 있음)'
    Write-Host '  2) 빌드는 사전에 완료되어 있어야 합니다 (dotnet build ..)'
    Write-Host '  3) Promaker 실행 → 상단 ribbon "기타" → "유틸" → "LLM Chat (토글)" 으로 chat panel 열기'
    Write-Host ''
    $ready = Read-Host '준비 되셨으면 Enter (취소 = q)'
    if ($ready -eq 'q') { return }

    $idx = 0
    foreach ($s in $Scenarios) {
        for ($t = 1; $t -le $trialPerScenario; $t++) {
            $idx++
            Write-Section ("[{0}/{1}]  {2}  trial #{3}" -f $idx, $totalTrial, $s.Id, $t)
            Write-Host '시나리오:' -ForegroundColor Green -NoNewline
            Write-Host (' ' + $s.Name)
            Write-Host 'Prompt (chat 에 그대로 붙여넣기):' -ForegroundColor Green
            Write-Host ''
            Write-Host ('   ' + $s.Prompt) -ForegroundColor White
            Write-Host ''
            if ($s.Id -eq 'S2') {
                Write-Host '※ S2 trial 시작 전 *반드시* "새 채팅" (panel 토글)으로 빈 session 시작' -ForegroundColor Yellow
            } elseif ($s.Id -eq 'S3') {
                Write-Host '※ S3 는 직전 S2 와 *같은 chat session* 에서 이어서 입력 (Sys1 이 응답에 남아있어야 함)' -ForegroundColor Yellow
            } else {
                Write-Host '※ trial 시작 전 "새 채팅" (panel 토글)으로 빈 session 시작' -ForegroundColor Yellow
            }
            Write-Host ''
            $startSig = Read-Host '준비되면 Enter — 즉시 chat 에 prompt 입력. (skip = s)'
            if ($startSig -eq 's') {
                Append-Marker $Mode $s.Id $t 'skip'
                continue
            }
            Append-Marker $Mode $s.Id $t 'start'

            Write-Host '... assistant 응답 완료 대기 ...' -ForegroundColor DarkGray
            Read-Host '응답이 완료되었으면 Enter'
            Append-Marker $Mode $s.Id $t 'end'
            Write-Host ('  -> trial #{0} of {1} 종료. 다음 trial 진행.' -f $t, $s.Id) -ForegroundColor DarkGreen
        }
    }

    Write-Section "Mode = $Mode 완료. 모든 trial 의 timestamp 가 $MarkerPath 에 기록됨."
    Write-Host '다음 단계:'
    Write-Host '  - 다른 mode 도 진행하려면 git checkout 후 재실행'
    Write-Host '  - 모든 mode 완료 후 분석:'
    Write-Host '       pwsh -File doc/run-pass15.ps1 -Analyze'
    return
}

# === Analyze mode ===
Write-Section 'Pass 1.5 분석 시작'

if (-not (Test-Path $MarkerPath)) {
    Write-Error "Marker 파일 없음: $MarkerPath"
    exit 1
}

# Marker 파일 파싱
$markers = @()
$tab = [char]9
Get-Content $MarkerPath -Encoding UTF8 | ForEach-Object {
    $cols = $_ -split $tab
    if ($cols.Count -ge 5) {
        $markers += [PSCustomObject]@{
            Time = [datetime]::ParseExact($cols[0], 'yyyy-MM-dd HH:mm:ss.fff', $null)
            Mode = $cols[1]; Scenario = $cols[2]; Trial = [int]$cols[3]; Phase = $cols[4]
        }
    }
}

# 가장 최근 mtime 의 ds2.log* 파일
$logFile = Get-ChildItem -Path $LogGlob -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $logFile) { Write-Error "Log file not found: $LogGlob"; exit 1 }
Write-Host "Log: $($logFile.FullName)"

# 라인 형식 (poc-roundtrip-analysis.ps1 와 동일)
$sep = '(?:[' + [char]0x2014 + '\-])'
$logPattern = '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\s*\] (\w+) ' + $sep + ' (.*)$'

$logLines = New-Object System.Collections.ArrayList
Get-Content $logFile.FullName -Encoding UTF8 | ForEach-Object {
    if ($_ -match $logPattern) {
        $null = $logLines.Add([PSCustomObject]@{
            Ts = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
            Logger = $Matches[3]; Body = $Matches[4]
        })
    }
}

# trial 별 (start, end) pair 수집
$trials = @{}
foreach ($m in $markers) {
    $key = "$($m.Mode)~$($m.Scenario)~$($m.Trial)"
    if (-not $trials.ContainsKey($key)) {
        $trials[$key] = [PSCustomObject]@{ Mode = $m.Mode; Scenario = $m.Scenario; Trial = $m.Trial; Start = $null; End = $null }
    }
    if ($m.Phase -eq 'start') { $trials[$key].Start = $m.Time }
    elseif ($m.Phase -eq 'end') { $trials[$key].End = $m.Time }
}

# trial 별 통계 산출
$results = @()
foreach ($key in ($trials.Keys | Sort-Object)) {
    $t = $trials[$key]
    if ($null -eq $t.Start -or $null -eq $t.End) { continue }
    $window = $logLines | Where-Object { $_.Ts -ge $t.Start -and $_.Ts -le $t.End }

    # parallel tool_use 검출 — RawStream 의 assistant 라인에서 tool_use block 카운트
    $stream = $window | Where-Object { $_.Logger -eq 'RawStream' }
    $assistantLines = $stream | Where-Object { $_.Body -match '"type":"assistant"' }

    # message-id 그룹핑
    $msgGroups = @{}
    foreach ($a in $assistantLines) {
        if ($a.Body -match '"id":"(msg_[^"]+)"') {
            $mid = $Matches[1]
            $toolUseCount = ([regex]::Matches($a.Body, '"type":"tool_use"')).Count
            if (-not $msgGroups.ContainsKey($mid)) { $msgGroups[$mid] = 0 }
            $msgGroups[$mid] += $toolUseCount
        }
    }
    $multiToolMessages = ($msgGroups.Values | Where-Object { $_ -ge 2 }).Count
    $maxToolUse = if ($msgGroups.Count -gt 0) { ($msgGroups.Values | Measure-Object -Maximum).Maximum } else { 0 }

    # turn count = result 라인 수 (각 turn 종료가 1개 result 라인)
    $resultLines = $stream | Where-Object { $_.Body -match '"type":"result"' }
    $turnCount = $resultLines.Count

    # llm gap = 연속 turn 의 result→다음 user 사이는 사용자 입력 시간이라 제외.
    # 대신 trial 의 wall-clock 시간 - server-side ToolCall elapsedMs 합 ≒ LLM 시간 추정
    $wallMs = ($t.End - $t.Start).TotalMilliseconds

    $toolCallLines = $window | Where-Object { $_.Logger -eq 'ToolCall' }
    $toolElapsedTotal = 0
    foreach ($tc in $toolCallLines) {
        if ($tc.Body -match 'elapsedMs=(\d+)') { $toolElapsedTotal += [int]$Matches[1] }
    }

    $results += [PSCustomObject]@{
        Mode = $t.Mode; Scenario = $t.Scenario; Trial = $t.Trial
        WallMs = [int]$wallMs
        TurnCount = $turnCount
        MultiToolMsgs = $multiToolMessages
        MaxToolUse = $maxToolUse
        ToolElapsedMs = $toolElapsedTotal
    }
}

if ($results.Count -eq 0) { Write-Warning '분석할 trial 이 없습니다.'; return }

# 결과 표 출력 + 시나리오별 평균
Write-Host ''
Write-Host '=== Trial 별 결과 ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

Write-Host ''
Write-Host '=== 시나리오별 평균 (Mode 분리) ===' -ForegroundColor Cyan
$summary = $results | Group-Object Scenario, Mode | ForEach-Object {
    $g = $_.Group
    [PSCustomObject]@{
        Scenario = $g[0].Scenario
        Mode = $g[0].Mode
        N = $g.Count
        AvgWallSec = [math]::Round((($g | Measure-Object WallMs -Average).Average) / 1000.0, 1)
        AvgTurns = [math]::Round(($g | Measure-Object TurnCount -Average).Average, 1)
        AvgMultiMsgs = [math]::Round(($g | Measure-Object MultiToolMsgs -Average).Average, 1)
        MaxToolUse = ($g | Measure-Object MaxToolUse -Maximum).Maximum
    }
}
$summary | Sort-Object Scenario, Mode | Format-Table -AutoSize | Out-String | Write-Host

# Δ% (treatment vs baseline)
Write-Host '=== Δ% (treatment vs baseline) ===' -ForegroundColor Cyan
$delta = @()
$byScenario = $summary | Group-Object Scenario
foreach ($sg in $byScenario) {
    $b = $sg.Group | Where-Object Mode -eq 'baseline' | Select-Object -First 1
    $tr = $sg.Group | Where-Object Mode -eq 'treatment' | Select-Object -First 1
    if ($null -eq $b -or $null -eq $tr) { continue }
    $delta += [PSCustomObject]@{
        Scenario = $sg.Name
        WallSec_B = $b.AvgWallSec; WallSec_T = $tr.AvgWallSec
        WallDelta = if ($b.AvgWallSec -gt 0) { [math]::Round((($tr.AvgWallSec - $b.AvgWallSec) / $b.AvgWallSec) * 100, 1) } else { 'n/a' }
        Turns_B = $b.AvgTurns; Turns_T = $tr.AvgTurns
        TurnDelta = if ($b.AvgTurns -gt 0) { [math]::Round((($tr.AvgTurns - $b.AvgTurns) / $b.AvgTurns) * 100, 1) } else { 'n/a' }
    }
}
$delta | Format-Table -AutoSize | Out-String | Write-Host

# 결과 markdown 저장
$reportPath = "$PSScriptRoot/pass15-results-$(Get-Date -Format 'yyyyMMdd-HHmm').md"
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('# Pass 1.5 결과').AppendLine()
$null = $sb.AppendLine('## Trial 별').AppendLine('```')
$null = $sb.AppendLine(($results | Format-Table -AutoSize | Out-String))
$null = $sb.AppendLine('```').AppendLine()
$null = $sb.AppendLine('## 시나리오별 평균').AppendLine('```')
$null = $sb.AppendLine(($summary | Sort-Object Scenario, Mode | Format-Table -AutoSize | Out-String))
$null = $sb.AppendLine('```').AppendLine()
$null = $sb.AppendLine('## Δ% (treatment vs baseline, 음수 = 단축)').AppendLine('```')
$null = $sb.AppendLine(($delta | Format-Table -AutoSize | Out-String))
$null = $sb.AppendLine('```')
Set-Content -Path $reportPath -Value $sb.ToString() -Encoding UTF8
Write-Host "→ 리포트 저장: $reportPath" -ForegroundColor Green
