namespace Ds2.Mermaid

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Store

module internal MermaidMapperCommon =

    /// ArrowLabel → ds2 ArrowType 변환
    let mapArrowType (label: ArrowLabel) : ArrowType =
        match label with
        | NoLabel     -> ArrowType.Start
        | Interlock   -> ArrowType.Reset
        | SelfReset   -> ArrowType.Reset
        | StartReset  -> ArrowType.StartReset
        | StartEdge   -> ArrowType.Start
        | ResetEdge   -> ArrowType.Reset
        | AutoPre     -> ArrowType.Start
        | ResetReset  -> ArrowType.ResetReset
        | Group       -> ArrowType.Group
        | Custom _    -> ArrowType.Start

    /// Mermaid 노드 라벨에서 Call 이름 분리
    let splitCallName (label: string) : string * string =
        match label.IndexOf('.') with
        | -1  -> ("imported", label)
        | idx -> (label.[..idx - 1], label.[idx + 1..])

    /// 노드의 조건 참조를 CallCondition으로 복원
    let restoreConditions
        (registerApiCall: ApiCall -> unit)
        (targetCall: Call)
        (nodeRefToCallId: Dictionary<string, Guid>)
        (uniqueNameToCallId: Dictionary<string, Guid>)
        (callsById: Dictionary<Guid, Call>)
        (node: MermaidNode) =
        let tryResolveSourceCall (sourceRef: string) =
            match nodeRefToCallId.TryGetValue(sourceRef) with
            | true, srcCallId ->
                match callsById.TryGetValue(srcCallId) with
                | true, srcCall -> Some srcCall
                | _ -> None
            | _ ->
                match uniqueNameToCallId.TryGetValue(sourceRef) with
                | true, srcCallId ->
                    match callsById.TryGetValue(srcCallId) with
                    | true, srcCall -> Some srcCall
                    | _ -> None
                | _ -> None

        let addCondition (condType: CallConditionType) (sourceNames: string list) =
            if sourceNames.IsEmpty then ()
            else
                let cond = CallCondition()
                cond.Type <- Some condType
                for srcName in sourceNames do
                    match tryResolveSourceCall srcName with
                    | Some srcCall ->
                        // source Call에 ApiCall이 있으면 첫 번째를 사용, 없으면 생성
                        let apiCall =
                            if srcCall.ApiCalls.Count > 0 then
                                srcCall.ApiCalls.[0]
                            else
                                let ac = ApiCall(srcCall.Name)
                                srcCall.ApiCalls.Add(ac)
                                registerApiCall ac
                                ac
                        cond.Conditions.Add(apiCall)
                    | None -> ()
                if cond.Conditions.Count > 0 then
                    targetCall.CallConditions.Add(cond)

        addCondition CallConditionType.AutoAux      node.AutoAuxConditionRefs
        addCondition CallConditionType.ComAux       node.ComAuxConditionRefs
        addCondition CallConditionType.SkipUnmatch  node.SkipUnmatchConditionRefs

    /// subgraph 표시 이름 결정
    let subgraphName (sg: MermaidSubgraph) : string =
        sg.DisplayName |> Option.defaultValue sg.Id
