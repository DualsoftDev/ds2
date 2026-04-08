module Ds2.View3D.Persistence

open System
open System.IO
open System.Text.Json
open Ds2.Core
open Ds2.View3D

/// JSON 옵션
let private jsonOptions = JsonOptions.createProjectSerializationOptions()

/// JSON 파일 기반 레이아웃 저장소
type JsonFileLayoutStore(baseDirectory: string) =

    let getFilePath (projectId: Guid) =
        Path.Combine(baseDirectory, $"scene_{projectId}.json")

    interface ILayoutStore with
        member _.LoadLayout(projectId: Guid) : Result<StoredLayout option, exn> =
            try
                let filePath = getFilePath projectId
                if File.Exists(filePath) then
                    Log.info "Loading layout from: %s" filePath
                    let json = File.ReadAllText(filePath)
                    let layout = JsonSerializer.Deserialize<StoredLayout>(json, jsonOptions)
                    Ok (Some layout)
                else
                    Log.info "No existing layout found for project: %A" projectId
                    Ok None
            with ex ->
                Log.error "Failed to load layout: %s" ex.Message
                Error ex

        member _.SaveLayout(layout: StoredLayout) : Result<unit, exn> =
            try
                Directory.CreateDirectory(baseDirectory) |> ignore
                let filePath = getFilePath layout.ProjectId
                let json = JsonSerializer.Serialize<StoredLayout>(layout, jsonOptions)
                File.WriteAllText(filePath, json)
                Log.info "Layout saved to: %s" filePath
                Ok ()
            with ex ->
                Log.error "Failed to save layout: %s" ex.Message
                Error ex

/// 인메모리 레이아웃 저장소 (테스트용, thread-safe)
type InMemoryLayoutStore() =
    let mutable layouts = Map.empty<Guid, StoredLayout>
    let lockObj = obj()

    interface ILayoutStore with
        member _.LoadLayout(projectId: Guid) : Result<StoredLayout option, exn> =
            lock lockObj (fun () ->
                Ok (Map.tryFind projectId layouts)
            )

        member _.SaveLayout(layout: StoredLayout) : Result<unit, exn> =
            lock lockObj (fun () ->
                layouts <- Map.add layout.ProjectId layout layouts
                Ok ()
            )
