namespace Ds2.CSV

open System
open System.IO
open System.Text
open Ds2.Core
open Ds2.Core.Store

module CsvExporter =

    let internal escape (value: string) =
        if String.IsNullOrEmpty(value) then
            ""
        elif value.Contains(",") || value.Contains("\"") || value.Contains("\n") then
            let escaped = value.Replace("\"", "\"\"")
            "\"" + escaped + "\""
        else
            value

    let private tagAddress (tag: IOTag option) =
        tag |> Option.map (fun current -> current.Address) |> Option.defaultValue ""

    let private tagName (tag: IOTag option) =
        tag |> Option.map (fun current -> current.Name) |> Option.defaultValue ""

    /// ApiCall 이 가리키는 ApiDef 의 소유 System 이름. Call 1개가 여러 System 을 구동할 수 있으므로
    /// (솔레노이드 1개 ↔ 실린더 N개) Call 단위가 아니라 **ApiCall(=출력 행) 단위**로 해석한다.
    let private resolveSystemName (store: DsStore) (call: Call) (apiCall: ApiCall option) =
        apiCall
        |> Option.bind (fun ac -> ac.ApiDefId)
        |> Option.bind (fun defId -> match store.ApiDefs.TryGetValue(defId) with true, d -> Some d | _ -> None)
        |> Option.bind (fun def -> match store.Systems.TryGetValue(def.ParentId) with true, s -> Some s.Name | _ -> None)
        |> Option.defaultValue call.DevicesAlias

    /// Reference Call 이면 (원본 Call, 원본이 있는 Work 의 "Flow.Work").
    /// 원본이 지워졌거나 참조가 순환하면 None — 가리킬 원본이 없으니 일반 행으로 쓴다.
    let private tryReferenceTarget (store: DsStore) (call: Call) =
        match call.ReferenceOf with
        | None -> None
        | Some _ ->
            let originalId =
                try Some (Queries.resolveOriginalCallId call.Id store)
                with :? InvalidOperationException -> None
            originalId
            |> Option.bind (fun id -> Queries.getCall id store)
            |> Option.bind (fun original ->
                Queries.getWork original.ParentId store
                |> Option.bind (fun work ->
                    Queries.getFlow work.ParentId store
                    |> Option.map (fun flow -> original, $"{flow.Name}.{work.LocalName}")))

    let private appendCallRows (store: DsStore) (builder: StringBuilder) (activeSystemName: string) (flowName: string) (workName: string) (call: Call) =
        let appendRow (deviceAlias: string) (systemName: string) (apiName: string) (inName: string) (inAddress: string) (outName: string) (outAddress: string) (referenceOf: string) =
            builder.AppendLine(
                $"{escape flowName},{escape workName},{escape deviceAlias},{escape systemName},{escape apiName},{escape inName},{escape inAddress},{escape outName},{escape outAddress},{escape activeSystemName},{escape referenceOf}")
            |> ignore

        match tryReferenceTarget store call with
        | Some (original, originWork) ->
            // Reference 는 ApiCall 이 없다. 예전엔 System 열에 별칭을 쓴 일반 행이라 다시 읽으면 유령 디바이스가 생겼다.
            // 디바이스·API 는 원본 값을 쓰고, 태그·주소는 원본 행에 있으므로 비운다.
            let deviceSystemName = resolveSystemName store original (Seq.tryHead original.ApiCalls)
            appendRow original.DevicesAlias deviceSystemName original.ApiName "" "" "" "" originWork
        | None ->
            if call.ApiCalls.Count = 0 then
                appendRow call.DevicesAlias (resolveSystemName store call None) call.ApiName "" "" "" "" ""
            else
                for apiCall in call.ApiCalls do
                    appendRow
                        call.DevicesAlias (resolveSystemName store call (Some apiCall)) call.ApiName
                        (tagName apiCall.InTag) (tagAddress apiCall.InTag)
                        (tagName apiCall.OutTag) (tagAddress apiCall.OutTag)
                        ""

    let private appendSystemRows (store: DsStore) (systemId: Guid) (activeSystemName: string) (builder: StringBuilder) =
        for flow in Queries.flowsOf systemId store do
            for work in Queries.worksOf flow.Id store do
                for call in Queries.callsOf work.Id store do
                    appendCallRows store builder activeSystemName flow.Name work.LocalName call

    /// System 하나만 내보낼 때는 ActiveSystem 을 비운다. 이 CSV 는 buildSystemImportPlan 으로 고른 System 에
    /// 넣는 용도라, 이름을 적으면 고른 System 대신 원래 이름의 System 이 새로 생긴다.
    let systemToCsv (store: DsStore) (systemId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine(CsvParser.header11Text) |> ignore
        appendSystemRows store systemId "" builder
        builder.ToString()

    /// 행마다 소속 Active System 이름을 적는다 — 예전엔 빠져서 다시 읽으면 모든 System 이 하나로 합쳐졌다.
    let projectToCsv (store: DsStore) (projectId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine(CsvParser.header11Text) |> ignore
        for system in Queries.activeSystemsOf projectId store do
            appendSystemRows store system.Id system.Name builder
        builder.ToString()

    let saveProjectToFile (store: DsStore) (outputPath: string) : Result<unit, string> =
        let projects = Queries.allProjects store
        if projects.IsEmpty then
            Error "프로젝트가 없습니다."
        else
            let content = projectToCsv store projects.Head.Id
            try
                File.WriteAllText(outputPath, content, Encoding.UTF8)
                Ok ()
            with ex ->
                Error $"Export 실패: {ex.Message}"
