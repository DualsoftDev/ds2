namespace DSPilot.Engine.Tracking

open System
open System.Collections.Generic
open System.Linq
open Microsoft.Data.Sqlite
open Dapper
open Ds2.Core
open DSPilot.Engine.Core
open DSPilot.Engine.Tracking.StateTransition

/// Tag to Call mapping entry
[<CLIMutable>]
type TagMappingEntry =
    { TagAddress: string
      CallId: Guid
      CallName: string
      FlowName: string
      IsInTag: bool }

/// PLC Tag to Call Mapper
type PlcToCallMapper(dbPath: string, store: DsStore) =

    let mutable tagMappings: Map<string, TagMappingEntry> = Map.empty
    let mutable callDirections: Map<Guid, CallDirection> = Map.empty

    /// Build tag mappings from DsStore (AASX data)
    member this.BuildMappings() =
        let mutable mappings = Map.empty
        let mutable directions = Map.empty

        let allFlows = DsQuery.allFlows store |> Seq.toList

        for flow in allFlows do
            let works = DsQuery.worksOf flow.Id store |> Seq.toList

            for work in works do
                let calls = DsQuery.callsOf work.Id store |> Seq.toList

                for call in calls do
                    if call.ApiCalls.Count > 0 then
                        let apiCall = call.ApiCalls.[0]

                        let mutable hasInTag = false
                        let mutable hasOutTag = false

                        // OutTag mapping
                        if Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(apiCall.OutTag) then
                            let outTag: IOTag = apiCall.OutTag.Value
                            let outAddress = outTag.Address
                            hasOutTag <- true
                            mappings <- mappings.Add(outAddress, {
                                TagAddress = outAddress
                                CallId = call.Id
                                CallName = call.Name
                                FlowName = flow.Name
                                IsInTag = false
                            })

                        // InTag mapping
                        if Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(apiCall.InTag) then
                            let inTag: IOTag = apiCall.InTag.Value
                            let inAddress = inTag.Address
                            hasInTag <- true
                            mappings <- mappings.Add(inAddress, {
                                TagAddress = inAddress
                                CallId = call.Id
                                CallName = call.Name
                                FlowName = flow.Name
                                IsInTag = true
                            })

                        // Determine Direction
                        let direction = determineDirection hasInTag hasOutTag
                        directions <- directions.Add(call.Id, direction)

        tagMappings <- mappings
        callDirections <- directions

        printfn "[PlcToCallMapper] Built %d tag mappings for %d calls" mappings.Count directions.Count

    /// Get Call mapping by tag address
    member this.GetCallByTag(tagAddress: string) : TagMappingEntry option =
        Map.tryFind tagAddress tagMappings

    /// Get Call mapping by tag address (alternative signature)
    member this.FindCallByTag(tagAddress: string) : TagMappingEntry option =
        this.GetCallByTag(tagAddress)

    /// Get Call Direction by CallId
    member this.GetDirection(callId: Guid) : CallDirection =
        match Map.tryFind callId callDirections with
        | Some direction -> direction
        | None -> CallDirection.None

    /// Check if tag is InTag
    member this.IsInTag(tagAddress: string) : bool =
        match this.GetCallByTag(tagAddress) with
        | Some mapping -> mapping.IsInTag
        | None -> false

    /// Check if tag is OutTag
    member this.IsOutTag(tagAddress: string) : bool =
        match this.GetCallByTag(tagAddress) with
        | Some mapping -> not mapping.IsInTag
        | None -> false

    /// Get all tag addresses
    member this.GetAllTagAddresses() : string list =
        tagMappings |> Map.toList |> List.map fst

    /// Get all mappings
    member this.GetAllMappings() : TagMappingEntry list =
        tagMappings |> Map.toList |> List.map snd

    /// Get diagnostic info for calls with incomplete tag mappings
    member this.GetIncompleteTagMappings() : (string * string * bool * bool) list =
        // Returns: (FlowName, CallName, hasInTag, hasOutTag)
        let allCalls =
            callDirections
            |> Map.toList
            |> List.map (fun (callId, direction) ->
                let mapping = tagMappings |> Map.toList |> List.map snd |> List.find (fun m -> m.CallId = callId)
                (mapping.FlowName, mapping.CallName, callId, direction)
            )

        allCalls
        |> List.filter (fun (_, _, callId, direction) ->
            direction = CallDirection.None ||
            (direction = CallDirection.InOnly && not (tagMappings |> Map.exists (fun _ m -> m.CallId = callId && m.IsInTag))) ||
            (direction = CallDirection.OutOnly && not (tagMappings |> Map.exists (fun _ m -> m.CallId = callId && not m.IsInTag)))
        )
        |> List.map (fun (flowName, callName, callId, _) ->
            let hasInTag = tagMappings |> Map.exists (fun _ m -> m.CallId = callId && m.IsInTag)
            let hasOutTag = tagMappings |> Map.exists (fun _ m -> m.CallId = callId && not m.IsInTag)
            (flowName, callName, hasInTag, hasOutTag)
        )

    /// Update Call Direction in database
    member this.UpdateCallDirectionsInDb() =
        async {
            use conn = new SqliteConnection($"Data Source={dbPath}")
            do! conn.OpenAsync() |> Async.AwaitTask

            for (callId, direction) in Map.toList callDirections do
                let directionStr =
                    match direction with
                    | CallDirection.InOut -> "InOut"
                    | CallDirection.InOnly -> "InOnly"
                    | CallDirection.OutOnly -> "OutOnly"
                    | _ -> "None"

                let sql = """
                    UPDATE dspCall
                    SET Direction = @Direction
                    WHERE CallId = @CallId
                """

                let parameters = dict [
                    "Direction", box directionStr
                    "CallId", box (callId.ToString())
                ]

                let! _ = conn.ExecuteAsync(sql, parameters) |> Async.AwaitTask
                ()

            printfn "[PlcToCallMapper] Updated Call Directions in database"
        }

/// Module-level functions for interoperability
module PlcToCallMapperModule =

    /// Create a new mapper instance
    let create (dbPath: string) (store: DsStore) : PlcToCallMapper =
        let mapper = PlcToCallMapper(dbPath, store)
        mapper.BuildMappings()
        mapper

    /// Get Call by tag address
    let findCallByTag (tagAddress: string) (mapper: PlcToCallMapper) : TagMappingEntry option =
        mapper.GetCallByTag(tagAddress)

    /// Determine Direction based on tag configuration
    let determineDirection (hasInTag: bool) (hasOutTag: bool) : CallDirection =
        DSPilot.Engine.Tracking.StateTransition.determineDirection hasInTag hasOutTag

    /// Get all tag addresses from mapper
    let getAllTagAddresses (mapper: PlcToCallMapper) : string list =
        mapper.GetAllTagAddresses()

    /// Build tag mappings from DsStore
    let buildTagMappings (store: DsStore) : Map<string, TagMappingEntry> =
        let mapper = PlcToCallMapper("", store)
        mapper.BuildMappings()
        mapper.GetAllMappings()
        |> List.map (fun m -> (m.TagAddress, m))
        |> Map.ofList
