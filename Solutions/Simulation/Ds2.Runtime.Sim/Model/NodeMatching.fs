namespace Ds2.Runtime.Sim.Model

open System
open Ds2.Core

[<RequireQualifiedAccess>]
module NodeMatching =

    let parseNodeState (stateStr: string) : Status4 =
        match stateStr.ToUpperInvariant() with
        | "R" -> Status4.Ready
        | "G" -> Status4.Going
        | "F" -> Status4.Finish
        | "H" -> Status4.Homing
        | _ -> Status4.Ready

    let nodeStateToString (state: Status4) : string =
        match state with
        | Status4.Ready  -> "R"
        | Status4.Going  -> "G"
        | Status4.Finish -> "F"
        | Status4.Homing -> "H"
        | _ -> "R"

    let tryParseGuid (s: string) : Guid option =
        match Guid.TryParse(s) with
        | true, guid -> Some guid
        | false, _ -> None
