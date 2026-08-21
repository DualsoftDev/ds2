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

    /// Save가 끝난 뒤 병합된 최신 Environment만 갱신한다.
    /// ZIP 부가 엔트리는 최초 import 때 보관한 값을 계속 유지한다.
    let updateEnvironment (project: Project) (env: obj) =
        match table.TryGetValue(project) with
        | true, r -> r.Value <- { r.Value with Environment = env }
        | _ -> ()

    let clear (project: Project) =
        table.Remove(project) |> ignore
