namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module internal AasxExportCore =

    // ────────────────────────────────────────────────────────────────────────────
    // idShort 검증 (AAS 규칙: [a-zA-Z][a-zA-Z0-9_]*)
    // ────────────────────────────────────────────────────────────────────────────

    /// idShort가 AAS 규칙을 준수하는지 검증
    /// - 규칙: 첫 글자는 영문자 [a-zA-Z], 나머지는 영문자/숫자/underscore [a-zA-Z0-9_]*
    /// - 위반 시 ArgumentException 발생
    let private validateIdShort (idShort: string) : unit =
        if String.IsNullOrWhiteSpace idShort then
            invalidArg "idShort" "idShort cannot be null or whitespace"

        let chars = idShort.ToCharArray()

        // 첫 글자 검증: 반드시 영문자
        if not (System.Char.IsLetter(chars.[0])) then
            invalidArg "idShort" (sprintf "idShort must start with a letter (a-zA-Z): '%s' starts with '%c'" idShort chars.[0])

        // 나머지 글자 검증: 영문자/숫자/underscore만 허용
        for i in 1 .. chars.Length - 1 do
            let c = chars.[i]
            if not (System.Char.IsLetterOrDigit(c) || c = '_') then
                invalidArg "idShort" (sprintf "idShort can only contain letters, digits, and underscores: '%s' contains invalid character '%c' at position %d" idShort c i)

    /// idShort를 AAS 규칙에 따라 검증하고 반환
    /// - 규칙 위반 시 예외 발생
    /// - 정확한 규격만 허용
    let sanitizeIdShort (idShort: string) : string =
        validateIdShort idShort
        idShort

    // ────────────────────────────────────────────────────────────────────────────
    // AAS SubmodelElement 생성 헬퍼 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// 타입이 지정된 AAS Property 생성 (내부용)
    let private mkTypedProp (idShort: string) (dataType: DataTypeDefXsd) (value: string) : ISubmodelElement =
        let p = Property(valueType = dataType)
        p.IdShort <- sanitizeIdShort idShort
        p.Value <- value
        p :> ISubmodelElement

    /// String Property 생성
    let mkProp idShort value = mkTypedProp idShort DataTypeDefXsd.String (if isNull value then "" else value)


    /// Boolean Property 생성
    let mkBoolProp idShort (value: bool) = mkTypedProp idShort DataTypeDefXsd.Boolean (if value then "true" else "false")

    /// Integer Property 생성
    let mkIntProp idShort (value: int) = mkTypedProp idShort DataTypeDefXsd.Int (value.ToString())

    /// Double Property 생성
    let mkDoubleProp idShort (value: float) = mkTypedProp idShort DataTypeDefXsd.Double (value.ToString("G", System.Globalization.CultureInfo.InvariantCulture))

    /// TimeSpan Property 생성 (XSD Duration 형식)
    let mkTimeSpanProp idShort (value: TimeSpan) = mkTypedProp idShort DataTypeDefXsd.Duration (System.Xml.XmlConvert.ToString(value))

    /// GUID Property 생성
    let mkGuidProp idShort (value: Guid) = mkTypedProp idShort DataTypeDefXsd.String (value.ToString())

    /// JSON 직렬화된 Property 생성
    let mkJsonProp<'T> idShort (obj: 'T) = mkProp idShort (Ds2.Serialization.JsonConverter.serialize obj)

    /// SubmodelElementCollection 생성
    /// AAS 규칙: SMC의 Value는 비어있으면 안됨 (최소 1개 이상의 요소 필요)
    /// 빈 리스트가 전달되면 기본 속성(idShort를 Name으로 사용)을 추가
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

    /// SubmodelElementCollection 생성 (optional) - 빈 리스트면 None 반환
    let mkSmcOpt (idShort: string) (elems: ISubmodelElement list) : ISubmodelElement option =
        if elems.IsEmpty then None
        else
            let smc = SubmodelElementCollection()
            smc.IdShort <- sanitizeIdShort idShort
            smc.Value <- ResizeArray<ISubmodelElement>(elems)
            Some (smc :> ISubmodelElement)

    /// SubmodelElementList 생성 (Collection 타입) - 빈 리스트는 생성하지 않음
    /// AASd-120: SubmodelElementList의 직접 자식은 idShort를 가지면 안됨
    let mkSml (idShort: string) (items: ISubmodelElement list) : ISubmodelElement option =
        if items.IsEmpty then None
        else
            let sml = SubmodelElementList(typeValueListElement = AasSubmodelElements.SubmodelElementCollection)
            sml.IdShort <- sanitizeIdShort idShort
            // AASd-120: SubmodelElementList 직접 자식의 idShort는 null이어야 함
            // ISubmodelElement는 IReferable을 상속하므로 직접 캐스팅
            let clearedItems =
                items
                |> List.map (fun elem ->
                    let referable = elem :> IReferable
                    referable.IdShort <- null
                    elem
                )
            sml.Value <- ResizeArray<ISubmodelElement>(clearedItems)
            Some (sml :> ISubmodelElement)

    /// SubmodelElementList 생성 (Property 타입) - 빈 리스트는 생성하지 않음
    /// AASd-120: SubmodelElementList의 직접 자식은 idShort를 가지면 안됨
    let mkSmlProp (idShort: string) (items: ISubmodelElement list) : ISubmodelElement option =
        if items.IsEmpty then None
        else
            let sml = SubmodelElementList(
                typeValueListElement = AasSubmodelElements.Property,
                valueTypeListElement = DataTypeDefXsd.String)  // AASd-109: Property의 valueType 지정
            sml.IdShort <- sanitizeIdShort idShort
            // AASd-120: SubmodelElementList 직접 자식의 idShort는 null이어야 함
            // ISubmodelElement는 IReferable을 상속하므로 직접 캐스팅
            let clearedItems =
                items
                |> List.map (fun elem ->
                    let referable = elem :> IReferable
                    referable.IdShort <- null
                    elem
                )
            sml.Value <- ResizeArray<ISubmodelElement>(clearedItems)
            Some (sml :> ISubmodelElement)

    /// MultiLanguageProperty 생성 (단일 언어 en 지원)
    /// 빈 값은 "N/A"로 대체 (AAS 규칙: value must not be empty)
    let mkMlp (idShort: string) (value: string) : ISubmodelElement =
        let mlp = MultiLanguageProperty()
        mlp.IdShort <- sanitizeIdShort idShort
        let v = if String.IsNullOrWhiteSpace value then "N/A" else value
        mlp.Value <- ResizeArray<ILangStringTextType>([LangStringTextType("en", v) :> ILangStringTextType])
        mlp :> ISubmodelElement

    /// Semantic Reference 생성
    let mkSemanticRef (semanticId: string) : IReference =
        Reference(
            ReferenceTypes.ExternalReference,
            ResizeArray<IKey>([Key(KeyTypes.GlobalReference, semanticId) :> IKey])) :> IReference

    /// Submodel 생성 헬퍼
    let mkSubmodel (id: string) (idShort: string) (semanticId: string) (elems: ISubmodelElement list) : Submodel =
        let sm = Submodel(id = id)
        sm.IdShort <- sanitizeIdShort idShort
        sm.SemanticId <- mkSemanticRef semanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elems)
        sm

    // ────────────────────────────────────────────────────────────────────────────
    // 리플렉션 기반 자동 속성 변환
    // ────────────────────────────────────────────────────────────────────────────

    /// 리플렉션을 사용하여 Properties 객체를 AAS SubmodelElement 리스트로 자동 변환
    /// 지원 타입: string, bool, int, float, TimeSpan, Guid, DateTime, Array, Enum, Option
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

    // ────────────────────────────────────────────────────────────────────────────
    // 도메인별 Properties → SubmodelElements 변환 함수 (통합)
    // ────────────────────────────────────────────────────────────────────────────
    //
    // 이전에는 32개 함수가 중복 선언되어 있었으나 (8 도메인 × 4 레벨),
    // 이제는 Common/PropertyConversion.fs에서 통합 관리됩니다.
    //
    // 하위 호환성을 위해 핵심 변환 함수는 internal로 유지:

    // 개별 도메인 함수들 (PropertyConversion.fs에서 참조하기 위해 internal로 노출)
    // F# 값 제한(value restriction)을 피하기 위해 명시적 매개변수 추가
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

