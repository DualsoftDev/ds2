namespace Ds2.SymbolImport

open System

/// <summary>심볼명 → DS2 도메인 (Flow / Work / Device / Api) 매핑 규칙.
/// DS1 mapper 의 MappingApi / MappingDevice / MappingGroup 룰 의미 추출.</summary>
module MapperRules =

    /// 매칭 후보 — 심볼명 분해 결과.
    type ParsedSymbol = {
        Original: SymbolEntry
        /// 분해된 segment 들 (예: "Conv1_Adv_LMT" → ["Conv1"; "Adv"; "LMT"])
        Segments: string[]
    }

    /// 매칭 결과 — DS2 엔티티 4단계 매핑 + 신뢰도.
    type Mapping = {
        Original: SymbolEntry
        FlowName: string       // DS2 Flow (= DS1 Area)
        WorkName: string       // DS2 Work
        DeviceName: string     // DS2 passive System (Device 인터페이스)
        ApiName: string        // DS2 ApiDef name
        IsAmbiguous: bool      // 룰 다중 후보로 모호 매칭
    }

    /// 심볼명 segment 분리 — '_' 또는 '.' 기준. 빈 segment 제거.
    let private splitSymbol (name: string) : string[] =
        if String.IsNullOrEmpty name then [||]
        else
            name.Split([| '_'; '.' |], StringSplitOptions.RemoveEmptyEntries)

    let parseSymbol (entry: SymbolEntry) : ParsedSymbol =
        { Original = entry; Segments = splitSymbol entry.Name }

    /// DS1 PrefixTrie.findCommonPrefix 동등 — 같은 그룹 내 심볼들의 *공통 prefix segment* 추출.
    /// 그 prefix 가 Device 이름, 나머지 suffix 가 Api 이름.
    let findCommonPrefix (symbols: ParsedSymbol[]) : string[] =
        if symbols.Length = 0 then [||]
        else
            let first = symbols.[0].Segments
            let mutable common = first.Length
            for s in symbols do
                let len = min common s.Segments.Length
                let mutable i = 0
                let mutable matching = true
                while matching && i < len do
                    if String.Equals(s.Segments.[i], first.[i], StringComparison.OrdinalIgnoreCase) then
                        i <- i + 1
                    else
                        matching <- false
                common <- min common i
            first |> Array.take common

    /// DS1 룰 #1 — segment 패턴 매핑.
    ///   N segments        : [Flow ; Work; Device; Api]   (관례 4단계)
    ///   3 segments        : [Flow ; Work; Api]            — Device 는 첫 두 segment 결합
    ///   2 segments        : [Flow ; Api]                  — Device = Flow, Work = Flow
    ///   1 segment         : [Api]                         — Flow/Work/Device 모두 "Default"
    /// 모호한 경우 IsAmbiguous=true.
    let private inferFromSegments (segs: string[]) : Mapping option =
        match segs.Length with
        | 0 -> None
        | 1 -> Some {
            Original = Unchecked.defaultof<_>
            FlowName = "Default"
            WorkName = "Default"
            DeviceName = "Default"
            ApiName = segs.[0]
            IsAmbiguous = true }
        | 2 -> Some {
            Original = Unchecked.defaultof<_>
            FlowName = segs.[0]
            WorkName = segs.[0]
            DeviceName = segs.[0]
            ApiName = segs.[1]
            IsAmbiguous = false }
        | 3 -> Some {
            Original = Unchecked.defaultof<_>
            FlowName = segs.[0]
            WorkName = segs.[1]
            DeviceName = segs.[1]
            ApiName = segs.[2]
            IsAmbiguous = false }
        | _ -> Some {
            Original = Unchecked.defaultof<_>
            FlowName = segs.[0]
            WorkName = segs.[1]
            DeviceName = segs.[2]
            ApiName = String.concat "_" (segs |> Array.skip 3)
            IsAmbiguous = false }

    /// 단일 entry → Mapping. segment 기반 추론.
    let mapEntry (entry: SymbolEntry) : Mapping option =
        let parsed = parseSymbol entry
        inferFromSegments parsed.Segments
        |> Option.map (fun m -> { m with Original = entry })

    /// 전체 entry list → Mapping list. None 항목은 unmatched.
    let mapAll (entries: SymbolEntry list) : Mapping list * SymbolEntry list =
        let mapped = ResizeArray<Mapping>()
        let unmatched = ResizeArray<SymbolEntry>()
        for entry in entries do
            match mapEntry entry with
            | Some m -> mapped.Add m
            | None -> unmatched.Add entry
        List.ofSeq mapped, List.ofSeq unmatched
