namespace Ds2.Core.Store

open System

/// TokenSpec 필드 파싱/직렬화, Duration 필터, IO 태그 생성
module Format =

    /// "key=value, ..." 형식의 텍스트를 Map으로 파싱
    let parseTokenSpecFields (text: string) : Map<string, string> =
        if String.IsNullOrWhiteSpace(text) then Map.empty
        else
            text.Split(',')
            |> Array.choose (fun p ->
                match p.Trim().Split('=', 2) with
                | [| k; v |] when not (String.IsNullOrWhiteSpace(k)) -> Some (k.Trim(), v.Trim())
                | _ -> None)
            |> Map.ofArray

    /// Map을 "key=value, ..." 형식으로 직렬화
    let formatTokenSpecFields (fields: Map<string, string>) : string =
        fields |> Map.toSeq |> Seq.map (fun (k, v) -> $"{k}={v}") |> String.concat ", "

    /// Duration 값을 비교 연산자 필터로 매칭 (>=, <=, >, <, =, 숫자, 부분 문자열)
    let matchDurationFilter (durationStr: string) (filter: string) : bool =
        let filter = filter.Trim()
        if String.IsNullOrEmpty(filter) then true
        else
            match Int32.TryParse(durationStr) with
            | false, _ -> durationStr.Contains(filter, StringComparison.OrdinalIgnoreCase)
            | true, value ->
                let tryOp (prefix: string) (op: int -> int -> bool) =
                    if filter.StartsWith(prefix) then
                        match Int32.TryParse(filter.[prefix.Length..]) with
                        | true, n -> Some (op value n)
                        | _ -> None
                    else None
                let ops = [ ">=", (>=); "<=", (<=); ">", (>); "<", (<); "=", (=) ]
                match ops |> List.tryPick (fun (p, op) -> tryOp p op) with
                | Some r -> r
                | None ->
                    match Int32.TryParse(filter) with
                    | true, exact -> value = exact
                    | _ -> durationStr.Contains(filter, StringComparison.OrdinalIgnoreCase)

    /// IO 태그 패턴 치환: $(F)=Flow, $(D)=Device, $(A)=Api
    let expandTagPattern (pattern: string) (flow: string) (device: string) (api: string) : string =
        pattern.Replace("$(F)", flow).Replace("$(D)", device).Replace("$(A)", api)

    /// PLC 주소 할당 결과
    type AddressAllocation = { Address: string; NextWord: int; NextBit: int }

    /// PLC 주소 할당: BOOL이면 비트 주소 (워드.비트), 그 외는 워드 주소
    let allocatePlcAddress (prefix: string) (currentWord: int) (currentBit: int) (dataType: string) : AddressAllocation =
        if dataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase) then
            let addr = $"{prefix}{currentWord}.{currentBit}"
            let nextBit = currentBit + 1
            if nextBit >= 16 then { Address = addr; NextWord = currentWord + 1; NextBit = 0 }
            else { Address = addr; NextWord = currentWord; NextBit = nextBit }
        else
            { Address = $"{prefix}{currentWord}"; NextWord = currentWord + 1; NextBit = 0 }
