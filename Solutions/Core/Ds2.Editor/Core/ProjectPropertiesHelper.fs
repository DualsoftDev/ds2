namespace Ds2.Editor

open System
open Ds2.Core

// =============================================================================
// ProjectProperties 헬퍼 모듈
// =============================================================================
[<RequireQualifiedAccess>]
module ProjectPropertiesHelper =

    /// Storage에서 배열로 변환 (개행 문자로 구분)
    let getPresetSystemTypes (props: ProjectProperties) : string[] =
        match props.PresetSystemTypesStorage with
        | None | Some "" -> [||]
        | Some s ->
            s.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun x -> x.Trim())

    /// 배열을 Storage에 저장 (개행 문자로 구분)
    let setPresetSystemTypes (props: ProjectProperties) (value: string[]) : unit =
        props.PresetSystemTypesStorage <-
            if Array.isEmpty value then None
            else Some(String.concat "\n" value)

    /// 프리셋 이름으로 SystemType 조회
    let getSystemTypeForPreset (presetName: string) (props: ProjectProperties) : string option =
        getPresetSystemTypes props
        |> Array.tryFind (fun entry -> entry.StartsWith(presetName + ":"))
        |> Option.map (fun entry ->
            let parts = entry.Split(':')
            if parts.Length >= 2 then parts.[1] else "ROBOT")

    /// 프리셋의 SystemType 설정 (불변 방식, 새로운 배열 반환)
    let setSystemTypeForPreset (presetName: string) (systemType: string) (props: ProjectProperties) : string[] =
        let entries = getPresetSystemTypes props
        entries
        |> Array.filter (fun e -> not (e.StartsWith(presetName + ":")))
        |> Array.append [| $"{presetName}:{systemType}" |]
