module Ds2.OpcUa.Server.Program

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Opc.Ua
open Opc.Ua.Configuration
open Ds2.Core
open Ds2.OpcUa.Server.NodeIds
open Ds2.OpcUa.Server.Server

/// Phase 3 · Full wire-up.
/// - ApplicationConfiguration 부팅
/// - 인증서 자동 발급 (없으면)
/// - DsUaServer 시작 · opc.tcp://0.0.0.0:4840 listen
/// - 시드 자산 5대 (스펙 §04 파일럿) 노드 등록 (임시 · AasHost 통합 전)
/// - Ctrl+C 대기

let private seedPilotAssets (server: DsUaServer) =
    let cnc =
        server.NodeManager.AddAssetWithDisplayNames(
            GlobalAssetId "urn:dualsoft:asset:cnc01",
            "CNC01",
            [
                SignalId "line1.cnc01.spindle-speed", "rpm", BuiltInType.Double, "Spindle Speed"
                SignalId "line1.cnc01.motor-temp",  "degC", BuiltInType.Double, "Motor Temperature"
                SignalId "line1.cnc01.cycle-count", "",     BuiltInType.Int64,  "Cycle Count"
            ])
    let pm =
        server.NodeManager.AddAssetWithDisplayNames(
            GlobalAssetId "urn:dualsoft:asset:pm03",
            "PM03",
            [ SignalId "line1.pm03.active-power", "kW", BuiltInType.Double, "Active Power" ])
    let vib =
        server.NodeManager.AddAssetWithDisplayNames(
            GlobalAssetId "urn:dualsoft:asset:vib11",
            "VIB11",
            [ SignalId "line1.vib11.rms", "mm/s", BuiltInType.Double, "RMS Vibration" ])
    let vis =
        server.NodeManager.AddAssetWithDisplayNames(
            GlobalAssetId "urn:dualsoft:asset:vis02",
            "VIS02",
            [ SignalId "line1.vis02.judgement", "", BuiltInType.String, "Judgement" ])
    let bcr =
        server.NodeManager.AddAssetWithDisplayNames(
            GlobalAssetId "urn:dualsoft:asset:bcr05",
            "BCR05",
            [ SignalId "line1.bcr05.code", "", BuiltInType.String, "Barcode" ])
    [ cnc; pm; vib; vis; bcr ]

[<EntryPoint>]
let main _argv =
    let root =
        match Environment.GetEnvironmentVariable "DS2_UASERVER_ROOT" with
        | null | "" -> Path.Combine(Directory.GetCurrentDirectory(), "data", "opcua-server")
        | v -> v
    Directory.CreateDirectory root |> ignore

    let allocator =
        NamespaceAllocator(Path.Combine(root, "nodeset-state.json")) :> INamespaceAllocator

    let cfg = ServerConfiguration.defaultConfig root
    let appConfig = ServerConfiguration.build cfg

    Console.WriteLine(sprintf "[Ds2.OpcUa.Server] endpoint = %s" cfg.EndpointUrl)
    Console.WriteLine(sprintf "[Ds2.OpcUa.Server] cert dir = %s" cfg.CertificateDir)

    // Validate + create own cert if missing.
    let prep : Task<bool> = ServerConfiguration.validateAndPrepare appConfig
    let certOk = prep.GetAwaiter().GetResult()
    if not certOk then
        Console.Error.WriteLine "인증서 검증/발급 실패 · 종료"
        1
    else

    let appInstance = ApplicationInstance(ApplicationConfiguration = appConfig)
    let server = new DsUaServer(allocator)

    let startTask = appInstance.Start server
    startTask.GetAwaiter().GetResult()

    let nsList = seedPilotAssets server
    Console.WriteLine(sprintf "[Ds2.OpcUa.Server] pilot assets loaded · namespaces = %A" nsList)

    Console.WriteLine "[Ds2.OpcUa.Server] Ctrl+C 로 종료. UaExpert 로 접속 · Objects/DS/Assets 브라우징."

    let quit = new ManualResetEventSlim(false)
    Console.CancelKeyPress.Add(fun args ->
        args.Cancel <- true
        Console.WriteLine "\n[Ds2.OpcUa.Server] 종료 신호 수신"
        quit.Set())
    quit.Wait()

    server.Stop()
    Console.WriteLine "[Ds2.OpcUa.Server] 종료"
    0
