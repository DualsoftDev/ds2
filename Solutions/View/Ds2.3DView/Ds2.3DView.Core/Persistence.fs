namespace Ds2.ThreeDView

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

// =============================================================================
// Persistence — 레이아웃 저장/복원 (ILayoutStore + JSON 파일 구현)
// =============================================================================

/// Layout persistence interface
type ILayoutStore =
    abstract LoadLayout: sceneId: string * mode: SceneMode -> LayoutPosition list
    abstract SaveLayout: sceneId: string * mode: SceneMode * positions: LayoutPosition list -> unit
    abstract ClearLayout: sceneId: string * mode: SceneMode -> unit

// ─────────────────────────────────────────────────────────────────────
// JSON File Implementation
// ─────────────────────────────────────────────────────────────────────

module Persistence =

    /// LayoutPosition용 직렬화 DTO (F# record → JSON 호환)
    [<CLIMutable>]
    type LayoutPositionDto =
        {
            [<JsonPropertyName("nodeId")>]
            NodeId: Guid

            [<JsonPropertyName("nodeKind")>]
            NodeKind: int

            [<JsonPropertyName("x")>]
            X: float

            [<JsonPropertyName("y")>]
            Y: float

            [<JsonPropertyName("z")>]
            Z: float
        }

    let internal toDto (p: LayoutPosition) : LayoutPositionDto =
        { NodeId = p.NodeId; NodeKind = int p.NodeKind; X = p.X; Y = p.Y; Z = p.Z }

    let internal fromDto (dto: LayoutPositionDto) : LayoutPosition =
        { NodeId = dto.NodeId; NodeKind = enum<NodeKind> dto.NodeKind; X = dto.X; Y = dto.Y; Z = dto.Z }

    let internal jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.Converters.Add(JsonFSharpConverter())
        opts

    let internal layoutFileName (sceneId: string) (mode: SceneMode) =
        $"layout_{sceneId}_{mode}.json"

/// JSON 파일 기반 레이아웃 저장소
type JsonFileLayoutStore(directory: string) =

    do
        if not (Directory.Exists(directory)) then
            Directory.CreateDirectory(directory) |> ignore

    let filePath sceneId mode =
        Path.Combine(directory, Persistence.layoutFileName sceneId mode)

    interface ILayoutStore with
        member _.LoadLayout(sceneId, mode) =
            let path = filePath sceneId mode
            if File.Exists(path) then
                try
                    let json = File.ReadAllText(path)
                    let dtos = JsonSerializer.Deserialize<Persistence.LayoutPositionDto[]>(json, Persistence.jsonOptions)
                    dtos |> Array.toList |> List.map Persistence.fromDto
                with _ ->
                    []
            else
                []

        member _.SaveLayout(sceneId, mode, positions) =
            let path = filePath sceneId mode
            let dtos = positions |> List.map Persistence.toDto |> List.toArray
            let json = JsonSerializer.Serialize(dtos, Persistence.jsonOptions)
            File.WriteAllText(path, json)

        member _.ClearLayout(sceneId, mode) =
            let path = filePath sceneId mode
            if File.Exists(path) then
                File.Delete(path)

/// 메모리 기반 레이아웃 저장소 (테스트용)
type InMemoryLayoutStore() =
    let mutable data = Map.empty<string * SceneMode, LayoutPosition list>

    interface ILayoutStore with
        member _.LoadLayout(sceneId, mode) =
            data |> Map.tryFind (sceneId, mode) |> Option.defaultValue []

        member _.SaveLayout(sceneId, mode, positions) =
            data <- data |> Map.add (sceneId, mode) positions

        member _.ClearLayout(sceneId, mode) =
            data <- data |> Map.remove (sceneId, mode)
