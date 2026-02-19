namespace Ds2.Core

open System


type IOTag() =
    member val Name : string = "" with get, set
    member val Address : string = "" with get, set
    member val Description : string = "" with get, set
    new(name: string, addr: string, desc: string) as this = IOTag() then
        this.Name <- name
        this.Address <- addr
        this.Description <- desc

type Xywh(x: int, y: int, w: int, h: int) =
    member val X : int = x with get, set
    member val Y : int = y with get, set
    member val W : int = w with get, set
    member val H : int = h with get, set


