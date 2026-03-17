namespace DSPilot.Engine

/// 엣지 감지 모듈
module EdgeDetection =

    /// 이전 값과 현재 값을 비교하여 엣지 타입 판단
    let detectEdge (previousValue: string option) (currentValue: string) : EdgeType =
        match previousValue with
        | None -> NoChange  // 첫 번째 값인 경우
        | Some prev when prev = "0" && currentValue = "1" -> Rising
        | Some prev when prev = "1" && currentValue = "0" -> Falling
        | _ -> NoChange

    /// 엣지 상태를 bool 튜플로 변환 (isRising, isFalling)
    let edgeToFlags (edge: EdgeType) : bool * bool =
        match edge with
        | Rising -> (true, false)
        | Falling -> (false, true)
        | NoChange -> (false, false)

    /// Rising Edge 여부 확인
    let isRising (edge: EdgeType) : bool =
        edge = Rising

    /// Falling Edge 여부 확인
    let isFalling (edge: EdgeType) : bool =
        edge = Falling
