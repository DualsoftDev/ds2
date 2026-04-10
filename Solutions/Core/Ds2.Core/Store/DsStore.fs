namespace rec Ds2.Core.Store

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Text.Json.Serialization
open Ds2.Core
open Ds2.Serialization

type DsStore() =

    member val Projects   = Dictionary<Guid, Project>()            with get, set
    member val Systems    = Dictionary<Guid, DsSystem>()           with get, set
    member val Flows      = Dictionary<Guid, Flow>()               with get, set
    member val Works      = Dictionary<Guid, Work>()               with get, set
    member val Calls      = Dictionary<Guid, Call>()               with get, set
    member val ApiDefs    = Dictionary<Guid, ApiDef>()             with get, set
    [<JsonIgnore>]
    member val ApiCalls   = Dictionary<Guid, ApiCall>()            with get, set
    member val ArrowWorks = Dictionary<Guid, ArrowBetweenWorks>()  with get, set
    member val ArrowCalls = Dictionary<Guid, ArrowBetweenCalls>()  with get, set

    /// Undo/Redo 시 마지막 트랜잭션에서 변경된 엔티티 ID 목록
    [<JsonIgnore>] member val LastTransactionAffectedIds : Guid list = [] with get, set

    [<JsonIgnore>] member this.ProjectsReadOnly  : IReadOnlyDictionary<Guid, Project>           = ReadOnlyDictionary(this.Projects)
    [<JsonIgnore>] member this.SystemsReadOnly   : IReadOnlyDictionary<Guid, DsSystem>          = ReadOnlyDictionary(this.Systems)
    [<JsonIgnore>] member this.FlowsReadOnly     : IReadOnlyDictionary<Guid, Flow>              = ReadOnlyDictionary(this.Flows)
    [<JsonIgnore>] member this.WorksReadOnly     : IReadOnlyDictionary<Guid, Work>              = ReadOnlyDictionary(this.Works)
    [<JsonIgnore>] member this.CallsReadOnly     : IReadOnlyDictionary<Guid, Call>              = ReadOnlyDictionary(this.Calls)
    [<JsonIgnore>] member this.ApiDefsReadOnly   : IReadOnlyDictionary<Guid, ApiDef>            = ReadOnlyDictionary(this.ApiDefs)
    [<JsonIgnore>] member this.ApiCallsReadOnly  : IReadOnlyDictionary<Guid, ApiCall>           = ReadOnlyDictionary(this.ApiCalls)
    [<JsonIgnore>] member this.ArrowWorksReadOnly: IReadOnlyDictionary<Guid, ArrowBetweenWorks> = ReadOnlyDictionary(this.ArrowWorks)
    [<JsonIgnore>] member this.ArrowCallsReadOnly: IReadOnlyDictionary<Guid, ArrowBetweenCalls> = ReadOnlyDictionary(this.ArrowCalls)

    static member empty() = DsStore()

    member internal _.DirectWrite<'T when 'T :> DsEntity>(dict: Dictionary<Guid, 'T>, entity: 'T) =
        dict.[entity.Id] <- entity

    member internal this.RebuildApiCallsDictionary() =
        this.ApiCalls.Clear()
        let register (ac: ApiCall) = this.ApiCalls.[ac.Id] <- ac
        for call in this.Calls.Values do
            for ac in call.ApiCalls do register ac
            let rec walkConditions (conds: ResizeArray<CallCondition>) =
                for cond in conds do
                    for ac in cond.Conditions do
                        if not (this.ApiCalls.ContainsKey(ac.Id)) then register ac
                    walkConditions cond.Children
            walkConditions call.CallConditions

    member internal this.RewireApiCallReferences() =
        let rewire (source: ResizeArray<ApiCall>) =
            let result = ResizeArray<ApiCall>(source.Count)
            for ac in source do
                match this.ApiCalls.TryGetValue(ac.Id) with
                | true, storeAc -> result.Add(storeAc)
                | false, _      -> printfn $"[WARN] RewireApiCallReferences: ApiCall {ac.Id} not found - skipped"
            result
        for call in this.Calls.Values do
            call.ApiCalls <- rewire call.ApiCalls
            for cond in call.CallConditions do
                cond.Conditions <- rewire cond.Conditions

    member this.SaveToFile(path: string) =
        try
            Ds2.Serialization.JsonConverter.saveToFile path this
            printfn $"[INFO] Saved: {path}"
        with ex ->
            eprintfn $"[ERROR] Save failed: {path} - {ex.Message}"
            reraise()

    member private this.ReplaceAllCollections(source: DsStore) =
        let replace (src: Dictionary<Guid, 'T>) (dst: Dictionary<Guid, 'T>) =
            dst.Clear()
            for kv in src do dst.[kv.Key] <- kv.Value
        replace source.Projects   this.Projects
        replace source.Systems    this.Systems
        replace source.Flows      this.Flows
        replace source.Works      this.Works
        replace source.Calls      this.Calls
        replace source.ApiDefs    this.ApiDefs
        replace source.ApiCalls   this.ApiCalls
        replace source.ArrowWorks this.ArrowWorks
        replace source.ArrowCalls this.ArrowCalls

    member private this.MigrateWorkNaming() =
        for work in this.Works.Values do
            if String.IsNullOrEmpty(work.FlowPrefix) then
                match this.Flows.TryGetValue(work.ParentId) with
                | true, flow ->
                    work.FlowPrefix <- flow.Name
                    if String.IsNullOrEmpty(work.LocalName) then
                        work.LocalName <- work.Name
                | _ -> ()

    member private this.MigrateSystemType() =
        for system in this.Systems.Values do
            if system.SystemType.IsNone then
                system.SystemType <- Some "Unit"
                printfn $"[INFO] MigrateSystemType: Set SystemType='Unit' for '{system.Name}' (Id={system.Id})"

    member private this.ApplyNewStore(newStore: DsStore, contextLabel: string) =
        try
            this.ReplaceAllCollections(newStore)
            this.RebuildApiCallsDictionary()
            this.RewireApiCallReferences()
            this.MigrateWorkNaming()
            this.MigrateSystemType()
            printfn $"[INFO] Store applied: {contextLabel}"
        with ex ->
            eprintfn $"[ERROR] ApplyNewStore failed: {contextLabel} - {ex.Message}"
            raise (InvalidOperationException($"{contextLabel} failed: {ex.Message}", ex))

    member this.LoadFromFile(path: string) =
        let loaded = Ds2.Serialization.JsonConverter.loadFromFile<DsStore> path
        if isNull (box loaded) then
            invalidOp "Loaded store is null."
        this.ApplyNewStore(loaded, $"load file '{path}'")

    member this.ReplaceStore(newStore: DsStore) =
        this.ApplyNewStore(newStore, "ReplaceStore")
