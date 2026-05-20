namespace Ds2.LightHouseService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Security.Cryptography

/// service config (todo-lighthouse-kb-server.md §3.11 SSOT).
///
/// 위치: `%PROGRAMDATA%\Dualsoft\LightHouseService\config.json`. install-service.ps1 이 사용자 입력 받아 작성.
/// 본 record 의 모든 필드는 §3.11 SSOT 와 1:1.
type IndexerVersionRange = {
    [<JsonPropertyName("min")>] Min: string
    [<JsonPropertyName("max")>] Max: string
}

/// **Phase 4 P4-C.3 (s6-r39)** — server-side embedding config (hybrid retrieval 시 query embedding 생성).
///
/// Enabled=false / 본 record null 시 BM25-only fallback (Phase 1 동작 유지). Enabled=true 시 SessionRegistry
/// 가 매 session attach 마다 새 `OllamaEmbedder` 생성 → facade own → session dispose 시 동반 dispose
/// (per-session lifecycle, 본 turn 단순화 박제. service-singleton non-owning 패턴은 backlog).
///
/// Ollama 는 localhost unauth 가정 → API key 박제 0. 향후 OpenAI/Anthropic embedding API 도입 시 별도
/// ApiKeyEncrypted 필드 추가 + DPAPI(LocalMachine) 박제 패턴 (TlsCertPasswordEncrypted 와 동일).
type EmbeddingConfigSection = {
    [<JsonPropertyName("enabled")>] Enabled: bool
    [<JsonPropertyName("baseUrl")>] BaseUrl: string
    [<JsonPropertyName("model")>] Model: string
    [<JsonPropertyName("dimension")>] Dimension: int
}

type ServiceConfig = {
    /// config schema 자체 버전. service binary upgrade 시 migration trigger (S1 DoD).
    [<JsonPropertyName("schemaVersion")>] SchemaVersion: int
    /// HTTPS bind URL — `http://` prefix 는 fail-fast (§3.7).
    [<JsonPropertyName("listenUrl")>] ListenUrl: string
    [<JsonPropertyName("tlsCertPath")>] TlsCertPath: string
    /// DPAPI (LocalMachine) base64 — 평문 저장 금지 (CR4).
    [<JsonPropertyName("tlsCertPasswordEncrypted")>] TlsCertPasswordEncrypted: string
    /// DPAPI (LocalMachine) base64 — 평문 저장 금지 (CR4).
    [<JsonPropertyName("preSharedKeyEncrypted")>] PreSharedKeyEncrypted: string
    /// storage root. envvar (`%PROGRAMDATA%`) 전개는 본 lib 측이 책임.
    [<JsonPropertyName("storageRoot")>] StorageRoot: string
    /// Kestrel MaxRequestBodySize. default 10 GB (N6).
    [<JsonPropertyName("maxUploadBytes")>] MaxUploadBytes: int64
    /// zip bomb 가드 (해제 byte / 압축 byte 비율). default 50:1.
    [<JsonPropertyName("zipBombRatioLimit")>] ZipBombRatioLimit: int
    /// session idle TTL (L2-3 backstop).
    [<JsonPropertyName("sessionIdleTtlMinutes")>] SessionIdleTtlMinutes: int
    [<JsonPropertyName("stagingSweepIntervalMinutes")>] StagingSweepIntervalMinutes: int
    /// log4net RollingFile retention.
    [<JsonPropertyName("logRetentionDays")>] LogRetentionDays: int
    [<JsonPropertyName("logMaxSizeMB")>] LogMaxSizeMB: int
    /// Audit log 별 retention (보안 추적, 권장 365 일).
    [<JsonPropertyName("auditRetentionDays")>] AuditRetentionDays: int
    /// §3.12 IndexerVersion gate. upload 시점 client 가 만든 index.db 의 Meta.indexer_version 검증.
    [<JsonPropertyName("indexerVersionRange")>] IndexerVersionRange: IndexerVersionRange
    /// **Phase 4 P4-C.3 (s6-r39)** — server-side hybrid retrieval 의 embedder backend (nullable).
    /// null / Enabled=false → BM25-only fallback. schemaVersion bump (1→2) 동반.
    [<JsonPropertyName("embedding")>] Embedding: EmbeddingConfigSection
}


/// 본 service binary 가 인식하는 config schemaVersion (§3.11 SSOT 와 1:1).
/// config 파일의 값이 본 값보다 *낮으면* in-place migration (backup → upgrade), *높으면* fail-fast.
[<RequireQualifiedAccess>]
module ConfigSchema =
    // s6-r39 P4-C.3: 1 → 2 bump (embedding section 신설). legacy schemaVersion=1 config 는 load 단계에서
    // migration in-place 진입 — embedding 필드 자동 채움 (Enabled=false, default URL/model/dim).
    [<Literal>]
    let Current = 2


[<RequireQualifiedAccess>]
module Config =

    /// 기본 config 위치 — `%PROGRAMDATA%\Dualsoft\LightHouseService\config.json`.
    /// CommonApplicationData = `C:\ProgramData` (Windows). 다른 OS 는 본 service 의 지원 대상 아님.
    let defaultPath () : string =
        let programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        Path.Combine(programData, "Dualsoft", "LightHouseService", "config.json")

    let private jsonOptions () =
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)
        opts.Converters.Add(JsonStringEnumConverter())
        opts

    /// DPAPI (LocalMachine) base64 → 평문 (CR4). install-service.ps1 / config 가 LocalMachine scope 사용.
    /// 다른 scope (CurrentUser) 로 암호화된 값 받으면 CryptographicException — fail-fast.
    ///
    /// 빈 base64 (= 미설치) → ArgumentException (caller 가 install script 안내).
    let decryptDpapi (base64: string) : string =
        if String.IsNullOrWhiteSpace base64 then
            raise (ArgumentException("DPAPI 암호화 값이 비어있음 — install-service.ps1 실행 필요"))
        let cipher = Convert.FromBase64String base64
        let plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine)
        Encoding.UTF8.GetString plain

    /// envvar (`%PROGRAMDATA%`, `%USERPROFILE%` 등) 전개. Windows 의 path literal 안전 처리.
    let expandEnv (s: string) : string =
        if String.IsNullOrEmpty s then s else Environment.ExpandEnvironmentVariables s

    /// config.json 파일 → ServiceConfig + schema_version check (S1 DoD).
    ///
    /// schemaVersion 검증:
    ///   - missing / 0 → fail-fast (corrupt config)
    ///   - schemaVersion < Current → migration in-place (Phase S1 = 1 만 — migration 미작성, 향후 schema bump 시 추가)
    ///   - schemaVersion > Current → fail-fast (service binary 가 낮음 — 사용자 binary 업그레이드 안내)
    ///   - schemaVersion = Current → OK
    let load (path: string) : ServiceConfig =
        if not (File.Exists path) then
            raise (FileNotFoundException(
                sprintf "Service config 미존재 — %s. install-service.ps1 실행 필요." path, path))

        let json = File.ReadAllText(path, Encoding.UTF8)
        let cfg = JsonSerializer.Deserialize<ServiceConfig>(json, jsonOptions())
        if obj.ReferenceEquals(cfg, null) then
            raise (InvalidDataException(sprintf "Service config 역직렬화 실패 — %s" path))

        match cfg.SchemaVersion with
        | v when v = ConfigSchema.Current -> cfg
        | 1 ->
            // s6-r39 P4-C.3: 1 → 2 in-place migration — embedding 필드 자동 채움 (Enabled=false 박제, BM25-only
            // fallback 유지 = 회귀 0). 사용자가 활성화하려면 config.json 의 embedding section 수동 편집 (또는
            // install-service.ps1 갱신 박제).
            let migrated = {
                cfg with
                    SchemaVersion = ConfigSchema.Current
                    Embedding = {
                        Enabled = false
                        BaseUrl = "http://localhost:11434"
                        Model = "bge-m3"
                        Dimension = 1024
                    }
            }
            migrated
        | v when v < ConfigSchema.Current ->
            raise (InvalidDataException(
                sprintf "Service config schemaVersion=%d 이 너무 낮음 — migration 미정의 (current=%d)"
                    v ConfigSchema.Current))
        | v ->
            raise (InvalidDataException(
                sprintf "Service config schemaVersion=%d > service binary supported=%d — binary 업그레이드 필요"
                    v ConfigSchema.Current))

    /// `listenUrl` 의 `http://` prefix fail-fast (§3.7 plain HTTP 거부).
    let validateHttpsOnly (cfg: ServiceConfig) =
        if String.IsNullOrWhiteSpace cfg.ListenUrl then
            raise (InvalidDataException("listenUrl 가 비어있음"))
        if cfg.ListenUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) then
            raise (InvalidDataException(
                sprintf "listenUrl=%s — plain HTTP 거부 (§3.7). https:// 만 허용." cfg.ListenUrl))
