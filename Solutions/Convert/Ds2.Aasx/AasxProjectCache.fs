namespace Ds2.Aasx

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

/// Import 시 로드된 원본 AASX 데이터 (Export 시 재사용)
type AasxProjectData = {
    /// 원본 AAS Environment (다른 서브모델 보존용, boxed)
    Environment : obj
    /// 원본 ZIP 엔트리 (썸네일·첨부파일 보존용)
    Entries     : Dictionary<string, byte[]>
}

/// store 에 AASX 런타임 데이터를 매달고 Project.Id 로 찾는 캐시.
///
/// **Project 인스턴스를 키로 쓰면 안 된다.** `StoreAuthoring.trackMutate` 의 undo 는
/// `dict.[id] &lt;- oldSnapshot` 으로 엔티티 인스턴스를 스냅샷으로 통째 교체한다. System 추가/삭제는
/// Project 를 TrackMutate 하므로(Nodes.fs / Remove.fs), 그 편집을 Undo 하는 순간 인스턴스 키가
/// 어긋나 캐시가 고아가 된다. 그러면 다음 저장에서 원본 썸네일과 보존 대상 비-DS 서브모델이
/// 경고 없이 빠진다 — 병합 실패는 예외로 저장을 중단시키지만 캐시 미스는 "보존할 것 없음" 으로
/// 읽히기 때문이다.
///
/// store 인스턴스는 import 의 `ReplaceStore`(내용 컬렉션만 교체) 를 거쳐도 유지되므로 안정적인
/// 앵커이고, ConditionalWeakTable 이라 store 가 GC 되면 원본 ZIP 바이트도 함께 풀린다.
module AasxProjectCache =

    let private table = ConditionalWeakTable<DsStore, Dictionary<Guid, AasxProjectData>>()

    let private tryGet (store: DsStore) (project: Project) : AasxProjectData option =
        match table.TryGetValue(store) with
        | true, byProject ->
            match byProject.TryGetValue(project.Id) with
            | true, data -> Some data
            | _ -> None
        | _ -> None

    let set (store: DsStore) (project: Project) (env: obj) (entries: Dictionary<string, byte[]>) =
        table.GetOrCreateValue(store).[project.Id] <- { Environment = env; Entries = entries }

    let tryGetEnvironment (store: DsStore) (project: Project) : obj option =
        tryGet store project |> Option.map (fun d -> d.Environment)

    let tryGetEntries (store: DsStore) (project: Project) : Dictionary<string, byte[]> option =
        tryGet store project |> Option.map (fun d -> d.Entries)

    /// Save가 끝난 뒤 병합된 최신 Environment만 갱신한다.
    /// ZIP 부가 엔트리는 최초 import 때 보관한 값을 계속 유지한다.
    let updateEnvironment (store: DsStore) (project: Project) (env: obj) =
        match table.TryGetValue(store) with
        | true, byProject ->
            match byProject.TryGetValue(project.Id) with
            | true, data -> byProject.[project.Id] <- { data with Environment = env }
            | _ -> ()
        | _ -> ()

    let clear (store: DsStore) (project: Project) =
        match table.TryGetValue(store) with
        | true, byProject -> byProject.Remove(project.Id) |> ignore
        | _ -> ()
