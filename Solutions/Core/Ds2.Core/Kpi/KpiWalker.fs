namespace Ds2.Core.Kpi

open System
open Ds2.Core
open Ds2.Core.StandardSubmodels
open Ds2.Core.Store

/// KPI 생성 대상 — 순회 결과 1건.
/// (entity, resolved metric, resolved signalId, resolved AAS-idShort)
type KpiTarget = {
    Kind: KpiEntityKind
    /// 엔티티 fqdn (System.Name · "{Flow}.{Work}" · "{Devices}.{Api}" · Arrow FQDN)
    EntityFqdn: string
    Metric: KpiMetric
    SignalId: SignalId
    IdShort: string
}

/// Project × DsStore 를 walk 해서 규약된 KPI 대상들을 열거.
/// 결과는 3-tuple (SubmodelId, SemanticId, IdShort) 로 idempotent guard 되므로,
/// 이 walker 는 안전하게 여러 번 호출 가능.
[<RequireQualifiedAccess>]
module KpiWalker =

    /// signalId 접두사. Project.Name 을 kebab lowercase 화 (없으면 "project").
    let private signalPrefix (project: Project) : string =
        let name =
            if String.IsNullOrEmpty project.Name then "project"
            else project.Name.ToLowerInvariant().Replace(' ', '-')
        sprintf "kpi.%s" name

    let private mkTargets (kind: KpiEntityKind) (fqdn: string) (metrics: KpiMetric list) (prefix: string) : KpiTarget list =
        metrics
        |> List.map (fun m ->
            let sid = KpiIdentifiers.signalId prefix kind fqdn m.IdShortSuffix
            let idS = KpiIdentifiers.idShort   kind fqdn m.IdShortSuffix
            { Kind = kind; EntityFqdn = fqdn; Metric = m; SignalId = sid; IdShort = idS })

    /// Project 하나의 모든 KPI 대상을 walk.
    /// 순서: Active Systems only → ArrowWorks → UserTags.
    /// Work와 Call KPI는 생성하지 않음.
    let walk (store: DsStore) (project: Project) : KpiTarget list =
        let prefix = signalPrefix project
        let acc = ResizeArray<KpiTarget>()

        // 1. System KPIs - Active Systems only
        let activeSystems =
            project.ActiveSystemIds
            |> Seq.choose (fun id -> Queries.getSystem id store)
            |> List.ofSeq
        for sys in activeSystems do
            let fqdn = sys.Name
            acc.AddRange(mkTargets SystemKind fqdn KpiKits.systemKit.Metrics prefix)

        // 2. Work KPIs - SKIP (생성하지 않음)
        // 3. Call KPIs - SKIP (생성하지 않음)

        // 4. ArrowWork KPIs (Active Systems only)
        for sys in activeSystems do
            for arr in Queries.arrowWorksOf sys.Id store do
                let srcName =
                    Queries.getWork arr.SourceId store
                    |> Option.map (fun w -> w.Name)
                    |> Option.defaultValue (arr.SourceId.ToString("N"))
                let tgtName =
                    Queries.getWork arr.TargetId store
                    |> Option.map (fun w -> w.Name)
                    |> Option.defaultValue (arr.TargetId.ToString("N"))
                let fqdn = sprintf "%s.%s->%s" sys.Name srcName tgtName
                acc.AddRange(mkTargets ArrowWorkKind fqdn KpiKits.arrowWorkKit.Metrics prefix)

        // 5. UserTag KPIs — Call 의 ApiCall.InTag / OutTag 를 pass-through (Active Systems only)
        //    IOTag 마다 KpiMetric 을 동적 생성 (DataType=String — 태그 실제 타입은 런타임 결정)
        for sys in activeSystems do
            for flow in Queries.flowsOf sys.Id store do
                for work in Queries.worksOf flow.Id store do
                    for call in Queries.callsOf work.Id store do
                        for ac in call.ApiCalls do
                            let addTag (label: string) (opt: IOTag option) =
                                match opt with
                                | Some tag when not (System.String.IsNullOrEmpty tag.Name) ->
                                    let fqdn = sprintf "%s.%s.%s.%s.%s" sys.Name flow.Name work.Name call.Name label
                                    let metric =
                                        { IdShortSuffix = tag.Name
                                          SemanticId    = KpiKits.semanticId "UserTag" tag.Name
                                          DataType      = XsString
                                          Unit          = ""
                                          UpdateHint    = OnChange
                                          DescriptionKr = sprintf "UserTAG passthrough (%s)" tag.Name
                                          DescriptionEn = sprintf "UserTAG passthrough (%s)" tag.Name }
                                    acc.AddRange(mkTargets UserTagKind fqdn [ metric ] prefix)
                                | _ -> ()
                            addTag "InTag"  ac.InTag
                            addTag "OutTag" ac.OutTag

        acc |> List.ofSeq
