using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Runtime.Engine.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>HomingCommand 가 시작한 1회성 원위치 세션. true 이면 OnHomingPhaseCompleted 가
    /// 정상 흐름 대신 자동 StopSimulation 으로 빠진다.</summary>
    private bool _homingOnlyMode;

    /// <summary>원위치 버튼이 "눌린" 상태 — XAML 의 시각 피드백(누름 색)과 양방향 바인딩.
    /// IsHomingButtonHotEnabled 가 자기 세션 도중 enabled 유지되도록 함께 알림.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    private bool _isHomingPressed;

    /// <summary>원위치 push-button 의 "누름" 진입 핸들러 (deadman switch 시작).
    /// 누른 시점에 hub+PLC 게이트웨이를 띄우고 자동 원위치 페이즈를 시작한다.
    /// 이 호출 후 사용자는 버튼을 *놓을 때까지* 누르고 있어야 한다 — 떼는 순간 EndHoming 이 STOP 한다.
    /// Control + 실 PLC 연결 + 정지 상태에서만 의미 있음 (XAML 가시성/Enabled 가 강제).</summary>
    public void BeginHoming()
    {
        if (!CanBeginHoming()) return;

        AddSimLog("원위치 시작 (버튼 누름) — 실 PLC 라인 home 복귀 진행", LogSeverity.System);
        _homingOnlyMode = true;
        IsHomingPressed = true;

        StartSimulation();

        if (_simEngine is null)
        {
            // Hub/PLC 시작 실패 — StartSimulation 내부에서 이미 로그/오류 기록됨.
            AddSimLog("원위치 중단 — 엔진 초기화 실패", LogSeverity.Error);
            _homingOnlyMode = false;
            IsHomingPressed = false;
            return;
        }

        // 진단용 — 엔진이 본 OUT 주소 카운트, homing 진입 여부.
        var outCount = _simEngine.IOMap.OutAddressToMappings.Count;
        var inCount = _simEngine.IOMap.InAddressToMappings.Count;
        AddSimLog(
            $"엔진 시작 OK — IO map: OUT={outCount}, IN={inCount}, homingPhase={IsHomingPhase}",
            LogSeverity.System);

        // 진단 — engine 의 home 관련 정보가 모델에서 얼마나 채워졌는지 dump.
        // (모델 구조가 적절하면 자동 plan 동작; 비어있으면 길 B 로 가도 식별 정보 없음)
        try
        {
            var idx = _simEngine.Index;
            var iom = _simEngine.IOMap;
            var totalWorks = idx.AllWorkGuids.Length;
            var totalCalls = idx.AllCallGuids.Length;

            // findInitialFlagRxWorkGuids — "fresh-play 시 Finish 상태여야 하는" Work
            var finishedFlag = SimIndexModule.findInitialFlagRxWorkGuids(idx);

            // computeAutoHomingPlan — (Finish 대상, Ready 대상) Work 집합. ADV/RET 컨벤션 + Start arrow 분석.
            var plan = SimIndexModule.computeAutoHomingPlan(idx);
            var planFinish = plan.Item1.Count;
            var planReady = plan.Item2.Count;

            // WorkResetPreds — 각 Work 의 reset partner 매핑 (이 Work 를 home 으로 되돌릴 partner Work).
            var resetPredsCount = idx.WorkResetPreds.Count(kv => kv.Value.Length > 0);

            // IO map size
            var outAddrs = iom.OutAddressToMappings.Count;
            var inAddrs = iom.InAddressToMappings.Count;
            var txWorks = iom.TxWorkToOutAddresses.Count;
            var rxWorks = iom.RxWorkToInAddresses.Count;

            // ApiName 컨벤션 확인 (ADV/RET 등)
            var apiNames = _storeProvider().Calls.Values
                .Select(c => c.ApiName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .Take(10)
                .ToArray();
            var apiNameSample = apiNames.Length > 0 ? string.Join(",", apiNames) : "(없음)";

            AddSimLog(
                $"[원위치 진단:1] AllWorks={totalWorks} AllCalls={totalCalls} " +
                $"OUT주소={outAddrs} IN주소={inAddrs} TxWorks={txWorks} RxWorks={rxWorks}",
                LogSeverity.Info);
            AddSimLog(
                $"[원위치 진단:2] computeAutoHomingPlan=(Finish:{planFinish}, Ready:{planReady})  " +
                $"isFinishedFlagged={finishedFlag.Count}  WorkResetPreds(non-empty)={resetPredsCount}",
                (planFinish + planReady + finishedFlag.Count + resetPredsCount) == 0 ? LogSeverity.Warn : LogSeverity.Info);
            AddSimLog(
                $"[원위치 진단:3] ApiName 샘플=[{apiNameSample}]",
                LogSeverity.Info);

            // 해석 가이드 — 사용자에게 무엇이 부족한지 명확히.
            if (planFinish == 0 && planReady == 0 && finishedFlag.Count == 0 && resetPredsCount == 0)
            {
                AddSimLog(
                    "[원위치 진단:결론] 엔진이 모델에서 home 관련 메타를 0개 인식. " +
                    "→ AASX 모델에 (a) Active Work 안에 같은 Device 의 ADV/RET Call + Start 화살표, 또는 " +
                    "(b) WorkProperties.IsFinished 수동 플래그, 또는 (c) Reset 화살표(ResetReset)가 있어야 " +
                    "engine 또는 길 B 가 home OUT 을 식별 가능합니다.",
                    LogSeverity.Warn);
            }
        }
        catch (Exception ex)
        {
            AddSimLog($"[원위치 진단] failed: {ex.Message}", LogSeverity.Warn);
        }

        if (!IsHomingPhase)
        {
            // 엔진의 정적 homing plan 이 비었지만 (이미 모델 설계대로면 모든 Work 가 home 상태라고 결론),
            // 사용자가 매뉴얼로 디바이스를 움직여 *현재 PLC 상태와 모델 예상이 어긋난* 케이스가 있을 수 있음.
            // 엔진 우회 — engine-aware runtime homing: 엔진의 정적 plan(finishTargets/readyTargets) +
            // WorkResetPreds + 현재 PLC IN 상태를 결합해 어긋난 Work 들의 home OUT 을 직접 송출.
            // PauseSimulation 으로 엔진 cycle 차단된 상태에서 동작 → DS 규칙 위반 없음 (manual control 과 동일 모델).
            // PLC 가 OUT 신호를 받아 액추에이터 구동 → IN echo 가 돌아오면 home 도달.
            // 사용자 release 시 BroadcastClearOwn 이 모든 OUT off → 안전 종료.
            _ = DriveEngineAwareHomingAsync();
        }
        else
        {
            AddSimLog("원위치 페이즈 진입 — 엔진이 OUT 송출 중. PLC 응답 대기.", LogSeverity.Info);
        }
    }

    /// <summary>
    /// engine-aware push-button homing: 엔진의 정적 home 메타(<c>computeAutoHomingPlan</c> +
    /// <c>WorkResetPreds</c>)와 현재 PLC IN 상태를 비교해, 어긋난 Work 의 home OUT 을 직접 송출.
    /// 엔진의 <c>StartWithHomingPhase</c> 가 빈 plan 으로 빠지는 케이스(=정적 model 분석으로는
    /// 이미 home 이라 결론)에 대한 보강. PauseSimulation 으로 cycle 차단된 paused engine 위에서 동작.
    /// 결정 로직 (후보 추출 + ComAux 게이트) 은 F# <c>EngineAwareHomingPlan</c> 에 위임.
    /// </summary>
    private async Task DriveEngineAwareHomingAsync()
    {
        if (_simEngine is null) return;

        var idx = _simEngine.Index;
        var iom = _simEngine.IOMap;

        // 모든 IN 주소를 hub cache 에서 일괄 조회 — initial scan 으로 이미 채워져 있음.
        var inValues = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var allInAddrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in iom.RxWorkToInAddresses)
            foreach (var addr in kv.Value)
                if (!string.IsNullOrWhiteSpace(addr)) allInAddrs.Add(addr);
        foreach (var addr in allInAddrs)
        {
            var v = await Hub.QueryTagFromManualAsync(addr);
            inValues[addr] = v == "true";
        }

        // 후보 추출 + ComAux 게이트 + OUT dedup 까지 F# 결정 위임.
        var plan = EngineAwareHomingPlan.buildPlan(idx, iom, _simEngine.State, inValues);

        // 목표 Ready 인데 WorkResetPreds 가 없는 work 경고 — F# 이 분류해 반환.
        foreach (var (_, workName) in plan.WorksMissingResetPreds)
            AddSimLog($"  · ⚠ {workName}: 목표=Ready, IN=true 이지만 WorkResetPreds 없음 → reset 불가", LogSeverity.Warn);

        if (plan.Candidates.Length == 0)
        {
            AddSimLog("[engine-aware homing] 어긋난 Work 없음 — 모든 디바이스가 이미 home 위치", LogSeverity.System);
            return;
        }

        if (plan.BlockedCallGuids.Length > 0)
        {
            // ComAux = "공통 보조 조건" = 비상정지/안전문/시스템 인터록 같이 *모든 모드에서 항상 충족돼야 하는*
            // 안전 floor. 미충족 Call 의 OUT 은 firing 차단.
            var blockedDetails = plan.BlockedCallGuids
                .Select(cg =>
                {
                    var callName = _storeProvider().Calls.TryGetValue(cg, out var call) ? call.Name : cg.ToString();
                    var workName = plan.Candidates.FirstOrDefault(c => c.CallGuid == cg)?.WorkName ?? "";
                    return $"{callName} ({workName})";
                })
                .ToList();
            AddSimLog(
                $"⛔ [B1 안전 차단] ComAux 조건 미충족 Call {blockedDetails.Count}건 — 해당 OUT 송출 차단됨. " +
                $"비상정지·안전문·인터록 등 공통 안전 조건이 미충족 상태입니다.",
                LogSeverity.Error);
            foreach (var bn in blockedDetails.Take(8))
                AddSimLog($"  ⛔ {bn}", LogSeverity.Error);
            if (blockedDetails.Count > 8)
                AddSimLog($"  ⛔ ... 외 {blockedDetails.Count - 8}건", LogSeverity.Error);
        }

        if (plan.OutsToFire.Length == 0)
        {
            AddSimLog(
                "[engine-aware homing] 통과한 OUT 0개 — 모든 후보가 ComAux 차단됨. " +
                "안전 조건 확인 후 다시 시도하세요.",
                LogSeverity.Warn);
            return;
        }

        AddSimLog(
            $"[engine-aware homing] {plan.Candidates.Length}개 후보 → ComAux 통과 {plan.OutsToFire.Length}개 OUT firing " +
            $"(차단 {plan.BlockedCallGuids.Length}건)",
            LogSeverity.System);

        // 진단 — 통과한 케이스 일부 표시.
        var passedSample = plan.Passed.GroupBy(c => c.WorkName).Take(8);
        foreach (var grp in passedSample)
            AddSimLog($"  · {grp.First().Reason}: {grp.Key}", LogSeverity.Info);

        // 송출. 모든 OUT 을 source="control" 로. PLC 가 OUT=true 를 받아 액추에이터 구동.
        // 사용자 release 시 StopHub 의 BroadcastClearOwnOutputsAsync 가 모든 OUT 을 false 로 → 안전 정지.
        foreach (var addr in plan.OutsToFire)
        {
            if (!IsHomingPressed) break;   // release 시 즉시 중단
            var ok = await Hub.WriteTagFromManualAsync(addr, "true");
            if (!ok)
                AddSimLog($"[engine-aware homing] 송신 실패: {addr}", LogSeverity.Warn);
        }
    }

    /// <summary>원위치 push-button 의 "놓음" 핸들러 (deadman switch 종료).
    /// homing 도중이든 완료 후이든, 버튼이 떼지면 즉시 StopSimulation 으로 깨끗이 정리.
    /// StopSimulation 의 BroadcastClearOwnOutputsAsync 가 자기 OUT 들을 모두 false 로 broadcast 해
    /// PLC 측 실 액추에이터 모션도 안전하게 중단된다.</summary>
    public void EndHoming()
    {
        if (!IsHomingPressed) return;
        IsHomingPressed = false;
        _homingOnlyMode = false;

        if (IsSimulating)
        {
            AddSimLog("원위치 종료 (버튼 놓음) — 시뮬·Hub·PLC 게이트웨이 정리", LogSeverity.System);
            StopSimulation();
        }
    }

    /// <summary>BeginHoming 진입 가능 조건 — XAML 측 Visibility/Enabled 와 별개로 백엔드 가드.</summary>
    public bool CanBeginHoming() =>
        SimulationCommandFacade.IsAccepted(
            SimulationCommandFacade.DecideBeginHoming(
                SelectedRuntimeMode, IsRealPlcConnected, IsSimulating, IsHomingPhase, IsHomingPressed));

    private void OnHomingPhaseCompleted(object? sender, EventArgs e)
    {
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        _dispatcher.BeginInvoke(() =>
        {
            IsHomingPhase = false;
            SimStatusText = SimText.Running;
            _setStatusText(SimText.Started);
            AddSimLog("시뮬레이션 자동 원위치 완료", LogSeverity.System);

            // Push-button 원위치 모드: 엔진이 home 도달을 알려도 STOP 하지 않고 사용자 release 까지 유지.
            // 단 그대로 Run 상태로 두면 token source 가 발화해 의도치 않은 cycle 시작이 가능 — 즉시 Pause 로 잠금.
            // 버튼 놓으면 EndHoming → StopSimulation 으로 정리.
            if (_homingOnlyMode)
            {
                if (CanPauseSimulation())
                {
                    PauseSimulation();
                }
                AddSimLog("원위치 완료 — 엔진 정지. 버튼 놓으면 세션 종료.", LogSeverity.System);
                SimStatusText = "원위치 완료 — 버튼 놓으면 종료";
                return;
            }

            // STEP 으로 시뮬을 시작한 경우: Homing 완료 후 자동 PauseSimulation + STEP 재진입.
            if (_pendingFirstStepAfterStart)
            {
                _pendingFirstStepAfterStart = false;
                PauseSimulation();
                _ = StepSimulationAsync();
            }
        });
    }
}
