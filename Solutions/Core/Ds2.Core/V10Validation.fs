namespace Ds2.Core

/// <summary>v10 spec §12 — Validation V1~V6.
/// V1~V4 Error, V5~V6 Warning.</summary>
module V10Validation =

    type Severity = Error | Warning

    type ValidationIssue = {
        Rule: string         // "V1".."V6"
        Severity: Severity
        Message: string
    }

    // V1: ActionType = Real(_, _) ⇒ OutTag required (Error)
    let validateApiCallV1 (apiDef: ApiDef) (apiCall: ApiCall) : ValidationIssue option =
        match apiDef.ActionType with
        | ActionType.Real _ when apiCall.OutTag.IsNone ->
            Some { Rule = "V1"; Severity = Error
                   Message = sprintf "ApiCall '%s' — ActionType=Real ⇒ OutTag 필수" apiCall.Name }
        | _ -> None

    // V2: SensingType = Real(_, _) ⇒ InTag required (Error)
    let validateApiCallV2 (apiDef: ApiDef) (apiCall: ApiCall) : ValidationIssue option =
        match apiDef.SensingType with
        | SensingType.Real _ when apiCall.InTag.IsNone ->
            Some { Rule = "V2"; Severity = Error
                   Message = sprintf "ApiCall '%s' — SensingType=Real ⇒ InTag 필수" apiCall.Name }
        | _ -> None

    // V3: TimePolicy.Append ms > 0 (Error)
    let private timePolicyMsCheck (ctx: string) (t: TimePolicy option) : ValidationIssue option =
        match t with
        | Some (Append ms) when ms <= 0 ->
            Some { Rule = "V3"; Severity = Error
                   Message = sprintf "%s — TimePolicy.Append ms 는 양의 정수 (실제: %d)" ctx ms }
        | _ -> None

    let validateApiDefV3 (apiDef: ApiDef) : ValidationIssue list =
        let actionT =
            match apiDef.ActionType with
            | ActionType.Real (_, t) -> t
            | ActionType.Virtual t -> t
        let sensingT =
            match apiDef.SensingType with
            | SensingType.Real (_, t) -> t
            | SensingType.Virtual t -> t
        [ timePolicyMsCheck (sprintf "ApiDef '%s' ActionType" apiDef.Name) actionT
          timePolicyMsCheck (sprintf "ApiDef '%s' SensingType" apiDef.Name) sensingT ]
        |> List.choose id

    // V4: Virtual(_) ⇒ Work.Duration defined (Error)
    let validateApiDefV4 (apiDef: ApiDef) (txWorkDuration: int option) (rxWorkDuration: int option) : ValidationIssue list =
        let isVirtual =
            match apiDef.ActionType, apiDef.SensingType with
            | ActionType.Virtual _, _ | _, SensingType.Virtual _ -> true
            | _ -> false
        if not isVirtual then []
        else
            let workOk = txWorkDuration |> Option.exists (fun d -> d > 0)
                      || rxWorkDuration |> Option.exists (fun d -> d > 0)
            if workOk then []
            else
                [{ Rule = "V4"; Severity = Error
                   Message = sprintf "ApiDef '%s' — Virtual ⇒ Work.Duration 정의 필수" apiDef.Name }]

    // V5: ValueSpec type ≡ IOTag.DataType (Warning)
    let private valueSpecMatchesDataType (spec: ValueSpec) (dataType: IOTagDataType) : bool =
        match spec, dataType with
        | UndefinedValue, _ -> true
        | BoolValue _,   IOTagDataType.BOOL   -> true
        | Int8Value _,   IOTagDataType.SINT   -> true
        | Int16Value _,  IOTagDataType.INT    -> true
        | Int32Value _,  IOTagDataType.DINT   -> true
        | Int64Value _,  IOTagDataType.LINT   -> true
        | UInt8Value _,  IOTagDataType.USINT  -> true
        | UInt16Value _, IOTagDataType.UINT   -> true
        | UInt32Value _, IOTagDataType.UDINT  -> true
        | UInt64Value _, IOTagDataType.ULINT  -> true
        | Float32Value _, IOTagDataType.REAL  -> true
        | Float64Value _, IOTagDataType.LREAL -> true
        | StringValue _, IOTagDataType.STRING -> true
        | _ -> false

    let validateApiCallV5 (apiCall: ApiCall) : ValidationIssue list =
        let issues = ResizeArray()
        apiCall.InTag |> Option.iter (fun tag ->
            if not (valueSpecMatchesDataType apiCall.InputSpec tag.DataType) then
                issues.Add { Rule = "V5"; Severity = Warning
                             Message = sprintf "ApiCall '%s' — InputSpec type ≢ InTag.DataType (%A)" apiCall.Name tag.DataType })
        apiCall.OutTag |> Option.iter (fun tag ->
            if not (valueSpecMatchesDataType apiCall.OutputSpec tag.DataType) then
                issues.Add { Rule = "V5"; Severity = Warning
                             Message = sprintf "ApiCall '%s' — OutputSpec type ≢ OutTag.DataType (%A)" apiCall.Name tag.DataType })
        List.ofSeq issues

    // V6: Latched ApiCall collision on same Device (Warning)
    // 같은 Device(ApiDef.ParentId) 안에 Latched ActionType ApiDef 가 2개 이상이면 Warning.
    let validateDeviceV6 (deviceId: System.Guid) (apiDefs: ApiDef list) : ValidationIssue list =
        let latched =
            apiDefs
            |> List.filter (fun ad ->
                ad.ParentId = deviceId
                && match ad.ActionType with
                   | ActionType.Real (Latched, _) -> true
                   | _ -> false)
        if latched.Length < 2 then []
        else
            let names = latched |> List.map (fun ad -> ad.Name) |> String.concat ", "
            [{ Rule = "V6"; Severity = Warning
               Message = sprintf "Device '%O' — Latched ApiDef 충돌 가능 (%s) — intentional?" deviceId names }]
