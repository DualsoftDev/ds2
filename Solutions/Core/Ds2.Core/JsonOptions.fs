namespace Ds2.Core

open System.Text.Json
open System.Text.Json.Serialization

module JsonOptions =

    let private addFSharpConverters (options: JsonSerializerOptions) =
        // 순서 중요: 구체적인 것(option, list, tuple, record)을 먼저, 범용 DU는 마지막
        options.Converters.Add(FSharpOptionConverterFactory())
        options.Converters.Add(FSharpListConverterFactory())
        options.Converters.Add(FSharpTupleConverterFactory())
        options.Converters.Add(FSharpRecordConverterFactory())
        options.Converters.Add(FSharpUnionConverterFactory())
        options

    /// 파일 저장/로드용 옵션 — camelCase, null 필드 제외, pretty-print
    let createProjectSerializationOptions () =
        let o = JsonSerializerOptions()
        o.WriteIndented               <- true
        o.PropertyNamingPolicy        <- JsonNamingPolicy.CamelCase
        o.PropertyNameCaseInsensitive <- true
        o.DefaultIgnoreCondition      <- JsonIgnoreCondition.WhenWritingNull
        o.IncludeFields               <- true
        addFSharpConverters o

    /// DeepCopy / Undo 백업용 옵션 — 최소 설정, 성능 우선
    let createDeepCopyOptions () =
        addFSharpConverters (JsonSerializerOptions())
