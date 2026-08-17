namespace Ds2.OpcUa.Server.AasClient

open System
open System.IO

/// AAS 파일 SSOT (aid-store) 스캐너.
///
/// 방법 A · 무서버 아키텍처 (수동 로딩):
///   - Editor / Vendor CI / 사람 모두 `<root>/shells/*.json` 등 파일에 직접 write.
///   - OPC UA Server 는 이 스캐너로 aid-store 를 읽음.
///   - 변경 반영은 사용자가 명시적으로 `Reload()` 를 호출해야 이루어짐
///     (FileSystemWatcher 미사용 — 오탐/락 이슈 회피, 재현성 확보).
///   - REST · SignalR · Journal 없음 (필요하면 git 로 감사).
///
/// 디렉토리 구조:
///   <root>/shells/{aasIdBase64Url}.json
///   <root>/submodels/{smIdBase64Url}.json
///   <root>/concept-descriptions/{cdIdBase64Url}.json
///   <root>/packages/{packageIdBase64Url}.aasx

type AasResourceKind =
    | Shell
    | Submodel
    | ConceptDescription
    | Package

type AasResourceChange =
    | Added    of AasResourceKind * idBase64: string
    | Modified of AasResourceKind * idBase64: string
    | Removed  of AasResourceKind * idBase64: string

/// aid-store 스냅샷 (kind + idBase64 → lastWriteUtc).
type private Snapshot = Map<AasResourceKind * string, DateTime>

/// aid-store 를 스캔하고, 수동 `Reload` 호출 시 이전 스냅샷과 비교해 델타를 계산.
type AasFileScanner(rootPath: string) =
    let subdirOf =
        function
        | Shell              -> "shells"
        | Submodel           -> "submodels"
        | ConceptDescription -> "concept-descriptions"
        | Package            -> "packages"

    let extOf =
        function
        | Package -> ".aasx"
        | _       -> ".json"

    let kinds = [ Shell; Submodel; ConceptDescription; Package ]

    do
        Directory.CreateDirectory rootPath |> ignore
        for k in kinds do
            Directory.CreateDirectory (Path.Combine(rootPath, subdirOf k)) |> ignore

    let dirOf k = Path.Combine(rootPath, subdirOf k)

    let idBase64Of (fullPath: string) =
        Path.GetFileNameWithoutExtension fullPath

    let snapshot () : Snapshot =
        kinds
        |> List.collect (fun k ->
            let dir = dirOf k
            if not (Directory.Exists dir) then []
            else
                Directory.EnumerateFiles(dir, "*" + extOf k)
                |> Seq.map (fun p -> (k, idBase64Of p), File.GetLastWriteTimeUtc p)
                |> List.ofSeq)
        |> Map.ofList

    let mutable current : Snapshot = Map.empty

    member _.RootPath = rootPath

    member _.List(kind: AasResourceKind) : string list =
        let dir = dirOf kind
        if not (Directory.Exists dir) then []
        else
            Directory.EnumerateFiles(dir, "*" + extOf kind)
            |> Seq.map idBase64Of
            |> List.ofSeq

    member _.TryLoad(kind: AasResourceKind, idBase64: string) : byte[] option =
        let p = Path.Combine(dirOf kind, idBase64 + extOf kind)
        if File.Exists p then Some (File.ReadAllBytes p) else None

    /// aid-store 를 다시 스캔하고, 이전 스냅샷 대비 델타를 반환.
    /// 최초 호출은 모든 파일을 `Added` 로 반환.
    member _.Reload() : AasResourceChange list =
        let next = snapshot ()
        let prev = current
        current <- next

        let added =
            next
            |> Map.toSeq
            |> Seq.filter (fun (k, _) -> not (Map.containsKey k prev))
            |> Seq.map (fun ((kind, id), _) -> Added(kind, id))

        let modified =
            next
            |> Map.toSeq
            |> Seq.choose (fun (k, t) ->
                match Map.tryFind k prev with
                | Some prevT when prevT <> t -> let (kind, id) = k in Some (Modified(kind, id))
                | _ -> None)

        let removed =
            prev
            |> Map.toSeq
            |> Seq.filter (fun (k, _) -> not (Map.containsKey k next))
            |> Seq.map (fun ((kind, id), _) -> Removed(kind, id))

        [ yield! added; yield! modified; yield! removed ]
