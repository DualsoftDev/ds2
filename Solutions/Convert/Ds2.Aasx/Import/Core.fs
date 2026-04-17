namespace Ds2.Aasx

open System
open System.Reflection
open AasCore.Aas3_1
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module internal AasxImportCore =

    let log = LogManager.GetLogger("Ds2.Aasx.AasxImporter")

    // ── 파싱 헬퍼 ────────────────────────────────────────────────────────────

    let valueOrEmpty (p: Property) = if p.Value = null then "" else p.Value

    let tryParseGuid (s: string) =
        match Guid.TryParse(s) with true, v -> Some v | _ -> None

    let tryParseIsoDuration (s: string) =
        try Some (System.Xml.XmlConvert.ToTimeSpan(s)) with _ -> None

    let extractGuidFromIdShort (idShort: string) =
        let parts = idShort.Split('_')
        if parts.Length = 2 then
            let hex = parts.[1]
            if hex.Length = 32 then
                try
                    sprintf "%s-%s-%s-%s-%s"
                        (hex.[0..7]) (hex.[8..11]) (hex.[12..15]) (hex.[16..19]) (hex.[20..])
                    |> tryParseGuid
                with _ -> None
            else None
        else None

    let getProp (smc: SubmodelElementCollection) (idShort: string) =
        if smc.Value = null then None
        else
            smc.Value |> Seq.tryPick (function
                | :? Property as p when p.IdShort = idShort && p.Value <> null -> Some p.Value
                | _ -> None)

    let fromJsonProp<'T> (smc: SubmodelElementCollection) (idShort: string) : 'T option =
        getProp smc idShort
        |> Option.bind (fun json ->
            try Some (Ds2.Serialization.JsonConverter.deserialize<'T> json)
            with ex -> log.Warn($"JSON 역직렬화 실패: {idShort} — {ex.Message}", ex); None)

    let getChildSmlSmcs (smc: SubmodelElementCollection) (idShort: string) =
        if smc.Value = null then []
        else
            smc.Value |> Seq.tryPick (function
                | :? SubmodelElementList as l when l.IdShort = idShort ->
                    Some (if l.Value = null then []
                          else l.Value |> Seq.choose (function :? SubmodelElementCollection as c -> Some c | _ -> None) |> Seq.toList)
                | _ -> None)
            |> Option.defaultValue []

    // ── Enum 파싱 (통합) ─────────────────────────────────────────────────────

    let private parseEnum<'T when 'T: struct and 'T :> ValueType and 'T: (new: unit -> 'T)> (defaultVal: 'T) (s: string) : 'T =
        let mutable result = defaultVal
        if Enum.TryParse<'T>(s, &result) then result else defaultVal

    let parseArrowType = parseEnum ArrowType.Unspecified
    let parseStatus4   = parseEnum Status4.Ready

    // ── 유틸리티 ─────────────────────────────────────────────────────────────

    let describeSmc (smc: SubmodelElementCollection) =
        let guid = getProp smc "Guid" |> Option.defaultValue "<missing>"
        let name = getProp smc "Name" |> Option.defaultValue "<missing>"
        $"Guid={guid}, Name={name}"

    let parseStrictList ownerLabel itemLabel (items: SubmodelElementCollection list) (parser: SubmodelElementCollection -> 'T option) =
        let rec loop acc = function
            | [] -> Some (List.rev acc)
            | smc :: tail ->
                match parser smc with
                | Some v -> loop (v :: acc) tail
                | None   -> log.Error($"AASX import failed: invalid {itemLabel} under {ownerLabel} ({describeSmc smc})."); None
        loop [] items

    // ── elementsToProps 핵심: 타입별 변환 ────────────────────────────────────

    let private trySetValue (propType: Type) (value: string) (pi: PropertyInfo) (target: obj) : unit option =
        let set (v: obj) = pi.SetValue(target, v); Some ()
        let tryP (parse: string -> bool * 'a) (wrap: 'a -> obj) =
            match parse value with true, v -> set (wrap v) | _ -> None

        match propType with
        | t when t = typeof<string>                -> set value
        | t when t = typeof<string option>         -> set (box (Some value))
        | t when t = typeof<bool>                  -> tryP Boolean.TryParse box
        | t when t = typeof<bool option>           -> tryP Boolean.TryParse (Some >> box)
        | t when t = typeof<int>                   -> tryP Int32.TryParse box
        | t when t = typeof<int option>            -> tryP Int32.TryParse (Some >> box)
        | t when t = typeof<int64>                 -> tryP Int64.TryParse box
        | t when t = typeof<int64 option>          -> tryP Int64.TryParse (Some >> box)
        | t when t = typeof<float>                 -> tryP Double.TryParse box
        | t when t = typeof<float option>          -> tryP Double.TryParse (Some >> box)
        | t when t = typeof<Guid>                  -> tryP Guid.TryParse box
        | t when t = typeof<DateTime>              -> tryP DateTime.TryParse box
        | t when t = typeof<DateTime option>       -> tryP DateTime.TryParse (Some >> box)
        | t when t = typeof<DateTimeOffset>        -> tryP DateTimeOffset.TryParse box
        | t when t = typeof<DateTimeOffset option> -> tryP DateTimeOffset.TryParse (Some >> box)
        | t when t = typeof<TimeSpan> ->
            match tryParseIsoDuration value with
            | Some v -> set (box v)
            | None   -> tryP TimeSpan.TryParse box
        | t when t = typeof<TimeSpan option> ->
            match tryParseIsoDuration value with
            | Some v -> set (box (Some v))
            | None   -> tryP TimeSpan.TryParse (Some >> box)
        | t when t.IsEnum ->
            try set (Enum.Parse(t, value)) with _ -> None
        | _ -> None

    // ── elementsToProps ──────────────────────────────────────────────────────

    /// SubmodelElementList → ResizeArray<string> 복원
    let private trySetResizeArray (pi: PropertyInfo) (sml: SubmodelElementList) (target: obj) : unit option =
        let propType = pi.PropertyType
        if propType.IsGenericType
           && propType.GetGenericTypeDefinition() = typedefof<ResizeArray<_>> then
            let elemType = propType.GetGenericArguments().[0]
            if elemType = typeof<string> then
                let list = pi.GetValue(target) :?> ResizeArray<string>
                if sml.Value <> null then
                    for elem in sml.Value do
                        match elem with
                        | :? Property as p when p.Value <> null -> list.Add(p.Value)
                        | _ -> ()
                Some ()
            elif elemType = typeof<Guid> then
                let list = pi.GetValue(target) :?> ResizeArray<Guid>
                if sml.Value <> null then
                    for elem in sml.Value do
                        match elem with
                        | :? Property as p when p.Value <> null ->
                            match Guid.TryParse(p.Value) with
                            | true, g -> list.Add(g)
                            | _ -> ()
                        | _ -> ()
                Some ()
            else None
        else None

    let internal elementsToProps<'T when 'T : (new : unit -> 'T)> (smc: SubmodelElementCollection) : 'T option =
        try
            let instance = new 'T()
            let props = typeof<'T>.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            if smc.Value <> null then
                for pi in props do
                    smc.Value
                    |> Seq.tryPick (function
                        | :? Property as p when p.IdShort = pi.Name && p.Value <> null ->
                            try trySetValue pi.PropertyType p.Value pi instance
                            with ex -> log.Warn($"Property 역직렬화 실패: {pi.Name} ({ex.Message})"); None
                        | :? SubmodelElementList as sml when sml.IdShort = pi.Name ->
                            try trySetResizeArray pi sml instance
                            with ex -> log.Warn($"SubmodelElementList 역직렬화 실패: {pi.Name} ({ex.Message})"); None
                        | _ -> None)
                    |> ignore
            Some instance
        with ex ->
            log.Warn($"elementsToProps 실패: {ex.Message}", ex); None

    // ── Arrow 변환 ───────────────────────────────────────────────────────────

    let smcToArrow<'T when 'T :> DsArrow> label (smc: SubmodelElementCollection) parentId
                                          (ctor: Guid -> Guid -> Guid -> ArrowType -> 'T) : 'T option =
        try
            match getProp smc "Source" |> Option.map Guid.Parse,
                  getProp smc "Target" |> Option.map Guid.Parse with
            | Some sourceId, Some targetId ->
                let arrow = ctor parentId sourceId targetId
                                (getProp smc "Type" |> Option.map parseArrowType |> Option.defaultValue ArrowType.Unspecified)
                arrow.Id <- getProp smc "Guid" |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
                Some arrow
            | _ -> log.Warn($"{label}: Source 또는 Target 누락"); None
        with ex -> log.Warn($"{label} 실패: {ex.Message}", ex); None

    let smcToArrowCall smc workId   = smcToArrow "smcToArrowCall" smc workId   (fun p s t a -> ArrowBetweenCalls(p, s, t, a))
    let smcToArrowWork smc systemId = smcToArrow "smcToArrowWork" smc systemId (fun p s t a -> ArrowBetweenWorks(p, s, t, a))
