namespace Ds2.Aasx

open System
open System.IO
open System.IO.Compression
open Ds2.Core
open Ds2.Core.Store

/// Public facade for converting PLC pipeline Ds2Csv output to AASX bytes.
/// Called from PlantDoctorAI.ReverseAI (C#) — does not expose Ds2.Core internal types.
/// Uses InternalsVisibleTo("Ds2.Aasx") to access internal Ds2.Core constructors.
module PlcAasxFacade =

    // ── CSV 파싱 ──────────────────────────────────────────────────────────────

    [<Struct>]
    type private Row =
        { Flow: string; Work: string; Device: string; System: string
          Api: string; InName: string; InAddress: string; OutName: string; OutAddress: string }

    let private csvUnesc (v: string) =
        if v.Length >= 2 && v.[0] = '"' && v.[v.Length - 1] = '"'
        then v.[1..v.Length - 2].Replace("\"\"", "\"")
        else v

    let private splitCsvLine (line: string) : string[] =
        let fields = System.Collections.Generic.List<string>()
        let cur = System.Text.StringBuilder()
        let mutable inQ = false
        for c in line do
            if c = '"' then
                inQ <- not inQ
                cur.Append(c) |> ignore
            elif c = ',' && not inQ then
                fields.Add(csvUnesc (cur.ToString()))
                cur.Clear() |> ignore
            else
                cur.Append(c) |> ignore
        fields.Add(csvUnesc (cur.ToString()))
        fields.ToArray()

    let private parseDs2Csv (csv: string) : Row list =
        if String.IsNullOrWhiteSpace(csv) then []
        else
            let lines = csv.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            [| for i in 1 .. lines.Length - 1 do
                   let cols = splitCsvLine lines.[i]
                   if cols.Length >= 9 then
                       let r =
                           { Flow = cols.[0]; Work = cols.[1]; Device = cols.[2]; System = cols.[3]
                             Api = cols.[4]; InName = cols.[5]; InAddress = cols.[6]
                             OutName = cols.[7]; OutAddress = cols.[8] }
                       // BUFFER/CLEAR sentinel 제거
                       if r.Device <> "BUFFER" && r.Device <> "CLEAR" then
                           yield r |]
            |> Array.toList

    // ── DsStore 구축 ──────────────────────────────────────────────────────────
    // Promaker 구조 일치: active system 1개(sampleName) + passive device 다수.
    // SYSTEM 컬럼 값이 여러 개여도 모두 단일 active system 아래 flow 로 귀속.
    // flow 이름 충돌 방지: 서로 다른 SYSTEM 값이 같은 flow 이름을 가지면 "SYS_FLOW" 로 한정.

    let private buildStore (sampleName: string) (rows: Row list) : DsStore * Project =
        let store = DsStore()

        let project = Project(sampleName)
        store.DirectWrite(store.Projects, project)

        // ── Active system: 1개(sampleName) ──────────────────────────────────
        let activeSystem = DsSystem(sampleName)
        activeSystem.SystemType <- Some "Cylinder_1"
        store.DirectWrite(store.Systems, activeSystem)
        project.ActiveSystemIds.Add(activeSystem.Id)

        // ── Passive device registry (Device 컬럼) ──────────────────────────
        let deviceReg = System.Collections.Generic.Dictionary<string, DsSystem>(StringComparer.OrdinalIgnoreCase)

        let getOrCreateDevice name =
            match deviceReg.TryGetValue(name) with
            | true, s -> s
            | _ ->
                let s = DsSystem(name)
                s.SystemType <- Some "Cylinder_1"
                store.DirectWrite(store.Systems, s)
                deviceReg.[name] <- s
                project.PassiveSystemIds.Add(s.Id)
                s

        rows
        |> List.map (fun r -> r.Device)
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.distinct
        |> List.iter (fun n -> getOrCreateDevice n |> ignore)

        // ── Flow registry — (sysName * flowName) → Flow (active system 아래) ─
        // 동일 flowName 이 여러 SYSTEM 에 존재할 수 있으므로 key 는 (sys,flow) 쌍.
        // flow 이름은 sys 가 비어있지 않으면 "SYS_FLOW" 로 한정.
        let flowReg = System.Collections.Generic.Dictionary<string * string, Flow>()

        let getOrCreateFlow sysName flowName =
            if String.IsNullOrWhiteSpace flowName then None
            else
                let key = (sysName, flowName)
                match flowReg.TryGetValue(key) with
                | true, f -> Some f
                | _ ->
                    let qualName =
                        if String.IsNullOrWhiteSpace sysName then flowName
                        else $"{sysName}_{flowName}"
                    let f = Flow(qualName, activeSystem.Id)
                    store.DirectWrite(store.Flows, f)
                    flowReg.[key] <- f
                    Some f

        // ── Work registry ──────────────────────────────────────────────────
        let workReg = System.Collections.Generic.Dictionary<string * string * string, Work>()

        let getOrCreateWork sysName flowName workName =
            if String.IsNullOrWhiteSpace workName then None
            else
                let key = (sysName, flowName, workName)
                match workReg.TryGetValue(key) with
                | true, w -> Some w
                | _ ->
                    match getOrCreateFlow sysName flowName with
                    | None -> None
                    | Some flow ->
                        let w = Work(flowName, workName, flow.Id)
                        store.DirectWrite(store.Works, w)
                        workReg.[key] <- w
                        Some w

        // ── ApiDef registry — (deviceName * apiName) → ApiDef ─────────────
        let apiDefReg = System.Collections.Generic.Dictionary<string * string, ApiDef>()

        let getOrCreateApiDef deviceName apiName =
            if String.IsNullOrWhiteSpace deviceName || String.IsNullOrWhiteSpace apiName then None
            else
                let key = (deviceName, apiName)
                match apiDefReg.TryGetValue(key) with
                | true, d -> Some d
                | _ ->
                    match deviceReg.TryGetValue(deviceName) with
                    | false, _ -> None
                    | true, dev ->
                        let d = ApiDef(apiName, dev.Id)
                        store.DirectWrite(store.ApiDefs, d)
                        apiDefReg.[key] <- d
                        Some d

        // Flow/Work 존재 보장
        for r in rows do
            getOrCreateFlow r.System r.Flow |> ignore
            getOrCreateWork r.System r.Flow r.Work |> ignore

        // Call + ApiCall 생성
        for r in rows do
            if not (String.IsNullOrWhiteSpace r.Api) then
                getOrCreateWork r.System r.Flow r.Work
                |> Option.iter (fun work ->
                    let apiDefOpt = getOrCreateApiDef r.Device r.Api
                    let call = Call(r.Device, r.Api, work.Id)
                    let apiCall = ApiCall(r.Api)
                    if not (String.IsNullOrWhiteSpace r.InName) || not (String.IsNullOrWhiteSpace r.InAddress) then
                        apiCall.InTag <- Some (IOTag(r.InName, r.InAddress, ""))
                    if not (String.IsNullOrWhiteSpace r.OutName) || not (String.IsNullOrWhiteSpace r.OutAddress) then
                        apiCall.OutTag <- Some (IOTag(r.OutName, r.OutAddress, ""))
                    apiDefOpt |> Option.iter (fun def -> apiCall.ApiDefId <- Some def.Id)
                    call.ApiCalls.Add(apiCall)
                    store.DirectWrite(store.Calls, call))

        store.RebuildApiCallsDictionary()
        (store, project)

    // ── 공개 엔트리 포인트 ────────────────────────────────────────────────────

    /// <summary>
    /// Ds2Csv 문자열 + 샘플명으로 AASX bytes 생성.
    /// PlantDoctorAI.ReverseAI (C#) 에서 직접 호출하는 public API.
    /// </summary>
    /// <returns>AASX ZIP bytes, 또는 변환 실패 시 null</returns>
    [<CompiledName("ExportDs2CsvToAasxBytes")>]
    let exportDs2CsvToAasxBytes (sampleName: string) (ds2Csv: string) (iriPrefix: string) : byte[] =
        try
            let rows = parseDs2Csv ds2Csv
            if rows.IsEmpty then
                failwithf "Ds2CSV 데이터 없음 (csv.Length=%d) — 파이프라인 결과를 확인하세요" ds2Csv.Length
            else
                let store, project = buildStore sampleName rows
                let tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aasx")
                try
                    AasxExporter.exportToAasxFile
                        store project
                        iriPrefix
                        tempPath
                        false
                    File.ReadAllBytes(tempPath)
                finally
                    if File.Exists(tempPath) then File.Delete(tempPath)
        with ex ->
            eprintfn "[PlcAasxFacade] exportDs2CsvToAasxBytes failed: %s" ex.Message
            null

    // ── 디바이스 분할 ZIP 내보내기 ────────────────────────────────────────────

    let private exportDeviceToZip (store: DsStore) (project: Project) (device: DsSystem) (iriPrefix: string) (folder: string) (zip: ZipArchive) =
        let tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aasx")
        try
            AasxExporter.exportDeviceAasx store project device iriPrefix tempPath
            let bytes = File.ReadAllBytes(tempPath)
            let safeName = String.concat "_" (device.Name.Split(Path.GetInvalidFileNameChars()))
            let entry = zip.CreateEntry($"{folder}/{safeName}.aasx")
            use stream = entry.Open()
            stream.Write(bytes, 0, bytes.Length)
        finally
            if File.Exists(tempPath) then File.Delete(tempPath)

    /// <summary>
    /// Ds2Csv → 디바이스별 AASX 분할 ZIP bytes 생성.
    /// ZIP 구조: active/{device}.aasx, passive/{device}.aasx
    /// </summary>
    [<CompiledName("ExportDs2CsvToDevicesZipBytes")>]
    let exportDs2CsvToDevicesZipBytes (sampleName: string) (ds2Csv: string) (iriPrefix: string) : byte[] =
        try
            let rows = parseDs2Csv ds2Csv
            if rows.IsEmpty then
                failwithf "Ds2CSV 데이터 없음 (csv.Length=%d) — 파이프라인 결과를 확인하세요" ds2Csv.Length
            let store, project = buildStore sampleName rows
            let ms = new MemoryStream()
            let zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen = true)
            try
                let getSystem id =
                    match store.Systems.TryGetValue(id) with
                    | true, s -> Some s
                    | _ -> None
                // active/ → 단일 active system AASX (1개)
                project.ActiveSystemIds
                |> Seq.choose getSystem
                |> Seq.iter (fun s -> exportDeviceToZip store project s iriPrefix "active" zip)
                // passive/ → Device별 AASX
                project.PassiveSystemIds
                |> Seq.choose getSystem
                |> Seq.iter (fun s -> exportDeviceToZip store project s iriPrefix "passive" zip)
            finally
                zip.Dispose()
            ms.ToArray()
        with ex ->
            eprintfn "[PlcAasxFacade] exportDs2CsvToDevicesZipBytes failed: %s" ex.Message
            null
