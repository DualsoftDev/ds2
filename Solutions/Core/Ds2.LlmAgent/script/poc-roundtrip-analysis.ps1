# PoC 1, 2 — Round-trip 시간 분해 + parallel tool_use 검출
#
# 사용법:
#   pwsh -File doc/poc-roundtrip-analysis.ps1 [-LogPath <path-or-glob>]
#
# 기본 LogPath = ../../../Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log*
# (RollingFileAppender 가 datePattern yyyyMMdd 를 base name 뒤에 붙여 ds2.log20260507 형태로 만들기 때문에
#  default 는 glob 으로 두고, 가장 최근 mtime 을 자동 선택. 특정 날짜를 보려면 인자로 직접 path 지정.)

param(
    [string]$LogPath = "../../../Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log*",
    [int]$RecentMinutes = 30,  # 최근 N 분 내 라인만 분석. 0 이면 전체.
    [switch]$ShowMessageGroups   # PoC 2 의 message-id 단위 multi tool_use 그룹 표 출력 (M-1)
)

# Glob 형태로 들어오면 가장 최근 mtime 의 파일을 선택 (M-2)
if ($LogPath -match '[\*\?]') {
    $candidate = Get-ChildItem -Path $LogPath -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $candidate) {
        Write-Error "No log file matched glob: $LogPath"
        exit 1
    }
    $LogPath = $candidate.FullName
} elseif (-not (Test-Path $LogPath)) {
    Write-Error "Log file not found: $LogPath"
    exit 1
}

# Console + 파일 입력 둘 다 UTF-8 (한글 출력 깨짐 방지)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== Source: $LogPath ==="
Write-Host ""

$cutoff = if ($RecentMinutes -gt 0) { (Get-Date).AddMinutes(-$RecentMinutes) } else { [datetime]::MinValue }

# 라인 형식: '2026-05-07 08:14:32.123 [DEBUG] RawStream {sep} {json}'
# log4net pattern 은 em-dash (U+2014) 를 쓰지만 향후 hyphen 변경 가능성 대비해 둘 다 매칭 (M-2).
$sep = '(?:[' + [char]0x2014 + '\-])'
$pattern = '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\s*\] (\w+) ' + $sep + ' (.*)$'

$rawLines = New-Object System.Collections.ArrayList
$toolCallLines = New-Object System.Collections.ArrayList

Get-Content $LogPath -Encoding UTF8 | ForEach-Object {
    if ($_ -match $pattern) {
        $ts = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
        if ($ts -lt $cutoff) { return }
        $logger = $Matches[3]
        $msg = $Matches[4]
        if ($logger -eq 'RawStream') {
            [void]$rawLines.Add([pscustomobject]@{ Ts = $ts; Msg = $msg })
        } elseif ($logger -eq 'ToolCall') {
            [void]$toolCallLines.Add([pscustomobject]@{ Ts = $ts; Msg = $msg })
        }
    }
}

if ($rawLines.Count -eq 0) {
    Write-Warning "No RawStream lines found. Promaker.LlmAgent.RawStream logger DEBUG 가 켜져 있는지 확인."
    exit 1
}

# RawStream 라인 → stream-json 이벤트 분류
$events = New-Object System.Collections.ArrayList
foreach ($r in $rawLines) {
    try {
        $obj = $r.Msg | ConvertFrom-Json -ErrorAction Stop
        $type = $obj.type
        $msgId = $null
        $toolUseCount = 0
        $toolUseNames = @()
        if ($type -eq 'assistant' -and $obj.message) {
            $msgId = $obj.message.id
            if ($obj.message.content) {
                foreach ($c in $obj.message.content) {
                    if ($c.type -eq 'tool_use') {
                        $toolUseCount++
                        $toolUseNames += $c.name
                    }
                }
            }
        }
        $toolResultCount = 0
        if ($type -eq 'user' -and $obj.message -and $obj.message.content) {
            foreach ($c in $obj.message.content) {
                if ($c.type -eq 'tool_result') { $toolResultCount++ }
            }
        }
        [void]$events.Add([pscustomobject]@{
            Ts = $r.Ts
            Type = $type
            MsgId = $msgId
            ToolUseCount = $toolUseCount
            ToolUseNames = ($toolUseNames -join ',')
            ToolResultCount = $toolResultCount
        })
    } catch {}
}

Write-Host "=== Stream events 요약 ==="
$events | Group-Object Type | Select-Object Name, Count | Format-Table -AutoSize

# ── PoC 2: parallel tool_use (line + message-id 두 단위 모두) ──────────────
# stream-json 은 단일 message 를 여러 라인으로 incremental delta emit. 라인 단위 카운트는
# parallel 을 놓칠 수 있어 message id 그룹핑이 SSOT (M-1 review 반영).
Write-Host ""
Write-Host "=== PoC 2: parallel tool_use 검출 (message-id 단위 — SSOT) ==="
$assistantEvents = $events | Where-Object { $_.Type -eq 'assistant' }

$msgGroups = $assistantEvents | Where-Object { $_.MsgId } | Group-Object MsgId | ForEach-Object {
    $g = $_.Group
    $totalTu = ($g | Measure-Object -Property ToolUseCount -Sum).Sum
    $allNames = ($g.ToolUseNames | Where-Object { $_ }) -join '|'
    [pscustomobject]@{
        MsgId = $_.Name
        Lines = $_.Count
        SumToolUse = $totalTu
        Names = $allNames
        FirstTs = ($g | Sort-Object Ts | Select-Object -First 1).Ts
    }
}

$multiToolMsgs = $msgGroups | Where-Object { $_.SumToolUse -gt 1 }
$singleToolMsgs = $msgGroups | Where-Object { $_.SumToolUse -eq 1 }
$noToolMsgs = $msgGroups | Where-Object { $_.SumToolUse -eq 0 }

Write-Host ("assistant message 총 {0} 개 — multi tool_use {1}, single tool_use {2}, text-only {3}" -f $msgGroups.Count, $multiToolMsgs.Count, $singleToolMsgs.Count, $noToolMsgs.Count)

if ($multiToolMsgs.Count -gt 0) {
    Write-Host "[OK] message-id 단위 다중 tool_use 발견 — Anthropic API parallel tool_use 동작:"
    $multiToolMsgs | Select-Object @{N='MsgId';E={$_.MsgId.Substring(0, [Math]::Min(12, $_.MsgId.Length))}}, @{N='Ts';E={$_.FirstTs.ToString('HH:mm:ss.fff')}}, SumToolUse, Names | Format-Table -AutoSize
} else {
    Write-Host "[NO] message 단위로도 parallel tool_use 미관측 — (a)/(c) 안 폐기 후보."
}

if ($ShowMessageGroups) {
    Write-Host ""
    Write-Host "=== 전체 message 그룹 (--ShowMessageGroups) ==="
    $msgGroups | Sort-Object FirstTs | Select-Object @{N='MsgId';E={$_.MsgId.Substring(0, [Math]::Min(12, $_.MsgId.Length))}}, @{N='Ts';E={$_.FirstTs.ToString('HH:mm:ss.fff')}}, Lines, SumToolUse, Names | Format-Table -AutoSize
}

# ── PoC 1: round-trip 시간 분해 ────────────────────────────────────────────
Write-Host ""
Write-Host "=== PoC 1: round-trip 분해 (LLM turn-around vs server-side dispatch) ==="

# user (tool_result) → 다음 assistant 사이 = LLM turn-around (prefill + reasoning + token gen)
$llmGaps = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $events.Count - 1; $i++) {
    if ($events[$i].Type -eq 'user' -and $events[$i+1].Type -eq 'assistant') {
        $gapMs = [int]($events[$i+1].Ts - $events[$i].Ts).TotalMilliseconds
        [void]$llmGaps.Add([pscustomobject]@{
            UserTs = $events[$i].Ts.ToString('HH:mm:ss.fff')
            NextAssistantTs = $events[$i+1].Ts.ToString('HH:mm:ss.fff')
            LlmGapMs = $gapMs
            ToolUseCount = $events[$i+1].ToolUseCount
        })
    }
}

if ($llmGaps.Count -gt 0) {
    Write-Host "tool_result emit → 다음 assistant 까지의 간격 (= LLM turn-around):"
    $llmGaps | Format-Table -AutoSize
    $llmTotal = ($llmGaps | Measure-Object -Property LlmGapMs -Sum).Sum
    $llmAvg = [math]::Round(($llmGaps | Measure-Object -Property LlmGapMs -Average).Average, 0)
    Write-Host ("총 LLM turn-around: {0} ms (n={1}, avg={2} ms)" -f $llmTotal, $llmGaps.Count, $llmAvg)
} else {
    Write-Host "user→assistant 쌍이 발견되지 않음. 세션이 비어 있거나 단일 turn 만 있음."
    $llmTotal = 0
}

# ToolCall server-side elapsedMs
Write-Host ""
$serverElapsed = New-Object System.Collections.ArrayList
foreach ($t in $toolCallLines) {
    if ($t.Msg -match 'elapsedMs=(\d+)') {
        [void]$serverElapsed.Add([int]$Matches[1])
    }
}
if ($serverElapsed.Count -gt 0) {
    $serverTotal = ($serverElapsed | Measure-Object -Sum).Sum
    $serverAvg = [math]::Round(($serverElapsed | Measure-Object -Average).Average, 0)
    $serverMax = ($serverElapsed | Measure-Object -Maximum).Maximum
    Write-Host ("server-side ToolCall elapsedMs: 총 {0} ms (n={1}, avg={2} ms, max={3} ms)" -f $serverTotal, $serverElapsed.Count, $serverAvg, $serverMax)
} else {
    Write-Host "ToolCall 라인 없음."
    $serverTotal = 0
}

# 비율
Write-Host ""
Write-Host "=== 결론 ==="
$grandTotal = $llmTotal + $serverTotal
if ($grandTotal -gt 0) {
    $llmRatio = [math]::Round($llmTotal / $grandTotal * 100, 1)
    $serverRatio = [math]::Round($serverTotal / $grandTotal * 100, 1)
    Write-Host ("LLM 측 비중: {0}% ({1} ms) | Server 측 비중: {2}% ({3} ms)" -f $llmRatio, $llmTotal, $serverRatio, $serverTotal)
    Write-Host ""
    if ($llmRatio -ge 90) {
        Write-Host "[가설 확정] LLM turn-around 가 압도적 — round-trip 압축 (batch tool / parallel tool_use) 의 ROI 가 큼."
    } elseif ($llmRatio -ge 70) {
        Write-Host "[가설 부분 확정] LLM 측이 주요 비용. round-trip 압축 효과 있음."
    } else {
        Write-Host "[가설 약화] server-side 비중이 의외로 큼 — 우선 server 측 병목 (Dispatcher hop / queueXxx) 점검 필요."
    }
}
