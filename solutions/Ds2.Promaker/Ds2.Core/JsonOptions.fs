namespace Ds2.Core

open System.Text.Json
open System.Text.Json.Serialization

module JsonOptions =

    type private JsonOptionsProfile =
        | ProjectSerialization
        | DeepCopy

    type private JsonOptionsConfig =
        { WriteIndented: bool
          PropertyNamingPolicy: JsonNamingPolicy option
          PropertyNameCaseInsensitive: bool
          DefaultIgnoreCondition: JsonIgnoreCondition option
          IncludeFields: bool }

    let private configFor profile =
        match profile with
        | ProjectSerialization ->
            { WriteIndented = true
              PropertyNamingPolicy = Some JsonNamingPolicy.CamelCase
              PropertyNameCaseInsensitive = true
              DefaultIgnoreCondition = Some JsonIgnoreCondition.WhenWritingNull
              IncludeFields = true }
        | DeepCopy ->
            { WriteIndented = false
              PropertyNamingPolicy = None
              PropertyNameCaseInsensitive = false
              DefaultIgnoreCondition = None
              IncludeFields = false }

    let private addFSharpConverter (options: JsonSerializerOptions) =
        let fsharpOptions =
            JsonFSharpOptions
                .Default()
                .WithIncludeRecordProperties(true)
        options.Converters.Add(JsonFSharpConverter(fsharpOptions))
        options

    let private createOptions profile =
        let config = configFor profile
        let options = JsonSerializerOptions()
        options.WriteIndented <- config.WriteIndented
        options.PropertyNameCaseInsensitive <- config.PropertyNameCaseInsensitive
        options.IncludeFields <- config.IncludeFields

        match config.PropertyNamingPolicy with
        | Some naming -> options.PropertyNamingPolicy <- naming
        | None -> ()

        match config.DefaultIgnoreCondition with
        | Some ignoreCondition -> options.DefaultIgnoreCondition <- ignoreCondition
        | None -> ()

        addFSharpConverter options

    let createProjectSerializationOptions () =
        createOptions ProjectSerialization

    let createDeepCopyOptions () =
        createOptions DeepCopy
