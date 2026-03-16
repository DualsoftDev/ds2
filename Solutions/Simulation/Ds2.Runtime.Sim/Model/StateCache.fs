namespace Ds2.Runtime.Sim.Model

open System
open System.Collections.Concurrent
open Ds2.Core

/// 시뮬레이션 상태 캐시 (스레드 안전, C# UI용)
type StateCache() =
    let cache = ConcurrentDictionary<Guid, Status4>()

    member _.Set(nodeGuid: Guid, state: Status4) = cache.[nodeGuid] <- state

    member _.TryGet(nodeGuid: Guid) =
        match cache.TryGetValue(nodeGuid) with
        | true, state -> Some state
        | false, _ -> None

    member _.GetOrDefault(nodeGuid: Guid, defaultState: Status4) =
        match cache.TryGetValue(nodeGuid) with
        | true, state -> state
        | false, _ -> defaultState

    member _.Clear() = cache.Clear()
