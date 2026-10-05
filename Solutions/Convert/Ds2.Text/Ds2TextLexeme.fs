namespace Ds2.Text

open System
open System.Collections.Generic
open System.Globalization
open System.Text

/// DS2 Text v4 의 어휘 규칙 — 이름 인용, JSON 리터럴, 시간 표기.
///
/// 참조 구현: `ds2_text/ds2text/flow.py` 의 `KEYWORDS`·`_q`·`_path`,
///           `ds2_text/ds2text/language.py` 의 `_json_literal`.
/// 여기의 규칙이 어긋나면 만들어 낸 원문을 DS2 Text 파서가 되읽지 못한다.
[<RequireQualifiedAccess>]
module Ds2TextLexeme =

    /// 예약어는 이름 자리에 그대로 둘 수 없고 JSON 문자열로 감싼다.
    /// flow.py 의 KEYWORDS 집합과 1:1 이다 — 한쪽만 늘리면 인용이 어긋난다.
    let keywords =
        HashSet<string>(
            [ "ds2"; "project"; "system"; "active"; "flow"; "entry"; "exit"; "token"; "ignore"; "work"
              "duration"; "initial"; "ready"; "finish"; "call"; "ref"; "start"; "when"; "permit"; "skip"
              "api"; "command"; "observe"; "none"; "action"; "sensing"; "normal"; "pulse"; "latch"; "virtual"
              "group"; "on"; "reset"; "mutual"; "true"; "false"; "and"; "or"; "not"; "nc"; "rise"; "fall"
              "completion"; "fresh"; "existing"; "timeout"; "type"; "enabled"; "auto"; "limits"
              "input"; "output"; "contact"; "no"; "bind"; "label"; "body"; "head"; "tail"
              "interlocked"; "at"; "in"; "any"; "undefined"; "from"; "as"; "product"
              "bool"; "int8"; "int16"; "int32"; "int64"; "uint8"; "uint16"; "uint32"; "uint64"
              "float32"; "float64"; "string"; "author"; "version"; "iri"; "description"; "datetime" ],
            StringComparer.Ordinal)

    /// JSON 문자열 리터럴. 한글은 이스케이프하지 않되(가독성),
    /// 줄바꿈으로 **보일 수 있는** 문자(U+0085·U+2028·U+2029)는 반드시 이스케이프한다 —
    /// 한 줄 길이 검사는 글자 수로 도는데 그 문자들이 실제 개행처럼 렌더되면 계산이 어긋난다.
    let jsonString (value: string) : string =
        let v = if isNull value then "" else value
        let sb = StringBuilder(v.Length + 2)
        sb.Append('"') |> ignore
        for ch in v do
            match ch with
            | '"'      -> sb.Append "\\\"" |> ignore
            | '\\'     -> sb.Append "\\\\" |> ignore
            | '\b'     -> sb.Append "\\b"  |> ignore
            | '\f'     -> sb.Append "\\f"  |> ignore
            | '\n'     -> sb.Append "\\n"  |> ignore
            | '\r'     -> sb.Append "\\r"  |> ignore
            | '\t'     -> sb.Append "\\t"  |> ignore
            | c when c < ' ' || c = '\u0085' || c = '\u2028' || c = '\u2029' ->
                sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore
        sb.Append('"') |> ignore
        sb.ToString()

    /// 이름은 Unicode 문자 또는 `_` 로 시작하고 문자·숫자·`_` 를 잇는다. 아니면 인용한다.
    let quote (name: string) : string =
        if String.IsNullOrEmpty name then jsonString name
        elif keywords.Contains name then jsonString name
        elif not (name.[0] = '_' || Char.IsLetter name.[0]) then jsonString name
        elif name |> Seq.skip 1 |> Seq.forall (fun c -> c = '_' || Char.IsLetter c || Char.IsNumber c) then name
        else jsonString name

    /// 경로의 각 조각을 따로 인용한다 — 점은 이름 내부에 넣지 않는다.
    let path (parts: string seq) = parts |> Seq.map quote |> String.concat "."

    /// 정규 Text 의 시간 표기는 밀리초이며 TimeSpan의 100ns 정밀도를 보존한다.
    /// TotalMilliseconds(double)를 반올림하면 작은 duration/limits가 0이 되거나
    /// 큰 TimeSpan의 하위 tick이 사라지므로 정수 tick만으로 표기한다.
    let ms (value: TimeSpan) =
        let whole = value.Ticks / TimeSpan.TicksPerMillisecond
        let fraction = abs (value.Ticks % TimeSpan.TicksPerMillisecond)
        let prefix = if value.Ticks < 0L && whole = 0L then "-" else ""
        let integral = prefix + whole.ToString(CultureInfo.InvariantCulture)
        if fraction = 0L then integral + "ms"
        else integral + "." + fraction.ToString("D4", CultureInfo.InvariantCulture).TrimEnd('0') + "ms"

    let msInt (value: int) = sprintf "%dms" value
