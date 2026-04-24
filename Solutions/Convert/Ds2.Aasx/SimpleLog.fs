namespace Ds2.Aasx

open System
open System.Diagnostics

type internal SimpleLogger(name: string) =
    let write level (message: string) (ex: exn option) =
        let errorText =
            match ex with
            | Some err when not (isNull err) -> $" | {err.GetType().Name}: {err.Message}"
            | _ -> ""

        Trace.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{level}] [{name}] {message}{errorText}")

    member _.Info(message: string) =
        write "INFO" message None

    member _.Warn(message: string) =
        write "WARN" message None

    member _.Warn(message: string, ex: exn) =
        write "WARN" message (Some ex)

    member _.Error(message: string) =
        write "ERROR" message None

module internal SimpleLog =
    let create name = SimpleLogger(name)
