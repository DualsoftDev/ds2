namespace Ds2.OpcUa.Server.NodeIds

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open Ds2.Core
open Ds2.Core.Encoding

/// ADR-002 §5 준수 · Namespace hot-append 프로토콜.
///
/// - namespace URI = `urn:ds:asset:{Base64Url(globalAssetId)}`
/// - 부팅 시 `nodeset-state.json` 에서 이전 (uri, nsIndex) 매핑 복원
/// - 새 자산 append → 새 nsIndex 할당 → 파일 원자적 교체 → ModelChangeEvent (Phase 3
///   후속에서 UA 스택 wire-up)
///
/// 이 클래스는 UA 스택 없이 순수 F# 로 계약을 확립하므로 유닛 테스트 가능.

type NamespaceAllocationResult =
    | Added   of index: int
    | Existed of index: int
    | Deprecated of index: int  // Removed 상태로 마크됨, 물리 삭제 금지

type NamespaceRecord = {
    Uri : string
    Index : int
    IsDeprecated : bool
}

type INamespaceAllocator =
    abstract StatePath : string
    abstract GetAll : unit -> NamespaceRecord list
    abstract TryGetIndex : uri: string -> int option
    abstract GlobalAssetIdToUri : gaid: GlobalAssetId -> string
    abstract EnsureForAsset : gaid: GlobalAssetId -> NamespaceAllocationResult
    abstract MarkDeprecated : uri: string -> bool

type NamespaceAllocator(statePath: string) =
    let sync = obj()
    let store = ConcurrentDictionary<string, NamespaceRecord>()
    // NamespaceArray[0] = "http://opcfoundation.org/UA/", [1] = server app URI, then user namespaces.
    let mutable nextIndex = 2

    let load () =
        if File.Exists statePath then
            let json = File.ReadAllText statePath
            let records = JsonSerializer.Deserialize<NamespaceRecord list>(json)
            for r in records do
                store.[r.Uri] <- r
                if r.Index >= nextIndex then nextIndex <- r.Index + 1

    let save () =
        let list = store.Values |> Seq.sortBy (fun r -> r.Index) |> List.ofSeq
        let tmp = statePath + ".tmp"
        let opts = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, opts))
        if File.Exists statePath then File.Delete statePath
        File.Move(tmp, statePath)

    do
        Directory.CreateDirectory(Path.GetDirectoryName statePath) |> ignore
        load()

    interface INamespaceAllocator with

        member _.StatePath = statePath

        member _.GetAll() =
            store.Values |> Seq.sortBy (fun r -> r.Index) |> List.ofSeq

        member _.TryGetIndex uri =
            match store.TryGetValue uri with
            | true, r -> Some r.Index
            | _ -> None

        member _.GlobalAssetIdToUri gaid =
            "urn:ds:asset:" + Base64Url.encode gaid.Value

        member this.EnsureForAsset gaid =
            let uri = (this :> INamespaceAllocator).GlobalAssetIdToUri gaid
            lock sync (fun () ->
                match store.TryGetValue uri with
                | true, r ->
                    if r.IsDeprecated then Deprecated r.Index
                    else Existed r.Index
                | _ ->
                    let idx = nextIndex
                    nextIndex <- nextIndex + 1
                    let record = { Uri = uri; Index = idx; IsDeprecated = false }
                    store.[uri] <- record
                    save()
                    Added idx)

        member _.MarkDeprecated uri =
            lock sync (fun () ->
                match store.TryGetValue uri with
                | true, r when not r.IsDeprecated ->
                    store.[uri] <- { r with IsDeprecated = true }
                    save()
                    true
                | _ -> false)
