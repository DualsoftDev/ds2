module Ds2.LightHouseService.Program

open System
open System.IO
open System.Net
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.Server.Kestrel.Https
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ModelContextProtocol.AspNetCore

/// **Phase S7 D-S7-1 (s6-r53)** — Kestrel UseHttps callback 안의 client cert 검증 helper.
///
/// `mtls.mode=off` → ClientCertificateMode.NoCertificate (현행 동작 유지, validation callback 미박제).
/// `mtls.mode=optional` → AllowCertificate + validation (cert 박제 시 검증, 미박제 시 통과).
/// `mtls.mode=required` → RequireCertificate + validation (cert 미박제 시 Kestrel TLS handshake 거부).
///
/// validation: (a) chain valid (cert.Verify() — 사내 CA root trust 의존) + (b) whitelist 박제 시 thumbprint match.
/// validation 실패 시 false → Kestrel 가 connection abort (HTTP 응답 자체 안 옴, audit log 박제 후).
/// **B5 phase 4 (s6-r69)** — `validationOverride` = test 친화 hook. None 박제 시 현행 production path (chain.Build +
/// whitelist) 사용. Some 박제 시 본 callback 만 사용 — IT 가 self-signed client cert 의 chain 부재를 우회하여
/// mTLS handshake 자체의 round-trip 검증 가능. production main 의 진입점 (configureApp 호출) 은 항상 None.
let private configureMtls
        (mtls: MtlsConfigSection)
        (validationOverride: (X509Certificate2 -> X509Chain -> SslPolicyErrors -> bool) option)
        (httpsOpts: HttpsConnectionAdapterOptions) : unit =
    let whitelist = mtls.AllowedThumbprints |> Set.ofArray
    let productionValidate (cert: X509Certificate2) (chain: X509Chain) (_errors: SslPolicyErrors) : bool =
        // chain valid 가 1차 — 사내 CA root trust 의존. self-signed dev cert 는 root trust 등록 의무 (사용자 책임).
        let chainValid =
            try chain.Build(cert)
            with ex ->
                Log.audit.Warn(sprintf "mtls: chain.Build 실패 — %s: %s" (ex.GetType().Name) ex.Message)
                false
        if not chainValid then
            // m1 자가 검열 정정 — chain.ChainStatus 박제 (debugging hint). 빈 배열도 가능 (chain.Build 가 ArgumentException
            // 으로 실패한 경우 위 try 분기에서 false 박제).
            let statuses =
                chain.ChainStatus
                |> Array.map (fun s -> s.Status.ToString())
                |> String.concat ","
            Log.audit.Warn(sprintf "mtls: chain 검증 실패 — subject=%s thumbprint=%s status=[%s]"
                (Log.sanitizeForLog cert.Subject) cert.Thumbprint statuses)
            false
        elif Set.isEmpty whitelist then
            // whitelist 미박제 = chain valid 만으로 통과 (사내 CA root trust 가 SSOT).
            Log.audit.Info(sprintf "mtls: chain valid 통과 — subject=%s thumbprint=%s"
                (Log.sanitizeForLog cert.Subject) cert.Thumbprint)
            true
        else
            // whitelist 박제 = 사내 CA root trust 외 추가 정밀화 (특정 cert 만 허용).
            let normalized = Config.normalizeThumbprint cert.Thumbprint
            if whitelist.Contains normalized then
                Log.audit.Info(sprintf "mtls: whitelist 통과 — subject=%s thumbprint=%s"
                    (Log.sanitizeForLog cert.Subject) normalized)
                true
            else
                Log.audit.Warn(sprintf "mtls: whitelist 미일치 — subject=%s thumbprint=%s"
                    (Log.sanitizeForLog cert.Subject) normalized)
                false
    let validateCert =
        match validationOverride with
        | Some f -> f
        | None -> productionValidate
    match mtls.Mode with
    | MtlsMode.Off ->
        httpsOpts.ClientCertificateMode <- ClientCertificateMode.NoCertificate
    | MtlsMode.Optional ->
        httpsOpts.ClientCertificateMode <- ClientCertificateMode.AllowCertificate
        httpsOpts.ClientCertificateValidation <- Func<X509Certificate2, X509Chain, SslPolicyErrors, bool>(validateCert)
    | MtlsMode.Required ->
        httpsOpts.ClientCertificateMode <- ClientCertificateMode.RequireCertificate
        httpsOpts.ClientCertificateValidation <- Func<X509Certificate2, X509Chain, SslPolicyErrors, bool>(validateCert)
    | other ->
        raise (InvalidDataException(sprintf "mtls.mode=%s — validateMtls 통과 후 진입했어야 함." other))

/// Phase S1 entry — config 로드 → DPAPI 복호화 → storage 초기화 → Kestrel HTTPS bind → auth middleware → endpoint.
///
/// Windows Service host (`UseWindowsService`) 로 등록되면 SCM 의 Start/Stop 이벤트로 lifecycle 관리.
/// console 실행 (개발/디버그) 도 동일 코드 — `WindowsServiceLifetime` 가 자동 fallback.
///
/// Phase S1 DoD: TLS bind 성공 + plain HTTP 거부 + DPAPI PSK 복호화 + auth middleware 가 빈 `GET /collections` 200 응답
///              + storage layout 초기화 + log4net 첫 라인 + EventLog service start 이벤트 + config schemaVersion check.

/// Phase S5e (s5e-r0) — main 의 WebApplication 빌드 로직을 pure 함수로 export.
/// IntegrationTests 가 자체 cfg + 평문 psk + in-memory self-signed cert 로 본 함수를 호출 →
/// DPAPI / file IO / log4net 의존 없이 실 Kestrel HTTPS bind round-trip e2e 검증 가능.
///
/// production main 측 책임 분리:
///   - main: (a) log4net + (b) config.json 로드 + (c) DPAPI 복호화 + (d) PFX file 로드 + (e) Storage.initialize → 본 함수 호출
///   - configureApp: cfg/psk/tlsCert 받아 builder 구성 → WebApplication 반환 (caller 가 app.Run/RunAsync)
///
/// storageRoot 는 cfg.StorageRoot 의 expandEnv 결과를 caller (main 또는 fixture) 가 미리 산출 후 cfg 에 박아 전달.
/// 본 함수 안에서는 추가 envvar 전개 안 함 (test 가 임의 temp dir 전달 시 envvar 우회).
/// **s6-r45 (C1)**: `embedderFactoryOverride` 인자 신규 — IT 의 mock IEmbeddingProvider 주입 path.
/// `None` 박제 시 cfg.Embedding 기반 OllamaEmbedder 정상 path (production main). `Some f` 박제 시 cfg.Embedding
/// 무시하고 본 factory 사용 — IT 가 daemon 의존 0 mock factory 박제 + hybrid retrieval round-trip 검증 가능.
let configureApp
        (cfg: ServiceConfig)
        (psk: string)
        (tlsCert: X509Certificate2)
        (embedderFactoryOverride: (unit -> Ds2.LightHouse.IEmbeddingProvider option) option)
        (mtlsValidationOverride: (X509Certificate2 -> X509Chain -> SslPolicyErrors -> bool) option)
        : WebApplication =

    let storageRoot = Config.expandEnv cfg.StorageRoot

    // WebApplication 빌드
    let builder = WebApplication.CreateBuilder()
    builder.Services.AddRouting() |> ignore
    builder.Services.AddSingleton(cfg) |> ignore
    builder.Services.AddHttpContextAccessor() |> ignore   // Phase S3 — AttachmentTools 의 HttpContext 접근 (SDK 1.2.0 자동 DI 검출).

    // **K2 (외부 review R5 합의)**: ASP.NET Core FormOptions default = MultipartBodyLengthLimit 134MB / ValueLengthLimit int.MaxValue.
    // Kestrel MaxRequestBodySize 만 박제하면 ReadFormAsync() 가 134MB 초과 시 InvalidDataException → 10GB upload (N6) 와 어긋남.
    // cfg.MaxUploadBytes 와 정합 박제 — multipart body 전체를 cfg 한도까지 허용.
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(fun (opts: Microsoft.AspNetCore.Http.Features.FormOptions) ->
        opts.MultipartBodyLengthLimit <- cfg.MaxUploadBytes
        opts.ValueLengthLimit <- Int32.MaxValue
        opts.MultipartHeadersLengthLimit <- 32768
    ) |> ignore

    // Phase S3 — SessionRegistry 가 ICollectionLifecycleNotifier 의 SSOT impl (S2 의 Logging swap).
    // resolver 는 storage 의 collection 디렉토리 조립 (Registry + Storage + ZipImport.collectionDirName).
    let attachmentResolver = AttachmentResolver.fromRegistry storageRoot
    builder.Services.AddSingleton<AttachmentResolver>(attachmentResolver) |> ignore

    // **s6-r48 #4** — production singleton embedder 의 ApplicationStopping hook 의무 박제용 ref capture.
    // override path 박제 시 None 유지 (mock 자체 lifecycle). build 후 lifetime hook 단계에서 본 ref 검사.
    let mutable singletonEmbedderForLifetime : IDisposable option = None

    // s6-r39 P4-C.3 / s6-r45 C1 / s6-r46 C2 / s6-r48 #4 — server-side hybrid retrieval 의 embedder factory.
    // override 박제 시 cfg.Embedding 무시 + 본 factory 사용 (IT mock path).
    // override 미박제 시 cfg.Embedding 기반 정상 path:
    //   - Enabled=false / config null → factory 가 항상 None (BM25-only fallback, Phase 1 동작 유지).
    //   - Enabled=true → **service-singleton OllamaEmbedder 1회 생성 + DI container own** + factory 가 매 호출
    //     마다 NonOwningEmbedder(singleton) 반환 (s6-r46 C2 — 자가 검열 Minor-3 정정, HttpClient socket
    //     exhaustion 회피). KB facade 는 wrap 만 own + dispose, inner singleton 은 WebApplication.Dispose 시
    //     DI container 가 동반 dispose.
    //   - backend 결함 (Ollama daemon down 등) 은 색인/검색 시점 lazy fail-fast (singleton 생성 자체는 throw 안 함).
    let embedderFactory : unit -> Ds2.LightHouse.IEmbeddingProvider option =
        match embedderFactoryOverride with
        | Some f ->
            Log.service.Info("C1: embedderFactory override 박제 (test path) — cfg.Embedding 무시")
            f
        | None ->
            let emb = cfg.Embedding
            if not emb.Enabled then
                fun () -> None
            elif String.IsNullOrWhiteSpace emb.BaseUrl
                 || String.IsNullOrWhiteSpace emb.Model
                 || emb.Dimension <= 0 then
                Log.service.Warn(
                    sprintf "P4-C.3: embedding config validation 실패 (baseUrl='%s' model='%s' dim=%d) — BM25-only fallback"
                        emb.BaseUrl emb.Model emb.Dimension)
                fun () -> None
            else
                Log.service.Info(
                    sprintf "C2: server-side embedding 활성 (service-singleton + NonOwning wrap) — baseUrl=%s model=%s dim=%d"
                        emb.BaseUrl emb.Model emb.Dimension)
                // **lifecycle (s6-r46 C2 + s6-r48 #4)**: singleton 은 configureApp closure capture. build 후
                // IHostApplicationLifetime.ApplicationStopping hook 박제 (graceful shutdown 시 명시 Dispose).
                // process kill 시는 OS 회수 정합 (HttpClient leak 누적 0 — 단일 instance).
                let singleton =
                    new Ds2.LightHouse.Ollama.OllamaEmbedder(emb.BaseUrl, emb.Model, emb.Dimension)
                    :> Ds2.LightHouse.IEmbeddingProvider
                singletonEmbedderForLifetime <- Some (singleton :> IDisposable)
                fun () ->
                    Some (new Ds2.LightHouse.NonOwningEmbedder(singleton) :> Ds2.LightHouse.IEmbeddingProvider)

    let sessionRegistry = SessionRegistry(attachmentResolver, embedderFactory)
    builder.Services.AddSingleton<ISessionRegistry>(sessionRegistry :> ISessionRegistry) |> ignore
    // ICollectionLifecycleNotifier 도 같은 instance — Phase S2 collection mutation API 가 본 notifier 호출.
    builder.Services.AddSingleton<ICollectionLifecycleNotifier>(fun sp ->
        sp.GetRequiredService<ISessionRegistry>() :> ICollectionLifecycleNotifier) |> ignore

    // Phase S2 — staging sweep BackgroundService.
    builder.Services.AddHostedService<StagingSweepService>() |> ignore
    // Phase S3 — session idle TTL sweep BackgroundService (L2-3).
    builder.Services.AddHostedService<SessionSweepService>() |> ignore

    // Phase S7 D-S7-2 (s6-r27) — in-memory EventBus (SSE pub-sub). process-wide singleton.
    builder.Services.AddSingleton<EventBus>() |> ignore

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
            // **D-S7-1 (s6-r53)** — mTLS client cert auth. mode="off" 박제 시 NoCertificate (현행). mode=
            // "optional"/"required" 시 ClientCertificateMode + validation callback 박제. validateMtls 가
            // main 단계에서 mode 정합 + AllowedThumbprints normalize 처리 완료.
            listenOptions.UseHttps(tlsCert, fun (httpsOpts: HttpsConnectionAdapterOptions) ->
                configureMtls cfg.Mtls mtlsValidationOverride httpsOpts) |> ignore)
    ) |> ignore

    // Windows Service host — 콘솔 실행 시 자동 fallback (UseWindowsService 가 SCM 미검출 시 console lifetime)
    builder.Host.UseWindowsService() |> ignore

    let app = builder.Build()

    // **s6-r48 #4** — production singleton embedder 의 graceful shutdown Dispose 박제.
    // IHostApplicationLifetime.ApplicationStopped 가 모든 in-flight request drain + Kestrel listener 종료 후 호출
    // (30s graceful drain 안전). process kill (taskkill /F) 시는 hook 안 호출 — OS 회수 정합.
    // override path 박제 시 본 ref 가 None 이라 hook 박제 안 함.
    // **s6-r70 review C-9**: ApplicationStopping → ApplicationStopped 정정 — Stopping 은 drain 시작 시점이라
    // in-flight embedder.GenerateAsync 호출 중 HttpClient dispose race 가능. Stopped 는 모든 request drain 후 안전.
    match singletonEmbedderForLifetime with
    | Some disposable ->
        let lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()
        lifetime.ApplicationStopped.Register(fun () ->
            Log.service.Info("#4: ApplicationStopped — service-singleton embedder Dispose")
            try disposable.Dispose() with ex ->
                Log.service.Warn(sprintf "#4: singleton embedder Dispose 실패 — %s: %s" (ex.GetType().Name) ex.Message)) |> ignore
    | None -> ()

    // 1. Public endpoints (인증 무관) — health probe 등. middleware 진입 전에 매핑.
    app.UseRouting() |> ignore
    Endpoints.mapPublic app

    // 2. 인증 middleware — Bearer PSK + X-User-Identity 검증 (모든 인증 endpoint 공통)
    // **s6-r70 review C-3**: cfg 인자 추가 — mtls.mode != off 시 cert subject CN ↔ X-User-Identity 강제 검증.
    app.Use(AuthMiddleware.middleware cfg psk) |> ignore

    // 3. 인증 통과 endpoint — Phase S2 collection 관리 API + Phase S3 session 발급/해제
    let notifier = app.Services.GetRequiredService<ICollectionLifecycleNotifier>()
    let registry = app.Services.GetRequiredService<ISessionRegistry>()
    let eventBus = app.Services.GetRequiredService<EventBus>()  // Phase S7 D-S7-2 (s6-r27)
    CollectionEndpoints.map app cfg notifier eventBus
    // **s6-r66 D-S7-4** — SessionEndpoints.map 시그니처 변경 (cfg 인자 추가, multi-tenant filter SSOT).
    SessionEndpoints.map app cfg registry
    // Phase S4 — file serving (citation 원문 stream, §3.9 / §4.2 Phase S4 / D6).
    // **s6-r70 review C-1**: cfg 인자 추가 (multi-tenant filter SSOT).
    FileServing.map app cfg storageRoot
    // Phase S7 D-S7-2 (s6-r27) — `GET /events` SSE endpoint (collection-added/updated/deleted + keepalive).
    // **s6-r70 review C-2** — cfg + storageRoot 인자 추가 (multi-tenant filter SSOT).
    EventsEndpoint.map app cfg storageRoot eventBus
    // Phase S7 D-S7-5 (s6-r60) — resumable chunked upload endpoint scaffold (POST/PATCH/finalize/GET/DELETE).
    // **phase 4 (s6-r74 b1)** — notifier 인자 추가 (finalize swap path 의 OnPayloadSwapped 호출 의무).
    UploadsEndpoint.map app cfg storageRoot notifier eventBus
    // **Phase S7 D-S7-4 admin endpoint (B-S7-4, s6-r71+)** — multi-tenant T2/T3 실 활용 path.
    // POST /admin/collections/{id}/owner (ImportedBy stamp) + PUT /admin/collections/{id}/acl (acl 편집).
    // 권한 = AuthMiddleware 통과한 모든 user (single trust pool, 별 admin-only ACL 은 backlog).
    AdminEndpoints.map app storageRoot

    // 4. Phase S3 MCP HTTP transport (`/mcp` prefix) — SessionAuth 미들웨어 추가 통과 후 진입.
    //    `UseWhen` 으로 MCP path 만 session 검증 (POST/DELETE /sessions 는 token 발급 자체라 본 미들웨어 통과 안 함).
    app.UseWhen(
        (fun ctx -> ctx.Request.Path.StartsWithSegments(PathString "/mcp")),
        (fun (app2: IApplicationBuilder) ->
            app2.Use(SessionAuth.middleware registry) |> ignore)
    ) |> ignore
    app.MapMcp("/mcp") |> ignore

    app

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

    let rawCfg = Config.load configPath
    Config.validateHttpsOnly rawCfg
    // **s6-r53 D-S7-1** — mtls.mode 정합 + AllowedThumbprints normalize. fail-fast (config 결함은 install 안내).
    let cfgMtls = Config.validateMtls rawCfg
    // **s6-r66 D-S7-4** — multiTenant.mode 정합 + normalize. fail-fast (config 결함은 reinstall 안내).
    let cfg = Config.validateMultiTenant cfgMtls
    Log.service.Info(sprintf "D-S7-1: mtls mode=%s whitelist=%d" cfg.Mtls.Mode cfg.Mtls.AllowedThumbprints.Length)
    Log.service.Info(sprintf "D-S7-4: multiTenant mode=%s" cfg.MultiTenant.Mode)

    let psk = Config.decryptDpapi cfg.PreSharedKeyEncrypted
    let tlsCertPassword = Config.decryptDpapi cfg.TlsCertPasswordEncrypted

    // storage layout 초기화 — fail-fast (permission 미달 시 reraise)
    let resolvedStorageRoot = Storage.initialize (Config.expandEnv cfg.StorageRoot)
    Log.service.Info(sprintf "storage root 초기화 완료 — %s" resolvedStorageRoot)

    // TLS 인증서 로드 — Kestrel HTTPS-only
    let tlsCertPath = Config.expandEnv cfg.TlsCertPath
    if not (File.Exists tlsCertPath) then
        raise (FileNotFoundException(sprintf "TLS 인증서 미존재 — %s" tlsCertPath, tlsCertPath))
    // .NET 9 권장 API — X509Certificate2 constructor 는 obsolete (FS0044). PFX path + password 전용 로더.
    let tlsCert = X509CertificateLoader.LoadPkcs12FromFile(tlsCertPath, tlsCertPassword)
    Log.service.Info(sprintf "TLS 인증서 로드 완료 — subject=%s thumbprint=%s"
        tlsCert.Subject tlsCert.Thumbprint)

    // Phase S5e — production 부팅의 (a)~(e) 단계 완료 후 pure builder 함수에 위임.
    // s6-r45 C1 — embedderFactoryOverride=None (cfg 기반 production path).
    // s6-r69 B5 phase 4 — mtlsValidationOverride=None (chain.Build + whitelist production path).
    let app = configureApp cfg psk tlsCert None None

    Log.service.Info(sprintf "Kestrel HTTPS listen 시작 — %s (maxUploadBytes=%d)"
        cfg.ListenUrl cfg.MaxUploadBytes)
    // service start 는 EventLog 에도 박제 (운영자가 SCM 에서 즉시 인지) — Warn level 로 m10 의 threshold 통과.
    Log.service.Warn(sprintf "Ds2.LightHouseService 시작 완료 — %s" cfg.ListenUrl)
    Log.audit.Info(sprintf "service start — listenUrl=%s storageRoot=%s" cfg.ListenUrl resolvedStorageRoot)

    app.Run()

    0
