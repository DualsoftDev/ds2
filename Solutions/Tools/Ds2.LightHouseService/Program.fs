module Ds2.LightHouseService.Program

open System
open System.IO
open System.Net
open System.Security.Cryptography.X509Certificates
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ModelContextProtocol.AspNetCore

/// Phase S1 entry — config 로드 → DPAPI 복호화 → storage 초기화 → Kestrel HTTPS bind → auth middleware → endpoint.
///
/// Windows Service host (`UseWindowsService`) 로 등록되면 SCM 의 Start/Stop 이벤트로 lifecycle 관리.
/// console 실행 (개발/디버그) 도 동일 코드 — `WindowsServiceLifetime` 가 자동 fallback.
///
/// Phase S1 DoD: TLS bind 성공 + plain HTTP 거부 + DPAPI PSK 복호화 + auth middleware 가 빈 `GET /collections` 200 응답
///              + storage layout 초기화 + log4net 첫 라인 + EventLog service start 이벤트 + config schemaVersion check.

[<EntryPoint>]
let main argv =
    // log4net 초기화 — log4net.config 가 publish output 옆에 함께 배포됨 (fsproj 의 None Include).
    // 미존재는 fail-fast — binary 배포 결함이라 사용자 안내 우선 (review M7).
    let log4netConfigPath =
        let baseDir = AppContext.BaseDirectory
        Path.Combine(baseDir, "log4net.config")
    if not (File.Exists log4netConfigPath) then
        raise (FileNotFoundException(
            sprintf "log4net.config 미존재 — %s. publish output 구성 확인." log4netConfigPath,
            log4netConfigPath))
    let configFile = FileInfo log4netConfigPath
    let repo = log4net.LogManager.GetRepository(System.Reflection.Assembly.GetExecutingAssembly())
    log4net.Config.XmlConfigurator.Configure(repo, configFile) |> ignore

    Log.service.Info(sprintf "Ds2.LightHouseService 시작 — argv=%A" argv)

    // config 로드 + schema check + DPAPI 복호화
    let configPath =
        match argv with
        | [| "--config"; p |] -> p
        | _ -> Config.defaultPath ()

    Log.service.Info(sprintf "config 경로 = %s" configPath)

    let cfg = Config.load configPath
    Config.validateHttpsOnly cfg
    let storageRoot = Config.expandEnv cfg.StorageRoot

    let psk = Config.decryptDpapi cfg.PreSharedKeyEncrypted
    let tlsCertPassword = Config.decryptDpapi cfg.TlsCertPasswordEncrypted

    // storage layout 초기화 — fail-fast (permission 미달 시 reraise)
    let resolvedStorageRoot = Storage.initialize storageRoot
    Log.service.Info(sprintf "storage root 초기화 완료 — %s" resolvedStorageRoot)

    // TLS 인증서 로드 — Kestrel HTTPS-only
    let tlsCertPath = Config.expandEnv cfg.TlsCertPath
    if not (File.Exists tlsCertPath) then
        raise (FileNotFoundException(sprintf "TLS 인증서 미존재 — %s" tlsCertPath, tlsCertPath))
    // .NET 9 권장 API — X509Certificate2 constructor 는 obsolete (FS0044). PFX path + password 전용 로더.
    let tlsCert = X509CertificateLoader.LoadPkcs12FromFile(tlsCertPath, tlsCertPassword)
    Log.service.Info(sprintf "TLS 인증서 로드 완료 — subject=%s thumbprint=%s"
        tlsCert.Subject tlsCert.Thumbprint)

    // WebApplication 빌드
    let builder = WebApplication.CreateBuilder()
    builder.Services.AddRouting() |> ignore
    builder.Services.AddSingleton(cfg) |> ignore
    builder.Services.AddHttpContextAccessor() |> ignore   // Phase S3 — AttachmentTools 의 HttpContext 접근 (SDK 1.2.0 자동 DI 검출).

    // Phase S3 — SessionRegistry 가 ICollectionLifecycleNotifier 의 SSOT impl (S2 의 Logging swap).
    // resolver 는 storage 의 collection 디렉토리 조립 (Registry + Storage + ZipImport.collectionDirName).
    let attachmentResolver = AttachmentResolver.fromRegistry (Config.expandEnv cfg.StorageRoot)
    builder.Services.AddSingleton<AttachmentResolver>(attachmentResolver) |> ignore
    let sessionRegistry = SessionRegistry(attachmentResolver)
    builder.Services.AddSingleton<ISessionRegistry>(sessionRegistry :> ISessionRegistry) |> ignore
    // ICollectionLifecycleNotifier 도 같은 instance — Phase S2 collection mutation API 가 본 notifier 호출.
    builder.Services.AddSingleton<ICollectionLifecycleNotifier>(fun sp ->
        sp.GetRequiredService<ISessionRegistry>() :> ICollectionLifecycleNotifier) |> ignore

    // Phase S2 — staging sweep BackgroundService.
    builder.Services.AddHostedService<StagingSweepService>() |> ignore
    // Phase S3 — session idle TTL sweep BackgroundService (L2-3).
    builder.Services.AddHostedService<SessionSweepService>() |> ignore

    // Phase S3 — MCP server host (todo-lighthouse-kb-server.md §3.1.3 / §4.2 Phase S3).
    // `ModelContextProtocol.AspNetCore 1.2.0` (D-S3-2, Promaker alignment).
    // `WithToolsFromAssembly()` 가 본 assembly 의 [<McpServerToolType>] 발견 — AttachmentTools 자동 등록.
    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly() |> ignore

    // Kestrel HTTPS-only — config 의 listenUrl 에서 host/port 추출 후 HTTPS endpoint 만 바인드.
    // plain HTTP listener 는 *애초 바인드 안 함* (§3.7). scheme check 는 Config.validateHttpsOnly 가 이미 강제 (review m12).
    let listenUri = Uri cfg.ListenUrl
    let host =
        match listenUri.Host with
        | "0.0.0.0" ->
            Log.service.Warn(
                "listenUrl host=0.0.0.0 — 모든 NIC bind. 사내 LAN 전용 정책 시 specific IP 권장 (review M5)")
            IPAddress.Any
        | "localhost" | "127.0.0.1" -> IPAddress.Loopback
        | h ->
            match IPAddress.TryParse h with
            | true, ip -> ip
            | _ ->
                raise (InvalidDataException(
                    sprintf "listenUrl host=%s — IP 주소만 허용 (DNS hostname 미지원, review m6)" h))
    let port = listenUri.Port

    // ASPNETCORE_URLS 환경변수 / default URL ("http://localhost:5000") 차단 — options.Listen 만 활성 (review M4).
    builder.WebHost.UseUrls("") |> ignore

    builder.WebHost.ConfigureKestrel(fun (options: KestrelServerOptions) ->
        options.Limits.MaxRequestBodySize <- Nullable cfg.MaxUploadBytes
        options.Listen(host, port, fun listenOptions ->
            listenOptions.UseHttps tlsCert |> ignore)
    ) |> ignore

    // Windows Service host — 콘솔 실행 시 자동 fallback (UseWindowsService 가 SCM 미검출 시 console lifetime)
    builder.Host.UseWindowsService() |> ignore

    let app = builder.Build()

    // 1. Public endpoints (인증 무관) — health probe 등. middleware 진입 전에 매핑.
    app.UseRouting() |> ignore
    Endpoints.mapPublic app

    // 2. 인증 middleware — Bearer PSK + X-User-Identity 검증 (모든 인증 endpoint 공통)
    app.Use(AuthMiddleware.middleware psk) |> ignore

    // 3. 인증 통과 endpoint — Phase S2 collection 관리 API + Phase S3 session 발급/해제
    let notifier = app.Services.GetRequiredService<ICollectionLifecycleNotifier>()
    let registry = app.Services.GetRequiredService<ISessionRegistry>()
    CollectionEndpoints.map app cfg notifier
    SessionEndpoints.map app registry

    // 4. Phase S3 MCP HTTP transport (`/mcp` prefix) — SessionAuth 미들웨어 추가 통과 후 진입.
    //    `UseWhen` 으로 MCP path 만 session 검증 (POST/DELETE /sessions 는 token 발급 자체라 본 미들웨어 통과 안 함).
    app.UseWhen(
        (fun ctx -> ctx.Request.Path.StartsWithSegments(PathString "/mcp")),
        (fun (app2: IApplicationBuilder) ->
            app2.Use(SessionAuth.middleware registry) |> ignore)
    ) |> ignore
    app.MapMcp("/mcp") |> ignore

    Log.service.Info(sprintf "Kestrel HTTPS listen 시작 — %s (maxUploadBytes=%d)"
        cfg.ListenUrl cfg.MaxUploadBytes)
    // service start 는 EventLog 에도 박제 (운영자가 SCM 에서 즉시 인지) — Warn level 로 m10 의 threshold 통과.
    Log.service.Warn(sprintf "Ds2.LightHouseService 시작 완료 — %s" cfg.ListenUrl)
    Log.audit.Info(sprintf "service start — listenUrl=%s storageRoot=%s" cfg.ListenUrl resolvedStorageRoot)

    app.Run()

    0
