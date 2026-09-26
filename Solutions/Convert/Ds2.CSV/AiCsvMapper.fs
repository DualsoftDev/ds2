namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

/// ds2-csv-for-ai/v1 → ImportPlan 매퍼.
///
/// BasicCsvMapper 와 갈리는 지점은 하나다 — **행 순서를 쓰지 않는다**.
/// 3열 매퍼는 데이터 행 순서로 Work 를 StartReset 체인으로 엮지만, 여기서는 ARROW 행에
/// 적힌 것만 만든다. 적지 않은 관계는 생기지 않는다.
///
/// 새 ImportPlanOperation 을 만들지 않는다. Passive 디바이스·ApiDef·ApiCall 은 기존
/// 디바이스 캐스케이드(linkCallsToDevicesMultiFlowWithDurations)가 그대로 만들고,
/// TokenSpec 은 CsvImporter 의 ensureTokenSpecsForSources 가 붙인다.
///
/// store 는 변경하지 않는다(plan 생성 계약 — 기존 두 매퍼와 동일).
module internal AiCsvMapper =

    /// 미지정 동작 시간. 기존 3열(ImportPlanDeviceOps.defaultWorkDuration)과 같은 값으로 맞춘다 —
    /// 한 제품 안에서 형식마다 타이밍이 다르면 «3열로 부른 모델과 7열로 부른 모델이 다르게 돈다» 가 된다.
    let private defaultDuration = TimeSpan.FromMilliseconds 500.0

    let mapToSystemPlan
        (store: DsStore)
        (projectId: Guid)
        (systemId: Guid)
        (document: AiCsvDocument)
        : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()

        // ---------- Flow ----------
        let flows = Dictionary<string, Flow>()
        for f in document.Flows do
            if not (flows.ContainsKey f.Name) then
                let created = Flow(f.Name, systemId)
                operations.Add(AddFlow created)
                flows.[f.Name] <- created

        // ---------- Work + Call ----------
        let works = Dictionary<string, Work>()              // "Flow.Work" → Work
        let callsByPath = Dictionary<string, Call>()        // "Flow.Work.Dev.Api" → Call
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option>>()

        for w in document.Works do
            match flows.TryGetValue w.FlowName with
            | false, _ -> ()                                 // 파서가 AI063 으로 이미 막는다
            | true, flow ->
                let work = Work(flow.Name, w.WorkName, flow.Id)
                work.TokenRole <- w.Roles
                // Call 이 없는 Work 만 Duration 을 가질 수 있다(파서가 AI021 로 강제).
                match w.Duration with
                | Some d -> work.Duration <- Some d
                | None -> ()
                operations.Add(AddWork work)
                works.[$"{w.FlowName}.{w.WorkName}"] <- work

                let callByKey = Dictionary<string, Call>()
                for (key, deviceAlias, apiName) in w.Nodes do
                    let call = Call(deviceAlias, apiName, work.Id)
                    operations.Add(AddCall call)
                    callByKey.[key] <- call
                    callsByPath.[$"{w.FlowName}.{w.WorkName}.{key}"] <- call
                    let bucket =
                        match callsByFlow.TryGetValue w.FlowName with
                        | true, existing -> existing
                        | false, _ ->
                            let created = ResizeArray()
                            callsByFlow.[w.FlowName] <- created
                            created
                    // systemNameHint = Some deviceAlias — 여러 Flow 가 같은 디바이스를 쓰면 하나의 Passive 로 병합.
                    bucket.Add(call, key, Some deviceAlias)

                // `>` 엣지 → Call 레벨 Start. 3열 매퍼의 «그룹 대표만 잇기» 최적화는 쓰지 않는다 —
                // 여기서는 사용자가 Group 을 ARROW 행으로 직접 적으므로 추론하지 않는다.
                for (srcKey, dstKey) in w.Edges do
                    match callByKey.TryGetValue srcKey, callByKey.TryGetValue dstKey with
                    | (true, src), (true, dst) ->
                        operations.Add(AddArrowCall(ArrowBetweenCalls(work.Id, src.Id, dst.Id, ArrowType.Start)))
                    | _ -> ()

        // ---------- ARROW ----------
        for a in document.Arrows do
            if a.IsCallLevel then
                // 4마디 끝점. 두 끝점이 같은 Work 소속이어야 ArrowBetweenCalls 의 parent 가 성립한다.
                let ownerOf (path: string) =
                    let parts = path.Split('.')
                    if parts.Length = 4 then Some $"{parts.[0]}.{parts.[1]}" else None
                match ownerOf a.Source, callsByPath.TryGetValue a.Source with
                | Some ownerKey, (true, srcCall) ->
                    match works.TryGetValue ownerKey with
                    | true, ownerWork ->
                        for t in a.Targets do
                            match ownerOf t, callsByPath.TryGetValue t with
                            | Some tOwner, (true, dstCall) when tOwner = ownerKey ->
                                operations.Add(AddArrowCall(ArrowBetweenCalls(ownerWork.Id, srcCall.Id, dstCall.Id, a.ArrowType)))
                            | _ -> ()
                    | _ -> ()
                | _ -> ()
            else
                match works.TryGetValue a.Source with
                | true, src ->
                    for t in a.Targets do
                        match works.TryGetValue t with
                        | true, dst ->
                            operations.Add(AddArrowWork(ArrowBetweenWorks(systemId, src.Id, dst.Id, a.ArrowType)))
                        | _ -> ()
                | _ -> ()

        // ---------- 디바이스 캐스케이드 ----------
        // API 행이 정한 Action/Sensing/Duration 을 캐스케이드에 넘긴다.
        let apiByKey = Dictionary<string, AiApiRow>()
        for a in document.Apis do apiByKey.[$"{a.Device}.{a.Api}"] <- a

        let durationOf (deviceAlias: string) (apiName: string) =
            match apiByKey.TryGetValue $"{deviceAlias}.{apiName}" with
            | true, row -> Some (row.Duration |> Option.defaultValue defaultDuration)
            | false, _ -> Some defaultDuration

        let callsByFlowList =
            callsByFlow
            |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value)
            |> List.ofSeq

        ImportPlanDeviceOps.linkCallsToDevicesMultiFlowWithDurations
            durationOf store projectId callsByFlowList operations

        { Operations = List.ofSeq operations }

    /// 문서에서 API 행을 이름으로 찾는다.
    let private findApiRow (document: AiCsvDocument) (key: string) =
        document.Apis |> List.tryFind (fun a -> $"{a.Device}.{a.Api}" = key)

    /// plan 을 적용한 **뒤** store 에 남는 것들 — ApiDef 특성·IO 태그·조건·초기 Finish.
    ///
    /// ImportPlanOperation 에는 «기존 엔티티의 속성을 바꾼다» 가 없다(순수 add-only). ApiDef 는
    /// 캐스케이드가 만들므로 그 id 를 미리 알 수 없고, 조건은 ApiCall 이 생긴 뒤라야 붙일 수 있다.
    /// 그래서 이 둘은 applyDirect 직후 store 를 직접 손보는 후처리로 둔다.
    let applyPostImport (store: DsStore) (document: AiCsvDocument) : string list =
        let warnings = ResizeArray<string>()

        // ---------- ApiDef 특성 + IO 태그 ----------
        let apiDefByName = Dictionary<string, ApiDef>()
        for sys in store.Systems.Values do
            for apiDef in Queries.apiDefsOf sys.Id store do
                apiDefByName.[$"{sys.Name}.{apiDef.Name}"] <- apiDef

        for row in document.Apis do
            match apiDefByName.TryGetValue $"{row.Device}.{row.Api}" with
            | false, _ ->
                warnings.Add $"AI-W4: API '{row.Device}.{row.Api}' 에 대응하는 ApiDef 를 찾지 못했습니다."
            | true, apiDef ->
                apiDef.ActionType <- row.Action
                apiDef.SensingType <- row.Sensing

        // ---------- IO 태그 ----------
        // ApiCall.InTag/OutTag 는 IOTag option 이다. spec 이 없으면 **건드리지 않는다** —
        // 캐스케이드가 만들어 둔 것을 None 으로 덮으면 배선이 사라진다.
        let parseDataType (s: string) =
            match s.ToUpperInvariant() with
            | "BOOL" -> Some IOTagDataType.BOOL
            | "SINT" -> Some IOTagDataType.SINT
            | "INT" -> Some IOTagDataType.INT
            | "DINT" -> Some IOTagDataType.DINT
            | "LINT" -> Some IOTagDataType.LINT
            | "USINT" -> Some IOTagDataType.USINT
            | "UINT" -> Some IOTagDataType.UINT
            | "UDINT" -> Some IOTagDataType.UDINT
            | "ULINT" -> Some IOTagDataType.ULINT
            | "REAL" -> Some IOTagDataType.REAL
            | "LREAL" -> Some IOTagDataType.LREAL
            | "STRING" -> Some IOTagDataType.STRING
            | _ -> None

        let buildTag (spec: AiTagSpec) (fallbackName: string) =
            let tag = IOTag()
            tag.Name <- (spec.Symbol |> Option.defaultValue fallbackName)
            tag.Address <- spec.Address
            match spec.DataType with
            | Some dt ->
                match parseDataType dt with
                | Some parsed -> tag.DataType <- parsed
                | None -> warnings.Add $"AI-W5: 알 수 없는 DataType '{dt}' — BOOL 로 둡니다."
            | None -> ()
            tag

        for call in store.Calls.Values do
            for apiCall in call.ApiCalls do
                match apiCall.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store) with
                | None -> ()
                | Some apiDef ->
                    let deviceName =
                        Queries.getSystem apiDef.ParentId store
                        |> Option.map (fun s -> s.Name) |> Option.defaultValue ""
                    match findApiRow document $"{deviceName}.{apiDef.Name}" with
                    | None -> ()
                    | Some row ->
                        match row.InTag with
                        | Some s -> apiCall.InTag <- Some (buildTag s $"{row.Device}_{row.Api}_IN")
                        | None -> ()
                        match row.OutTag with
                        | Some s -> apiCall.OutTag <- Some (buildTag s $"{row.Device}_{row.Api}_OUT")
                        | None -> ()

        // ---------- 초기 Finish ----------
        // TokenRole 이 아니라 «초기 상태가 Finish» 표시. R1 상호 재무장의 복귀측에 쓴다.
        let workByPath = Dictionary<string, Work>()
        for w in store.Works.Values do
            match Queries.getFlow w.ParentId store with
            | Some flow -> workByPath.[$"{flow.Name}.{w.LocalName}"] <- w
            | None -> ()
        for row in document.Works do
            if row.InitFinish then
                match workByPath.TryGetValue $"{row.FlowName}.{row.WorkName}" with
                | true, work -> work.Status4 <- Status4.Finish
                | false, _ -> ()

        // ---------- 조건 ----------
        // Ds2.CSV 는 Ds2.Core 만 참조하므로 Panel 확장 API 를 못 쓴다. 엔티티를 직접 만든다.
        // leaf 는 참조 대상 ApiCall 의 DeepCopy — Panel.Condition.fs 의 build 와 같은 규약이다.
        let apiCallByKey = Dictionary<string, ApiCall>()
        for call in store.Calls.Values do
            for apiCall in call.ApiCalls do
                match apiCall.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store) with
                | None -> ()
                | Some apiDef ->
                    let deviceName =
                        Queries.getSystem apiDef.ParentId store
                        |> Option.map (fun s -> s.Name) |> Option.defaultValue ""
                    let key = $"{deviceName}.{apiDef.Name}"
                    if not (apiCallByKey.ContainsKey key) then apiCallByKey.[key] <- apiCall

        let rec buildCondition (expr: AiCondExpr) (cond: Condition) =
            match expr with
            | AiLeaf(dev, api, contact, spec) ->
                match apiCallByKey.TryGetValue $"{dev}.{api}" with
                | false, _ ->
                    warnings.Add $"AI-W6: 조건 leaf '{dev}.{api}' 에 해당하는 ApiCall 이 없습니다."
                | true, src ->
                    let copy = src.DeepCopy()
                    copy.Id <- src.Id
                    copy.ContactKind <- contact
                    // 기대값 — 적지 않으면 «그 신호가 켜졌는가» 를 뜻하는 true.
                    copy.InputSpec <-
                        match spec with
                        | Some s ->
                            match ValueSpecText.tryParseAs (ValueSpec.BoolValue(Single true)) s with
                            | Some v -> v
                            | None ->
                                warnings.Add $"AI-W7: 기대값 '{s}' 를 읽을 수 없어 true 로 둡니다."
                                ValueSpec.BoolValue(Single true)
                        | None -> ValueSpec.BoolValue(Single true)
                    cond.ApiCalls.Add copy
            | AiRawLeaf(symbol, contact) ->
                let dummy = ApiCall(symbol)
                dummy.ContactKind <- contact
                cond.ApiCalls.Add dummy
            | AiGroup(isOr, isInverted, children) ->
                // 평탄한 leaf 묶음이면 현재 그룹에 그대로 싣는다(불필요한 중첩을 만들지 않는다).
                let allLeaf = children |> List.forall (function AiGroup _ -> false | _ -> true)
                if allLeaf then
                    cond.IsOR <- isOr
                    cond.IsInverted <- isInverted
                    for c in children do buildCondition c cond
                else
                    cond.IsOR <- isOr
                    cond.IsInverted <- isInverted
                    for c in children do
                        match c with
                        | AiGroup _ ->
                            let child = Condition()
                            buildCondition c child
                            cond.Children.Add child
                        | _ -> buildCondition c cond

        for row in document.Conds do
            let parts = row.OwnerPath.Split('.')
            let cond = Condition(Type = Some row.CondType)
            buildCondition row.Expr cond
            if row.IsCallLevel && parts.Length = 4 then
                let workKey = $"{parts.[0]}.{parts.[1]}"
                let callKey = $"{parts.[2]}.{parts.[3]}"
                match workByPath.TryGetValue workKey with
                | true, work ->
                    let target =
                        Queries.callsOf work.Id store
                        |> List.tryFind (fun c -> $"{c.DevicesAlias}.{c.ApiName}" = callKey)
                    match target with
                    | Some call -> call.Conditions.Add cond
                    | None -> warnings.Add $"AI-W8: 조건 소유 Call '{row.OwnerPath}' 을 찾지 못했습니다."
                | false, _ -> warnings.Add $"AI-W8: 조건 소유 Work '{workKey}' 을 찾지 못했습니다."
            else
                match workByPath.TryGetValue row.OwnerPath with
                | true, work -> work.Conditions.Add cond
                | false, _ -> warnings.Add $"AI-W8: 조건 소유 Work '{row.OwnerPath}' 을 찾지 못했습니다."

        List.ofSeq warnings
