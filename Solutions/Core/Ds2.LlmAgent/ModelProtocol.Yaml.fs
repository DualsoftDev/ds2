namespace Ds2.LlmAgent

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open YamlDotNet.Core
open YamlDotNet.RepresentationModel

/// Phase 1 YAML protocol — yaml ↔ json 양방향 변환 helper.
///
/// **Wire = JSON object** (LLM tool_use native, escape 0).
/// **View = YAML** (디스크 SSOT / 사람 시야).
///
/// SSOT: `Apps/Promaker/Docs/yaml-protocol-v0.md` §2.0 (Parser subset) + §8 (Wire 결정).
///
/// 본 module 은 *얇은 transformer* — schema 의미는 `ModelProtocol.fs` 가 담당.
[<RequireQualifiedAccess>]
module ModelProtocolYaml =

    // ─── Parser subset 강제 (SSOT §2.0) ─────────────────────────────────────
    //
    // YAML 표준의 일부 feature 만 허용. anchor / tag / merge / multi-document /
    // YAML 1.1 boolean coercion 모두 거부. 동일 표현 다중 경로 (anchor) 와 type
    // coercion 표면 (custom tag) 을 사전 차단해 SSOT 의 의미 모호성을 0 으로.
    //
    // 거부는 *parse 시점* 에 수행 — 변환 후 lossy 검사보다 메시지 명확.

    let private VALIDATION_ERROR = "VALIDATION_ERROR"

    let private fail (msg: string) : 'T =
        invalidOp (sprintf "%s: %s" VALIDATION_ERROR msg)

    /// YAML 1.2 core schema 의 boolean literal 만 허용 (true/false/True/False/TRUE/FALSE).
    /// YAML 1.1 boolean coercion (yes/no/on/off/Yes/No/On/Off ...) 은 거부 — `device: on` 같은
    /// 의도치 않은 bool 변환 차단.
    let private yaml12Bools = Set.ofList [ "true"; "false"; "True"; "False"; "TRUE"; "FALSE" ]

    let private yaml11OnlyBools =
        Set.ofList [
            "yes"; "Yes"; "YES"; "no"; "No"; "NO"
            "on"; "On"; "ON"; "off"; "Off"; "OFF"
            // YAML 1.1 의 단축 boolean (PyYAML/libyaml 일부 구현) — defensive (m1).
            "y"; "Y"; "n"; "N"
        ]

    /// YAML null literal token — string 값이 이 set 에 들면 plain emit 시 null 로 오인되므로 quoted 강제.
    let private yamlNullTokens = Set.ofList [ "null"; "Null"; "NULL"; "~"; "" ]

    /// JSON string → YAML plain scalar 안전 검사 (Phase 2 §3.1 #4).
    /// ASCII identifier 패턴 = 첫 글자 [A-Za-z_], 이후 [A-Za-z0-9_\-]*. dotted-path ("Cyl1.ADV") /
    /// 공백 포함 ("A -> B : Start") / 숫자 시작 / 특수문자 모두 미매칭 → quoted.
    let private plainScalarSafeRegex = Regex(@"^[A-Za-z_][A-Za-z0-9_\-]*$", RegexOptions.Compiled)

    /// scalar 가 *unquoted* 인지 확인 — YamlScalarNode.Style 이 Plain 이면 unquoted.
    let private isPlainScalar (s: YamlScalarNode) : bool =
        s.Style = ScalarStyle.Plain

    /// YamlNode 안의 모든 anchor / tag / merge key / 1.1-only boolean coercion 를 사전 검증.
    /// YamlDotNet 는 anchor/tag 를 그대로 model 에 보존하므로 walk 로 검사.
    let private rejectForbiddenFeatures (node: YamlNode) : unit =
        let rec walk (n: YamlNode) =
            // Anchor 검사 — `&name` 정의 측. AnchorName 은 struct 이고 빈 값에 .Value 접근 시 throw.
            if not n.Anchor.IsEmpty then
                fail (sprintf "YAML anchor (&%s) 는 미지원. anchor/alias 사용 금지 (SSOT §2.0)." n.Anchor.Value)
            // Tag 검사 — YamlDotNet 가 자동 부여하는 implicit tag (`tag:yaml.org,2002:str` 등) 는 제외.
            // 사용자가 명시한 custom tag (`!foo`, `!!python/...`) 만 거부.
            if not n.Tag.IsEmpty then
                let tag = n.Tag.Value
                let isImplicit =
                    tag.StartsWith("tag:yaml.org,2002:")
                    || tag = "?"
                if not isImplicit then
                    fail (sprintf "YAML custom tag (%s) 는 미지원. !tag 사용 금지 (SSOT §2.0)." tag)
            match n with
            | :? YamlMappingNode as m ->
                for kv in m.Children do
                    // merge key `<<` 는 mapping 의 key 자리에 scalar 로 등장.
                    match kv.Key with
                    | :? YamlScalarNode as ks when ks.Value = "<<" ->
                        fail "YAML merge key (<<) 는 미지원 (SSOT §2.0)."
                    | _ -> ()
                    walk kv.Key
                    walk kv.Value
            | :? YamlSequenceNode as s ->
                for c in s.Children do walk c
            | :? YamlScalarNode as s ->
                // YAML 1.1 boolean coercion 차단 — unquoted (Plain) 이고 1.1-only literal 이면 거부.
                // quoted ("yes") 는 사용자 의도 string 이므로 허용.
                if isPlainScalar s && yaml11OnlyBools.Contains s.Value then
                    fail (sprintf "YAML 1.1 boolean coercion '%s' 는 미지원 (SSOT §2.0). 따옴표로 감싸세요 (예: \"%s\")." s.Value s.Value)
            | _ -> ()
        walk node

    // ─── YAML → JSON ─────────────────────────────────────────────────────────

    /// YamlScalarNode → JSON literal 문자열 변환.
    /// - Plain scalar 이고 숫자 형태 → number
    /// - Plain scalar 이고 yaml12 boolean → bool
    /// - Plain scalar "null" / "~" / 빈값 → null
    /// - 그 외 (quoted, 비-숫자 plain) → string
    /// SSOT §2.3 의 duration grammar 가 string 강제이므로 `"500ms"` 같은 quoted 는 string 유지.
    let private scalarToJsonLiteral (s: YamlScalarNode) : string =
        let raw = s.Value
        if isPlainScalar s then
            if String.IsNullOrEmpty raw || raw = "null" || raw = "~" || raw = "Null" || raw = "NULL" then
                "null"
            elif yaml12Bools.Contains raw then
                raw.ToLowerInvariant()
            else
                // 숫자 시도 — Int64 / Double parse 성공 시 number, 실패 시 string.
                let mutable iv = 0L
                let mutable dv = 0.
                if Int64.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, &iv) then
                    raw
                elif Double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, &dv) then
                    raw
                else
                    JsonSerializer.Serialize(raw)
        else
            // quoted / literal / folded — 항상 string.
            JsonSerializer.Serialize(raw)

    let private writeNodeAsJson (writer: Utf8JsonWriter) (node: YamlNode) : unit =
        let rec write (n: YamlNode) =
            match n with
            | :? YamlMappingNode as m ->
                writer.WriteStartObject()
                let seenKeys = System.Collections.Generic.HashSet<string>()
                for kv in m.Children do
                    let key =
                        match kv.Key with
                        | :? YamlScalarNode as ks -> ks.Value
                        | _ -> fail "YAML mapping key 는 scalar 만 허용."
                    if not (seenKeys.Add key) then
                        fail (sprintf "YAML duplicate map key '%s' (SSOT §2.0)." key)
                    writer.WritePropertyName key
                    write kv.Value
                writer.WriteEndObject()
            | :? YamlSequenceNode as s ->
                writer.WriteStartArray()
                for c in s.Children do write c
                writer.WriteEndArray()
            | :? YamlScalarNode as s ->
                let lit = scalarToJsonLiteral s
                use doc = JsonDocument.Parse(lit)
                doc.RootElement.WriteTo(writer)
            | other ->
                fail (sprintf "YAML node type %s 는 미지원." (other.GetType().Name))
        write node

    /// YAML 문자열 → JsonDocument. parser subset 검증 후 변환.
    /// 호출자는 반환된 JsonDocument 를 dispose 책임.
    let yamlToJson (yaml: string) : JsonDocument =
        if String.IsNullOrWhiteSpace yaml then
            fail "YAML 입력이 비어있습니다."
        let stream = YamlStream()
        // YamlDotNet 의 parser exception 은 원본 YamlException — 메시지 가공만.
        try
            stream.Load(new StringReader(yaml))
        with
        | :? YamlException as ex ->
            fail (sprintf "YAML parse 실패: %s" ex.Message)
        if stream.Documents.Count = 0 then
            fail "YAML document 가 없습니다."
        if stream.Documents.Count > 1 then
            fail "YAML multi-document (---) 는 미지원 (SSOT §2.0). 단일 document 만 허용."
        let root = stream.Documents.[0].RootNode
        rejectForbiddenFeatures root
        use ms = new MemoryStream()
        let writerOpts = JsonWriterOptions(Indented = false)
        do
            use writer = new Utf8JsonWriter(ms, writerOpts)
            writeNodeAsJson writer root
            writer.Flush()
        ms.Position <- 0L
        JsonDocument.Parse(ms.ToArray())

    // ─── JSON → YAML ─────────────────────────────────────────────────────────

    let rec private jsonToYamlNode (el: JsonElement) : YamlNode =
        match el.ValueKind with
        | JsonValueKind.Object ->
            let m = YamlMappingNode()
            for prop in el.EnumerateObject() do
                m.Add(YamlScalarNode(prop.Name) :> YamlNode, jsonToYamlNode prop.Value)
            m :> YamlNode
        | JsonValueKind.Array ->
            let s = YamlSequenceNode()
            for c in el.EnumerateArray() do
                s.Add(jsonToYamlNode c)
            s :> YamlNode
        | JsonValueKind.String ->
            // SSOT §3 예시: ASCII identifier 는 plain emit (사람 친화 view), 그 외 DoubleQuoted.
            // 안전 검사 = ① ASCII identifier 패턴 일치 (`^[A-Za-z_][A-Za-z0-9_\-]*$`)
            //          ② YAML 1.1/1.2 boolean token 미포함 ("true"/"yes"/"on" 등 — bool 변환 회피)
            //          ③ null token 미포함 ("null"/"Null"/"NULL" — null 변환 회피)
            // 예: "cylinder" / "active" / "Start" → plain.  "Cyl1.ADV" / "Adv -> Ret : Start" → quoted.
            let v = el.GetString()
            let s = YamlScalarNode(v)
            let asciiId = not (String.IsNullOrEmpty v) && plainScalarSafeRegex.IsMatch v
            let reserved =
                yaml12Bools.Contains v
                || yaml11OnlyBools.Contains v
                || yamlNullTokens.Contains v
            if asciiId && not reserved then
                s.Style <- ScalarStyle.Plain
            else
                s.Style <- ScalarStyle.DoubleQuoted
            s :> YamlNode
        | JsonValueKind.Number ->
            YamlScalarNode(el.GetRawText()) :> YamlNode
        | JsonValueKind.True -> YamlScalarNode("true") :> YamlNode
        | JsonValueKind.False -> YamlScalarNode("false") :> YamlNode
        | JsonValueKind.Null -> YamlScalarNode("null") :> YamlNode
        | other ->
            fail (sprintf "JSON value kind %A 는 YAML 변환 미지원." other)

    /// JsonDocument / JsonElement → YAML 문자열 (디스크 SSOT / 사용자 미리보기).
    let jsonElementToYaml (root: JsonElement) : string =
        let stream = YamlStream()
        let doc = YamlDocument(jsonToYamlNode root)
        stream.Add(doc)
        let sb = StringBuilder()
        use sw = new StringWriter(sb)
        stream.Save(sw, false) // assignAnchors = false
        sb.ToString()

    let jsonToYaml (jsonText: string) : string =
        if String.IsNullOrWhiteSpace jsonText then
            fail "JSON 입력이 비어있습니다."
        use doc = JsonDocument.Parse(jsonText)
        jsonElementToYaml doc.RootElement
