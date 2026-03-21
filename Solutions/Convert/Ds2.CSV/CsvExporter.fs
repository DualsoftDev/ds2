namespace Ds2.CSV

open System
open System.IO
open System.Text
open Ds2.Core
open Ds2.Store

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

    let private appendCallRows (builder: StringBuilder) (flowName: string) (workName: string) (call: Call) =
        let appendRow (inName: string) (inAddress: string) (outName: string) (outAddress: string) =
            builder.AppendLine(
                $"{escape flowName},{escape workName},{escape call.DevicesAlias},{escape call.ApiName},{escape inName},{escape inAddress},{escape outName},{escape outAddress}")
            |> ignore

        if call.ApiCalls.Count = 0 then
            appendRow "" "" "" ""
        else
            for apiCall in call.ApiCalls do
                appendRow (tagName apiCall.InTag) (tagAddress apiCall.InTag) (tagName apiCall.OutTag) (tagAddress apiCall.OutTag)

    let private appendSystemRows (store: DsStore) (systemId: Guid) (builder: StringBuilder) =
        for flow in DsQuery.flowsOf systemId store do
            for work in DsQuery.worksOf flow.Id store do
                for call in DsQuery.callsOf work.Id store do
                    appendCallRows builder flow.Name work.Name call

    let systemToCsv (store: DsStore) (systemId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine("Flow,Work,Device,Api,InName,InAddress,OutName,OutAddress") |> ignore
        appendSystemRows store systemId builder
        builder.ToString()

    let projectToCsv (store: DsStore) (projectId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine("Flow,Work,Device,Api,InName,InAddress,OutName,OutAddress") |> ignore
        for system in DsQuery.activeSystemsOf projectId store do
            appendSystemRows store system.Id builder
        builder.ToString()

    let saveProjectToFile (store: DsStore) (outputPath: string) : Result<unit, string> =
        let projects = DsQuery.allProjects store
        if projects.IsEmpty then
            Error "프로젝트가 없습니다."
        else
            let content = projectToCsv store projects.Head.Id
            try
                File.WriteAllText(outputPath, content, Encoding.UTF8)
                Ok ()
            with ex ->
                Error $"Export 실패: {ex.Message}"
