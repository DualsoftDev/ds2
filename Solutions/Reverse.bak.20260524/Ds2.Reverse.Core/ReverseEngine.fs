/// 인과 검출 → DAG 정제 → DsStore 생성 파이프라인 (v16 알고리즘의 F# 포팅).
namespace Ds2.Reverse.Core

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Reverse.Core

module ReverseEngine =

    /// 입력: capture events + arrow candidates + flow-call 매핑.
    /// 출력: DsStore + DetectionReport.
    type Input = {
        ProjectName: string
        ActiveSystemName: string
        /// flow_name → [(call_full_name, address)]
        FlowCalls: Map<string, (string * string) list>
        /// arrow 후보 — declared kind 포함 (LogicRungs 있으면 자동 보강).
        Candidates: ArrowCandidate list
        /// capture rising-edge events
        Events: CapturedEvent list
        Config: CausationConfig
        /// PLC 래더 로직 rungs — 재귀 expansion + AND/OR 강도 분석 활성화.
        /// None 이면 Candidates 만 사용.
        LogicRungs: LogicRung list option
        /// LogicRungs 의 재귀 expansion 최대 깊이.
        LogicMaxDepth: int
        /// LogicRungs 에서 추출한 candidate 의 strength 임계값 (이 미만은 제외).
        /// 0.0 ~ 1.0 — 0.3 정도가 합리적.
        LogicStrengthThreshold: float
        /// Cross-flow arrows — flow 간 작업 연결 (arrowWorks emit).
        /// Src/Tgt 형식: "{flowName}.{workName}" — ex: "F1.W2".
        CrossFlowCandidates: ArrowCandidate list
        /// Call → (flow, work) 명시적 매핑 hint. 있으면 union-find 자동 분할 대신 사용.
        /// arrows.json 의 parent_work 같은 ground truth 활용 시 정확도 향상.
        WorkAssignments: Map<string, string * string>
        /// B5.2 Dynamic threshold — true 면 events 로부터 noise level 추정 후 cfg 자동 조정.
        AutoTuneThreshold: bool
    }

    /// 기본 LogicRungs 비활성화 input 빌더.
    let mkInput projectName activeSystemName flowCalls candidates events cfg =
        { ProjectName = projectName
          ActiveSystemName = activeSystemName
          FlowCalls = flowCalls
          Candidates = candidates
          Events = events
          Config = cfg
          LogicRungs = None
          LogicMaxDepth = 5
          LogicStrengthThreshold = 0.3
          CrossFlowCandidates = []
          WorkAssignments = Map.empty
          AutoTuneThreshold = false }

    /// arrow candidate src/tgt 가 다른 work 의 call 일 수 있음 — suffix match 로 페어 enumerate.
    /// Call name 은 Promaker 호환 위해 "DevicesAlias.ApiName" 형식 + api 부분의 '.' 는 '_' 로
    /// sanitize 됨. Candidate 의 target ("D1.RET") 도 같은 sanitize 적용 후 suffix 매칭.
    let private suffixMatch (nameToId: Map<string, Guid>) (target: string) : Guid list =
        // target 안 '.' 가 sanitize 됐는지 비교 위해 두 형식 모두 시도.
        let sanitized = target.Replace(".", "_")
        let needles = [
            target                           // 원본 그대로
            sanitized                        // 전체 sanitize
            "." + target                     // suffix as-is
            "." + sanitized                  // sanitized suffix
        ]
        nameToId
        |> Map.toSeq
        |> Seq.filter (fun (nm, _) ->
            needles |> List.exists (fun n ->
                if n.StartsWith "." then nm.EndsWith n
                else nm = n))
        |> Seq.map snd
        |> Seq.distinct
        |> List.ofSeq

    /// 메인 파이프라인.
    let run (input: Input) : DsStore * DetectionReport =
        let store, _projId, sysId = ModelBuilder.emptyStore input.ProjectName input.ActiveSystemName

        // B4.1 + B5.2 — autoTune 활성 시 events 로부터 noise level 추정 후 cfg 자동 조정.
        let cycleMsForAnalysis =
            match input.Config.CycleHintMs with
            | Some c -> c
            | None -> input.Config.WindowMs
        let noise = CausationDetection.estimateNoiseLevel input.Events cycleMsForAnalysis
        let effectiveConfig =
            if input.AutoTuneThreshold then
                CausationConfig.withNoiseLevel noise input.Config
            else input.Config
        let input = { input with Config = effectiveConfig }

        // 1) Flows + Works + Calls
        //    flow 안 calls 를 in-active-work candidate pairs 로 union-find →
        //    connected component 마다 별도 work. 단일 component → 1-work (기존 동작).
        let callNameToId = Dictionary<string, Guid>()
        let callIdToWorkId = Dictionary<Guid, Guid>()
        let workCalls = Dictionary<Guid, ResizeArray<Guid>>()

        // Union-Find on flow-local call names.
        let ufParent = Dictionary<string, string>()
        let rec ufFind x =
            if not (ufParent.ContainsKey x) then
                ufParent.[x] <- x; x
            elif ufParent.[x] = x then x
            else
                let r = ufFind ufParent.[x]
                ufParent.[x] <- r
                r
        let ufUnion a b =
            let ra = ufFind a
            let rb = ufFind b
            if ra <> rb then ufParent.[ra] <- rb

        // 후보 arrow 의 src/tgt 짧은 이름 (suffix 매칭) → 풀네임 매칭 후 union.
        // candidate src/tgt 의 . 가 _ 로 sanitize 될 수 있어 양쪽 시도.
        let matchCallInFlow (_flowName: string) (callsInFlow: string list) (target: string) : string option =
            let sanitized = target.Replace(".", "_")
            let needles = [target; sanitized; "." + target; "." + sanitized]
            callsInFlow
            |> List.tryFind (fun nm ->
                needles |> List.exists (fun n ->
                    if n.StartsWith "." then nm.EndsWith n
                    else nm = n))

        for KeyValue(flowName, callList) in input.FlowCalls |> Map.toSeq |> dict do
            let flowId = ModelBuilder.addFlow store sysId flowName
            let callsInFlow = callList |> List.map fst
            let addrMap = callList |> Map.ofList

            // WorkAssignments hint 가 있는 calls 와 없는 calls 분리.
            // hint 있는 것: workName 으로 직접 그룹핑.
            // hint 없는 것: union-find 자동 분할 (기존 동작).
            let callsWithHint, callsWithoutHint =
                callsInFlow
                |> List.partition (fun c ->
                    match Map.tryFind c input.WorkAssignments with
                    | Some (f, _) when f = flowName -> true
                    | _ -> false)

            let groupsFromHint =
                callsWithHint
                |> List.groupBy (fun c ->
                    let _, w = input.WorkAssignments.[c] in w)
                |> List.map (fun (w, cs) -> Some w, cs)

            // hint 없는 calls 는 union-find
            let groupsFromUf =
                if List.isEmpty callsWithoutHint then []
                else
                    for c in callsWithoutHint do ufParent.[c] <- c
                    for cand in input.Candidates do
                        match matchCallInFlow flowName callsWithoutHint cand.Src,
                              matchCallInFlow flowName callsWithoutHint cand.Tgt with
                        | Some s, Some t -> ufUnion s t
                        | _ -> ()
                    callsWithoutHint
                    |> List.groupBy ufFind
                    |> List.map (fun (_, cs) -> None, cs)

            let allGroups = groupsFromHint @ groupsFromUf
            // Work 생성 (hint 가 이름 결정. 없으면 W1/W2/.. 또는 Main).
            let mutable autoIdx = 0
            let workForGroup =
                allGroups
                |> List.map (fun (nameOpt, comp) ->
                    let localName =
                        match nameOpt with
                        | Some w -> w
                        | None ->
                            if allGroups.Length = 1 then "Main"
                            else autoIdx <- autoIdx + 1; sprintf "W%d" autoIdx
                    let workId = ModelBuilder.addWork store flowId flowName localName
                    workCalls.[workId] <- ResizeArray()
                    comp, workId)
            // call assignment
            for (comp, workId) in workForGroup do
                for callFullName in comp do
                    let addr =
                        match Map.tryFind callFullName addrMap with
                        | Some a -> a | None -> ""
                    let callId =
                        ModelBuilder.addCallWithApi store workId flowId callFullName addr
                    callNameToId.[callFullName] <- callId
                    callIdToWorkId.[callId] <- workId
                    workCalls.[workId].Add callId

        // 2) name → times 인덱스 — call name 과 동일 형식으로 normalize.
        let timesByName = Dictionary<string, ResizeArray<int64>>()
        for ev in input.Events do
            let key = ModelBuilder.normalizeFullName ev.Name
            match timesByName.TryGetValue key with
            | true, lst -> lst.Add ev.T
            | _ -> let r = ResizeArray() in r.Add ev.T; timesByName.[key] <- r
        for KeyValue(_, lst) in timesByName do lst.Sort()

        let getTimes name =
            match timesByName.TryGetValue name with
            | true, lst -> lst :> seq<int64>
            | _ -> Seq.empty

        let report = DetectionReport.empty()
        report.NoiseLevel <- noise

        // B7.1 Anomaly detection — 첫 20 cycle 학습 후 전체 cycle deviation 측정.
        // 학습 데이터가 충분할 때만 (>= 5 cycles) 실행.
        let learnedCycles = min 20 (max 1 (int (List.length input.Events / 10)))
        if learnedCycles >= 5 then
            let evPairs = input.Events |> List.map (fun e -> e.T, e.Name)
            let pattern =
                AnomalyDetection.learn evPairs cycleMsForAnalysis learnedCycles
            if pattern.NCyclesLearned >= 5 then
                let _, anomalous =
                    AnomalyDetection.analyzeAllCycles pattern evPairs cycleMsForAnalysis 4.0
                for cycleIdx in anomalous do
                    // 해당 cycle 의 deviation score 재계산
                    let t0 = int64 cycleIdx * cycleMsForAnalysis
                    let cevents =
                        evPairs
                        |> List.filter (fun (t, _) ->
                            t >= t0 && t < t0 + cycleMsForAnalysis)
                    let score = AnomalyDetection.scoreCycle pattern cevents t0
                    report.AnomalousCycles.Add(cycleIdx, score)

        let nameToIdMap = callNameToId |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

        // 2.5) LogicRungs 가 주어지면 재귀 expand + AND/OR 강도 분석 → candidate 보강
        // logicStrengthByKey: (src, tgt) → strength (0~1) — confidence 계산 시 hybrid weight
        let logicStrengthByKey = Dictionary<string * string, float>()
        let effectiveCandidates =
            match input.LogicRungs with
            | None -> input.Candidates
            | Some rungs ->
                let extracted =
                    LogicGraph.extractCandidates rungs input.LogicMaxDepth input.LogicStrengthThreshold
                for (src, tgt, strength) in extracted do
                    logicStrengthByKey.[(src, tgt)] <- strength
                let logicCands =
                    extracted |> List.map (fun (src, tgt, _strength) ->
                        { Src = src; Tgt = tgt; DeclaredKind = "trigger" })
                let existing = input.Candidates |> List.map (fun c -> c.Src, c.Tgt) |> Set.ofList
                let merged =
                    input.Candidates @
                    (logicCands |> List.filter (fun c -> not (Set.contains (c.Src, c.Tgt) existing)))
                merged

        // 3) Tier-1 candidates 게이팅
        // declared kind 별 그룹: work 내 (s_cid, t_cid) → (decision, kind)
        let perWorkSeq = Dictionary<Guid, ResizeArray<Guid * Guid * CausationScore * string>>()
        let perWorkGrp = Dictionary<Guid, Dictionary<Set<Guid>, Guid * Guid * CausationScore>>()

        // Multi-source cluster 인정 — 같은 tgt name 에 여러 src 가 있는 candidates 그룹화.
        // 그 cluster 들에 한해 standard score 외에 clusterScore 도 시도.
        let candByTgtName =
            effectiveCandidates
            |> List.filter (fun c -> c.DeclaredKind <> "group")
            |> List.groupBy (fun c -> c.Tgt)
            |> dict
        let tgtIsMultiSrc tgtName =
            match candByTgtName.TryGetValue tgtName with
            | true, lst -> List.length lst >= 2
            | _ -> false
        // Cache: (tgtFullName) → Map<srcFullName, ClusterScore>
        let clusterCache = Dictionary<string, Map<string, ClusterScore>>()
        let evalCluster (tgtFullName: string) (workId: Guid) (tgtShortName: string) =
            if clusterCache.ContainsKey tgtFullName then clusterCache.[tgtFullName]
            else
                // 같은 work 안 candidates 중 이 tgt 매칭
                let cands =
                    match candByTgtName.TryGetValue tgtShortName with
                    | true, lst -> lst
                    | _ -> []
                let srcsTimes =
                    cands
                    |> List.collect (fun c ->
                        suffixMatch nameToIdMap c.Src
                        |> List.filter (fun sId ->
                            callIdToWorkId.[sId] = workId)
                        |> List.map (fun sId ->
                            let sName = store.Calls.[sId].Name
                            sName, getTimes sName))
                    |> List.distinct
                let r = CausationDetection.clusterScore input.Config srcsTimes (getTimes tgtFullName)
                clusterCache.[tgtFullName] <- r
                r

        // 같은 (sId, tId) 페어 한 번만 score 계산 (arrows_minimal 의 중복 arrow 처리).
        let processedPairs = HashSet<Guid * Guid>()
        let mutable cands = 0
        for cand in effectiveCandidates do
            let sIds = suffixMatch nameToIdMap cand.Src
            let tIds = suffixMatch nameToIdMap cand.Tgt
            for sId in sIds do
                for tId in tIds do
                    if sId <> tId && processedPairs.Add(sId, tId) then
                        let pwS = callIdToWorkId.[sId]
                        let pwT = callIdToWorkId.[tId]
                        if pwS = pwT then
                            cands <- cands + 1
                            let sName =
                                let c = store.Calls.[sId] in c.Name
                            let tName =
                                let c = store.Calls.[tId] in c.Name
                            // declared kind 별 effective config — reset 은 cross-cycle 허용 (full window)
                            let scoreCfg =
                                match cand.DeclaredKind.ToLowerInvariant() with
                                | "reset" -> { input.Config with CycleHintMs = None }
                                | _ -> input.Config
                            let sco = CausationDetection.score scoreCfg (getTimes sName) (getTimes tName)
                            // Multi-source fallback: standard gate fail + multi-src 면 cluster mode 시도
                            let stdDecision = CausationDetection.gate cand.DeclaredKind sco
                            // Mutex fallback: declared mutex/resetreset 면 mutexScore 평가
                            let kindLower = cand.DeclaredKind.ToLowerInvariant()
                            let mutexFallback () =
                                if kindLower = "mutex" || kindLower = "resetreset" then
                                    let passes, _, _, _ =
                                        CausationDetection.mutexScore input.Config
                                            (getTimes sName) (getTimes tName)
                                    if passes then
                                        // mutex pattern 확인 — ResetReset code 4 emit
                                        let augmented = { sco with PassesSeq = true }
                                        Some (EmitSequential(4, augmented))
                                    else None
                                else None
                            let finalDecision =
                                match stdDecision with
                                | EmitSequential _ | EmitGroup _ -> stdDecision
                                | Dropped _ ->
                                    match mutexFallback () with
                                    | Some d -> d
                                    | None when cand.DeclaredKind <> "group"
                                                && tgtIsMultiSrc cand.Tgt ->
                                        let cs = evalCluster tName pwS cand.Tgt
                                        match Map.tryFind sName cs with
                                        | Some cScore when cScore.PassesSeq ->
                                            let augmented = { sco with
                                                                Sufficiency = cScore.Suff
                                                                LagMean = cScore.LagMean
                                                                LagStd = cScore.LagStd
                                                                LagCv = cScore.LagCv
                                                                PassesSeq = true }
                                            EmitSequential(1, augmented)
                                        | _ -> stdDecision
                                    | None -> stdDecision
                            // logic strength lookup (declared kind 와 무관, src/tgt 키)
                            let logicStr =
                                match logicStrengthByKey.TryGetValue((cand.Src, cand.Tgt)) with
                                | true, v -> Some v
                                | _ -> None
                            match finalDecision with
                            | EmitSequential(_, s) ->
                                let lst =
                                    match perWorkSeq.TryGetValue pwS with
                                    | true, v -> v
                                    | _ -> let r = ResizeArray() in perWorkSeq.[pwS] <- r; r
                                lst.Add(sId, tId, s, cand.DeclaredKind)
                                report.PassedSeq <- report.PassedSeq + 1
                                let conf = CausationDetection.confidence s logicStr
                                report.EmittedConfidence.Add(sName, tName, conf)
                            | EmitGroup s ->
                                let dict =
                                    match perWorkGrp.TryGetValue pwS with
                                    | true, v -> v
                                    | _ -> let r = Dictionary() in perWorkGrp.[pwS] <- r; r
                                let key = set [sId; tId]
                                if dict.ContainsKey key then
                                    report.RemovedGroupDup <- report.RemovedGroupDup + 1
                                else
                                    // canonical 정렬 — 작은 이름이 src
                                    let canonSrc, canonTgt =
                                        if sName <= tName then sId, tId else tId, sId
                                    dict.[key] <- (canonSrc, canonTgt, s)
                                    report.GroupEmitted.Add(sName, tName)
                                    report.PassedGrp <- report.PassedGrp + 1
                                    let conf = CausationDetection.confidence s logicStr
                                    report.EmittedConfidence.Add(sName, tName, conf)
                            | Dropped(reason, s) ->
                                report.DroppedCausation <- report.DroppedCausation + 1
                                report.DroppedDetail.Add(sName, tName, s, reason)
        report.TotalCandidates <- cands

        // 4) DAG enforcement (work 별) + declared_kind 별 ArrowType
        // declared_kind → ArrowType 매핑
        let kindToArrowType (declaredKind: string) =
            match declaredKind.ToLowerInvariant() with
            | "reset" -> ArrowType.Reset
            | "trigger_reset" | "startreset" -> ArrowType.StartReset
            | "mutex" | "resetreset" -> ArrowType.ResetReset
            | _ -> ArrowType.Start
        // DAG enforcement 는 "Start-방향" edges 에만 적용. Reset/ResetReset 는
        // 정상적으로 cycle 을 형성할 수 있으므로 제외 (StartReset 는 Start 의미 포함).
        let isDagEdgeKind (k: string) =
            match k.ToLowerInvariant() with
            | "trigger" | "start" | "trigger_reset" | "startreset" -> true
            | _ -> false
        for KeyValue(workId, seqEdges) in perWorkSeq do
            let nodes = workCalls.[workId] |> List.ofSeq
            let kindBySrcTgt =
                seqEdges
                |> Seq.map (fun (s, t, _, dk) -> (s, t), dk)
                |> Seq.distinctBy fst
                |> Map.ofSeq
            let dagInputs, nonDagInputs =
                seqEdges
                |> Seq.toList
                |> List.partition (fun (_, _, _, dk) -> isDagEdgeKind dk)
            let edgesList =
                dagInputs |> List.map (fun (s, t, sc, _) -> s, t, sc)
            let accepted, cycRemoved = DagEnforcement.topoBreakCycle edgesList nodes
            for (s, t, _) in cycRemoved do
                report.RemovedCycle <- report.RemovedCycle + 1
                report.CycleWarn.Add(store.Calls.[s].Name, store.Calls.[t].Name)
            let kept, trRemoved = DagEnforcement.transitiveReduction accepted Set.empty
            for (s, t, _) in trRemoved do
                report.RemovedTransitive <- report.RemovedTransitive + 1
                report.TransitiveLog.Add(store.Calls.[s].Name, store.Calls.[t].Name)
            for (s, t, _) in kept do
                let dk = Map.tryFind (s, t) kindBySrcTgt |> Option.defaultValue "trigger"
                let atype = kindToArrowType dk
                ModelBuilder.addArrowCall store workId s t atype |> ignore
            // Reset / ResetReset edges 직접 emit (DAG check 없음)
            for (s, t, _, dk) in nonDagInputs do
                let atype = kindToArrowType dk
                ModelBuilder.addArrowCall store workId s t atype |> ignore

        // 5) Group emit
        for KeyValue(workId, grpDict) in perWorkGrp do
            for KeyValue(_, (s, t, _)) in grpDict do
                ModelBuilder.addArrowCall store workId s t ArrowType.Group |> ignore

        // 6) Cross-flow → arrowWorks emit.
        //    Src/Tgt 형식: "{flowName}.{workName}" — work name 정확 매칭 우선.
        //    만약 flow 안 단일 work 이거나 work name 매칭 안 되면 default work 사용.
        if not (List.isEmpty input.CrossFlowCandidates) then
            let kindToType (k: string) =
                match k with
                | "group" -> ArrowType.Group
                | "reset" -> ArrowType.Reset
                | "trigger_reset" -> ArrowType.StartReset
                | "mutex" -> ArrowType.ResetReset
                | _ -> ArrowType.Start
            let parseRef (s: string) =
                match s.IndexOf '.' with
                | -1 -> s, ""
                | i -> s.Substring(0, i), s.Substring(i + 1)
            // Work 검색: (flowPrefix, localName) 정확 매칭. localName 빈 문자열이면 flow 의 단일 work.
            let findWork (flowName: string) (workName: string) : Guid option =
                let candidates =
                    store.Works
                    |> Seq.filter (fun kv -> kv.Value.FlowPrefix = flowName)
                    |> Seq.toList
                if List.isEmpty candidates then None
                elif workName = "" || candidates.Length = 1 then
                    Some (candidates.[0].Key)   // 단일 work
                else
                    candidates
                    |> List.tryFind (fun kv -> kv.Value.LocalName = workName)
                    |> Option.map (fun kv -> kv.Key)

            let mutable awEmitted = 0
            let mutable awSkipped = 0
            let emitted = HashSet<Guid * Guid * ArrowType>()
            for cand in input.CrossFlowCandidates do
                let srcFlow, srcWorkName = parseRef cand.Src
                let tgtFlow, tgtWorkName = parseRef cand.Tgt
                let atype = kindToType cand.DeclaredKind
                match findWork srcFlow srcWorkName, findWork tgtFlow tgtWorkName with
                | Some sId, Some tId ->
                    if sId <> tId && emitted.Add(sId, tId, atype) then
                        ModelBuilder.addArrowWork store sysId sId tId atype |> ignore
                        awEmitted <- awEmitted + 1
                | _ ->
                    awSkipped <- awSkipped + 1
            report.CycleWarn.Add(
                sprintf "cross-flow emitted %d, skipped %d (work name mismatch)"
                    awEmitted awSkipped, "")

        report.FinalArrowCount <- store.ArrowCalls.Count
        store, report
