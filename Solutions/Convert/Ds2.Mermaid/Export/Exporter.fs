namespace Ds2.Mermaid

open System
open System.Text
open System.Text.RegularExpressions
open System.Collections.Generic
open Ds2.Core
open Ds2.Store

/// DsStore → Mermaid flowchart 변환 모듈
module MermaidExporter =

    let private sanitizePattern = Regex(@"[\s\-\.:/\\()\[\]{}<>""']", RegexOptions.Compiled)

    /// ID에서 Mermaid 예약어와 특수문자 제거
    let private sanitizeId (id: string) : string =
        if String.IsNullOrWhiteSpace(id) then "unknown"
        else sanitizePattern.Replace(id, "_")

    /// 화살표를 Mermaid 문자열로 출력
    let private emitArrow (sb: StringBuilder) (indent: string) (sourceId: string) (targetId: string) (arrowType: ArrowType) =
        match arrowType with
        | ArrowType.Start      -> sb.AppendLine($"{indent}{sourceId} --> {targetId}")                  |> ignore
        | ArrowType.Reset      -> sb.AppendLine($"{indent}{sourceId} -.->|reset| {targetId}")          |> ignore
        | ArrowType.StartReset -> sb.AppendLine($"{indent}{sourceId} -->|startReset| {targetId}")      |> ignore
        | ArrowType.ResetReset -> sb.AppendLine($"{indent}{sourceId} -.->|resetReset| {targetId}")     |> ignore
        | ArrowType.Group      -> sb.AppendLine($"{indent}{sourceId} o--o|group| {targetId}")          |> ignore
        | _                    -> sb.AppendLine($"{indent}{sourceId} --> {targetId}")                   |> ignore

    /// Call의 조건을 라벨 suffix로 생성 (예: "<br>Auto: src1, src2<br>Common: src3")
    let private buildConditionSuffix
        (store: DsStore)
        (callIdToNodeId: Dictionary<Guid, string>)
        (call: Call)
        : string =
        let grouped = Dictionary<CallConditionType, ResizeArray<string>>()

        for cond in call.CallConditions do
            match cond.Type with
            | Some condType ->
                for apiCall in cond.Conditions do
                    // ApiCall이 속한 Call을 찾아서 source Call 이름 사용
                    for kvp in store.CallsReadOnly do
                        let srcCall = kvp.Value
                        if srcCall.ApiCalls |> Seq.exists (fun ac -> ac.Id = apiCall.Id) then
                            if not (grouped.ContainsKey(condType)) then
                                grouped.[condType] <- ResizeArray<string>()
                            let refKey =
                                match callIdToNodeId.TryGetValue(srcCall.Id) with
                                | true, nodeId -> nodeId
                                | _ -> srcCall.Name
                            grouped.[condType].Add(refKey)
            | None -> ()

        let sb = StringBuilder()
        for condType in [| CallConditionType.AutoAux; CallConditionType.ComAux; CallConditionType.SkipUnmatch |] do
            match grouped.TryGetValue(condType) with
            | true, names when names.Count > 0 ->
                let prefix =
                    match condType with
                    | CallConditionType.AutoAux      -> "AutoAux"
                    | CallConditionType.ComAux       -> "ComAux"
                    | _                              -> "SkipUnmatch"
                let joined = String.Join(", ", names)
                sb.Append($"<br>{prefix}: {joined}") |> ignore
            | _ -> ()
        sb.ToString()

    /// Work를 Mermaid subgraph로 변환 (indent 레벨 지정 가능)
    let private convertWork
        (sb: StringBuilder) (store: DsStore) (prefix: string) (indent: string) (work: Work)
        (callIdToNodeId: Dictionary<Guid, string>) (workIdMap: Dictionary<Guid, string>) =
        let workName = sanitizeId work.Name
        let workId = sanitizeId $"{prefix}_{workName}"
        workIdMap.[work.Id] <- workId

        sb.AppendLine($"""{indent}subgraph {workId}["{work.Name}"]""") |> ignore
        let innerIndent = indent + "    "

        let calls = DsQuery.callsOf work.Id store
        for call in calls do
            let nodeId = sanitizeId $"{prefix}_{workName}_{call.Name}"
            callIdToNodeId.[call.Id] <- nodeId

        // Call 노드 생성 (조건 정보를 라벨에 포함)
        for call in calls do
            let nodeId = callIdToNodeId.[call.Id]
            let condSuffix = buildConditionSuffix store callIdToNodeId call
            sb.AppendLine($"""{innerIndent}{nodeId}["{call.Name}{condSuffix}"]""") |> ignore

        // Call 간 화살표 (ArrowBetweenCalls)
        let callArrows = DsQuery.arrowCallsOf work.Id store
        for arrow in callArrows do
            match callIdToNodeId.TryGetValue(arrow.SourceId), callIdToNodeId.TryGetValue(arrow.TargetId) with
            | (true, sourceId), (true, targetId) ->
                emitArrow sb innerIndent sourceId targetId arrow.ArrowType
            | _ -> ()

        sb.AppendLine($"{indent}end") |> ignore

    // ========================================================================
    // 공개 API
    // ========================================================================

    /// 단일 Flow를 Mermaid flowchart 문자열로 변환
    let flowToMermaid (store: DsStore) (flowId: Guid) : string =
        match DsQuery.getFlow flowId store with
        | None -> ""
        | Some flow ->
            let sb = StringBuilder()
            sb.AppendLine("graph LR") |> ignore

            let flowName = sanitizeId flow.Name
            let callIdToNodeId = Dictionary<Guid, string>()
            let workIdMap = Dictionary<Guid, string>()

            let works = DsQuery.worksOf flow.Id store
            for work in works do
                convertWork sb store flowName "    " work callIdToNodeId workIdMap

            // 해당 Flow의 Work들 간 화살표만 추출
            let flowWorkIds = works |> List.map (fun w -> w.Id) |> Set.ofList
            let systemId = flow.ParentId
            let workArrows = DsQuery.arrowWorksOf systemId store
            let relevantArrows =
                workArrows
                |> List.filter (fun a -> flowWorkIds.Contains(a.SourceId) && flowWorkIds.Contains(a.TargetId))

            if relevantArrows.Length > 0 then
                sb.AppendLine("    %% Work connections") |> ignore
                for arrow in relevantArrows do
                    match workIdMap.TryGetValue(arrow.SourceId), workIdMap.TryGetValue(arrow.TargetId) with
                    | (true, sourceId), (true, targetId) ->
                        emitArrow sb "    " sourceId targetId arrow.ArrowType
                    | _ -> ()

            sb.ToString()

    /// System 1개를 subgraph로 출력하는 내부 헬퍼
    let private emitSystem
        (sb: StringBuilder) (store: DsStore) (system: DsSystem)
        (callIdToNodeId: Dictionary<Guid, string>) (workIdMap: Dictionary<Guid, string>) =
        let systemName = sanitizeId system.Name
        sb.AppendLine($"""    subgraph {systemName}["{system.Name}"]""") |> ignore

        let flows = DsQuery.flowsOf system.Id store
        for flow in flows do
            let flowName = sanitizeId $"{systemName}_{flow.Name}"
            sb.AppendLine($"""        subgraph {flowName}["{flow.Name}"]""") |> ignore

            let works = DsQuery.worksOf flow.Id store
            for work in works do
                convertWork sb store flowName "            " work callIdToNodeId workIdMap

            sb.AppendLine("        end") |> ignore

        // ArrowBetweenWorks (System 레벨)
        let workArrows = DsQuery.arrowWorksOf system.Id store
        for arrow in workArrows do
            match workIdMap.TryGetValue(arrow.SourceId), workIdMap.TryGetValue(arrow.TargetId) with
            | (true, sourceId), (true, targetId) ->
                emitArrow sb "        " sourceId targetId arrow.ArrowType
            | _ -> ()

        sb.AppendLine("    end") |> ignore

    /// System 단위 Mermaid flowchart 변환 (3-depth: System > Flow > Work > Call)
    /// Active Systems 후 %% [Passive] 마커 아래 Passive(Device) Systems 출력
    let systemToMermaid (store: DsStore) (projectId: Guid) : string =
        let sb = StringBuilder()
        sb.AppendLine("graph LR") |> ignore

        let callIdToNodeId = Dictionary<Guid, string>()
        let workIdMap = Dictionary<Guid, string>()

        let activeSystems = DsQuery.activeSystemsOf projectId store
        for system in activeSystems do
            emitSystem sb store system callIdToNodeId workIdMap

        let passiveSystems = DsQuery.passiveSystemsOf projectId store
        if not passiveSystems.IsEmpty then
            sb.AppendLine("    %% [Passive]") |> ignore
            for system in passiveSystems do
                emitSystem sb store system callIdToNodeId workIdMap

        sb.ToString()

    /// 프로젝트 전체를 Mermaid 파일로 저장
    let saveProjectToFile (store: DsStore) (outputPath: string) : Result<unit, string> =
        let projects = DsQuery.allProjects store
        if projects.IsEmpty then
            Error "프로젝트가 없습니다."
        else
            let content = systemToMermaid store projects.Head.Id
            if String.IsNullOrWhiteSpace(content) then
                Error "내보낼 내용이 없습니다."
            else
                try
                    System.IO.File.WriteAllText(outputPath, content, Text.Encoding.UTF8)
                    Ok ()
                with ex ->
                    Error $"Export 실패: {ex.Message}"

    /// Mermaid 문자열을 파일로 내보내기
    let exportToFile (mermaidContent: string) (outputPath: string) : Result<unit, string> =
        try
            System.IO.File.WriteAllText(outputPath, mermaidContent, Text.Encoding.UTF8)
            Ok ()
        with ex ->
            Error $"Export 실패: {ex.Message}"
