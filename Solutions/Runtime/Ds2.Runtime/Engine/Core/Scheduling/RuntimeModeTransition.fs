namespace Ds2.Runtime.Engine.Core

open Ds2.Core

/// <summary>
/// 사용자가 RuntimeMode 콤보를 변경했을 때 적용할 정책 결정.
/// I/O 미설정 차단, 트레이 정리, 연속투입 자동 해제까지 한 번에 분류.
/// 메시지 텍스트도 F# 에서 결정 — C# 은 결과를 받아 UI 적용만 담당.
/// </summary>
type RuntimeModeTransitionDecision = {
    /// 전환을 받아들일지 여부. false 이면 이전 모드로 revert.
    Accepted: bool
    /// Accepted=false 일 때 사용자에게 보여줄 안내 메시지.
    RejectionMessage: string option
    /// 전환 후 트레이 상태를 정리해야 하는지 (Monitoring 외 모드).
    ShouldRestoreTray: bool
    /// 전환 후 연속투입 토글을 자동 해제해야 하는지.
    ShouldDisableContinuousInjection: bool
}

module RuntimeModeTransition =

    let private rejectIoMissing (newMode: RuntimeMode) : RuntimeModeTransitionDecision =
        let msg =
            sprintf
                "%s 모드는 I/O 매핑 (ApiCall 의 OutTag/InTag 주소) 이 설정되어야 사용할 수 있습니다.\n\n프로젝트에 I/O 를 먼저 설정한 후 다시 시도해 주세요."
                (newMode.ToString())
        {
            Accepted = false
            RejectionMessage = Some msg
            ShouldRestoreTray = false
            ShouldDisableContinuousInjection = false
        }

    /// 모드 전환 시점에 적용할 정책을 한 번에 결정.
    ///
    /// - <paramref name="newMode"/>: 사용자가 선택한 새 모드.
    /// - <paramref name="hasIoConfigured"/>: 현재 store 에 ApiCall 의 IO 매핑이 1개 이상 있는지.
    /// - <paramref name="isRealPlcConnected"/>: 실 PLC 연결 옵션 (UI 체크박스).
    /// - <paramref name="isContinuousInjectionEnabled"/>: 현재 연속투입 토글 상태.
    let evaluate
            (newMode: RuntimeMode)
            (hasIoConfigured: bool)
            (isRealPlcConnected: bool)
            (isContinuousInjectionEnabled: bool)
            : RuntimeModeTransitionDecision =
        // Simulation 외 모드는 외부 Hub 신호 + I/O 매핑 필수. 미설정 상태면 진입 거부.
        if newMode <> RuntimeMode.Simulation && not hasIoConfigured then
            rejectIoMissing newMode
        else
            let injectionAvailable =
                RuntimeCommandPolicy.isContinuousInjectionAvailable newMode isRealPlcConnected
            {
                Accepted = true
                RejectionMessage = None
                ShouldRestoreTray = newMode <> RuntimeMode.Monitoring
                ShouldDisableContinuousInjection =
                    isContinuousInjectionEnabled && not injectionAvailable
            }
