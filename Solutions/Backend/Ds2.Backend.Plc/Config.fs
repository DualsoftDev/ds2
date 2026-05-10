namespace Ds2.Backend.Plc

open System
open Ev2.PLC.Common
open Ev2.PLC.Protocol.LS

[<RequireQualifiedAccess>]
type PlcVendor =
    | LsXgi
    | LsXgk
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

type PlcConnectionConfig = {
    Name        : string
    Vendor      : PlcVendor
    IpAddress   : string
    Port        : int
    /// LS 의 경우 내장 이더넷 vs FEnet 모듈 구분.
    LocalEthernet : bool
    /// MX 전용 — 기본값은 0,255,1023,0 (자국 CPU).
    NetworkNumber : byte
    StationNumber : byte
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
        Vendor = PlcVendor.LsXgi
        IpAddress = ip
        Port = 2004
        LocalEthernet = true
        NetworkNumber = 0uy
        StationNumber = 0uy
        TimeoutMs = 3000
        ScanInterval = Some (TimeSpan.FromMilliseconds 100.0)
        Tags = []
    }

    let defaultMx name ip = {
        Name = name
        Vendor = PlcVendor.Mitsubishi
        IpAddress = ip
        Port = 5007
        LocalEthernet = true
        NetworkNumber = 0uy
        StationNumber = 0xFFuy
        TimeoutMs = 3000
        ScanInterval = Some (TimeSpan.FromMilliseconds 100.0)
        Tags = []
    }

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
        | PlcVendor.LsXgk ->
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
