namespace Ds2.Core

open System
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection

// ── FSharpOption<T> ──────────────────────────────────────────────

/// Some x → x, None → null
type FSharpOptionConverter<'T>() =
    inherit JsonConverter<'T option>()

    override _.Read(reader, _typeToConvert, options) =
        if reader.TokenType = JsonTokenType.Null then None
        else Some(JsonSerializer.Deserialize<'T>(&reader, options))

    override _.Write(writer, value, options) =
        match value with
        | None -> writer.WriteNullValue()
        | Some v -> JsonSerializer.Serialize(writer, v, options)

type FSharpOptionConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ option>

    override _.CreateConverter(t, _options) =
        let innerType = t.GetGenericArguments().[0]
        let converterType = typedefof<FSharpOptionConverter<_>>.MakeGenericType(innerType)
        Activator.CreateInstance(converterType) :?> JsonConverter

// ── FSharpList<T> ────────────────────────────────────────────────

/// F# list ↔ JSON array
type FSharpListConverter<'T>() =
    inherit JsonConverter<'T list>()

    override _.Read(reader, _typeToConvert, options) =
        let arr = JsonSerializer.Deserialize<'T[]>(&reader, options)
        if isNull (box arr) then [] else arr |> Array.toList

    override _.Write(writer, value, options) =
        JsonSerializer.Serialize(writer, (value |> List.toArray), options)

type FSharpListConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ list>

    override _.CreateConverter(t, _options) =
        let innerType = t.GetGenericArguments().[0]
        let converterType = typedefof<FSharpListConverter<_>>.MakeGenericType(innerType)
        Activator.CreateInstance(converterType) :?> JsonConverter

// ── F# DU (AdjacentTag: {"Case":"...","Fields":[...]}) ──────────

/// 범용 F# Discriminated Union 컨버터 (AdjacentTag 포맷)
/// - 필드 없는 케이스: {"Case":"CaseName"}
/// - 필드 있는 케이스: {"Case":"CaseName","Fields":[field1, field2, ...]}
type FSharpUnionConverter<'T>() =
    inherit JsonConverter<'T>()

    static let unionType = typeof<'T>
    static let cases = FSharpType.GetUnionCases(unionType)

    override _.Read(reader, _typeToConvert, options) =
        if reader.TokenType = JsonTokenType.StartObject then
            let mutable caseName = ""
            let mutable fieldsElement: JsonElement option = None

            use doc = JsonDocument.ParseValue(&reader)
            let root = doc.RootElement

            match root.TryGetProperty("Case") with
            | true, prop -> caseName <- prop.GetString()
            | _ -> ()

            match root.TryGetProperty("Fields") with
            | true, prop -> fieldsElement <- Some prop
            | _ -> ()

            let case = cases |> Array.find (fun c -> c.Name = caseName)
            let fieldInfos = case.GetFields()

            if fieldInfos.Length = 0 then
                FSharpValue.MakeUnion(case, [||]) :?> 'T
            else
                let fields =
                    match fieldsElement with
                    | Some elem ->
                        fieldInfos |> Array.mapi (fun i fi ->
                            JsonSerializer.Deserialize(elem.[i].GetRawText(), fi.PropertyType, options))
                    | None -> [||]
                FSharpValue.MakeUnion(case, fields) :?> 'T
        else
            failwithf "Expected StartObject for DU, got %A" reader.TokenType

    override _.Write(writer, value, options) =
        let case, fields = FSharpValue.GetUnionFields(value, unionType)
        writer.WriteStartObject()
        writer.WriteString("Case", case.Name)
        if fields.Length > 0 then
            writer.WritePropertyName("Fields")
            writer.WriteStartArray()
            for field in fields do
                JsonSerializer.Serialize(writer, field, options)
            writer.WriteEndArray()
        writer.WriteEndObject()

type FSharpUnionConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) =
        FSharpType.IsUnion(t, true)
        && not (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ option>)
        && not (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ list>)

    override _.CreateConverter(t, _options) =
        let converterType = typedefof<FSharpUnionConverter<_>>.MakeGenericType(t)
        Activator.CreateInstance(converterType) :?> JsonConverter

// ── F# Tuple (JSON array) ───────────────────────────────────────

/// F# tuple ↔ JSON array: (a, b) → [a, b]
type FSharpTupleConverter<'T>() =
    inherit JsonConverter<'T>()

    static let tupleType = typeof<'T>
    static let elementTypes = FSharpType.GetTupleElements(tupleType)

    override _.Read(reader, _typeToConvert, options) =
        use doc = JsonDocument.ParseValue(&reader)
        let arr = doc.RootElement
        let values =
            elementTypes |> Array.mapi (fun i t ->
                JsonSerializer.Deserialize(arr.[i].GetRawText(), t, options))
        FSharpValue.MakeTuple(values, tupleType) :?> 'T

    override _.Write(writer, value, options) =
        let values = FSharpValue.GetTupleFields(value)
        writer.WriteStartArray()
        for v in values do
            JsonSerializer.Serialize(writer, v, options)
        writer.WriteEndArray()

type FSharpTupleConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) = FSharpType.IsTuple(t)

    override _.CreateConverter(t, _options) =
        let converterType = typedefof<FSharpTupleConverter<_>>.MakeGenericType(t)
        Activator.CreateInstance(converterType) :?> JsonConverter

// ── F# Record (프로퍼티 기반 직렬화) ────────────────────────────

/// F# record ↔ JSON object (프로퍼티 이름으로 직렬화)
type FSharpRecordConverter<'T>() =
    inherit JsonConverter<'T>()

    static let recordType = typeof<'T>
    static let fields = FSharpType.GetRecordFields(recordType, true)

    override _.Read(reader, _typeToConvert, options) =
        use doc = JsonDocument.ParseValue(&reader)
        let root = doc.RootElement
        let values =
            fields |> Array.map (fun fi ->
                let propName =
                    match options.PropertyNamingPolicy with
                    | null -> fi.Name
                    | policy -> policy.ConvertName(fi.Name)
                match root.TryGetProperty(propName) with
                | true, elem -> JsonSerializer.Deserialize(elem.GetRawText(), fi.PropertyType, options)
                | _ ->
                    // camelCase 안 됐을 수 있으니 원래 이름도 시도
                    match root.TryGetProperty(fi.Name) with
                    | true, elem -> JsonSerializer.Deserialize(elem.GetRawText(), fi.PropertyType, options)
                    | _ ->
                        if fi.PropertyType.IsGenericType
                           && fi.PropertyType.GetGenericTypeDefinition() = typedefof<_ option>
                        then null // option None
                        else JsonSerializer.Deserialize("null", fi.PropertyType, options))
        FSharpValue.MakeRecord(recordType, values, true) :?> 'T

    override _.Write(writer, value, options) =
        writer.WriteStartObject()
        for fi in fields do
            let propName =
                match options.PropertyNamingPolicy with
                | null -> fi.Name
                | policy -> policy.ConvertName(fi.Name)
            let fieldValue = fi.GetValue(value)
            // WhenWritingNull 설정이면 null 필드 건너뛰기
            if options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
               && isNull fieldValue then ()
            else
                writer.WritePropertyName(propName)
                JsonSerializer.Serialize(writer, fieldValue, fi.PropertyType, options)
        writer.WriteEndObject()

type FSharpRecordConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) = FSharpType.IsRecord(t, true)

    override _.CreateConverter(t, _options) =
        let converterType = typedefof<FSharpRecordConverter<_>>.MakeGenericType(t)
        Activator.CreateInstance(converterType) :?> JsonConverter
