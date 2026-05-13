// [PoC 의도] System.Text.Json 의 `obj[]` 박싱 + F# anonymous record 직렬화 검증.
//
// 배경: e2e-cache-hit.fsx 진단 시 "혹시 obj 박싱하면 element 가 빈 객체 `{}` 로 직렬화되나?" 의심
// 발생 → 본 1회용 스니펫으로 확인. 결과: 정상 polymorphic 직렬화 (`cache_control` 필드 포함).
//
// 출력 (.NET 9 / FSharp.Core 9.0):
//   STJ object[]: [{"cache_control":{"type":"ephemeral"},"text":"a","type":"text"},{"text":"b","type":"text"}]
//   STJ typed:    [{"cache_control":{"type":"ephemeral"},"text":"a","type":"text"},{"cache_control":null,"text":"b","type":"text"}]
//
// 실행: dotnet fsi Apps/Promaker/Docs/Poc/probe.fsx

open System.Text.Json

let arr : obj[] = [|
    {| ``type`` = "text"; text = "a"; cache_control = {| ``type`` = "ephemeral" |} |}
    {| ``type`` = "text"; text = "b" |}
|]
printfn "STJ object[]: %s" (JsonSerializer.Serialize arr)

let arr2 = [|
    {| ``type`` = "text"; text = "a"; cache_control = Some {| ``type`` = "ephemeral" |} |}
    {| ``type`` = "text"; text = "b"; cache_control = None |}
|]
printfn "STJ typed:    %s" (JsonSerializer.Serialize arr2)
