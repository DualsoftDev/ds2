namespace Ds2.Aasx

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core

/// Import 시 로드된 원본 AASX 데이터 (Export 시 재사용)
type AasxProjectData = {
    /// 원본 AAS Environment (다른 서브모델 보존용, boxed)
    Environment : obj
    /// 원본 ZIP 엔트리 (썸네일·첨부파일 보존용)
    Entries     : Dictionary<string, byte[]>
}

/// Project 인스턴스에 AASX 런타임 데이터를 연결하는 캐시.
/// ConditionalWeakTable 사용 → Project GC 시 자동 해제.
module AasxProjectCache =

    let private table = ConditionalWeakTable<Project, AasxProjectData ref>()

    let set (project: Project) (env: obj) (entries: Dictionary<string, byte[]>) =
        let data = ref { Environment = env; Entries = entries }
        table.AddOrUpdate(project, data)

    let tryGetEnvironment (project: Project) : obj option =
        match table.TryGetValue(project) with
        | true, r -> Some r.Value.Environment
        | _ -> None

    let tryGetEntries (project: Project) : Dictionary<string, byte[]> option =
        match table.TryGetValue(project) with
        | true, r -> Some r.Value.Entries
        | _ -> None

    let clear (project: Project) =
        table.Remove(project) |> ignore
