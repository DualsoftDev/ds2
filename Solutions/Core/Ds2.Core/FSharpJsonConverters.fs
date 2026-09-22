namespace Ds2.Core

open System
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection

// =============================================================================
// F# 타입(option/list/tuple/DU/record) ↔ System.Text.Json 컨버터.
//
// **성능 계약 — 리플렉션은 타입당 1회만.**
// record/DU/tuple 의 생성·분해를 `FSharpValue.MakeRecord` / `MakeUnion` /
// `GetUnionFields` / `PropertyInfo.GetValue` 로 하면 **호출마다** 리플렉션이 돌아
// 값 하나당 수십 µs 가 든다. 실측(AID 1.86MB): 직렬화 182ms / 역직렬화 367ms,
// 할당 88MB / 155MB. 이를 `FSharpValue.PreCompute*` 델리게이트를 `static let` 으로
// 타입당 1회만 만들어 재사용하도록 바꾸면 11ms / 102ms, 할당 6MB / 38MB 가 된다
// (직렬화 16×, 역직렬화 3.6×). 모델 저장 208→21ms, 열기 403→127ms.
// 와이어 포맷은 바이트 단위로 동일하다 — 여기를 손댈 때 이 계약을 깨지 말 것.
//
// 반대로 **JsonDocument 를 걷어내는 것은 이득이 없다**(실측 99ms vs 102ms).
// 남은 역직렬화 비용은 필드별 JsonSerializer.Deserialize 디스패치·박싱이라
// 구조를 크게 바꿔야 하고 수지가 맞지 않는다 — 시도했다가 되돌리지 않도록 적어 둔다.
// =============================================================================

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
    // 팩토리의 CanConvert 가 IsUnion(t, true) 로 private 표현 DU 까지 받으므로 여기도 true —
    // 빠뜨리면 private DU 에서 "전용 형식 표현" ArgumentException 으로 타입 초기화가 터진다.
    static let cases = FSharpType.GetUnionCases(unionType, true)
    // 케이스 배열의 인덱스 = union tag (GetUnionCases 는 tag 순으로 돌려준다).
    static let constructors = cases |> Array.map (fun c -> FSharpValue.PreComputeUnionConstructor(c, true))
    static let fieldReaders  = cases |> Array.map (fun c -> FSharpValue.PreComputeUnionReader(c, true))
    static let tagReader = FSharpValue.PreComputeUnionTagReader(unionType, true)

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

            if String.IsNullOrEmpty caseName then
                failwithf "DU 역직렬화: 'Case' 필드 누락/null (type %s)" unionType.Name

            let caseIndex =
                match cases |> Array.tryFindIndex (fun c -> c.Name = caseName) with
                | Some i -> i
                | None -> failwithf "Unknown union case '%s' for type %s" caseName unionType.Name
            let case = cases.[caseIndex]
            let fieldInfos = case.GetFields()

            if fieldInfos.Length = 0 then
                constructors.[caseIndex] [||] :?> 'T
            else
                let elem =
                    match fieldsElement with
                    | Some e -> e
                    | None ->
                        failwithf "DU 역직렬화: case '%s' 는 %d 개 필드가 필요하나 'Fields' 누락 (type %s)"
                            caseName fieldInfos.Length unionType.Name
                if elem.GetArrayLength() < fieldInfos.Length then
                    failwithf "DU 역직렬화: case '%s' 는 %d 개 필드가 필요하나 'Fields' 길이 %d (type %s)"
                        caseName fieldInfos.Length (elem.GetArrayLength()) unionType.Name
                let fields =
                    fieldInfos |> Array.mapi (fun i fi ->
                        JsonSerializer.Deserialize(elem.[i], fi.PropertyType, options))
                constructors.[caseIndex] fields :?> 'T
        else
            failwithf "Expected StartObject for DU, got %A" reader.TokenType

    override _.Write(writer, value, options) =
        let tag = tagReader (box value)
        let case = cases.[tag]
        let fields = fieldReaders.[tag] (box value)
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
    static let construct = FSharpValue.PreComputeTupleConstructor(tupleType)
    static let readElements = FSharpValue.PreComputeTupleReader(tupleType)

    override _.Read(reader, _typeToConvert, options) =
        use doc = JsonDocument.ParseValue(&reader)
        let arr = doc.RootElement
        if arr.GetArrayLength() < elementTypes.Length then
            failwithf "Tuple 역직렬화: %d 개 원소가 필요하나 JSON 배열 길이 %d (type %s)"
                elementTypes.Length (arr.GetArrayLength()) tupleType.Name
        let values =
            elementTypes |> Array.mapi (fun i t -> JsonSerializer.Deserialize(arr.[i], t, options))
        construct values :?> 'T

    override _.Write(writer, value, options) =
        let values = readElements (box value)
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
    static let construct = FSharpValue.PreComputeRecordConstructor(recordType, true)
    // 반환 배열 순서 = GetRecordFields 순서 (선언 순).
    static let readFields = FSharpValue.PreComputeRecordReader(recordType, true)

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
                | true, elem -> JsonSerializer.Deserialize(elem, fi.PropertyType, options)
                | _ ->
                    // camelCase 안 됐을 수 있으니 원래 이름도 시도
                    match root.TryGetProperty(fi.Name) with
                    | true, elem -> JsonSerializer.Deserialize(elem, fi.PropertyType, options)
                    | _ ->
                        if fi.PropertyType.IsGenericType
                           && fi.PropertyType.GetGenericTypeDefinition() = typedefof<_ option>
                        then null // option None
                        elif fi.PropertyType.IsValueType
                        then Activator.CreateInstance(fi.PropertyType) // M7: value-type 키 누락 → 타입 기본값 (option None 과 대칭)
                        else JsonSerializer.Deserialize("null", fi.PropertyType, options))
        construct values :?> 'T

    override _.Write(writer, value, options) =
        let values = readFields (box value)
        writer.WriteStartObject()
        fields |> Array.iteri (fun i fi ->
            let propName =
                match options.PropertyNamingPolicy with
                | null -> fi.Name
                | policy -> policy.ConvertName(fi.Name)
            let fieldValue = values.[i]
            // WhenWritingNull 설정이면 null 필드 건너뛰기
            if options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
               && isNull fieldValue then ()
            else
                writer.WritePropertyName(propName)
                JsonSerializer.Serialize(writer, fieldValue, fi.PropertyType, options))
        writer.WriteEndObject()

type FSharpRecordConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) = FSharpType.IsRecord(t, true)

    override _.CreateConverter(t, _options) =
        let converterType = typedefof<FSharpRecordConverter<_>>.MakeGenericType(t)
        Activator.CreateInstance(converterType) :?> JsonConverter
