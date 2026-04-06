namespace Ds2.Core


/// 위치 및 크기 정보 (UI용)
type Xywh(x: int, y: int, w: int, h: int) =
    member val X: int = x with get, set
    member val Y: int = y with get, set
    member val W: int = w with get, set
    member val H: int = h with get, set
