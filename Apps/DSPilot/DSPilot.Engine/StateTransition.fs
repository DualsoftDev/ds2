namespace DSPilot.Engine

/// 상태 전환 모듈
module StateTransition =

    /// Call 상태 전환 규칙
    /// Ready + OutTag Rising Edge → Going (장치가 작업 시작)
    /// Going + InTag Rising Edge → Finish (장치가 완료 신호 수신)
    /// Going + OutTag Falling Edge (InTag 없는 경우) → Finish
    let determineNewState
        (currentState: CallState)
        (isInTag: bool)
        (hasInTag: bool)
        (edge: EdgeType) : CallState option =

        match currentState, isInTag, hasInTag, edge with
        | Ready, false, _, Rising -> Some Going         // Ready → Going (OutTag Rising)
        | Going, true, _, Rising -> Some Finish         // Going → Finish (InTag Rising)
        | Going, false, false, Falling -> Some Finish   // Going → Finish (OutTag Falling, no InTag)
        | _ -> None                                     // 상태 변화 없음

    /// 상태 변화 여부와 새 상태 반환
    let tryTransition
        (currentStateStr: string)
        (isInTag: bool)
        (hasInTag: bool)
        (edge: EdgeType) : (CallState * bool) =

        // 문자열을 CallState로 변환
        let currentState =
            match currentStateStr with
            | "Ready" -> Ready
            | "Going" -> Going
            | "Finish" -> Finish
            | _ -> Ready  // 기본값

        match determineNewState currentState isInTag hasInTag edge with
        | Some newState -> (newState, true)   // 상태 변화 발생
        | None -> (currentState, false)       // 상태 유지

    /// CallState를 문자열로 변환
    let stateToString (state: CallState) : string =
        match state with
        | Ready -> "Ready"
        | Going -> "Going"
        | Finish -> "Finish"
