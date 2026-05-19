namespace Ds2.Editor

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Ds2.Core.Store

/// <summary>
/// "ApiList:SystemType" 문자열 배열로 표현되는 SystemType 프리셋의
/// 파싱/병합/조회 결정 로직. file IO 와 JSON 역직렬화는 호출자(C#) 담당.
/// </summary>
module SystemTypePreset =

    type Entry = {
        SystemType: string
        ApiNames: string[]
    }

    let private cylinderPrefix = "Cylinder_"
    let private cylinderTemplate = "Cylinder_#"

    /// "Cylinder_5" → "Cylinder_#" 로 정규화. 그 외는 원본.
    let normalizeSystemTypeForDisplay (name: string) =
        if String.IsNullOrEmpty(name) then name
        elif name.StartsWith(cylinderPrefix, StringComparison.Ordinal) then
            let suffix = name.Substring(cylinderPrefix.Length)
            match Int32.TryParse(suffix) with
            | true, _ -> cylinderTemplate
            | _ -> name
        else name

    /// "Cylinder_#" → DevicePresets 에 등록된 "Cylinder_1".."Cylinder_N" 시퀀스.
    /// 그 외 이름은 원본 1개.
    let expandSystemTypeTemplate (name: string) : string seq =
        if String.Equals(name, cylinderTemplate, StringComparison.Ordinal) then
            DevicePresets.Entries()
            |> Seq.map (fun (model, _, _) -> model)
            |> Seq.filter (fun s ->
                s.StartsWith(cylinderPrefix, StringComparison.Ordinal)
                && (match Int32.TryParse(s.Substring(cylinderPrefix.Length)) with
                    | true, _ -> true
                    | _ -> false))
        else
            Seq.singleton name

    /// "ApiList:SystemType" 문자열을 파싱해 (SystemType, ApiNames) 시퀀스로.
    /// 마지막 ':' 기준 분리. ';' 또는 ',' 로 API 분리. 빈 항목/중복 SystemType 은 skip.
    let parseEntries (mappings: string seq) : Entry list =
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        [ for mapping in mappings do
            if not (String.IsNullOrWhiteSpace mapping) then
                let idx = mapping.LastIndexOf(':')
                if idx >= 0 then
                    let apiList = mapping.Substring(0, idx).Trim()
                    let systemType = mapping.Substring(idx + 1).Trim()
                    if not (String.IsNullOrWhiteSpace systemType) && seen.Add(systemType) then
                        let apis =
                            apiList.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.map (fun a -> a.Trim())
                            |> Array.filter (fun a -> not (String.IsNullOrWhiteSpace a))
                        yield { SystemType = systemType; ApiNames = apis } ]

    /// 사용자 저장본 + 기본값 합집합. 저장본 순서가 우선, SystemType 단위 dedup.
    let mergeWithDefaults (saved: string seq) (defaults: string seq) : string list =
        let savedList = saved |> List.ofSeq
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for m in savedList do
            let idx = m.LastIndexOf(':')
            if idx >= 0 then
                seen.Add(m.Substring(idx + 1).Trim()) |> ignore
        [ yield! savedList
          for d in defaults do
            let idx = d.LastIndexOf(':')
            let sysType = if idx >= 0 then d.Substring(idx + 1).Trim() else ""
            if not (String.IsNullOrEmpty sysType) && seen.Add(sysType) then
                yield d ]

    /// DevicePresets 의 (SystemType, ApiList) 를 "ApiList:SystemType" 문자열 배열로 변환.
    /// 같은 prefix 의 numbered 'Cylinder_N' 은 첫 등장 위치에서 단일 템플릿
    /// "ApiList:Cylinder_#" 으로 축약.
    let buildDefaultMappingStrings () : string list =
        let mutable cylinderEmitted = false
        [ for (model, apiList, _) in DevicePresets.Entries() do
            let api = if isNull apiList then "" else apiList
            if model.StartsWith(cylinderPrefix, StringComparison.Ordinal) then
                if not cylinderEmitted then
                    cylinderEmitted <- true
                    yield sprintf "%s:%s" api cylinderTemplate
            else
                yield sprintf "%s:%s" api model ]

    /// TagWizard 의 SystemType 목록 — 프로젝트 프리셋 + AASX preset key 합집합 (대소문자 무시 dedup).
    /// '#' 포함 템플릿 (예: "Cylinder_#") 은 AddCall 용이라 TagWizard 의 구체 FB 매핑 대상에서 제외.
    /// 결과는 OrdinalIgnoreCase 정렬.
    let mergeDeviceTemplateNames (presetTypes: string seq) (aasxKeys: string seq) : string list =
        let isTemplate (n: string) = not (String.IsNullOrEmpty n) && n.Contains '#'
        let set = System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        for t in presetTypes do
            if not (isTemplate t) then set.Add(t) |> ignore
        for k in aasxKeys do
            if not (isTemplate k) then set.Add(k) |> ignore
        set |> List.ofSeq

    /// 특정 SystemType 의 API 이름 목록.
    /// 1) entries 에서 정확 일치 우선
    /// 2) 없으면 'Cylinder_#' 같은 템플릿 entry 의 '#' 을 \d+ 로 매칭
    let lookupApiNames (entries: Entry list) (systemType: string) : string[] =
        if String.IsNullOrEmpty systemType then [||]
        else
            let exact =
                entries
                |> List.tryFind (fun e ->
                    String.Equals(e.SystemType, systemType, StringComparison.OrdinalIgnoreCase))
            match exact with
            | Some e when e.ApiNames.Length > 0 -> e.ApiNames
            | _ ->
                entries
                |> List.tryPick (fun e ->
                    if String.IsNullOrEmpty e.SystemType || not (e.SystemType.Contains '#') then
                        None
                    else
                        let pattern =
                            "^" + Regex.Escape(e.SystemType).Replace("\\#", @"\d+") + "$"
                        if Regex.IsMatch(systemType, pattern, RegexOptions.IgnoreCase) then
                            Some(if isNull e.ApiNames then [||] else e.ApiNames)
                        else None)
                |> Option.defaultValue [||]
