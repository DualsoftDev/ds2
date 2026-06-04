namespace Ds2.Mermaid

open System
open System.Collections.Generic
open System.Text.RegularExpressions

/// `callPath` → `CallId` 매칭 후 `IoTagSidecar` 에 등록.
/// callPath 구분자 `.` 는 escape-aware split — 백슬래시 `\.` 는 리터럴.
[<RequireQualifiedAccess>]
module IoTagBinder =

    /// callPath 의 escape-aware 정규화.
    /// 입력: `Main.공정\.A.실린더.ExtStart.ADV`
    /// 출력: 동일하게 normalized — 매칭 키는 raw string 그대로 사용 (양쪽 동일 규약 전제).
    /// 따라서 split 은 불필요 — callIndex 의 키 생성 시 동일 규약 따르면 됨.
    let normalize (callPath: string) : string =
        if isNull callPath then "" else callPath.Trim()

    /// callPath 구성요소를 escape 처리하여 결합.
    /// 입력: ["Main"; "공정.A"; "실린더"; "ExtStart"; "ADV"]
    /// 출력: `Main.공정\.A.실린더.ExtStart.ADV`
    let joinSegments (segments: string seq) : string =
        segments
        |> Seq.map (fun s ->
            if isNull s then "" else s.Replace("\\", "\\\\").Replace(".", "\\."))
        |> String.concat "."

    /// 로드된 IoTag 들을 callIndex 와 매칭하여 IoTagSidecar 에 등록.
    /// 반환값: (boundCount, unmatchedCallPaths)
    let bind
        (callIndex: IReadOnlyDictionary<string, Guid>)
        (tags: IoTagPairLoader.LoadedTag seq)
        : int * string list =
        let unmatched = ResizeArray<string>()
        let mutable bound = 0
        for tag in tags do
            let key = normalize tag.CallPath
            match callIndex.TryGetValue key with
            | true, callId ->
                IoTagSidecar.set callId tag.Record
                bound <- bound + 1
            | _ ->
                unmatched.Add key
        bound, (unmatched |> List.ofSeq)
