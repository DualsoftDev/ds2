namespace rec Ds2.Core.Store

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Text.Json.Serialization
open System.Threading
open Ds2.Core
open Ds2.Serialization

type DsStore() =

    let mutable revision = 0

    // Round-trip 최적화 — doc: Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md
    /// Monotonic mutation counter (runtime-only — 직렬화/디스크 저장 제외).
    /// commit / undo / redo / load / replace 직후 `BumpRevision` 으로 1 증가.
    /// **Read 는 `Volatile.Read` 로 memory ordering 보장** (round-trip §J6 review 반영) — UI dispatcher
    /// 외 thread 가 BumpRevision 호출 시에도 stale read 방지. `RenderSnapshotEnvelope` 의 (rev, body)
    /// 동시 캡쳐 경로는 `DsStoreSnapshotExtensions.RenderSnapshotEnvelope` 에서 본 getter 1회 read.
    [<JsonIgnore>]
    member _.Revision = Volatile.Read(&revision)

    /// `Interlocked.Increment` 기반 atomic ++. internal — Ds2.Editor / Ds2.LlmAgent / Promaker 의
    /// transaction commit hook (`Authoring.fs withTransaction / applyTransaction`, `DsStore.ApplyNewStore`) 만 호출.
    member internal _.BumpRevision() =
        Interlocked.Increment(&revision) |> ignore

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
            // 조건 내 ApiCall은 등록하지 않음 — Call 본체 ApiCall과 동일 ID이지만 독립 인스턴스

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
            // 조건 내 ApiCall은 rewire 제외 — 독립 인스턴스 유지 (Value 공유 방지)

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
                system.SystemType <- Some "Cylinder_1"
                printfn $"[INFO] MigrateSystemType: Set SystemType='Cylinder' for '{system.Name}' (Id={system.Id})"

    member private this.ApplyNewStore(newStore: DsStore, contextLabel: string) =
        try
            this.ReplaceAllCollections(newStore)
            this.RebuildApiCallsDictionary()
            this.RewireApiCallReferences()
            this.MigrateWorkNaming()
            this.MigrateSystemType()
            // round-trip §1.3 hook (3 지점 중 하나): load / replace / import / new — store 전체 교체 후 1회 ++.
            // LLM chat 의 LastSentRevision 무효화 → 다음 송신에 새 snapshot 자동 첨부.
            // **참고 (round-trip §n2)**: 새 store 인스턴스 자체로 교체되는 경로 (`MainViewModel.Reset` →
            // `LlmChatViewModel.UpdateStore`) 는 본 hook 통과 안 함 — 새 인스턴스의 revision = 0 으로 시작하고
            // UpdateStore 가 _lastSentRevision 을 null 로 reset 하여 정합 유지. 두 path 의 의미 차이 명시.
            this.BumpRevision()
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
