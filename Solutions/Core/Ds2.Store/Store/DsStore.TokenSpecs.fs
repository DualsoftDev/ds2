namespace Ds2.Store

open System
open System.Runtime.CompilerServices
open Ds2.Core

[<Extension>]
type DsStoreTokenSpecExtensions =
    [<Extension>]
    static member GetTokenSpecs(store: DsStore) : TokenSpec list =
        match DsQuery.allProjects store |> List.tryHead with
        | Some project -> project.TokenSpecs |> Seq.toList
        | None -> []
