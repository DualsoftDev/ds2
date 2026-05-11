namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text
open Ds2.Core
open Ds2.Core.Store

/// LLM chat round-trip 최소화용 store snapshot 직렬화.
/// doc: Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md §4.1 (grammar 정의).
/// 본 파일 안의 `§M2` / `§M4` 등은 같은 doc 의 v5.1 review 이슈 ID.
///
/// 출력 grammar (informal, v5):
///   projects: (empty) | project+
///   project = "  ProjectName:" / "    systems:" + system+ / "    flows:" + flow* / "    work-arrows:" + warrows?
///   system  = "      SystemName (active|passive[/cylinder|/clamp|/robot|/device]) [{ApiDef list}]"
///   flow    = "      FlowName @OwnerSystem:" / "        works:" + work+
///   work    = "          LocalName [Call DAG]"
///   warrows = "      @SystemName:" / "        Source(WorkFullName) →(S|R|SR|RR|G|U) Target" *
///
/// Call DAG: linear chain "A → B → C", fan-out "A → (B, C)", multi-root "; " 결합.
/// 단순화: fan-in 노드는 첫 도달 시점에서만 child 출력 — 복잡 그래프는 fallback (truncated) 처리.
[<RequireQualifiedAccess>]
module StoreSnapshot =

    let private indent (sb: StringBuilder) (spaces: int) =
        for _ in 1 .. spaces do sb.Append(' ') |> ignore

    /// XML escape — entity name (Project / System / Flow / Work / Call / ApiDef) 이 `<`, `>`, `&` 같은
    /// 메타문자를 포함하더라도 `<store-snapshot>` wrapper 를 깨거나 prompt injection 으로 변질되는 것을 차단.
    /// 일반 모델링 사용 시 entity name 에 메타문자가 들어갈 일은 없지만, 사용자가 import 한 외부 파일이나
    /// rename 으로 우회 가능하므로 정석으로 escape.
    let private escapeXml (s: string) =
        if isNull s then ""
        else
            let sb = StringBuilder(s.Length)
            for ch in s do
                match ch with
                | '<' -> sb.Append("&lt;") |> ignore
                | '>' -> sb.Append("&gt;") |> ignore
                | '&' -> sb.Append("&amp;") |> ignore
                | _   -> sb.Append(ch) |> ignore
            sb.ToString()

    /// ArrowType enum → 1~2자 약자. 신규 ArrowType case 추가 시 본 매핑도 함께 갱신 필요
    /// (round-trip §n5 review — silent "U" fallback 회피 책임은 enum 추가 시점에).
    let private arrowAbbrev (t: ArrowType) =
        match t with
        | ArrowType.Start       -> "S"
        | ArrowType.Reset       -> "R"
        | ArrowType.StartReset  -> "SR"
        | ArrowType.ResetReset  -> "RR"
        | ArrowType.Group       -> "G"
        | ArrowType.Unspecified -> "U"
        | _                     -> "U"  // future ArrowType case — doc 의 grammar 룰 갱신과 함께 case 추가 권장

    /// SystemType (예: "Cylinder_1", "Clamp_2") → "/cylinder" 같은 약자. 미식별 시 "/device" fallback.
    /// passive system 표시에만 사용. active 는 kind 자체 미표기.
    let private kindSuffix (systemType: string option) =
        match systemType with
        | None -> ""
        | Some t ->
            let lower = t.ToLowerInvariant()
            if lower.StartsWith("cylinder") then "/cylinder"
            elif lower.StartsWith("clamp") then "/clamp"
            elif lower.StartsWith("robot") then "/robot"
            else "/device"  // device + 그 외 미식별 (LLM 에 generic 으로 노출)

    /// Work 안 Call DAG 직렬화. nodes = work 의 Calls, edges = work 의 ArrowBetweenCalls.
    let private renderCallDag (store: DsStore) (workId: Guid) =
        let calls = Queries.callsOf workId store |> List.toArray
        if calls.Length = 0 then ""
        else
            let arrows = Queries.arrowCallsOf workId store |> List.toArray
            // adjacency
            let outAdj = Dictionary<Guid, ResizeArray<Guid>>()
            let inDeg = Dictionary<Guid, int>()
            for c in calls do
                outAdj.[c.Id] <- ResizeArray()
                inDeg.[c.Id] <- 0
            for a in arrows do
                if outAdj.ContainsKey(a.SourceId) && inDeg.ContainsKey(a.TargetId) then
                    outAdj.[a.SourceId].Add(a.TargetId)
                    inDeg.[a.TargetId] <- inDeg.[a.TargetId] + 1
            let nameOf (id: Guid) =
                match store.Calls.TryGetValue(id) with
                | true, c -> escapeXml c.Name
                | _ -> id.ToString("N").Substring(0, 8)
            let visited = HashSet<Guid>()
            // enqueued: 큐에 push 된 적 있는 노드 — fan-in 시 같은 자식이 두 부모의 fan-out 괄호에
            // 중복 표기 + 큐 중복 push 되는 것 차단 (round-trip §J1 review 반영).
            let enqueued = HashSet<Guid>()
            // representedEdges: chain/fan-out 으로 표현된 (source, target) edge 추적. 표현 안 된 edge 는
            // chain 끝에 별도 표기 (round-trip §H2 review — `A → C, B → C` 같은 fan-in 의 B → C 누락 차단).
            let representedEdges = HashSet<struct(Guid * Guid)>()
            let segments = ResizeArray<string>()
            let queue = Queue<Guid>()
            let roots =
                calls
                |> Array.filter (fun c -> inDeg.[c.Id] = 0)
                |> Array.sortBy (fun c -> c.Name)
            for r in roots do queue.Enqueue(r.Id)
            // disconnected / cycle 안의 노드 (root 아닌 잔여) 도 임의 순서로 큐 끝에 추가
            for c in calls |> Array.sortBy (fun c -> c.Name) do
                if inDeg.[c.Id] > 0 then queue.Enqueue(c.Id)
            while queue.Count > 0 do
                let startId = queue.Dequeue()
                if not (visited.Contains startId) then
                    let segSb = StringBuilder()
                    segSb.Append(nameOf startId) |> ignore
                    visited.Add(startId) |> ignore
                    let mutable cur = startId
                    let mutable continueChain = true
                    while continueChain do
                        let outs = outAdj.[cur]
                        // candidates: visited (이미 어떤 segment 에 등장) 또는 enqueued (이미 다른 fan-out 괄호 표기)
                        // 둘 다 제외 — fan-in 동시 표기 + 큐 중복 push 동시 차단.
                        let candidates =
                            outs
                            |> Seq.filter (fun n -> not (visited.Contains n) && not (enqueued.Contains n))
                            |> Seq.toArray
                        if candidates.Length = 0 then
                            continueChain <- false
                        elif candidates.Length = 1 then
                            let next = candidates.[0]
                            segSb.Append(" → ") |> ignore
                            segSb.Append(nameOf next) |> ignore
                            representedEdges.Add(struct(cur, next)) |> ignore
                            visited.Add(next) |> ignore
                            cur <- next
                        else
                            // fan-out: " → (a, b, c)" 단발 표기. child 의 처리 분기:
                            //   - leaf (outAdj 비어있음): visited 등록만 — 별도 segment 의 root 로 단독 노출 회피.
                            //   - 후속 chain 보유: enqueued 등록 + 큐 push → 다음 segment 의 root 로 walk.
                            //     같은 이름이 fan-out 괄호 + 별도 segment root 양쪽에 등장 = LLM 이 동일 노드의
                            //     분기로 인식해야 함 (의도된 동작, doc grammar 명시).
                            segSb.Append(" → (") |> ignore
                            segSb.Append(candidates |> Array.map nameOf |> String.concat ", ") |> ignore
                            segSb.Append(")") |> ignore
                            for n in candidates do
                                representedEdges.Add(struct(cur, n)) |> ignore
                                if outAdj.[n].Count = 0 then visited.Add(n) |> ignore
                                else
                                    enqueued.Add(n) |> ignore
                                    queue.Enqueue(n)
                            continueChain <- false
                    segments.Add(segSb.ToString())
            // round-trip §H2 — fan-in 등으로 chain 표현에서 누락된 edge 검출 + 별도 표기.
            // 예: `A → C, B → C` 의 B → C edge 가 chain 에서 visited 차단으로 누락 → 여기서 명시.
            let missingEdges =
                arrows
                |> Array.filter (fun a -> not (representedEdges.Contains(struct(a.SourceId, a.TargetId))))
            let chainStr = String.concat "; " segments
            if missingEdges.Length = 0 then chainStr
            else
                let extra =
                    missingEdges
                    |> Array.sortBy (fun a -> nameOf a.SourceId)
                    |> Array.map (fun a -> $"{nameOf a.SourceId} → {nameOf a.TargetId}")
                    |> String.concat ", "
                if String.IsNullOrEmpty chainStr then extra
                else chainStr + "; " + extra

    /// round-trip §M4 분리: system 한 개의 한 줄 표시.
    let private renderSystem (sb: StringBuilder) (store: DsStore) (activeIds: HashSet<Guid>) (s: DsSystem) =
        let isActive = activeIds.Contains(s.Id)
        let role = if isActive then "active" else "passive"
        let kind = if isActive then "" else kindSuffix s.SystemType
        let apiDefNames =
            Queries.apiDefsOf s.Id store
            |> List.map (fun d -> d.Name)
            |> List.sort
            |> List.map escapeXml
        let apiPart =
            if List.isEmpty apiDefNames then ""
            else " {" + String.concat ", " apiDefNames + "}"
        sb.AppendLine() |> ignore
        indent sb 6
        sb.Append($"{escapeXml s.Name} ({role}{kind}){apiPart}") |> ignore

    /// round-trip §M4 분리: flow 1개의 works DAG 표시.
    let private renderFlow (sb: StringBuilder) (store: DsStore) (f: Flow) =
        let ownerName =
            match store.Systems.TryGetValue(f.ParentId) with
            | true, s -> escapeXml s.Name
            | _ -> "?"
        sb.AppendLine() |> ignore
        indent sb 6
        sb.Append($"{escapeXml f.Name} @{ownerName}:") |> ignore
        let works =
            Queries.worksOf f.Id store
            |> List.sortBy (fun w -> w.LocalName)
        sb.AppendLine() |> ignore
        indent sb 8
        sb.Append("works:") |> ignore
        for w in works do
            let dag = renderCallDag store w.Id
            sb.AppendLine() |> ignore
            indent sb 10
            sb.Append($"{escapeXml w.LocalName} [{dag}]") |> ignore

    /// round-trip §M2/Critical fix: `ArrowBetweenWorks.ParentId = systemId` (Arrows.fs:26 도메인 룰).
    /// 이전 구현이 flow.Id 와 비교하여 work-arrows 가 항상 빈 결과로 누락되던 버그 수정.
    /// system 단위 grouping (work-arrows 는 같은 system 의 두 work 사이) — work full name 사용으로 flow 간
    /// LocalName 충돌 회피.
    let private renderWorkArrows (sb: StringBuilder) (store: DsStore) (systems: DsSystem array) =
        let nameOf (id: Guid) =
            match store.Works.TryGetValue(id) with
            | true, w -> escapeXml w.Name
            | _ -> "?"
        let groups =
            systems
            |> Array.choose (fun s ->
                let arrows = Queries.arrowWorksOf s.Id store
                if List.isEmpty arrows then None
                else Some (s, arrows |> List.sortBy (fun a -> nameOf a.SourceId)))
        if not (Array.isEmpty groups) then
            sb.AppendLine() |> ignore
            indent sb 4
            sb.Append("work-arrows:") |> ignore
            for (s, arrows) in groups do
                sb.AppendLine() |> ignore
                indent sb 6
                sb.Append($"@{escapeXml s.Name}:") |> ignore
                for a in arrows do
                    sb.AppendLine() |> ignore
                    indent sb 8
                    sb.Append($"{nameOf a.SourceId} →{arrowAbbrev a.ArrowType} {nameOf a.TargetId}") |> ignore

    /// 빈 store / 정상 store 모두 처리하는 단일 진입점.
    let render (store: DsStore) : string =
        let sb = StringBuilder()
        let projects =
            Queries.allProjects store
            |> List.sortBy (fun p -> p.Name)
        if List.isEmpty projects then
            sb.Append("projects: (empty)") |> ignore
        else
            sb.Append("projects:") |> ignore
            for p in projects do
                sb.AppendLine() |> ignore
                indent sb 2
                sb.Append(escapeXml p.Name) |> ignore
                sb.Append(':') |> ignore

                let activeIds = HashSet(p.ActiveSystemIds)
                let systems =
                    Seq.append p.ActiveSystemIds p.PassiveSystemIds
                    |> Seq.distinct
                    |> Seq.choose (fun sid ->
                        match store.Systems.TryGetValue(sid) with
                        | true, s -> Some s
                        | _ -> None)
                    |> Seq.sortBy (fun s -> s.Name)
                    |> Seq.toArray

                sb.AppendLine() |> ignore
                indent sb 4
                sb.Append("systems:") |> ignore
                for s in systems do renderSystem sb store activeIds s

                let flows =
                    systems
                    |> Array.collect (fun s -> Queries.flowsOf s.Id store |> List.toArray)
                    |> Array.sortBy (fun f -> f.Name)
                sb.AppendLine() |> ignore
                indent sb 4
                sb.Append("flows:") |> ignore
                for f in flows do renderFlow sb store f

                renderWorkArrows sb store systems
        sb.ToString()


/// C# interop — `store.RenderSnapshot()` / `store.RenderSnapshotEnvelope(revision)` 호출 가능.
[<Extension>]
type DsStoreSnapshotExtensions =
    [<Extension>]
    static member RenderSnapshot(store: DsStore) =
        StoreSnapshot.render store

    /// round-trip §M4 — `<store-snapshot revision="N"> ... </store-snapshot>` envelope 생성 (wire format SSOT).
    /// ViewModel 측에서 envelope wrap 정책을 알 책임 회피 — `_store.RenderSnapshotEnvelope(rev)` 1줄 호출.
    [<Extension>]
    static member RenderSnapshotEnvelope(store: DsStore, revision: int) =
        let body = StoreSnapshot.render store
        sprintf "<store-snapshot revision=\"%d\">\n%s\n</store-snapshot>" revision body

    /// round-trip §J6 — revision 과 body 를 함께 캡쳐하여 BumpRevision race 차단. caller 가 revision 을
    /// 별도 read 후 envelope 호출 사이에 다른 thread mutation 이 끼면 envelope 의 attribute 와 body 가
    /// 불일치할 수 있음. 본 helper 는 (rev, envelope) 단일 호출 내에서 캡쳐 — UI dispatcher 외에서도 안전.
    [<Extension>]
    static member RenderSnapshotEnvelopeAtomic(store: DsStore) : struct(int * string) =
        let rev = store.Revision
        let body = StoreSnapshot.render store
        struct(rev, sprintf "<store-snapshot revision=\"%d\">\n%s\n</store-snapshot>" rev body)
