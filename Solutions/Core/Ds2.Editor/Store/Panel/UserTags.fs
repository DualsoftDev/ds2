namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.LoggingHelpers


// ─── UserTag helpers ─────────────────────────────────────────────────

module internal PanelUserTagOps =

    /// LoggingSystemProperties 가 없으면 즉시 생성
    let ensureLoggingProps (sys: DsSystem) : LoggingSystemProperties =
        match sys.GetLoggingProperties() with
        | Some p -> p
        | None ->
            let p = LoggingSystemProperties()
            sys.SetLoggingProperties(p)
            p

    /// ResizeArray<string> → UserTagPanelItem list (인덱스 보존)
    let toPanelItems (raw: System.Collections.Generic.IList<string>) : UserTagPanelItem list =
        raw
        |> Seq.mapi (fun i encoded ->
            match UserTagHelpers.parse encoded with
            | Some tag ->
                Some (UserTagPanelItem(
                    i, tag.Name,
                    UserTagHelpers.logLevelToString tag.LogLevel,
                    tag.TagAddress,
                    UserTagHelpers.valueTypeToString tag.ValueType))
            | None -> None)
        |> Seq.choose id
        |> Seq.toList

    let buildTag (name: string) (logLevel: string) (tagAddress: string) (valueType: string) : UserTag =
        {
            Name = name
            LogLevel = UserTagHelpers.parseLogLevel logLevel
            TagAddress = tagAddress
            ValueType = UserTagHelpers.parseValueType valueType
        }


// ─── UserTag extensions ──────────────────────────────────────────────

[<Extension>]
type DsStorePanelUserTagExtensions =

    [<Extension>]
    static member GetUserTagsForSystem(store: DsStore, systemId: Guid) : UserTagPanelItem list =
        match Queries.getSystem systemId store with
        | Some sys ->
            match sys.GetLoggingProperties() with
            | Some p -> PanelUserTagOps.toPanelItems p.UserTags
            | None -> []
        | None -> []

    /// 프로젝트 전체에 정의된 UserTag 를 System 단위로 평탄화한 결과 반환.
    /// Tag Inspector 의 "사용자 태그" 탭이 호출하는 진입점.
    [<Extension>]
    static member GetAllUserTagsForProject(store: DsStore) : ProjectUserTagRow list =
        Queries.allProjects store
        |> List.collect (fun project -> Queries.projectSystemsOf project.Id store)
        |> List.collect (fun sys ->
            match sys.GetLoggingProperties() with
            | None -> []
            | Some props ->
                props.UserTags
                |> Seq.mapi (fun i encoded ->
                    match UserTagHelpers.parse encoded with
                    | Some tag ->
                        Some (ProjectUserTagRow(
                            sys.Id, sys.Name, i,
                            tag.Name,
                            UserTagHelpers.logLevelToString tag.LogLevel,
                            tag.TagAddress,
                            UserTagHelpers.valueTypeToString tag.ValueType))
                    | None -> None)
                |> Seq.choose id
                |> Seq.toList)

    [<Extension>]
    static member AddUserTag
        (store: DsStore, systemId: Guid,
         name: string, logLevel: string, tagAddress: string, valueType: string) : int =
        StoreLog.debug($"AddUserTag systemId={systemId}, name={name}")
        StoreLog.requireSystem(store, systemId) |> ignore
        let mutable newIndex = -1
        store.WithTransaction($"사용자 태그 추가 \"{name}\"", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                let props = PanelUserTagOps.ensureLoggingProps sys
                let tag = PanelUserTagOps.buildTag name logLevel tagAddress valueType
                props.UserTags.Add(UserTagHelpers.format tag)
                newIndex <- props.UserTags.Count - 1))
        store.EmitAndHistory(SystemPropsChanged systemId)
        newIndex

    [<Extension>]
    static member UpdateUserTag
        (store: DsStore, systemId: Guid, index: int,
         name: string, logLevel: string, tagAddress: string, valueType: string) : bool =
        StoreLog.debug($"UpdateUserTag systemId={systemId}, index={index}, name={name}")
        StoreLog.requireSystem(store, systemId) |> ignore
        let mutable ok = false
        store.WithTransaction("사용자 태그 편집", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                match sys.GetLoggingProperties() with
                | Some p when index >= 0 && index < p.UserTags.Count ->
                    let tag = PanelUserTagOps.buildTag name logLevel tagAddress valueType
                    p.UserTags.[index] <- UserTagHelpers.format tag
                    ok <- true
                | _ -> ()))
        if ok then store.EmitAndHistory(SystemPropsChanged systemId)
        ok

    [<Extension>]
    static member RemoveUserTag(store: DsStore, systemId: Guid, index: int) : bool =
        StoreLog.debug($"RemoveUserTag systemId={systemId}, index={index}")
        StoreLog.requireSystem(store, systemId) |> ignore
        let mutable ok = false
        store.WithTransaction("사용자 태그 삭제", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                match sys.GetLoggingProperties() with
                | Some p when index >= 0 && index < p.UserTags.Count ->
                    p.UserTags.RemoveAt(index)
                    ok <- true
                | _ -> ()))
        if ok then store.EmitAndHistory(SystemPropsChanged systemId)
        ok

    /// CSV 일괄 추가: (name, logLevel, tagAddress, valueType) 리스트를 한 transaction 으로 append.
    [<Extension>]
    static member AddUserTagsBatch
        (store: DsStore, systemId: Guid,
         entries: System.Collections.Generic.IReadOnlyList<struct (string * string * string * string)>) : int =
        if isNull (box entries) || entries.Count = 0 then 0
        else
            StoreLog.requireSystem(store, systemId) |> ignore
            let mutable added = 0
            store.WithTransaction($"사용자 태그 일괄 추가 ({entries.Count}건)", fun () ->
                store.TrackMutate(store.Systems, systemId, fun sys ->
                    let props = PanelUserTagOps.ensureLoggingProps sys
                    for e in entries do
                        let struct (name, level, addr, vt) = e
                        if not (System.String.IsNullOrWhiteSpace(name)) then
                            let tag = PanelUserTagOps.buildTag name level addr vt
                            props.UserTags.Add(UserTagHelpers.format tag)
                            added <- added + 1))
            if added > 0 then store.EmitAndHistory(SystemPropsChanged systemId)
            added

    /// CSV 교체: 기존 항목 전부 삭제 후 새 항목들 추가.
    [<Extension>]
    static member ReplaceUserTags
        (store: DsStore, systemId: Guid,
         entries: System.Collections.Generic.IReadOnlyList<struct (string * string * string * string)>) : int =
        StoreLog.requireSystem(store, systemId) |> ignore
        let mutable count = 0
        store.WithTransaction($"사용자 태그 교체 ({entries.Count}건)", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                let props = PanelUserTagOps.ensureLoggingProps sys
                props.UserTags.Clear()
                for e in entries do
                    let struct (name, level, addr, vt) = e
                    if not (System.String.IsNullOrWhiteSpace(name)) then
                        let tag = PanelUserTagOps.buildTag name level addr vt
                        props.UserTags.Add(UserTagHelpers.format tag)
                        count <- count + 1))
        store.EmitAndHistory(SystemPropsChanged systemId)
        count
