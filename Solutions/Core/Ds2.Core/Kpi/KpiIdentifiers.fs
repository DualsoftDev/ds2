namespace Ds2.Core.Kpi

open System
open System.Security.Cryptography
open System.Text
open Ds2.Core

/// KPI SME · SignalId · Namespace 식별자 생성기. 모든 이름은 결정론적.
///
/// - `entityHash8` : Fqdn(전역 유일 이름) → SHA1 8자 (Guid 대신 이름 기반이라
///   엔티티 재생성/이관에도 안정)
/// - `idShort`     : AAS SubmodelElement idShort (128자 이내 검증됨)
/// - `signalId`    : Ds2.Core.SignalId — kebab-case, lowercase, [a-z0-9-_.]
[<RequireQualifiedAccess>]
module KpiIdentifiers =

    /// Fqdn(전역 유일한 문자열) 을 SHA1 하고 앞 8자를 소문자 hex 로 반환.
    /// SHA1.HashData 정적 API 사용 (스레드 세이프).
    let entityHash8 (fqdn: string) : string =
        let bytes = Encoding.UTF8.GetBytes(fqdn |> string)
        let hash = SHA1.HashData(bytes)
        let sb = StringBuilder(8)
        for i = 0 to 3 do sb.AppendFormat("{0:x2}", hash.[i]) |> ignore
        sb.ToString()

    /// 엔티티 타입별 짧은 프리픽스 (idShort · signalId 에 삽입).
    let entityShort (kind: KpiEntityKind) : string =
        match kind with
        | SystemKind    -> "Sys"
        | WorkKind      -> "Wk"
        | CallKind      -> "Ac"
        | ArrowWorkKind -> "Arw"
        | UserTagKind   -> "Tag"

    /// AAS idShort 는 `[A-Za-z_][A-Za-z0-9_]*` 만 허용. 사용자 유래 문자열 (UserTag 이름 등)에
    /// 특수문자 · 공백 · 한글이 섞일 수 있어 안전하게 sanitize 후 뒤에 짧은 hash 를 붙여 유일성 유지.
    let private sanitizeIdShortSegment (raw: string) : string =
        if System.String.IsNullOrEmpty raw then "x"
        else
            let sb = StringBuilder(raw.Length)
            for c in raw do
                if (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_' then
                    sb.Append(c) |> ignore
                else
                    sb.Append('_') |> ignore
            let s = sb.ToString()
            // 유니크성 보강: raw 의 hash 를 뒤에 8-hex 로 추가.
            // 이렇게 하면 "#100 포지션.리프트" · "#100_포지션.리프트" 등 sanitize 결과가 겹쳐도
            // 원본 raw 가 다르면 최종 idShort 는 다름.
            let uniq = entityHash8 raw
            let head = if s.Length > 40 then s.Substring(0, 40) else s
            sprintf "%s_%s" head uniq

    /// AAS SME IdShort:  `Kpi_{typeShort}_{hash8}_{sanitizedMetric}`
    /// 예) `Kpi_Sys_a1b2c3d4_OEE_xxxxxxxx`, `Kpi_Tag_deadbeef_100_포지션..._yyyyyyyy → sanitized`
    let idShort (kind: KpiEntityKind) (entityFqdn: string) (metricSuffix: string) : string =
        sprintf "Kpi_%s_%s_%s" (entityShort kind) (entityHash8 entityFqdn) (sanitizeIdShortSegment metricSuffix)

    /// SignalId 유효 문자 [a-z0-9-_.] 만 허용. 그 외는 '_' 로 치환.
    let private sanitizeSignalIdKebab (raw: string) : string =
        let sb = StringBuilder()
        for i, c in Seq.indexed raw do
            if System.Char.IsUpper c && i > 0 then sb.Append('-') |> ignore
            let lc = System.Char.ToLowerInvariant c
            if (lc >= 'a' && lc <= 'z') || (lc >= '0' && lc <= '9') || lc = '-' || lc = '_' || lc = '.' then
                sb.Append(lc) |> ignore
            else
                sb.Append('_') |> ignore
        sb.ToString()

    /// Ds2.Core.SignalId  형식: `{prefix}.{typeShort}.{hash8}.{metric-kebab}`
    /// - `prefix` : 일반적으로 `kpi.{project}` 또는 `kpi.{lineId}`
    /// - metric 은 kebab-case 소문자화 · 안전문자만 유지 (한국어/특수문자 → '_')
    /// - 유니크성 보장 위해 metric 원본 hash 도 뒤에 부착
    let signalId (prefix: string) (kind: KpiEntityKind) (entityFqdn: string) (metricSuffix: string) : SignalId =
        let kebab = sanitizeSignalIdKebab metricSuffix
        // metric name 이 sanitize 후 겹칠 수 있으므로 원본 hash 추가로 고유성 확보 (Korean UserTag 케이스)
        let metricHash = entityHash8 metricSuffix
        let head = if kebab.Length > 32 then kebab.Substring(0, 32) else kebab
        let raw =
            sprintf "%s.%s.%s.%s-%s"
                (prefix.ToLowerInvariant().TrimEnd('.'))
                (entityShort kind |> _.ToLowerInvariant())
                (entityHash8 entityFqdn)
                head
                metricHash
        SignalId raw

    /// OperationalData Item.SemanticId 규칙: 신호 identity URN.
    let opDataItemSemanticId (signalId: SignalId) : SemanticId =
        SemanticId (sprintf "urn:dualsoft:signal:%s" signalId.Value)

    /// AIMC Mapping source path (AID 내부 경로) — spec §04-D 컨벤션.
    /// 형식: `InterfaceOPCUA/InteractionMetadata/{aidInteractionIdShort}`
    let aidSourcePath (aidInteractionIdShort: string) : string =
        sprintf "InterfaceOPCUA/InteractionMetadata/%s" aidInteractionIdShort

    /// AIMC Mapping sink path (OperationalData 내부 경로).
    /// 형식: `OperationalData/{opDataItemIdShort}`
    let opDataSinkPath (opDataItemIdShort: string) : string =
        sprintf "OperationalData/%s" opDataItemIdShort

    /// AIMC Mapping SME idShort (source+sink 결정론 hash).
    /// [A-Za-z0-9_] 만 사용 가능 → uint32 hex 로 부호 방지. 여기와 export/import 가 동일 함수 공유해야 함.
    let aimcMappingIdShort (source: string) (sink: string) : string =
        let combined = source + "->" + sink
        sprintf "Mapping_%08x" (uint32 (combined.GetHashCode()))
