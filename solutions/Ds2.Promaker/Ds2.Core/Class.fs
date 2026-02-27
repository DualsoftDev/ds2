namespace Ds2.Core

open System

// Xywh → Ds2.Core.Contracts/Class.fs

type IOTag() =
    member val Name : string = "" with get, set
    member val Address : string = "" with get, set
    member val Description : string = "" with get, set
    new(name: string, addr: string, desc: string) as this = IOTag() then
        this.Name <- name
        this.Address <- addr
        this.Description <- desc
