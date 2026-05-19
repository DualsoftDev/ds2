namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core

/// <summary>
/// STEP 모드 첫 진입 시 source 를 어떤 방식으로 priming 할지의 결정.
/// 이후 STEP 은 자연 cascade 를 따라가므로 한 번만 수행 (호출자가 _stepPrimingDone 플래그로 차단).
/// </summary>
type StepPrimingAction =
    /// 모든 token source 중 토큰 미보유 work 를 StartSourceWork.
    | StartAllSourcesWithoutToken
    /// 선택된 work 가 token source 이고 토큰 미보유 → StartSourceWork(guid).
    | StartSelectedSource of Guid
    /// 선택된 work 가 source 가 아니고 현재 Ready → ForceWorkState(guid, Going).
    | ForceSelectedReadyToGoing of Guid
    /// 아무 동작도 필요 없음 (이미 token 보유, source 아님 + Ready 도 아님, 또는 빈 선택).
    | NoAction

module StepPrimingPlan =

    /// 결정 — engine 호출은 호출자가 담당. 본 함수는 순수 입력 → 액션 분류.
    ///
    /// - <paramref name="isTokenSource"/>: 주어진 work 가 token source 인지.
    /// - <paramref name="hasToken"/>: 주어진 work 가 현재 토큰을 보유 중인지.
    /// - <paramref name="workState"/>: 주어진 work 의 현재 상태 (없으면 None).
    /// - <paramref name="selectedSourceGuid"/>: 사용자가 선택한 work guid (Empty 면 미지정).
    /// - <paramref name="autoStartSources"/>: 자동 모든 source priming 여부.
    let decide
            (isTokenSource: Guid -> bool)
            (hasToken: Guid -> bool)
            (workState: Guid -> Status4 option)
            (selectedSourceGuid: Guid)
            (autoStartSources: bool)
            : StepPrimingAction =
        if autoStartSources then
            StartAllSourcesWithoutToken
        elif selectedSourceGuid = Guid.Empty then
            NoAction
        elif isTokenSource selectedSourceGuid then
            if hasToken selectedSourceGuid then NoAction
            else StartSelectedSource selectedSourceGuid
        else
            match workState selectedSourceGuid with
            | Some Status4.Ready -> ForceSelectedReadyToGoing selectedSourceGuid
            | _ -> NoAction
