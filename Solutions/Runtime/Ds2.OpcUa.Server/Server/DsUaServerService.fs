namespace Ds2.OpcUa.Server.Server

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Opc.Ua.Configuration
open Ds2.OpcUa.Server.NodeIds

/// 서버 기동 후 자산 seed 를 담당하는 훅.
/// 파일럿/튜토리얼 프로젝트마다 자체 구현체를 DI 등록 (`AddSingleton<IUaAssetSeeder, ...>()`).
/// 미등록 시 서버는 자산 없이 기동 (외부에서 AasHost 를 통해 shell 이 push 되는 것을 가정).
type IUaAssetSeeder =
    abstract Seed : server: DsUaServer -> IReadOnlyList<int>

/// IHostedService 구현 · Web/CLI 프로세스에 인-프로세스 OPC UA 서버 embed.
///
/// Program.cs:
///   builder.Services.AddHostedService<DsUaServerService>();
///   builder.Services.AddSingleton<IUaAssetSeeder, PilotAssetSeeder>();  // 필요 시
///
/// 서버 루트는 DS2_UASERVER_ROOT env 또는 AppContext.BaseDirectory/data/opcua-server.
type DsUaServerService(
        logger: ILogger<DsUaServerService>,
        seeders: seq<IUaAssetSeeder>) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) = backgroundTask {
        let root =
            match Environment.GetEnvironmentVariable "DS2_UASERVER_ROOT" with
            | null | "" -> Path.Combine(AppContext.BaseDirectory, "data", "opcua-server")
            | v -> v
        Directory.CreateDirectory root |> ignore

        let allocator =
            NamespaceAllocator(Path.Combine(root, "nodeset-state.json")) :> INamespaceAllocator
        let cfg = ServerConfiguration.defaultConfig root
        let appConfig = ServerConfiguration.build cfg

        logger.LogInformation("[UaServer] 시작 · endpoint = {Endpoint}", cfg.EndpointUrl)

        let! certOk = ServerConfiguration.validateAndPrepare appConfig
        if not certOk then
            logger.LogError("[UaServer] 인증서 발급 실패 · 서버를 시작하지 않음")
        else
            let appInstance = ApplicationInstance(ApplicationConfiguration = appConfig)
            let server = new DsUaServer(allocator)
            do! appInstance.Start server

            let seederList = seeders |> Seq.toList
            if seederList.IsEmpty then
                logger.LogInformation
                    "[UaServer] 준비됨 · 등록된 IUaAssetSeeder 없음 · 자산은 외부에서 push"
            else
                for seeder in seederList do
                    try
                        let indices = seeder.Seed server
                        logger.LogInformation(
                            "[UaServer] Seed 완료 · {Seeder} · 자산 {Count}대 · ns index = {Indices}",
                            seeder.GetType().Name, indices.Count, indices)
                    with ex ->
                        logger.LogError(
                            ex, "[UaServer] Seed 실패 · {Seeder}", seeder.GetType().Name)

            try
                do! Task.Delay(Timeout.InfiniteTimeSpan, ct)
            with :? OperationCanceledException -> ()
            logger.LogInformation "[UaServer] 종료 중…"
            server.Stop()
    }
