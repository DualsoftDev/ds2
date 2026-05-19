namespace Ds2.Editor

open System
open System.Text.RegularExpressions

/// <summary>
/// TagWizard Step 1 (Flow/System Base 주소) 의 *기준 주소 파싱* 과 *자동 할당 룰* 결정.
/// 외부 DLL (AAStoPLC.TagWizard) 의존 없는 순수 함수만 모았다 — C# 측은 본 모듈에 위임 후
/// FBTagMapPresetDto / FBBaseAddressSet 같은 DTO mapping 만 처리.
/// </summary>
module TagWizardBaseAddress =

    /// <summary>"%IW1234.0.0" / "4000" 등에서 첫 정수를 추출.
    /// FBBaseAddressSet 의 string base 표기 (실제 정수 부분) 를 풀어내는 단일 source.</summary>
    [<CompiledName("TryParseFirstNumeric")>]
    let tryParseFirstNumeric (text: string) : int option =
        if String.IsNullOrWhiteSpace text then None
        else
            let m = Regex.Match(text, @"\d+")
            if not m.Success then None
            else
                match Int32.TryParse m.Value with
                | true, n -> Some n
                | _ -> None

    /// <summary>Flow 별 IW/QW/MW base 의 기존 설정 (parse 결과). 없는 값은 None.</summary>
    type FlowBaseExisting = {
        IwBase: int option
        QwBase: int option
        MwBase: int option
    }

    /// <summary>FlowBase 자동 할당 결과 — IW/QW/MW 모두 동일 base 값 (1000 단위 incremental).</summary>
    type FlowBaseAssignment = {
        FlowName: string
        IwBase: string
        QwBase: string
        MwBase: string
    }

    /// <summary>Flow 이름 순서대로 자동 base 할당.
    /// 기존 설정이 있으면 그 값을, 없으면 (index * 1000) 을 IW/QW/MW 동일하게 사용.
    /// LoadFlowBase 의 그리드 초기 채움 룰 단일 source.</summary>
    [<CompiledName("AssignDefaultFlowBases")>]
    let assignDefaultFlowBases
            (flowNames: string seq)
            (existing: System.Collections.Generic.IReadOnlyDictionary<string, FlowBaseExisting>)
            : FlowBaseAssignment list =
        flowNames
        |> Seq.mapi (fun i name ->
            let key = if isNull name then "" else name
            let fallback = i * 1000
            let mutable cfg = Unchecked.defaultof<FlowBaseExisting>
            let hit = not (isNull (box existing)) && existing.TryGetValue(key, &cfg)
            let formatOr (v: int option) (fb: int) =
                match v with
                | Some n -> string n
                | None -> string fb
            if hit then
                { FlowName = key
                  IwBase = formatOr cfg.IwBase fallback
                  QwBase = formatOr cfg.QwBase fallback
                  MwBase = formatOr cfg.MwBase fallback }
            else
                let s = string fallback
                { FlowName = key; IwBase = s; QwBase = s; MwBase = s })
        |> List.ofSeq
