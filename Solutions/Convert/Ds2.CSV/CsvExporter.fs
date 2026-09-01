namespace Ds2.CSV

open System
open System.IO
open System.Text
open Ds2.Core
open Ds2.Core.Store

module CsvExporter =

    let private escape (value: string) =
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

    let private appendCallRows (store: DsStore) (builder: StringBuilder) (flowName: string) (workName: string) (call: Call) =
        let appendRow (systemName: string) (inName: string) (inAddress: string) (outName: string) (outAddress: string) =
            builder.AppendLine(
                $"{escape flowName},{escape workName},{escape call.DevicesAlias},{escape systemName},{escape call.ApiName},{escape inName},{escape inAddress},{escape outName},{escape outAddress}")
            |> ignore

        if call.ApiCalls.Count = 0 then
            appendRow (resolveSystemName store call None) "" "" "" ""
        else
            for apiCall in call.ApiCalls do
                appendRow
                    (resolveSystemName store call (Some apiCall))
                    (tagName apiCall.InTag) (tagAddress apiCall.InTag)
                    (tagName apiCall.OutTag) (tagAddress apiCall.OutTag)

    let private csvHeader = "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress"

    let private appendSystemRows (store: DsStore) (systemId: Guid) (builder: StringBuilder) =
        for flow in Queries.flowsOf systemId store do
            for work in Queries.worksOf flow.Id store do
                for call in Queries.callsOf work.Id store do
                    appendCallRows store builder flow.Name work.LocalName call

    let systemToCsv (store: DsStore) (systemId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine(csvHeader) |> ignore
        appendSystemRows store systemId builder
        builder.ToString()

    let projectToCsv (store: DsStore) (projectId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine(csvHeader) |> ignore
        for system in Queries.activeSystemsOf projectId store do
            appendSystemRows store system.Id builder
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
