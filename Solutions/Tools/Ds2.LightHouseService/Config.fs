namespace Ds2.LightHouseService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Security.Cryptography
open Ds2.LightHouse.Protocol

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

/// **Phase S7 D-S7-4 (s6-r66)** — multi-tenant policy config.
///
/// Mode 박제:
///   - "T1" — flat (default, 현행). 모든 collection 이 모든 user 에게 visible+writable. registry.collections[].acl 무시.
///   - "T2" — per-user namespace. `ImportedBy` 가 X-User-Identity 와 일치하는 entry 만 visible+writable. 빈 ImportedBy
///     (legacy entry, T1 시절 등록) 은 전체 공개 유지 (사용자 결정: 마이그레이션 부담 0, admin 수동 이전 의무).
///   - "T3" — collection ACL. registry.collections[].acl = `{users: [...], readOnly: bool}` 검증. acl 누락 = 전체 공개
///     (legacy / T1 시절 등록). acl.users 비어있음 = 전체 공개. acl.users 가 X-User-Identity 포함하면 visible.
///     readOnly=true 면 mutation (payload swap / delete) reject.
///
/// 진입 trigger = PII 격리 요구 발생 시 (사전 박제). T1→T2/T3 전환 시 admin 이 config.json 편집 + 기존 entry 의 acl
/// 박제 의무 (legacy fallback 으로 안전망 유지).
type MultiTenantConfigSection = {
    [<JsonPropertyName("mode")>] Mode: string
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
    /// **Phase S7 D-S7-4 (s6-r66)** — T2/T3 multi-tenant opt-in. schemaVersion bump (3→4) 동반.
    /// Mode="T1" / null → 현행 (flat, 회귀 0). "T2"/"T3" → registry filter + acl 검증 진입.
    [<JsonPropertyName("multiTenant")>] MultiTenant: MultiTenantConfigSection
}


/// 본 service binary 가 인식하는 config schemaVersion (§3.11 SSOT 와 1:1).
/// config 파일의 값이 본 값보다 *낮으면* in-place migration (backup → upgrade), *높으면* fail-fast.
[<RequireQualifiedAccess>]
module ConfigSchema =
    // s6-r39 P4-C.3: 1 → 2 bump (embedding section 신설).
    // s6-r53 D-S7-1:  2 → 3 bump (mtls section 신설).
    // s6-r66 D-S7-4:  3 → 4 bump (multiTenant section 신설). legacy schemaVersion=1/2/3 config 는 load 단계에서
    // chain migration 진입 — embedding 필드 (Enabled=false) + mtls 필드 (Mode="off") + multiTenant 필드 (Mode="T1")
    // 자동 채움 (모두 회귀 0 default).
    [<Literal>]
    let Current = 4

/// **Phase S7 D-S7-1 (s6-r53)** — mTLS mode literal SSOT (config + Program.fs + tests 정합).
[<RequireQualifiedAccess>]
module MtlsMode =
    [<Literal>]
    let Off = "off"
    [<Literal>]
    let Optional = "optional"
    [<Literal>]
    let Required = "required"

/// **Phase S7 D-S7-4 (s6-r66)** — multi-tenant mode literal SSOT (config + MultiTenantPolicy + tests 정합).
[<RequireQualifiedAccess>]
module MultiTenantMode =
    [<Literal>]
    let T1 = "T1"
    [<Literal>]
    let T2 = "T2"
    [<Literal>]
    let T3 = "T3"


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
    /// **s6-r66 D-S7-4 (scope 확장)**: in-place migration chain (v1→v2→v3→v4) 제거. paired-release `/dist`
    /// 실행 0회 = prod 배포 0건 = legacy schemaVersion 의 config.json 가 존재하는 인스턴스 0건. migration path
    /// 가 dead code (사용자 지적 정합). 정책 단순화 — schemaVersion ≠ Current 면 모두 fail-fast + reinstall 안내.
    /// dev local 의 stale config 는 admin 이 수동 reinstall 의무 (install-service.ps1 가 fresh template 배포).
    ///
    /// schemaVersion 검증:
    ///   - missing / 0 → fail-fast (corrupt config)
    ///   - schemaVersion ≠ Current → fail-fast (reinstall 안내)
    ///   - schemaVersion = Current → OK
    let load (path: string) : ServiceConfig =
        if not (File.Exists path) then
            raise (FileNotFoundException(
                sprintf "Service config 미존재 — %s. install-service.ps1 실행 필요." path, path))

        let json = File.ReadAllText(path, Encoding.UTF8)
        let cfg = JsonSerializer.Deserialize<ServiceConfig>(json, jsonOptions())
        if obj.ReferenceEquals(cfg, null) then
            raise (InvalidDataException(sprintf "Service config 역직렬화 실패 — %s" path))

        if cfg.SchemaVersion = ConfigSchema.Current then cfg
        else
            raise (InvalidDataException(
                sprintf "Service config schemaVersion=%d ≠ service binary supported=%d — install-service.ps1 reinstall 필요"
                    cfg.SchemaVersion ConfigSchema.Current))

    /// `listenUrl` 의 `http://` prefix fail-fast (§3.7 plain HTTP 거부).
    let validateHttpsOnly (cfg: ServiceConfig) =
        if String.IsNullOrWhiteSpace cfg.ListenUrl then
            raise (InvalidDataException("listenUrl 가 비어있음"))
        if cfg.ListenUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) then
            raise (InvalidDataException(
                sprintf "listenUrl=%s — plain HTTP 거부 (§3.7). https:// 만 허용." cfg.ListenUrl))

    /// **N-1 (s6-r74 c)** — thumbprint normalize SSOT 위임. Protocol `CertValidator.normalize` (strict, client behavior
    /// 정합) 직접 호출. 본 wrapper 는 caller routing 호환 한정 유지 — 추후 caller 가 Protocol 직접 호출 시 폐기 가능.
    /// 의미 변경: 비-hex 문자 silent strip → 즉시 빈 string + 길이 40/64 검증을 Normalize 자체에서 박제.
    let normalizeThumbprint (s: string) : string =
        CertValidator.normalize s

    /// **Phase S7 D-S7-1 (s6-r53) + N-1 (s6-r74 c)** — `Mtls.Mode` 값 검증 + AllowedThumbprints normalize.
    /// 부적합 시 fail-fast (config 결함 — install script 안내).
    /// **N-1 변경**: Protocol `CertValidator.normalize` SSOT. raw entry 별 normalize → 빈 string 시 reject
    /// (정합 결함, 길이/형식 둘 다 검증 후 empty). 기존 silent skip 박제 폐기 — strict fail-fast 의무.
    let validateMtls (cfg: ServiceConfig) : ServiceConfig =
        // **R6-M1 (s6-r76, external review backlog)** — `cfg.Mtls` null guard. schema=4 (s6-r66) 박제 이후
        // ConfigSchema.load 가 항상 default 박제 → 실 NRE risk 0. defense-in-depth (config bypass / future
        // schema 의 nullable optional 도입 시 회귀 차단). validateMultiTenant 와 정합 박제.
        let mtls =
            if obj.ReferenceEquals(box cfg.Mtls, null) then { Mode = MtlsMode.Off; AllowedThumbprints = Array.empty }
            else cfg.Mtls
        let mode = if isNull mtls.Mode then "" else mtls.Mode.Trim().ToLowerInvariant()
        match mode with
        | "" | "off" | "optional" | "required" -> ()
        | other ->
            raise (InvalidDataException(
                sprintf "mtls.mode=%s — \"off\"|\"optional\"|\"required\" 만 허용." other))
        let normalizedMode = if mode = "" then MtlsMode.Off else mode
        let raw = if isNull mtls.AllowedThumbprints then Array.empty else mtls.AllowedThumbprints
        let tps =
            raw
            |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
            |> Array.map (fun s ->
                let normalized = CertValidator.normalize s
                if String.IsNullOrEmpty normalized then
                    raise (InvalidDataException(
                        sprintf "mtls.allowedThumbprints 결함 — '%s' (hex 40/64 길이 + ':' / 공백 / hyphen / tab 외 non-hex 거부)" s))
                normalized)
        { cfg with Mtls = { Mode = normalizedMode; AllowedThumbprints = tps } }

    /// **Phase S7 D-S7-4 (s6-r66)** — `MultiTenant.Mode` 값 검증 + normalize.
    /// 부적합 시 fail-fast (config 결함 — admin 안내). null/"" → "T1" default 정합.
    let validateMultiTenant (cfg: ServiceConfig) : ServiceConfig =
        let mode =
            if obj.ReferenceEquals(box cfg.MultiTenant, null) || isNull cfg.MultiTenant.Mode then ""
            else cfg.MultiTenant.Mode.Trim()
        let normalized =
            match mode with
            | "" -> MultiTenantMode.T1
            | "T1" | "T2" | "T3" -> mode
            | "t1" | "t2" | "t3" -> mode.ToUpperInvariant()
            | other ->
                raise (InvalidDataException(
                    sprintf "multiTenant.mode=%s — \"T1\"|\"T2\"|\"T3\" 만 허용." other))
        { cfg with MultiTenant = { Mode = normalized } }
