namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module internal AasxImportCore =

    let log = LogManager.GetLogger("Ds2.Aasx.AasxImporter")

    let valueOrEmpty (p: Property) = if p.Value = null then "" else p.Value

    let getProp (smc: SubmodelElementCollection) (idShort: string) : string option =
        if smc.Value = null then None
        else
            smc.Value
            |> Seq.tryPick (function
                | :? Property as p when p.IdShort = idShort ->
                    if p.Value = null then None else Some p.Value
                | _ -> None)

    let fromJsonProp<'T> (smc: SubmodelElementCollection) (idShort: string) : 'T option =
        getProp smc idShort
        |> Option.bind (fun json ->
            try Some (Ds2.Serialization.JsonConverter.deserialize<'T> json)
            with ex -> log.Warn($"JSON 역직렬화 실패: {idShort} — {ex.Message}", ex); None)

    let getChildSmlSmcs (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection list =
        if smc.Value = null then []
        else
            smc.Value
            |> Seq.tryPick (function
                | :? SubmodelElementList as l when l.IdShort = idShort ->
                    if l.Value = null then Some []
                    else
                        Some (l.Value |> Seq.choose (function
                            | :? SubmodelElementCollection as c -> Some c
                            | _ -> None) |> Seq.toList)
                | _ -> None)
            |> Option.defaultValue []

    let parseArrowType (s: string) : ArrowType =
        match Enum.TryParse<ArrowType>(s) with
        | true, v -> v
        | _ -> ArrowType.Unspecified

    let parseStatus4 (s: string) : Status4 =
        match Enum.TryParse<Status4>(s) with
        | true, v -> v
        | _ -> Status4.Ready

    let describeSmc (smc: SubmodelElementCollection) : string =
        let guidText = getProp smc Guid_ |> Option.defaultValue "<missing>"
        let nameText = getProp smc Name_ |> Option.defaultValue "<missing>"
        $"Guid={guidText}, Name={nameText}"

    let parseStrictList
        (ownerLabel: string)
        (itemLabel: string)
        (items: SubmodelElementCollection list)
        (parser: SubmodelElementCollection -> 'T option)
        : 'T list option =
        let rec loop acc rest =
            match rest with
            | [] -> Some(List.rev acc)
            | smc :: tail ->
                match parser smc with
                | Some value -> loop (value :: acc) tail
                | None ->
                    log.Error($"AASX import failed: invalid {itemLabel} under {ownerLabel} ({describeSmc smc}).")
                    None
        loop [] items

    // ── 변환 계층 ──────────────────────────────────────────────────────────────

    let smcToArrow<'T when 'T :> DsArrow> (label: string) (smc: SubmodelElementCollection) (parentId: Guid)
                                                   (ctor: Guid -> Guid -> Guid -> ArrowType -> 'T) : 'T option =
        try
            match getProp smc Source_ |> Option.map Guid.Parse, getProp smc Target_ |> Option.map Guid.Parse with
            | Some sourceId, Some targetId ->
                let id        = getProp smc Guid_ |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
                let arrowType = getProp smc Type_  |> Option.map parseArrowType |> Option.defaultValue ArrowType.Unspecified
                let arrow = ctor parentId sourceId targetId arrowType
                arrow.Id <- id
                Some arrow
            | _ -> log.Warn($"{label}: Source 또는 Target 누락"); None
        with ex -> log.Warn($"{label} 실패: {ex.Message}", ex); None

    let smcToArrowCall smc workId   = smcToArrow "smcToArrowCall" smc workId   (fun p s t a -> ArrowBetweenCalls(p, s, t, a))
    let smcToArrowWork smc systemId = smcToArrow "smcToArrowWork" smc systemId (fun p s t a -> ArrowBetweenWorks(p, s, t, a))
