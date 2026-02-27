namespace Ds2.Core

open System.Text.Json
open System.Text.Json.Serialization

module JsonOptions =

    let private addFSharpConverter (options: JsonSerializerOptions) =
        let fsharpOptions =
            JsonFSharpOptions
                .Default()
                .WithIncludeRecordProperties(true)
        options.Converters.Add(JsonFSharpConverter(fsharpOptions))
        options

    let createProjectSerializationOptions () =
        let options = JsonSerializerOptions()
        options.WriteIndented <- true
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.PropertyNameCaseInsensitive <- true
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        options.IncludeFields <- true
        addFSharpConverter options

    let createDeepCopyOptions () =
        let options = JsonSerializerOptions()
        addFSharpConverter options