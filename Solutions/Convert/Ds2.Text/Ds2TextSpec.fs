namespace Ds2.Text

open System
open System.Globalization
open Ds2.Core

/// `ValueSpec` → DS2 Text 값 사양 표기.
///
/// 참조 구현: `ds2_text/ds2text/value_specs.py` 의 `format_spec`·`_literal`.
///   `bool == true` · `int16 in {10, 20}` · `float32 in [9.5, 10.5)` · `int16 any` · `undefined`
/// 입력·조건은 `==`, Call 출력은 `=` 를 쓴다.
[<RequireQualifiedAccess>]
module Ds2TextSpec =

    let private inv = CultureInfo.InvariantCulture

    /// 소수 리터럴. `json.dumps` 는 10.0 을 "10.0" 으로 적는다 —
    /// .NET 기본 표기는 "10" 이라 정수로 읽히므로 소수점을 되살린다.
    let private floatLit (text: string) =
        if text.Contains "." || text.Contains "e" || text.Contains "E"
           || text.Contains "N" || text.Contains "I" then text
        else text + ".0"

    let private f32 (v: float32) = floatLit (v.ToString("R", inv))
    let private f64 (v: float)   = floatLit (v.ToString("R", inv))

    /// 한 타입의 ValueSpec 본문을 적는다. 타입 이름이 항상 앞에 붙는다 —
    /// `at "주소"` 를 끼워 넣을 때 첫 토큰이 타입이라는 계약에 의존한다.
    let private render (typeName: string) (lit: 'T -> string) (op: string) (spec: ValueSpec<'T>) =
        match spec with
        | Undefined   -> typeName + " any"
        | Single x    -> typeName + " " + op + " " + lit x
        | Multiple xs -> typeName + " in {" + (xs |> List.map lit |> String.concat ", ") + "}"
        | Ranges rs   ->
            let segment (r: RangeSegment<'T>) =
                let lower =
                    match r.Lower with
                    | Some (v, Closed) -> "[" + lit v
                    | Some (v, Open)   -> "(" + lit v
                    | None             -> "(*"
                let upper =
                    match r.Upper with
                    | Some (v, Closed) -> lit v + "]"
                    | Some (v, Open)   -> lit v + ")"
                    | None             -> "*)"
                lower + ", " + upper
            typeName + " in " + (rs |> List.map segment |> String.concat " | ")

    /// 타입까지 포함한 사양 문자열. 세미콜론은 붙이지 않는다.
    let format (op: string) (spec: ValueSpec) : string =
        match spec with
        | UndefinedValue -> "undefined"
        | BoolValue    v -> render "bool"    (fun (x: bool)   -> if x then "true" else "false") op v
        | Int8Value    v -> render "int8"    (fun (x: sbyte)  -> x.ToString inv) op v
        | Int16Value   v -> render "int16"   (fun (x: int16)  -> x.ToString inv) op v
        | Int32Value   v -> render "int32"   (fun (x: int)    -> x.ToString inv) op v
        | Int64Value   v -> render "int64"   (fun (x: int64)  -> x.ToString inv) op v
        | UInt8Value   v -> render "uint8"   (fun (x: byte)   -> x.ToString inv) op v
        | UInt16Value  v -> render "uint16"  (fun (x: uint16) -> x.ToString inv) op v
        | UInt32Value  v -> render "uint32"  (fun (x: uint32) -> x.ToString inv) op v
        | UInt64Value  v -> render "uint64"  (fun (x: uint64) -> x.ToString inv) op v
        | Float32Value v -> render "float32" f32 op v
        | Float64Value v -> render "float64" f64 op v
        | StringValue  v -> render "string"  Ds2TextLexeme.jsonString op v

    /// `at "주소"` 는 타입 뒤에 들어간다 — `int16 at "D10" == 4`.
    /// 타입이 없는 `undefined` 에는 주소를 붙일 수 없다(파서가 거절한다).
    let formatWithTag (op: string) (address: string option) (spec: ValueSpec) : string =
        let body = format op spec
        match address with
        | None -> body
        | Some addr ->
            match body.IndexOf ' ' with
            | -1  -> body
            | idx -> body.Substring(0, idx) + " at " + Ds2TextLexeme.jsonString addr + " " + body.Substring(idx + 1)

    /// 기본 binding 은 「주소 없는 bool 단일 true」다 — 이것과 같으면 줄을 생략한다.
    let isDefaultBoolTrue (address: string option) (spec: ValueSpec) =
        address.IsNone && (match spec with BoolValue (Single true) -> true | _ -> false)

    /// 조건 leaf 를 기존 match 표기(`Api == 값`)로 적을 수 있는지.
    /// native 어댑터의 "match" 분기가 만드는 사양(Bool·String·Int64·Float64 단일값)과 정확히 같다.
    let tryMatchLiteral (spec: ValueSpec) : string option =
        match spec with
        | BoolValue    (Single x) -> Some (if x then "true" else "false")
        | StringValue  (Single x) -> Some (Ds2TextLexeme.jsonString x)
        | Int64Value   (Single x) -> Some (x.ToString inv)
        | Float64Value (Single x) -> Some (f64 x)
        | _ -> None
