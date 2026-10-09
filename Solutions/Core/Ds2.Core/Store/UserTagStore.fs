namespace Ds2.Core.Store

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.LoggingHelpers

/// 프로젝트 전체 UserTag 행 — 어느 System 소속인지 SystemId / SystemName 을 동반한다.
/// DSPilot(이상알람·태그 모니터·서버 측 교체)·Promaker(Tag Inspector)·Hub 가 같은 행을 읽는다.
[<Sealed>]
type ProjectUserTagRow
    (systemId: Guid, systemName: string,
     index: int, name: string, logLevel: string, tagAddress: string, valueType: string,
     matchOp: string, matchValue: string) =
    member _.SystemId   = systemId
    member _.SystemName = systemName
    member _.Index      = index
    member _.Name       = name
    member _.LogLevel   = logLevel
    member _.TagAddress = tagAddress
    member _.ValueType  = valueType
    member _.MatchOp    = matchOp
    member _.MatchValue = matchValue

/// UserTag 모델 데이터의 정본 CRUD — System 의 LoggingProperties.UserTags(인코딩 문자열 목록)를 읽고 쓴다.
///
/// Undo·편집 이벤트는 여기 없다(모델 계층). 편집기(Ds2.Editor)는 이 함수들을 트랜잭션으로 감싸고,
/// 서버(DSPilot·Hub)는 그대로 쓴다. 예전에는 이 쓰기가 Ds2.Editor 의 패널 코드에 있어서 서버가
/// 편집기 전체를 참조해야 했다.
///
/// 입력 1건 = (name, logLevel, tagAddress, valueType, matchOp, matchValue). 레거시 4컬럼 CSV 는
/// 호출 측이 빈 matchOp/matchValue 로 정규화해서 넘긴다(빈 matchOp 는 값 타입 기본 연산으로 채워진다).
[<RequireQualifiedAccess>]
module UserTagStore =

    /// LoggingSystemProperties 가 없으면 즉시 만든다.
    let ensureLoggingProps (sys: DsSystem) : LoggingSystemProperties =
        match sys.GetLoggingProperties() with
        | Some p -> p
        | None ->
            let p = LoggingSystemProperties()
            sys.SetLoggingProperties(p)
            p

    let buildTag (name: string) (logLevel: string) (tagAddress: string) (valueType: string)
                 (matchOp: string) (matchValue: string) : UserTag =
        let vt = UserTagHelpers.parseValueType valueType
        let op =
            if String.IsNullOrWhiteSpace(matchOp)
            then UserTagHelpers.defaultMatchOpFor vt
            else UserTagHelpers.parseMatchOp matchOp
        {
            Name = name
            LogLevel = UserTagHelpers.parseLogLevel logLevel
            TagAddress = tagAddress
            ValueType = vt
            MatchOp = op
            MatchValue = if isNull matchValue then "" else matchValue.Trim()
        }

    /// 한 System 의 UserTag 를 (저장 인덱스, 해석된 태그) 로. 해석에 실패한 항목은 건너뛰되 인덱스는 보존한다.
    let parsedTagsOf (sys: DsSystem) : (int * UserTag) list =
        match sys.GetLoggingProperties() with
        | None -> []
        | Some props ->
            props.UserTags
            |> Seq.mapi (fun i encoded -> UserTagHelpers.parse encoded |> Option.map (fun tag -> i, tag))
            |> Seq.choose id
            |> Seq.toList

    /// entries 를 끝에 추가. 빈 이름은 건너뛴다. 반환 = 추가한 수.
    let appendAll
        (sys: DsSystem)
        (entries: IReadOnlyList<struct (string * string * string * string * string * string)>) : int =
        if isNull (box entries) || entries.Count = 0 then 0
        else
            let props = ensureLoggingProps sys
            let mutable added = 0
            for e in entries do
                let struct (name, level, addr, vt, op, mv) = e
                if not (String.IsNullOrWhiteSpace(name)) then
                    props.UserTags.Add(UserTagHelpers.format (buildTag name level addr vt op mv))
                    added <- added + 1
            added

    /// System 의 UserTag 전부를 entries 로 교체(기존 항목 삭제 후 추가). 반환 = 기록한 수.
    let replaceAll
        (sys: DsSystem)
        (entries: IReadOnlyList<struct (string * string * string * string * string * string)>) : int =
        let props = ensureLoggingProps sys
        props.UserTags.Clear()
        appendAll sys entries

    let private rowOf (sys: DsSystem) (index: int, tag: UserTag) =
        ProjectUserTagRow(
            sys.Id, sys.Name, index,
            tag.Name,
            UserTagHelpers.logLevelToString tag.LogLevel,
            tag.TagAddress,
            UserTagHelpers.valueTypeToString tag.ValueType,
            UserTagHelpers.matchOpToString tag.MatchOp,
            tag.MatchValue)

    /// 프로젝트 전체에 정의된 UserTag 를 System 단위로 평탄화.
    let allRows (store: DsStore) : ProjectUserTagRow list =
        Queries.allProjects store
        |> List.collect (fun project -> Queries.projectSystemsOf project.Id store)
        |> List.collect (fun sys -> parsedTagsOf sys |> List.map (rowOf sys))

/// C# 소비자(DSPilot·Hub·Promaker)용 DsStore 확장. 편집기의 Undo 가 필요한 조작은 Ds2.Editor 쪽 확장을 쓴다.
[<Extension>]
type DsStoreUserTagExtensions =

    /// 프로젝트 전체 UserTag 행 — Tag Inspector·DSPilot 이상알람·태그 모니터의 진입점.
    [<Extension>]
    static member GetAllUserTagsForProject(store: DsStore) : ProjectUserTagRow list =
        UserTagStore.allRows store

    /// 서버 측 교체(DSPilot UserTag 적용·AASX 재export) — Undo·편집 이벤트 없이 모델만 바꾸고 revision 을 올린다.
    /// System 이 없으면 KeyNotFoundException. 반환 = 기록한 수.
    [<Extension>]
    static member ReplaceUserTags
        (store: DsStore, systemId: Guid,
         entries: IReadOnlyList<struct (string * string * string * string * string * string)>) : int =
        match Queries.getSystem systemId store with
        | None -> raise (KeyNotFoundException($"System not found. id={systemId}"))
        | Some sys ->
            let count = UserTagStore.replaceAll sys entries
            store.BumpRevision()
            count
