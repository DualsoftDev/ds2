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

/// **Phase S7 D-S7-1 (s6-r53)** — mTLS (client certificate auth) config.
///
/// Mode 박제:
///   - "off" — Kestrel ClientCertificateMode.NoCertificate (현행). PSK 단독 인증. default.
///   - "optional" — ClientCertificateMode.AllowCertificate. cert 박제 시 chain + whitelist 검증 + audit log
///     박제. PSK 는 항상 추가 검증 (defense-in-depth). cert 미박제도 통과 (PSK 만 검증).
///   - "required" — ClientCertificateMode.RequireCertificate. TLS handshake 단계에서 cert 미박제 = connection
///     refused (HTTP 응답 자체 안 옴). chain + whitelist 검증 + PSK 동시 검증.
///
/// AllowedThumbprints 박제:
///   - 빈 배열 / null → chain 검증만 (사내 CA root trust 의존). default.
///   - thumbprint hex (대소문자 무관, ':' / 공백 허용) 리스트 → cert.Thumbprint 가 normalize 후 본 리스트에
///     존재해야 통과. whitelist 외 cert 는 chain valid 라도 401 audit + rejected.
///
/// PSK fallback 공존 — D-S7-1 박제 ("PSK 는 fallback 으로 잠시 공존 후 단계적 제거"). 본 turn 은 PSK 항상
/// 검증 (mTLS layer 가 추가 defense-in-depth). 단계적 제거 trigger 는 별 turn (mtls.mode="required" 정합
/// 후 PSK 검증 skip option 추가).
type MtlsConfigSection = {
    [<JsonPropertyName("mode")>] Mode: string
    [<JsonPropertyName("allowedThumbprints")>] AllowedThumbprints: string[]
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
    /// **Phase S7 D-S7-1 (s6-r53)** — mTLS client cert auth. schemaVersion bump (2→3) 동반.
    /// Mode="off" / null → 현행 (PSK 단독). "optional"/"required" → Kestrel ClientCertificateMode 진입.
    [<JsonPropertyName("mtls")>] Mtls: MtlsConfigSection
}


/// 본 service binary 가 인식하는 config schemaVersion (§3.11 SSOT 와 1:1).
/// config 파일의 값이 본 값보다 *낮으면* in-place migration (backup → upgrade), *높으면* fail-fast.
[<RequireQualifiedAccess>]
module ConfigSchema =
    // s6-r39 P4-C.3: 1 → 2 bump (embedding section 신설).
    // s6-r53 D-S7-1:  2 → 3 bump (mtls section 신설). legacy schemaVersion=1/2 config 는 load 단계에서
    // migration in-place 진입 — embedding 필드 (Enabled=false) + mtls 필드 (Mode="off") 자동 채움.
    [<Literal>]
    let Current = 3

/// **Phase S7 D-S7-1 (s6-r53)** — mTLS mode literal SSOT (config + Program.fs + tests 정합).
[<RequireQualifiedAccess>]
module MtlsMode =
    [<Literal>]
    let Off = "off"
    [<Literal>]
    let Optional = "optional"
    [<Literal>]
    let Required = "required"


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

        // s6-r53 D-S7-1: 1→2→3 chain migration. 각 step 별 helper (default 채움) → 재귀 1회 step. legacy
        // schemaVersion=1 → 2 (Embedding default) → 3 (Mtls default) 모두 회귀 0 (Enabled=false / Mode="off"
        // 박제로 현행 동작 유지). schemaVersion=2 → 3 단일 step.
        let migrate1to2 (c: ServiceConfig) : ServiceConfig =
            // s6-r39 P4-C.3: embedding 필드 자동 채움 (Enabled=false BM25-only fallback).
            { c with
                SchemaVersion = 2
                Embedding = {
                    Enabled = false
                    BaseUrl = "http://localhost:11434"
                    Model = "bge-m3"
                    Dimension = 1024 } }
        let migrate2to3 (c: ServiceConfig) : ServiceConfig =
            // s6-r53 D-S7-1: mtls 필드 자동 채움 (Mode="off" 현행 동작 유지).
            { c with
                SchemaVersion = 3
                Mtls = {
                    Mode = MtlsMode.Off
                    AllowedThumbprints = Array.empty } }

        match cfg.SchemaVersion with
        | v when v = ConfigSchema.Current -> cfg
        | 1 -> cfg |> migrate1to2 |> migrate2to3
        | 2 -> cfg |> migrate2to3
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

    /// thumbprint normalize — hex 추출 후 대문자. 입력의 ':' / 공백 / hyphen 제거. 비-hex 문자가 있으면
    /// 그대로 반환 (validateMtls 에서 별도 거부). cert.Thumbprint 의 .NET default 가 대문자 hex 라 정합.
    let normalizeThumbprint (s: string) : string =
        if isNull s then "" else
        let sb = StringBuilder(s.Length)
        for ch in s do
            if (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F') || (ch >= 'a' && ch <= 'f') then
                sb.Append(Char.ToUpperInvariant ch) |> ignore
        sb.ToString()

    /// **Phase S7 D-S7-1 (s6-r53)** — `Mtls.Mode` 값 검증 + AllowedThumbprints normalize.
    /// 부적합 시 fail-fast (config 결함 — install script 안내).
    let validateMtls (cfg: ServiceConfig) : ServiceConfig =
        let mode = if isNull cfg.Mtls.Mode then "" else cfg.Mtls.Mode.Trim().ToLowerInvariant()
        match mode with
        | "" | "off" | "optional" | "required" -> ()
        | other ->
            raise (InvalidDataException(
                sprintf "mtls.mode=%s — \"off\"|\"optional\"|\"required\" 만 허용." other))
        let normalizedMode = if mode = "" then MtlsMode.Off else mode
        let tps =
            (if isNull cfg.Mtls.AllowedThumbprints then Array.empty else cfg.Mtls.AllowedThumbprints)
            |> Array.map normalizeThumbprint
            |> Array.filter (fun s -> s.Length > 0)
        // 각 thumbprint 가 SHA-1 (40 hex) 또는 SHA-256 (64 hex) 길이. 다른 길이 = config 결함.
        for tp in tps do
            if tp.Length <> 40 && tp.Length <> 64 then
                raise (InvalidDataException(
                    sprintf "mtls.allowedThumbprints 항목 길이=%d (정상 SHA-1=40 / SHA-256=64 hex) — %s" tp.Length tp))
        { cfg with Mtls = { Mode = normalizedMode; AllowedThumbprints = tps } }
