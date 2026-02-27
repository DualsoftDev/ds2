namespace Ds2.Core

type Xywh(x: int, y: int, w: int, h: int) =
    member val X : int = x with get, set
    member val Y : int = y with get, set
    member val W : int = w with get, set
    member val H : int = h with get, set
