namespace Ds2.Backend.Plc

open System
open System.Collections.Generic
open Ev2.PLC.Common
open Ev2.PLC.Protocol.LS

[<RequireQualifiedAccess>]
type PlcVendor =
    | LsXgi
    | LsXgk
    | LsXgb
    | Mitsubishi

/// Hub address ↔ PLC tag 매핑 한 항목.
/// 게이트웨이는 이 리스트만 주기 스캔/쓰기 라우팅에 사용한다.
type PlcTagDef = {
    /// SignalHub 가 사용하는 주소 문자열 (Promaker IO map 의 address 와 일치해야 함).
    HubAddress : string
    /// PLC 측 실제 주소 (예: "%MX100", "D100", "M50"). 보통 HubAddress 와 동일.
    PlcAddress : string
    /// 데이터 타입 — Read/Write 할 때 PlcValue 의 컨크리트 케이스를 결정한다.
    DataType   : CoreDataTypesModule.PlcDataType
}

/// 미쓰비시 MELSEC Ethernet 전송 방식 — Q/iQ-R 의 MC 프로토콜은 TCP 와 UDP 양쪽 지원.
/// LS XGi/XGk 는 항상 TCP 라 이 값은 Mitsubishi 일 때만 의미가 있다.
[<RequireQualifiedAccess>]
type PlcTransport =
    | Tcp
    | Udp

type PlcConnectionConfig = {
    Name        : string
    /// Active System that owns this connection when it originated from a
    /// Project-scoped AID endpoint. None keeps legacy/manual configurations valid.
    SystemId    : Guid option
    Vendor      : PlcVendor
    IpAddress   : string
    Port        : int
    /// LS 의 경우 내장 이더넷 vs FEnet 모듈 구분.
    LocalEthernet : bool
    /// MX 전용 — 기본값은 0,255,1023,0 (자국 CPU).
    NetworkNumber : byte
    StationNumber : byte
    /// MX 전용 — TCP/UDP 선택. LS 에서는 무시 (항상 TCP).
    Transport   : PlcTransport
    /// 통신 timeout (ms)
    TimeoutMs   : int
    /// 스캔 주기. None 이면 스캔 안 함 (write-only 게이트웨이).
    ScanInterval : TimeSpan option
    Tags        : PlcTagDef list
}

type PlcGatewayConfig = {
    Connections : PlcConnectionConfig list
}

[<RequireQualifiedAccess>]
module PlcConnectionConfig =
    let defaultLs name ip = {
        Name = name
        SystemId = None
        Vendor = PlcVendor.LsXgi
        IpAddress = ip
        Port = 2004
        LocalEthernet = true
        NetworkNumber = 0uy
        StationNumber = 0uy
        Transport = PlcTransport.Tcp
        TimeoutMs = 3000
        ScanInterval = Some (TimeSpan.FromMilliseconds 100.0)
        Tags = []
    }

    let defaultMx name ip = {
        Name = name
        SystemId = None
        Vendor = PlcVendor.Mitsubishi
        IpAddress = ip
        Port = 5007
        LocalEthernet = true
        NetworkNumber = 0uy
        StationNumber = 0xFFuy
        Transport = PlcTransport.Tcp
        TimeoutMs = 3000
        ScanInterval = Some (TimeSpan.FromMilliseconds 100.0)
        Tags = []
    }

/// 태그 1개를 전역에서 유일하게 식별하는 복합키 — (SystemId, 주소).
/// 멀티 PLC 에서는 서로 다른 PLC 가 같은 주소를 쓸 수 있어 주소 단독으로는 식별이 안 된다
/// (예전 라우팅 테이블이 주소 단독 키라 "마지막 등록만 살아남는" 문제가 있었다).
///
/// **Address 는 반드시 TagKey.create 로 정규화해서 만들 것.** 기존 계층들이 주소를
/// StringComparer.OrdinalIgnoreCase 로 비교해 왔는데, 레코드 기본 비교는 대소문자를 구분하므로
/// 생성 시점에 대문자로 정규화해 둔다. 그래야 기본 구조적 비교가 곧 대소문자 무시 비교가 된다.
/// 원본 표기가 필요한 곳(로그/UI)은 정규화 전 주소를 따로 보관해야 한다.
[<StructuralEquality; StructuralComparison>]
type TagKey = {
    /// 이 태그를 보유한 연결의 소유 System. None = 귀속 미상(레거시 AASX·수동 설정·구버전 송신자).
    SystemId : Guid option
    /// 정규화(Trim + 대문자)된 주소.
    Address  : string
}

[<RequireQualifiedAccess>]
module TagKey =
    /// 주소 정규화 — 키 생성과 조회가 반드시 같은 규칙을 쓰도록 여기 한 곳에만 둔다.
    let normalize (address: string) =
        if isNull address then "" else address.Trim().ToUpperInvariant()

    /// 정본 생성자.
    let create (systemId: Guid option) (address: string) =
        { SystemId = systemId; Address = normalize address }

    /// System 귀속이 없는 키 — 레거시 설정 및 systemId 를 안 실어 보내는 구버전 송신자용.
    let legacy (address: string) = create None address

    /// 로그/오류 메시지용 표기. 귀속 미상은 "(system 미상)".
    let describe (key: TagKey) =
        match key.SystemId with
        | Some sid -> sprintf "%s@%O" key.Address sid
        | None     -> sprintf "%s@(system 미상)" key.Address

/// 한 주소를 보유한 연결 1개. 중복 진단과 폴백 판정에 함께 쓴다.
type TagAddressOwner = {
    ConnectionName : string
    SystemId       : Guid option
}

/// 주소 중복의 성격. 복합키로 해결되는지 여부가 갈린다.
type TagAddressConflict =
    /// SystemId 가 서로 달라 (SystemId, 주소) 복합키로 구분된다.
    /// 다만 systemId 를 안 싣는 구버전 송신자(구 Pi5 수집기 등)와 섞이면 여전히 구분 불가.
    | AcrossSystems
    /// 같은 System 안에서(또는 양쪽 다 귀속 미상) 두 연결이 같은 주소를 보유 —
    /// 복합키로도 구분되지 않는다. 모델/설정 자체를 고쳐야 하는 케이스.
    | WithinSameSystem

/// 주소 중복 1건.
type TagAddressDuplicate = {
    /// 정규화된 주소.
    Address  : string
    Owners   : TagAddressOwner list
    Conflict : TagAddressConflict
}

[<RequireQualifiedAccess>]
module PlcGatewayConfig =

    /// 정규화 주소 → 그 주소를 보유한 연결 목록(설정 순서 유지).
    /// 게이트웨이 라우팅 폴백(systemId 미제공 요청의 소유자 판정)과 중복 진단이 공유하는 단일 인덱스.
    let addressOwners (cfg: PlcGatewayConfig) : IReadOnlyDictionary<string, TagAddressOwner list> =
        let acc = Dictionary<string, ResizeArray<TagAddressOwner>>(StringComparer.Ordinal)
        for connection in cfg.Connections do
            for tag in connection.Tags do
                let address = TagKey.normalize tag.HubAddress
                let bucket =
                    match acc.TryGetValue address with
                    | true, existing -> existing
                    | _ ->
                        let created = ResizeArray<TagAddressOwner>()
                        acc.[address] <- created
                        created
                // 같은 연결이 같은 주소를 두 번 들고 있는 건 연결 내부 중복이라 여기서 세지 않는다
                // (AID 빌드 단계에서 이미 dedup 됨). 연결 간 중복만 진단 대상.
                if not (bucket |> Seq.exists (fun o -> String.Equals(o.ConnectionName, connection.Name, StringComparison.Ordinal))) then
                    bucket.Add { ConnectionName = connection.Name; SystemId = connection.SystemId }
        let result = Dictionary<string, TagAddressOwner list>(StringComparer.Ordinal)
        for kv in acc do
            result.[kv.Key] <- List.ofSeq kv.Value
        result :> IReadOnlyDictionary<_, _>

    /// 2개 이상의 연결이 보유한 주소 목록. 없으면 빈 리스트 = 멀티 PLC 라도 주소가 안 겹치는 정상 상태.
    let duplicateAddresses (cfg: PlcGatewayConfig) : TagAddressDuplicate list =
        [ for kv in addressOwners cfg do
            if kv.Value.Length > 1 then
                // 소유자들의 SystemId 가 전부 다르고 하나도 None 이 아니어야 복합키로 구분된다.
                let distinctSystems = kv.Value |> List.map _.SystemId |> List.distinct
                let anyUnowned = kv.Value |> List.exists (fun o -> o.SystemId.IsNone)
                let conflict =
                    if anyUnowned || distinctSystems.Length <> kv.Value.Length then WithinSameSystem
                    else AcrossSystems
                yield { Address = kv.Key; Owners = kv.Value; Conflict = conflict } ]

    /// systemId 를 싣지 않은(구버전) 요청의 소유 System 판정.
    ///   Ok (Some sid) = 소유자 유일 → 그 System 으로 확정
    ///   Ok None       = 그 주소를 아무도 안 가짐 → 호출자가 "알 수 없는 주소"로 처리
    ///   Error owners  = 소유자 2개 이상 → 모호. 조용히 아무 데나 보내지 말고 명시적으로 실패시킬 것.
    let resolveSoleOwner (owners: IReadOnlyDictionary<string, TagAddressOwner list>) (address: string)
        : Result<Guid option option, TagAddressOwner list> =
        match owners.TryGetValue(TagKey.normalize address) with
        | true, [ single ] -> Ok (Some single.SystemId)
        | true, many when many.Length > 1 -> Error many
        | _ -> Ok None

/// C# 호출자(Promaker)가 Ev2.PLC.Common 네임스페이스를 직접 import 하지 않고도
/// PlcDataType 을 얻을 수 있도록 노출하는 팩토리.
[<RequireQualifiedAccess>]
module PlcDataTypes =
    let Bool    = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.Bool
    let Int16   = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.Int16
    let UInt16  = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.UInt16
    let Int32   = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.Int32
    let UInt32  = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.UInt32
    let Float32 = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.Float32
    let Float64 = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType.Float64

/// 주소 문자열 패턴 → PlcDataType 추론. 시퀀스 IO 는 보통 비트 단위라 default Bool.
/// 워드 디바이스가 명백한 prefix(D/W/R/%MW/%MD) 만 Int16/Int32 로 분기.
[<RequireQualifiedAccess>]
module PlcAddressInfer =
    open System

    /// LS XGI 주소: "%MX0", "%DW10" 등.  Mitsubishi: "D100", "M50", "X1A".
    /// 추론 실패시 Bool 반환 — 시퀀스 IO 가 압도적이라 안전한 기본값.
    let dataType (vendor: PlcVendor) (address: string) : Ev2.PLC.Common.CoreDataTypesModule.PlcDataType =
        let s = if isNull address then "" else address.Trim()
        let upper = s.ToUpperInvariant()
        match vendor with
        | PlcVendor.LsXgi
        | PlcVendor.LsXgk
        | PlcVendor.LsXgb ->
            // LS 표기: %<영역><타입문자><주소>.  타입문자 X=Bit, B=Byte, W=Word, D=DWord, L=LWord
            // 첫 % 이후 타입 문자 (보통 두번째 문자) 를 보고 분기.
            // 단, 워드/DWord 주소에 .N 비트 인덱스가 붙은 경우(%QW3070.3) 는 비트 액세스 → Bool.
            // (XGI 는 비트 단위로 access 가능한 워드 영역에서 .N 인덱스 사용.)
            let typeChar =
                if upper.StartsWith("%") && upper.Length >= 3 then upper.[2]
                else if upper.Length >= 2 then upper.[1]
                else 'X'
            // .N 인덱스가 있으면 비트 access 로 간주.
            let hasBitIndex = upper.Contains(".")
            match typeChar with
            | 'X' -> PlcDataTypes.Bool
            | 'B' when hasBitIndex -> PlcDataTypes.Bool
            | 'B' -> PlcDataTypes.UInt16   // 바이트는 가장 가까운 워드 read 로 처리
            | 'W' when hasBitIndex -> PlcDataTypes.Bool
            | 'W' -> PlcDataTypes.Int16
            | 'D' when hasBitIndex -> PlcDataTypes.Bool
            | 'D' -> PlcDataTypes.Int32
            | 'L' when hasBitIndex -> PlcDataTypes.Bool
            | 'L' -> PlcDataTypes.Float64
            | _   -> PlcDataTypes.Bool
        | PlcVendor.Mitsubishi ->
            // Mitsubishi 비트 디바이스: X Y M L F B SB DX DY S TS TC SS SC CS CC
            // 워드 디바이스: D W R ZR T C SD SW
            let firstChar = if upper.Length > 0 then upper.[0] else 'M'
            match firstChar with
            | 'D' | 'W' | 'R' | 'Z' -> PlcDataTypes.Int16
            | 'T' | 'C' when upper.Length >= 2 && upper.[1] = 'N' -> PlcDataTypes.Int16  // TN/CN = current value
            | _ -> PlcDataTypes.Bool

/// PlcGatewayConfig(Agent 가 aasx IOMap + PlcConnection 으로 조립) → 수집기(Pi5) push payload 매핑.
/// 분리 아키텍처: Agent 가 이 payload 를 Hub 로 push, Pi5 가 받아 plc.json 병합.
/// 문자열 라벨(vendor/dtype)은 Pi5 Config 의 toVendor/toDataType 이 파싱하는 값과 정확히 일치해야 한다.
[<RequireQualifiedAccess>]
module CollectorConfig =
    open Ds2.Backend.Common

    let private vendorStr (v: PlcVendor) =
        match v with
        | PlcVendor.LsXgi      -> "LsXgi"
        | PlcVendor.LsXgk      -> "LsXgk"
        | PlcVendor.LsXgb      -> "LsXgb"
        | PlcVendor.Mitsubishi -> "Mitsubishi"

    let private dtypeStr (d: CoreDataTypesModule.PlcDataType) =
        match d with
        | CoreDataTypesModule.PlcDataType.Bool    -> "Bool"
        | CoreDataTypesModule.PlcDataType.Int16   -> "Int16"
        | CoreDataTypesModule.PlcDataType.UInt16  -> "UInt16"
        | CoreDataTypesModule.PlcDataType.Int32   -> "Int32"
        | CoreDataTypesModule.PlcDataType.UInt32  -> "UInt32"
        | CoreDataTypesModule.PlcDataType.Float32 -> "Float32"
        | CoreDataTypesModule.PlcDataType.Float64 -> "Float64"
        | _                                       -> "Bool"

    /// PlcGatewayConfig → CollectorConfigPayload. ScanInterval None(write-only)이면 scanMs=100 기본.
    let fromGateway (cfg: PlcGatewayConfig) : CollectorConfigPayload =
        { Connections =
            cfg.Connections
            |> List.map (fun c ->
                { Name = c.Name
                  Vendor = vendorStr c.Vendor
                  Ip = c.IpAddress
                  Port = c.Port
                  LocalEthernet = c.LocalEthernet
                  TimeoutMs = c.TimeoutMs
                  // 수집기가 push 하는 TagWrite.SystemId 의 출처. 예전엔 여기서 SystemId 가 누락돼
                  // 분리 아키텍처(Pi5)에서는 System 귀속이 통째로 소실됐다.
                  SystemId = TagWriteSystem.ofGuid c.SystemId
                  ScanMs =
                    c.ScanInterval
                    |> Option.map (fun t -> int t.TotalMilliseconds)
                    |> Option.defaultValue 100
                  Tags =
                    c.Tags
                    |> List.map (fun t ->
                        { Hub = t.HubAddress; Plc = t.PlcAddress; Dtype = dtypeStr t.DataType })
                    |> List.toArray })
            |> List.toArray }
