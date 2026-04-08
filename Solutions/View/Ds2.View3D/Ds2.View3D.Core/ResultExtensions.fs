module Ds2.View3D.ResultExtensions

/// Result 계산식 빌더
type ResultBuilder() =
    member _.Return(x) = Ok x
    member _.ReturnFrom(x: Result<'a, 'e>) = x
    member _.Bind(x: Result<'a, 'e>, f: 'a -> Result<'b, 'e>) =
        match x with
        | Ok value -> f value
        | Error err -> Error err
    member _.Zero() = Ok ()

let result = ResultBuilder()

/// Result list를 Result<list, error>로 변환
let sequenceResultA (results: Result<'a, 'e> list) : Result<'a list, 'e> =
    let rec go acc remaining =
        match remaining with
        | [] -> Ok (List.rev acc)
        | (Ok x) :: rest -> go (x :: acc) rest
        | (Error e) :: _ -> Error e
    go [] results

/// Option을 Result로 변환
let ofOption (error: 'e) (opt: 'a option) : Result<'a, 'e> =
    match opt with
    | Some value -> Ok value
    | None -> Error error

/// Result에 매핑 함수 적용
let map (f: 'a -> 'b) (result: Result<'a, 'e>) : Result<'b, 'e> =
    match result with
    | Ok value -> Ok (f value)
    | Error err -> Error err

/// Result에 에러 매핑 함수 적용
let mapError (f: 'e1 -> 'e2) (result: Result<'a, 'e1>) : Result<'a, 'e2> =
    match result with
    | Ok value -> Ok value
    | Error err -> Error (f err)
