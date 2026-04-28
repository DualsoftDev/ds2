namespace Ds2.Aasx


open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module internal AasxExportCore =

    // idShort 규칙: 첫 글자 영문자, 나머지 영문자/숫자/밑줄 [a-zA-Z][a-zA-Z0-9_]*
    let private validateIdShort (idShort: string) : unit =
        if String.IsNullOrWhiteSpace idShort then
            invalidArg "idShort" "idShort cannot be null or whitespace"
        let chars = idShort.ToCharArray()
        if not (System.Char.IsLetter(chars.[0])) then
            invalidArg "idShort" (sprintf "idShort must start with a letter: '%s'" idShort)
        for i in 1 .. chars.Length - 1 do
            let c = chars.[i]
            if not (System.Char.IsLetterOrDigit(c) || c = '_') then
                invalidArg "idShort" (sprintf "idShort invalid char '%c' at %d in '%s'" c i idShort)

    let sanitizeIdShort (idShort: string) : string =
        validateIdShort idShort
        idShort

    let private mkTypedProp (idShort: string) (dataType: DataTypeDefXsd) (value: string) : ISubmodelElement =
        let p = Property(valueType = dataType)
        p.IdShort <- sanitizeIdShort idShort
        p.Value <- value
        p :> ISubmodelElement

    let mkProp idShort value = mkTypedProp idShort DataTypeDefXsd.String (if isNull value then "" else value)
    let mkBoolProp idShort (value: bool) = mkTypedProp idShort DataTypeDefXsd.Boolean (if value then "true" else "false")
    let mkIntProp idShort (value: int) = mkTypedProp idShort DataTypeDefXsd.Int (value.ToString())
    let mkDoubleProp idShort (value: float) = mkTypedProp idShort DataTypeDefXsd.Double (value.ToString("G", System.Globalization.CultureInfo.InvariantCulture))
    let mkTimeSpanProp idShort (value: TimeSpan) = mkTypedProp idShort DataTypeDefXsd.Duration (System.Xml.XmlConvert.ToString(value))
    let mkGuidProp idShort (value: Guid) = mkTypedProp idShort DataTypeDefXsd.String (value.ToString())
    let mkJsonProp<'T> idShort (obj: 'T) = mkProp idShort (Ds2.Serialization.JsonConverter.serialize obj)

    // AAS 규칙: SMC Value는 비어있으면 안됨 (최소 1개 이상의 요소 필요)
    let mkSmc (idShort: string) (elems: ISubmodelElement list) : ISubmodelElement =
        let finalElems =
            if elems.IsEmpty then
                // 빈 경우 최소한의 메타데이터 추가 (AAS 검증 통과를 위해)
                [ mkProp "IdShort" idShort ]
            else
                elems
        let smc = SubmodelElementCollection()
        smc.IdShort <- sanitizeIdShort idShort
        smc.Value <- ResizeArray<ISubmodelElement>(finalElems)
        smc :> ISubmodelElement

    let mkSmcOpt (idShort: string) (elems: ISubmodelElement list) : ISubmodelElement option =
        if elems.IsEmpty then None
        else
            let smc = SubmodelElementCollection()
            smc.IdShort <- sanitizeIdShort idShort
            smc.Value <- ResizeArray<ISubmodelElement>(elems)
            Some (smc :> ISubmodelElement)

    // AASd-120: SubmodelElementList 직접 자식의 idShort는 null이어야 함
    let mkSml (idShort: string) (items: ISubmodelElement list) : ISubmodelElement option =
        if items.IsEmpty then None
        else
            let sml = SubmodelElementList(typeValueListElement = AasSubmodelElements.SubmodelElementCollection)
            sml.IdShort <- sanitizeIdShort idShort
            let clearedItems =
                items
                |> List.map (fun elem ->
                    let referable = elem :> IReferable
                    referable.IdShort <- null
                    elem
                )
            sml.Value <- ResizeArray<ISubmodelElement>(clearedItems)
            Some (sml :> ISubmodelElement)

    let mkSmlProp (idShort: string) (items: ISubmodelElement list) : ISubmodelElement option =
        if items.IsEmpty then None
        else
            let sml = SubmodelElementList(
                typeValueListElement = AasSubmodelElements.Property,
                valueTypeListElement = DataTypeDefXsd.String)  // AASd-109
            sml.IdShort <- sanitizeIdShort idShort
            let clearedItems =
                items
                |> List.map (fun elem ->
                    let referable = elem :> IReferable
                    referable.IdShort <- null
                    elem
                )
            sml.Value <- ResizeArray<ISubmodelElement>(clearedItems)
            Some (sml :> ISubmodelElement)

    let mkMlp (idShort: string) (value: string) : ISubmodelElement =
        let mlp = MultiLanguageProperty()
        mlp.IdShort <- sanitizeIdShort idShort
        let v = if String.IsNullOrWhiteSpace value then "N/A" else value
        mlp.Value <- ResizeArray<ILangStringTextType>([LangStringTextType("en", v) :> ILangStringTextType])
        mlp :> ISubmodelElement

    let mkSemanticRef (semanticId: string) : IReference =
        Reference(
            ReferenceTypes.ExternalReference,
            ResizeArray<IKey>([Key(KeyTypes.GlobalReference, semanticId) :> IKey])) :> IReference

    let withSemId (uri: string option) (elem: ISubmodelElement) : ISubmodelElement =
        match uri with
        | None -> elem
        | Some u ->
            (elem :> IHasSemantics).SemanticId <- mkSemanticRef u
            elem

    let mkSubmodel (id: string) (idShort: string) (semanticId: string) (elems: ISubmodelElement list) : Submodel =
        let sm = Submodel(id = id)
        sm.IdShort <- sanitizeIdShort idShort
        sm.SemanticId <- mkSemanticRef semanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elems)
        sm

    let private fbTagMapPortToSmc (port: FBTagMapPort) : ISubmodelElement =
        let baseElems = [
            mkProp    "FBPort"     port.FBPort
            mkProp    "Direction"  port.Direction
            mkProp    "DataType"   port.DataType
            mkProp    "TagPattern" port.TagPattern
            mkBoolProp "IsDummy"   port.IsDummy
        ]
        let resultElems =
            [ if not (String.IsNullOrEmpty port.VarName) then yield mkProp "VarName" port.VarName
              if not (String.IsNullOrEmpty port.Address)  then yield mkProp "Address"  port.Address ]
        mkSmc port.FBPort (baseElems @ resultElems)

    let private fbTagMapInstanceToSmc (inst: FBTagMapInstance) : ISubmodelElement =
        let baseElems = [
            mkProp "FBTypeName"  inst.FBTypeName
            mkProp "FlowName"    inst.FlowName
            mkProp "WorkName"    inst.WorkName
            mkProp "DeviceAlias" inst.DeviceAlias
            mkProp "ApiDefName"  inst.ApiDefName
        ]
        let portsElem =
            if inst.Ports.Count > 0 then
                let portList = inst.Ports |> Seq.map fbTagMapPortToSmc |> Seq.toList
                [ mkSml "Ports" portList |> Option.defaultValue (mkProp "Ports" "[]") ]
            else []
        let idShort =
            let s = $"{inst.FlowName}_{inst.DeviceAlias}_{inst.ApiDefName}"
            if String.IsNullOrWhiteSpace s || s = "__" then "FBTagMapInst" else s
        mkSmc idShort (baseElems @ portsElem)

    let internal controlIoConfigElems (cp: ControlSystemProperties) : ISubmodelElement list =
        [ if cp.FBTagMapPresets.Count > 0 then
              yield mkJsonProp "FBTagMapPresets" cp.FBTagMapPresets ]

    /// 리플렉션을 사용하여 Properties 객체를 AAS SubmodelElement 리스트로 자동 변환
    /// 지원 타입: string, bool, int, float, TimeSpan, Guid, DateTime, Array, ResizeArray, Enum, Option
    let private propsToElements<'T> (props: 'T) : ISubmodelElement list =
        let t = typeof<'T>
        let properties = t.GetProperties(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Instance)

        properties
        |> Array.choose (fun prop ->
            let name = prop.Name
            let value = prop.GetValue(props)
            let propType = prop.PropertyType

            if isNull value then None
            elif propType = typeof<string> then
                let s = value :?> string
                if String.IsNullOrEmpty s then None else Some (mkProp name s)
            elif propType = typeof<string option> then
                match value :?> string option with Some v -> Some (mkProp name v) | None -> None
            elif propType = typeof<bool> then Some (mkBoolProp name (value :?> bool))
            elif propType = typeof<int> then Some (mkIntProp name (value :?> int))
            elif propType = typeof<int option> then
                match value :?> int option with Some v -> Some (mkIntProp name v) | None -> None
            elif propType = typeof<int64> then Some (mkProp name (value.ToString()))
            elif propType = typeof<int64 option> then
                match value :?> int64 option with Some v -> Some (mkProp name (v.ToString())) | None -> None
            elif propType = typeof<float> then Some (mkDoubleProp name (value :?> float))
            elif propType = typeof<float option> then
                match value :?> float option with Some v -> Some (mkDoubleProp name v) | None -> None
            elif propType = typeof<TimeSpan> then Some (mkTimeSpanProp name (value :?> TimeSpan))
            elif propType = typeof<TimeSpan option> then
                match value :?> TimeSpan option with Some v -> Some (mkTimeSpanProp name v) | None -> None
            elif propType = typeof<Guid> then Some (mkGuidProp name (value :?> Guid))
            elif propType = typeof<DateTime> then Some (mkProp name ((value :?> DateTime).ToString("O")))
            elif propType = typeof<DateTime option> then
                match value :?> DateTime option with Some v -> Some (mkProp name (v.ToString("O"))) | None -> None
            elif propType = typeof<DateTimeOffset> then Some (mkProp name ((value :?> DateTimeOffset).ToString("O")))
            elif propType = typeof<DateTimeOffset option> then
                match value :?> DateTimeOffset option with Some v -> Some (mkProp name (v.ToString("O"))) | None -> None
            elif propType.IsArray then
                let arr = value :?> System.Array
                if arr.Length = 0 then None
                else
                    let elemType = propType.GetElementType()
                    if elemType = typeof<Guid> then
                        mkSmlProp name (arr |> Seq.cast<Guid> |> Seq.map (fun id -> mkGuidProp "Id" id) |> Seq.toList)
                    elif elemType = typeof<string> then
                        mkSmlProp name (arr |> Seq.cast<string> |> Seq.map (fun s -> mkProp "Tag" s) |> Seq.toList)
                    else
                        Some (mkJsonProp name value)
            elif propType.IsEnum then Some (mkProp name (value.ToString()))
            else None)
        |> Array.toList

    // F# 값 제한(value restriction) 우회용 typed wrappers (PropertyConversion.fs에서 사용)
    let internal simulationSystemPropsToElements props = propsToElements<SimulationSystemProperties> props
    let internal simulationFlowPropsToElements props = propsToElements<SimulationFlowProperties> props
    let internal simulationWorkPropsToElements props = propsToElements<SimulationWorkProperties> props
    let internal simulationCallPropsToElements props = propsToElements<SimulationCallProperties> props

    let internal controlSystemPropsToElements props = propsToElements<ControlSystemProperties> props
    let internal controlFlowPropsToElements props = propsToElements<ControlFlowProperties> props
    let internal controlWorkPropsToElements props = propsToElements<ControlWorkProperties> props
    let internal controlCallPropsToElements props = propsToElements<ControlCallProperties> props

    let internal monitoringSystemPropsToElements props = propsToElements<MonitoringSystemProperties> props
    let internal monitoringFlowPropsToElements props = propsToElements<MonitoringFlowProperties> props
    let internal monitoringWorkPropsToElements props = propsToElements<MonitoringWorkProperties> props
    let internal monitoringCallPropsToElements props = propsToElements<MonitoringCallProperties> props

    let internal loggingSystemPropsToElements props = propsToElements<LoggingSystemProperties> props
    let internal loggingFlowPropsToElements props = propsToElements<LoggingFlowProperties> props
    let internal loggingWorkPropsToElements props = propsToElements<LoggingWorkProperties> props
    let internal loggingCallPropsToElements props = propsToElements<LoggingCallProperties> props

    let internal maintenanceSystemPropsToElements props = propsToElements<MaintenanceSystemProperties> props
    let internal maintenanceFlowPropsToElements props = propsToElements<MaintenanceFlowProperties> props
    let internal maintenanceWorkPropsToElements props = propsToElements<MaintenanceWorkProperties> props
    let internal maintenanceCallPropsToElements props = propsToElements<MaintenanceCallProperties> props

    let internal costAnalysisSystemPropsToElements props = propsToElements<CostAnalysisSystemProperties> props
    let internal costAnalysisFlowPropsToElements props = propsToElements<CostAnalysisFlowProperties> props
    let internal costAnalysisWorkPropsToElements props = propsToElements<CostAnalysisWorkProperties> props
    let internal costAnalysisCallPropsToElements props = propsToElements<CostAnalysisCallProperties> props

    let internal qualitySystemPropsToElements props = propsToElements<QualitySystemProperties> props
    let internal qualityFlowPropsToElements props = propsToElements<QualityFlowProperties> props
    let internal qualityWorkPropsToElements props = propsToElements<QualityWorkProperties> props
    let internal qualityCallPropsToElements props = propsToElements<QualityCallProperties> props

    let internal hmiSystemPropsToElements props = propsToElements<HMISystemProperties> props
    let internal hmiFlowPropsToElements props = propsToElements<HMIFlowProperties> props
    let internal hmiWorkPropsToElements props = propsToElements<HMIWorkProperties> props
    let internal hmiCallPropsToElements props = propsToElements<HMICallProperties> props

