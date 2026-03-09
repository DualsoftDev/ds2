namespace Ds2.Core

open System.Text.Json
open System.Text.Json.Serialization

module JsonOptions =

    let private addFSharpConverter (options: JsonSerializerOptions) =
        options.Converters.Add(
            JsonFSharpConverter(
                JsonFSharpOptions.Default().WithIncludeRecordProperties(true)))
        options

    /// 파일 저장/로드용 옵션 — camelCase, null 필드 제외, pretty-print
    let createProjectSerializationOptions () =
        let o = JsonSerializerOptions()
        o.WriteIndented               <- true
        o.PropertyNamingPolicy        <- JsonNamingPolicy.CamelCase
        o.PropertyNameCaseInsensitive <- true
        o.DefaultIgnoreCondition      <- JsonIgnoreCondition.WhenWritingNull
        o.IncludeFields               <- true
        addFSharpConverter o

    /// DeepCopy / Undo 백업용 옵션 — 최소 설정, 성능 우선
    let createDeepCopyOptions () =
        addFSharpConverter (JsonSerializerOptions())
