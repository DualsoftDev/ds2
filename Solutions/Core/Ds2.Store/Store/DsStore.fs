namespace rec Ds2.Store

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open Ds2.Core
open log4net

type DsStore() =
    let log = LogManager.GetLogger(typedefof<DsStore>)

    member val Projects = Dictionary<Guid, Project>() with get, set
    member val Systems = Dictionary<Guid, DsSystem>() with get, set
    member val Flows = Dictionary<Guid, Flow>() with get, set
    member val Works = Dictionary<Guid, Work>() with get, set
    member val Calls = Dictionary<Guid, Call>() with get, set
    member val ApiDefs = Dictionary<Guid, ApiDef>() with get, set
    member val ApiCalls = Dictionary<Guid, ApiCall>() with get, set
    member val ArrowWorks   = Dictionary<Guid, ArrowBetweenWorks>() with get, set
    member val ArrowCalls   = Dictionary<Guid, ArrowBetweenCalls>() with get, set
    member val HwButtons = Dictionary<Guid, HwButton>() with get, set
    member val HwLamps = Dictionary<Guid, HwLamp>() with get, set
    member val HwConditions = Dictionary<Guid, HwCondition>() with get, set
    member val HwActions = Dictionary<Guid, HwAction>() with get, set

    member this.ProjectsReadOnly : IReadOnlyDictionary<Guid, Project> = ReadOnlyDictionary(this.Projects)
    member this.SystemsReadOnly : IReadOnlyDictionary<Guid, DsSystem> = ReadOnlyDictionary(this.Systems)
    member this.FlowsReadOnly : IReadOnlyDictionary<Guid, Flow> = ReadOnlyDictionary(this.Flows)
    member this.WorksReadOnly : IReadOnlyDictionary<Guid, Work> = ReadOnlyDictionary(this.Works)
    member this.CallsReadOnly : IReadOnlyDictionary<Guid, Call> = ReadOnlyDictionary(this.Calls)
    member this.ApiDefsReadOnly : IReadOnlyDictionary<Guid, ApiDef> = ReadOnlyDictionary(this.ApiDefs)
    member this.ApiCallsReadOnly : IReadOnlyDictionary<Guid, ApiCall> = ReadOnlyDictionary(this.ApiCalls)
    member this.ArrowWorksReadOnly   : IReadOnlyDictionary<Guid, ArrowBetweenWorks> = ReadOnlyDictionary(this.ArrowWorks)
    member this.ArrowCallsReadOnly   : IReadOnlyDictionary<Guid, ArrowBetweenCalls> = ReadOnlyDictionary(this.ArrowCalls)
    member this.HwButtonsReadOnly : IReadOnlyDictionary<Guid, HwButton> = ReadOnlyDictionary(this.HwButtons)
    member this.HwLampsReadOnly : IReadOnlyDictionary<Guid, HwLamp> = ReadOnlyDictionary(this.HwLamps)
    member this.HwConditionsReadOnly : IReadOnlyDictionary<Guid, HwCondition> = ReadOnlyDictionary(this.HwConditions)
    member this.HwActionsReadOnly : IReadOnlyDictionary<Guid, HwAction> = ReadOnlyDictionary(this.HwActions)

    static member empty() = DsStore()

    member internal _.DirectWrite<'T when 'T :> DsEntity>(dict: Dictionary<Guid, 'T>, entity: 'T) =
        dict.[entity.Id] <- entity

    member internal this.RewireApiCallReferences() =
        let rewire (source: ResizeArray<ApiCall>) =
            let result = ResizeArray<ApiCall>(source.Count)
            for ac in source do
                match this.ApiCalls.TryGetValue(ac.Id) with
                | true, storeAc -> result.Add(storeAc)
                | false, _ -> log.Warn($"RewireApiCallReferences: ApiCall {ac.Id} not found in store - skipped")
            result
        for call in this.Calls.Values do
            call.ApiCalls <- rewire call.ApiCalls
            for cond in call.CallConditions do
                cond.Conditions <- rewire cond.Conditions

    member this.SaveToFile(path: string) =
        try
            Ds2.Serialization.JsonConverter.saveToFile path this
            log.Info($"Saved: {path}")
        with ex ->
            log.Error($"Save failed: {path} - {ex.Message}", ex)
            reraise()

    member private this.ReplaceAllCollections(source: DsStore) =
        let replace (src: Dictionary<Guid, 'T>) (dst: Dictionary<Guid, 'T>) =
            dst.Clear()
            for kv in src do
                dst.[kv.Key] <- kv.Value
        replace source.Projects     this.Projects
        replace source.Systems      this.Systems
        replace source.Flows        this.Flows
        replace source.Works        this.Works
        replace source.Calls        this.Calls
        replace source.ApiDefs      this.ApiDefs
        replace source.ApiCalls     this.ApiCalls
        replace source.ArrowWorks   this.ArrowWorks
        replace source.ArrowCalls   this.ArrowCalls
        replace source.HwButtons    this.HwButtons
        replace source.HwLamps      this.HwLamps
        replace source.HwConditions this.HwConditions
        replace source.HwActions    this.HwActions

    /// JSON 마이그레이션: FlowPrefix가 비어있는 Work에 부모 Flow 이름 설정
    member private this.MigrateWorkNaming() =
        for work in this.Works.Values do
            if System.String.IsNullOrEmpty(work.FlowPrefix) then
                match this.Flows.TryGetValue(work.ParentId) with
                | true, flow ->
                    work.FlowPrefix <- flow.Name
                    if System.String.IsNullOrEmpty(work.LocalName) then
                        work.LocalName <- work.Name
                | _ -> ()

    member private this.ApplyNewStore(newStore: DsStore, contextLabel: string) =
        try
            this.ReplaceAllCollections(newStore)
            this.RewireApiCallReferences()
            this.MigrateWorkNaming()
            log.Info($"Store applied: {contextLabel}")
        with ex ->
            log.Error($"ApplyNewStore failed: {contextLabel} - {ex.Message}", ex)
            raise (InvalidOperationException($"{contextLabel} failed: {ex.Message}", ex))

    member this.LoadFromFile(path: string) =
        let loaded = Ds2.Serialization.JsonConverter.loadFromFile<DsStore> path
        if isNull (box loaded) then
            invalidOp "Loaded store is null."
        this.ApplyNewStore(loaded, sprintf "load file '%s'" path)

    member this.ReplaceStore(newStore: DsStore) =
        this.ApplyNewStore(newStore, "ReplaceStore")
