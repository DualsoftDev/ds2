namespace Ds2.LightHouse

open System

/// RefLocator 의 main unit (§3.13 EBNF — `Unit = "p" | "slide" | "sheet"`).
type RefUnit =
    | P
    | Slide
    | Sheet

/// RefLocator 의 sub key (`SubKey = "img" | ...`). Phase 1 = `Img` 만.
type RefSubKey =
    | Img

/// 한 RefLocator 의 main 부분 (`Unit=Value`).
///
/// `Value` 는 raw string — `p` / `slide` 는 digits, `sheet` 는 `sheet-name` 또는 `sheet-name!range`.
/// strict value 검증은 호출자 책임 (lib 는 round-trip 보존만).
type RefMain = {
    Unit: RefUnit
    Value: string
}

/// sub fragment (`SubKey=SubValue`).
type RefSub = {
    Key: RefSubKey
    Value: string
}

/// §3.13 RefLocator 의 parsed 표현.
type RefLocator = {
    Main: RefMain
    Subs: RefSub array
}

/// RefLocator 저장형 ↔ parsed 변환 + citation 표시형 변환.
///
/// todo-lighthouse-kb-index.md §3.13 SSOT. EBNF:
///   RefLocator = Unit "=" Value ( "#" SubKey "=" SubValue )*
///   Unit       = "p" | "slide" | "sheet"
///   SubKey     = "img" | ...
///
/// 표시형 변환 (`formatDisplay`) 은 LLM / UI 책임 (§3.13) 이지만, lib unit test 의 round-trip 검증
/// 편의를 위해 default 구현 제공. 표시형은 SSOT 아님 — LLM 응답에 박힐 때는 system prompt 의 규약 따름.
[<RequireQualifiedAccess>]
module RefLocator =

    let private unitToken =
        function
        | P -> "p"
        | Slide -> "slide"
        | Sheet -> "sheet"

    let private subKeyToken =
        function
        | Img -> "img"

    let private parseUnit (token: string) : RefUnit option =
        match token with
        | "p" -> Some P
        | "slide" -> Some Slide
        | "sheet" -> Some Sheet
        | _ -> None

    let private parseSubKey (token: string) : RefSubKey option =
        match token with
        | "img" -> Some Img
        | _ -> None

    /// **Backlog 5 (시트명 `#` hotfix)** — Main.Value 의 URL-style escape (public).
    /// EBNF 의 fragment separator `#` 가 sheet-name 안에 들어가는 산업 .xlsx (`5-1. #201` 등) 의 round-trip 안전성 보장.
    ///   - `%` → `%25` (escape 자체의 escape, decode ambiguity 회피)
    ///   - `#` → `%23` (fragment separator 충돌 해소)
    /// 그 외 char (한글 / 하이픈 / 공백 / `!` / `=` / `.` / `&` 등) 는 raw 박제 — EBNF spec 의 sheet-name 자유 char.
    /// `=` 는 첫 `=` 만 split 정합 (r2 Major-2) 라 raw 안전.
    /// 본 helper 는 Main.Value 만 encode — sub value (img=N) 는 digits 가정 + escape 불요.
    ///
    /// Public 노출 사유 — caller (`OoxmlExtractor.ExtractXlsx` 등) 가 `sprintf "sheet=%s" sheetName` 직접 박제
    /// 시 raw `#` 진입 회피. `toStored` 는 RefLocator record 입력 받지만 caller 가 record build 없이 raw string
    /// 박제 path 도 본 helper 로 정합 — `sprintf "sheet=%s" (RefLocator.encodeMainValue sheetName)`.
    ///
    /// **Replace 순서 의무 (review Mn-1)** — `%` → `%25` 먼저, `#` → `%23` 나중. 역전 시 raw `#` 의 encode 가
    /// `%23` → `%2523` 으로 이중 escape 됨 (`%` 도 encode 대상이라 두 번째 Replace 가 첫 `%23` 의 `%` 를 다시 잡음).
    let encodeMainValue (value: string) : string =
        value.Replace("%", "%25").Replace("#", "%23")

    /// `encodeMainValue` 의 역 — Replace 순서 의무 (`%23` → `#` 먼저, `%25` → `%` 다음).
    /// 순서 역전 시 `%2523` (raw `%23`) decode 가 `%23` → `#` 로 잘못 처리됨.
    let private decodeMainValue (value: string) : string =
        value.Replace("%23", "#").Replace("%25", "%")

    /// `Unit=Value` 형태 fragment 파싱. value 가 비면 None.
    let private parseFragment (fragment: string) : (string * string) option =
        let idx = fragment.IndexOf('=')
        if idx <= 0 || idx = fragment.Length - 1 then None
        else
            let key = fragment.Substring(0, idx)
            let value = fragment.Substring(idx + 1)
            if String.IsNullOrEmpty value then None
            else Some (key, value)

    /// 저장형 → parsed. EBNF 위반 입력은 None.
    ///
    /// **Backlog 5 (시트명 `#` hotfix)** — Main.Value 자동 decode (`%23` → `#`, `%25` → `%`).
    /// Stored 안 `#` 는 항상 fragment separator (sheet-name 의 `#` 는 caller 가 `%23` 으로 encode 후 stored 박제 의무).
    ///
    /// 위반 예: 빈 문자열 / `page=14` (Unit 비매칭) / `p=` (빈 value) / `p=14#=2` (빈 SubKey) / `p` (= 없음).
    let tryParse (stored: string) : RefLocator option =
        if String.IsNullOrEmpty stored then None
        else
            let fragments = stored.Split('#')
            match parseFragment fragments.[0] with
            | None -> None
            | Some (unitToken, value) ->
                match parseUnit unitToken with
                | None -> None
                | Some u ->
                    let subResults =
                        fragments
                        |> Array.skip 1
                        |> Array.map (fun frag ->
                            parseFragment frag
                            |> Option.bind (fun (k, v) ->
                                parseSubKey k |> Option.map (fun sk -> { Key = sk; Value = v })))
                    if subResults |> Array.exists Option.isNone then None
                    else
                        let subs = subResults |> Array.map Option.get
                        Some { Main = { Unit = u; Value = decodeMainValue value }; Subs = subs }

    /// 저장형 → parsed. EBNF 위반 시 ArgumentException (fail-fast 정책).
    let parse (stored: string) : RefLocator =
        match tryParse stored with
        | Some r -> r
        | None -> raise (ArgumentException(sprintf "RefLocator EBNF 위반: %s" stored))

    /// parsed → 저장형. `tryParse >> Option.map toStored = id` round-trip 보장.
    ///
    /// **Backlog 5** — Main.Value 자동 encode (`#` → `%23`, `%` → `%25`). 산업 .xlsx 의 sheet-name 안 `#`
    /// (호기/부품 번호 표기) 가 raw 박제 시 fragment separator 충돌 → caller 가 raw `Value` 전달해도 자동 escape.
    let toStored (locator: RefLocator) : string =
        let mainPart = sprintf "%s=%s" (unitToken locator.Main.Unit) (encodeMainValue locator.Main.Value)
        if Array.isEmpty locator.Subs then mainPart
        else
            let subParts =
                locator.Subs
                |> Array.map (fun s -> sprintf "%s=%s" (subKeyToken s.Key) s.Value)
                |> String.concat "#"
            sprintf "%s#%s" mainPart subParts

    /// citation 표시형 default 변환 (§3.13 표 의 한국어 conv).
    ///
    /// 표시형 SSOT 는 LLM 응답이 따르는 system prompt 에 있음 — 본 함수는 lib unit test / 로컬 UI 의 hint.
    /// 예: `{ Main = { Unit = P; Value = "14" }; Subs = [|{ Key = Img; Value = "2" }|] }` → `"p.14 그림 2"`.
    let formatDisplay (locator: RefLocator) : string =
        let mainDisplay =
            match locator.Main.Unit with
            | P -> sprintf "p.%s" locator.Main.Value
            | Slide -> sprintf "슬라이드 %s" locator.Main.Value
            | Sheet ->
                // sheet=BOM!A1:D40 → "시트 BOM A1:D40"
                let value = locator.Main.Value.Replace("!", " ")
                sprintf "시트 %s" value

        let subDisplay (sub: RefSub) : string =
            match sub.Key with
            | Img -> sprintf "그림 %s" sub.Value

        if Array.isEmpty locator.Subs then mainDisplay
        else
            let subs = locator.Subs |> Array.map subDisplay |> String.concat " "
            sprintf "%s %s" mainDisplay subs
