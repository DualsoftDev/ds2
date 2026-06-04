namespace Ds2.Mermaid

open System
open System.IO
open System.Text.Json

/// `*.iotag.json` 파일에서 IoTag 페어 데이터를 읽어들이는 로더.
/// 스키마: IoTagPair-DesignDraft.md §3 참조.
[<RequireQualifiedAccess>]
module IoTagPairLoader =

    /// 로드 결과 — `callPath` 와 `IoTagRecord` 페어 목록.
    type LoadedTag = {
        CallPath: string
        Record: IoTagRecord
    }

    type LoadResult = {
        Version: int
        MermaidRef: string option
        Source: string option
        Vendor: string option
        Tags: LoadedTag list
    }

    let private tryGetStringOpt (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty(name) with
        | true, p when p.ValueKind = JsonValueKind.String ->
            let s = p.GetString()
            if String.IsNullOrEmpty s then None else Some s
        | _ -> None

    let private parseTag (el: JsonElement) : LoadedTag option =
        match tryGetStringOpt el "callPath", tryGetStringOpt el "address" with
        | Some callPath, Some address ->
            let record = {
                Address   = address
                Symbol    = tryGetStringOpt el "symbol"
                Comment   = tryGetStringOpt el "comment"
                DataType  = tryGetStringOpt el "dataType"
                Direction = tryGetStringOpt el "direction"
            }
            Some { CallPath = callPath; Record = record }
        | _ -> None

    /// JSON 문자열에서 파싱. 잘못된 항목은 조용히 스킵.
    let parseContent (json: string) : Result<LoadResult, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let version =
                match root.TryGetProperty "version" with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                | _ -> 1
            let tags =
                match root.TryGetProperty "tags" with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    arr.EnumerateArray()
                    |> Seq.choose parseTag
                    |> Seq.toList
                | _ -> []
            Ok {
                Version    = version
                MermaidRef = tryGetStringOpt root "mermaidRef"
                Source     = tryGetStringOpt root "source"
                Vendor     = tryGetStringOpt root "vendor"
                Tags       = tags
            }
        with ex ->
            Error $"iotag.json 파싱 실패: {ex.Message}"

    /// 파일 경로에서 로드. 파일 부재 시 빈 결과(에러 아님).
    let loadFile (path: string) : Result<LoadResult, string> =
        if not (File.Exists path) then
            Ok { Version = 1; MermaidRef = None; Source = None; Vendor = None; Tags = [] }
        else
            try
                parseContent (File.ReadAllText path)
            with ex ->
                Error $"iotag.json 읽기 실패: {ex.Message}"

    /// mermaid 파일 경로에서 페어 iotag.json 경로 추론.
    /// `project.mmd` → `project.iotag.json` (확장자 `.mmd` / `.md` 모두 지원).
    let resolvePairPath (mermaidPath: string) : string =
        let dir  = Path.GetDirectoryName mermaidPath
        let stem = Path.GetFileNameWithoutExtension mermaidPath
        Path.Combine(dir, stem + FileFormat.IoTagPairSuffix)
