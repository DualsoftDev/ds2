namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Text.Json

/// doc-level YAML (ModelProtocol schema v0) → Mermaid 다이어그램 변환.
///
/// 2개 분석 차원:
/// - Work flow: 각 active system 의 각 Flow 안 Work 노드 + work-scope arrows (mermaid 1개)
/// - Call flow: 각 active system 의 각 Flow 의 각 Work 안 Call 노드 + call-scope arrows (work 단위로 mermaid N개 분리)
///
/// passive doc / patch-only doc → 빈 list (Mermaid tab 자체 미생성 트리거).
///
/// SSOT: yaml-protocol-v0.md §2.2 (active schema) / §2.4 (Arrow) / §2.5 (Path) / §1.7 (concurrent 중복 Call 허용).
/// Ev2 GenerateMermaidTool 패턴 차용 — theme header, classDef (advance/retract/neutral),
/// Reset connector 매핑 (`-.->`, `o-.->`, `<-.->`).
[<RequireQualifiedAccess>]
module ModelProtocolMermaid =

    /// dialog 가 1 단위로 표시하는 mermaid block — 제목 + diagram 텍스트.
    type MermaidBlock = {
        Title: string
        Mermaid: string
    }

    // ── Theme / classDef (Ev2 차용, Promaker 톤 그대로) ─────────────────────

    let private themeHeader =
        "%%{init: {'theme':'base','themeVariables':{" +
        "'fontFamily':'\"Segoe UI\",\"Inter\",system-ui,sans-serif'," +
        "'fontSize':'13px'," +
        "'primaryColor':'#1e293b'," +
        "'primaryTextColor':'#e2e8f0'," +
        "'primaryBorderColor':'#475569'," +
        "'lineColor':'#94a3b8'," +
        "'secondaryColor':'#334155'," +
        "'tertiaryColor':'#0f172a'," +
        "'clusterBkg':'#0b1220'," +
        "'clusterBorder':'#334155'," +
        "'edgeLabelBackground':'#1e293b'," +
        "'titleColor':'#f1f5f9'" +
        "}}}%%"

    let private classDefs = [
        "  classDef advance fill:#0c4a6e,stroke:#38bdf8,stroke-width:1.5px,color:#e0f2fe,rx:6,ry:6;"
        "  classDef retract fill:#7c2d12,stroke:#fb923c,stroke-width:1.5px,color:#fff7ed,rx:6,ry:6;"
        "  classDef neutral fill:#1e293b,stroke:#94a3b8,stroke-width:1.5px,color:#e2e8f0,rx:6,ry:6;"
    ]

    // Promaker ApiDef 이름 관례 (cylinder ADV/RET / clamp CLP/UNCLP / custom PUNCH 등) 에 맞춰 확장.
    // Ev2 의 advance/retract keyword set 을 그대로 차용 + Promaker custom device 의 PUNCH 같은 신호도
    // forward 의미로 묶음. SSOT 변경 없는 cosmetic 매핑 — 색상 차이만 부여.
    let private advanceActions =
        Set.ofList [
            "ADV"; "ADVANCE"; "FORWARD"; "GO"; "OPEN"; "UP"; "ON"; "OUT"; "WORK"; "RUN"; "PUNCH"
            "CLP"; "CLAMP"; "TILT_UP"
        ]
    let private retractActions =
        Set.ofList [
            "RET"; "RETRACT"; "REVERSE"; "BACK"; "CLOSE"; "DOWN"; "OFF"; "IN"; "HOME"; "STOP"
            "UNCLP"; "UNCLAMP"; "TILT_DOWN"
        ]

    // Arrow 시각 구분 — line weight + style 로 4종 분리.
    //   Reset:      얇은 점선 (-.->)            — target 측만 arrow head
    //   StartReset: 두꺼운 실선 (==>)           — Reset 과 line weight 로 구분 (mermaid 가 source-side rect marker 미지원이라 weight 로 대체)
    //   ResetReset: 얇은 점선 양방향 (<-.->)    — src/tgt 양쪽 arrow head
    //   그 외 (Start / Group / Unspecified) → 얇은 실선 (-->).
    let private resetConnectors =
        let d = Dictionary<string, string>(StringComparer.Ordinal)
        d.Add("Reset",      "-.->")
        d.Add("StartReset", "==>")
        d.Add("ResetReset", "<-.->")
        d
    let private defaultConnector = "-->"

    /// mermaid 노드 ID 용 — ASCII alphanumeric + `_` 만 보존, 그 외 (한글 포함 모든 unicode + 특수문자) → `_`.
    /// `Char.IsLetterOrDigit` 는 한글 글자도 true 반환 → mermaid v11 의 unicode id 처리가 환경/플러그인 의존이라 ASCII 제한이 안전.
    /// SSOT §2.5: entity 이름 자체는 한글 허용 (NFC) 이지만 mermaid node id 는 별개 layer. 라벨 텍스트 ("...") 는 원본 보존.
    let private sanitizeId (raw: string) : string =
        let sb = System.Text.StringBuilder(raw.Length)
        for c in raw do
            let isAsciiAlNum =
                (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
            if isAsciiAlNum || c = '_' then sb.Append c |> ignore
            else sb.Append '_' |> ignore
        sb.ToString()

    /// `Cyl1.ADV` 의 ADV 부분 등을 keyword set 과 매칭해 색상 class 선택.
    /// dot 이 없으면 neutral.
    let private actionClass (callName: string) : string =
        let idx = callName.LastIndexOf('.')
        if idx < 0 then "neutral"
        else
            let action = callName.Substring(idx + 1).ToUpperInvariant()
            if advanceActions.Contains action then "advance"
            elif retractActions.Contains action then "retract"
            else "neutral"

    let private connectorFor (arrowType: string) : string =
        match resetConnectors.TryGetValue arrowType with
        | true, c -> c
        | _ -> defaultConnector

    /// arrow type → edge label (Start / Unspecified 는 시각 노이즈 — 라벨 생략).
    let private edgeLabel (arrowType: string) : string =
        if arrowType = "Start" || arrowType = "Unspecified" then ""
        else sprintf "|%s|" arrowType

    /// 두 emitter 공유 — `%%init%%` + `flowchart <dir>` + classDef 3건. direction = "TD" / "LR" 등.
    let private emitHeader (lines: ResizeArray<string>) (direction: string) : unit =
        lines.Add themeHeader
        lines.Add (sprintf "flowchart %s" direction)
        for cd in classDefs do lines.Add cd

    /// 두 emitter 공유 — `<indent><frId> <conn>[<|label|>] <toId>` 한 줄 emit.
    let private emitArrowLine
            (lines: ResizeArray<string>)
            (indent: string)
            (frId: string)
            (conn: string)
            (label: string)
            (toId: string) : unit =
        if label = "" then
            lines.Add (sprintf "%s%s %s %s" indent frId conn toId)
        else
            lines.Add (sprintf "%s%s %s%s %s" indent frId conn label toId)

    // ── doc JSON 순회 helpers ────────────────────────────────────────────────

    /// "flow <name>" 키 식별 — SSOT §2.5 flow-key grammar.
    let private tryFlowKey (key: string) : string option =
        if not (key.StartsWith("flow ")) then None
        else
            let name = key.Substring(5).Trim()
            if String.IsNullOrEmpty name then None else Some name

    let private collectFlows (systemEl: JsonElement) : (string * JsonElement) list =
        if systemEl.ValueKind <> JsonValueKind.Object then []
        else
            systemEl.EnumerateObject()
            |> Seq.choose (fun prop ->
                tryFlowKey prop.Name |> Option.map (fun name -> name, prop.Value))
            |> Seq.toList

    let private collectActiveSystems (root: JsonElement) : (string * JsonElement) list =
        match ModelProtocol.tryProp root "systems" with
        | Some systemsEl when systemsEl.ValueKind = JsonValueKind.Array ->
            systemsEl.EnumerateArray()
            |> Seq.choose (fun s ->
                let kindOpt = ModelProtocol.tryProp s "kind" |> Option.bind ModelProtocol.tryString
                match kindOpt with
                | Some "active" ->
                    let name =
                        ModelProtocol.tryProp s "system"
                        |> Option.bind ModelProtocol.tryString
                        |> Option.defaultValue "Active"
                    Some (name, s)
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private collectWorks (flowEl: JsonElement) : (string * JsonElement) list =
        match ModelProtocol.tryProp flowEl "works" with
        | Some worksEl when worksEl.ValueKind = JsonValueKind.Object ->
            worksEl.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value)
            |> Seq.toList
        | _ -> []

    /// arrows 배열 → (fromRaw, toRaw, type) tuple list. parse 실패 항목은 silently skip
    /// — mermaid view 는 보조 기능이므로 에러로 멈추지 않음 (validate 단계가 정합 책임).
    let private collectArrows (el: JsonElement) : (string * string * string) list =
        match ModelProtocol.tryProp el "arrows" with
        | Some arrEl when arrEl.ValueKind = JsonValueKind.Array ->
            arrEl.EnumerateArray()
            |> Seq.choose (fun arr ->
                match ModelProtocol.extractArrowString arr with
                | Ok raw ->
                    match ModelProtocol.parseArrowSpec raw with
                    | Ok spec ->
                        let t = spec.TypeRaw |> Option.defaultValue "Unspecified"
                        Some (spec.FromRaw, spec.ToRaw, t)
                    | Error _ -> None
                | Error _ -> None)
            |> Seq.toList
        | _ -> []

    let private collectCalls (workEl: JsonElement) : string list =
        match ModelProtocol.tryProp workEl "calls" with
        | Some callsEl when callsEl.ValueKind = JsonValueKind.Array ->
            callsEl.EnumerateArray()
            |> Seq.choose ModelProtocol.tryString
            |> Seq.toList
        | _ -> []

    // ── Mermaid 1: Work flow ─────────────────────────────────────────────────

    /// StartReset 색 — Tailwind orange-400 (retract class 와 일관). 두꺼운 실선 (`==>`) 위에 색만 override.
    let private startResetLinkStyle = "stroke:#fb923c,stroke-width:2.5px"

    /// 누적된 StartReset edge index 들을 한 줄 linkStyle 로 모아 emit (없으면 skip).
    let private emitStartResetLinkStyle (lines: ResizeArray<string>) (indent: string) (indices: ResizeArray<int>) : unit =
        if indices.Count > 0 then
            let idxStr = String.Join(",", indices |> Seq.map string)
            lines.Add (sprintf "%slinkStyle %s %s;" indent idxStr startResetLinkStyle)

    /// 모든 active system 의 Flow 안 Work 노드 + work-scope arrows 를 한 mermaid 로.
    /// (system, flow) 쌍마다 subgraph 묶음. arrows 가 없어도 work 노드만 있으면 emit.
    /// active 가 0건이거나 (work 0건 + arrow 0건) 이면 None — 호출처는 block 자체 skip.
    let jsonElementToWorkFlowMermaid (root: JsonElement) : string option =
        let activeSystems = collectActiveSystems root
        if List.isEmpty activeSystems then None
        else
            let lines = ResizeArray<string>()
            emitHeader lines "TD"
            let mutable rendered = false
            let mutable arrowIdx = 0
            let startResetIndices = ResizeArray<int>()
            for (sysName, sysEl) in activeSystems do
                for (flowName, flowEl) in collectFlows sysEl do
                    let works = collectWorks flowEl
                    let arrows = collectArrows flowEl
                    if not (List.isEmpty works) || not (List.isEmpty arrows) then
                        rendered <- true
                        let subId = sanitizeId (sysName + "__" + flowName)
                        lines.Add (sprintf "  subgraph %s[\"%s.%s\"]" subId sysName flowName)
                        lines.Add "    direction TB"
                        for (workName, _workEl) in works do
                            let nodeId = sanitizeId (subId + "__" + workName)
                            lines.Add (sprintf "    %s[\"%s\"]:::neutral" nodeId workName)
                        for (fr, t, atype) in arrows do
                            let frId = sanitizeId (subId + "__" + fr)
                            let toId = sanitizeId (subId + "__" + t)
                            emitArrowLine lines "    " frId (connectorFor atype) (edgeLabel atype) toId
                            if atype = "StartReset" then startResetIndices.Add arrowIdx
                            arrowIdx <- arrowIdx + 1
                        lines.Add "  end"
            emitStartResetLinkStyle lines "  " startResetIndices
            if rendered then
                Some (String.Join("\n", lines))
            else
                None

    // ── Mermaid 2: Call flow (work 단위 분리) ────────────────────────────────

    /// 각 active system 의 각 Flow 의 각 Work 마다 mermaid 1개 — work 안 Call 노드 + call-scope arrows.
    /// calls 가 없으면 해당 work 는 skip (조용한 noise 회피).
    let jsonElementToCallFlowMermaids (root: JsonElement) : MermaidBlock list =
        let blocks = ResizeArray<MermaidBlock>()
        for (sysName, sysEl) in collectActiveSystems root do
            for (flowName, flowEl) in collectFlows sysEl do
                for (workName, workEl) in collectWorks flowEl do
                    let calls = collectCalls workEl
                    if not (List.isEmpty calls) then
                        let arrows = collectArrows workEl
                        let lines = ResizeArray<string>()
                        emitHeader lines "LR"
                        // SSOT §1.7: 같은 Work 안 같은 ApiDef 가 N회 등장 (concurrent) 가능. mermaid id 는
                        // 중복 정의되어도 첫 정의 라벨 유지 — arrow 측은 ApiDef 이름이 unique 일 때만 source/target
                        // 으로 등장 가능 (concurrent 는 arrow 없음) 이므로 id collision 시각 영향 0.
                        let seen = HashSet<string>(StringComparer.Ordinal)
                        for call in calls do
                            if seen.Add call then
                                let id = sanitizeId call
                                let cls = actionClass call
                                lines.Add (sprintf "  %s[\"%s\"]:::%s" id call cls)
                        let mutable arrowIdx = 0
                        let startResetIndices = ResizeArray<int>()
                        for (fr, t, atype) in arrows do
                            let frId = sanitizeId fr
                            let toId = sanitizeId t
                            emitArrowLine lines "  " frId (connectorFor atype) (edgeLabel atype) toId
                            if atype = "StartReset" then startResetIndices.Add arrowIdx
                            arrowIdx <- arrowIdx + 1
                        emitStartResetLinkStyle lines "  " startResetIndices
                        let title = sprintf "%s.%s.%s" sysName flowName workName
                        blocks.Add { Title = title; Mermaid = String.Join("\n", lines) }
        blocks |> List.ofSeq

    /// dialog 가 일괄 표시하는 block list — 첫 entry = Work flow (있으면), 이후 = Call flow per work.
    /// passive-only doc / patch-only doc → 빈 list (UI 측 Mermaid tab 자체 미생성).
    let jsonElementToBlocks (root: JsonElement) : MermaidBlock list =
        let work =
            match jsonElementToWorkFlowMermaid root with
            | Some m -> [ { Title = "Work flow"; Mermaid = m } ]
            | None -> []
        let calls =
            jsonElementToCallFlowMermaids root
            |> List.map (fun b -> { b with Title = "Call flow — " + b.Title })
        work @ calls
