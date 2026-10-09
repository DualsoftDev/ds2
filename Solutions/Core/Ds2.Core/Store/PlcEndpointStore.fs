namespace Ds2.Core.Store

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.StandardSubmodels
open Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes

/// store 안의 AID endpoint(InterfaceXGT · InterfaceMicrexSx)를 System 단위로 읽고 쓴다.
///
/// PLC 접속 정보의 정본은 AASX 안 AID 다 — 편집기(Promaker)가 쓰고, 수집기(Hub)가 읽어 게이트웨이를 조립하고,
/// DSPilot 이 보여 준다. 세 쪽이 같은 규칙으로 기록해야 하므로 "소유 Project 찾기 → AID 없으면 생성 →
/// 바인딩 보장(없으면 주소로 생성, 있으면 endpoint 갱신 + 새 주소 병합) → 같은 System 의 상대 바인딩 제거"
/// 를 여기 한 곳에 둔다. 예전엔 이 글루가 Ds2.Hub.Shared(C#) 에 있어 DSPilot 이 쓸 수 없었다.
///
/// 모델만 바꾼다 — dirty 표시·Undo 는 호출자 몫이다. 접속 축 검증과 base 조립은 AidXgtEndpointSettings /
/// AidMicrexSxEndpointSettings 가 한다.
[<RequireQualifiedAccess>]
module PlcEndpointStore =

    let private owningProject (store: DsStore) (systemId: Guid) : Project option =
        store.Projects.Values |> Seq.tryFind (fun p -> p.ActiveSystemIds.Contains systemId)

    let private tryAid (store: DsStore) (project: Project option) : AssetInterfacesDescription option =
        project
        |> Option.bind (fun p ->
            match store.AssetInterfaces.TryGetValue p.Id with
            | true, aid -> Some aid
            | _ -> None)

    /// 단일 active System 프로젝트만 — 다중 System 에서 무주인 endpoint 를 임의로 고르지 않는다.
    let private onlyActiveSystemProject (store: DsStore) : Project option =
        store.Projects.Values
        |> Seq.tryHead
        |> Option.filter (fun p -> p.ActiveSystemIds.Count = 1)

    let private trimOrEmpty (s: string) = if isNull s then "" else s.Trim()

    let private orEmpty (xs: seq<string>) = if isNull xs then Seq.empty else xs

    /// 편집 폼의 벤더·프로파일 → XGT endpoint 쓰기 요청. BaseUri/SystemId 는 요청에서 쓰이지 않는다.
    let xgtRequest (vendor: PlcVendorChoice) (profile: PlcVendorProfile) : AidXgtConnectionInfo =
        AidXgtConnectionInfo(
            "", string vendor, PlcTransports.normalize profile.Transport,
            trimOrEmpty profile.IpAddress, profile.Port, trimOrEmpty profile.UsbDeviceSelector,
            profile.LocalEthernet, profile.NetworkNumber, profile.StationNumber,
            profile.TimeoutMs, profile.ScanIntervalMs, None)

    /// 지정 active System 에 귀속된 XGT endpoint. 없으면 null.
    let tryReadXgt (store: DsStore) (systemId: Guid) : AidXgtConnectionInfo =
        match tryAid store (owningProject store systemId) with
        | Some aid -> AidXgtEndpointSettings.tryReadForSystem (aid, systemId)
        | None -> null

    /// 단일 active System 프로젝트에서 systemRef 없는 구버전 endpoint 를 읽는다 — 표시 폴백용.
    /// tryReadXgt 는 무주인 endpoint 를 의도적으로 제외하므로, 구버전 파일은 접속값이 있어도 '미지정' 으로 보였다.
    /// 저장 시 귀속은 EnsureBindingForSystem 의 "무주인 endpoint 1개 claim" 규칙이 처리한다.
    let tryReadLegacyUnassignedXgt (store: DsStore) : AidXgtConnectionInfo =
        match tryAid store (onlyActiveSystemProject store) with
        | Some aid -> AidXgtEndpointSettings.tryReadFirst aid
        | None -> null

    /// 지정 active System 에 귀속된 SX endpoint. 없으면 null.
    let tryReadMicrexSx (store: DsStore) (systemId: Guid) : AidMicrexSxConnectionInfo =
        match tryAid store (owningProject store systemId) with
        | Some aid -> AidMicrexSxEndpointSettings.tryReadForSystem (aid, systemId)
        | None -> null

    /// 지정 active System 의 XGT endpoint 를 보장한다. 성공하면 같은 System 의 SX 바인딩은 지운다 — 한 System 은
    /// PLC 1대다(남겨 두면 수집기가 AID 에서 연결을 하나 더 만들어 "1대만 설정했는데 2대" 가 된다).
    /// 반환 = 기록 여부. 비-LS 벤더는 XGT 로 표현할 수 없어 false.
    let ensureXgt
        (store: DsStore) (systemId: Guid) (vendor: PlcVendorChoice) (profile: PlcVendorProfile) (addresses: seq<string>)
        : bool =
        if isNull profile || not (PlcVendorProfile.IsAidXgtVendor vendor) then false
        else
            match owningProject store systemId with
            | None -> false
            | Some project ->
                let aid = store.GetOrCreateAssetInterfaces project.Id
                let written =
                    AidXgtEndpointSettings.ensureBindingForSystem (aid, systemId, xgtRequest vendor profile, orEmpty addresses)
                if written <= 0 then false
                else
                    AidMicrexSxEndpointSettings.removeForSystem (aid, systemId) |> ignore
                    true

    /// 지정 active System 의 SX endpoint 를 보장한다. 성공하면 같은 System 의 XGT 바인딩은 지운다.
    let ensureMicrexSx
        (store: DsStore) (systemId: Guid) (profile: PlcVendorProfile)
        (ioMapPath: string) (writableAreas: seq<string>) (addresses: seq<string>)
        : bool =
        if isNull profile then false
        else
            match owningProject store systemId with
            | None -> false
            | Some project ->
                let aid = store.GetOrCreateAssetInterfaces project.Id
                let written =
                    AidMicrexSxEndpointSettings.ensureBindingForSystem (
                        aid, systemId, trimOrEmpty profile.IpAddress, profile.Port,
                        (if isNull ioMapPath then "" else ioMapPath), orEmpty writableAreas,
                        profile.TimeoutMs, profile.ScanIntervalMs, orEmpty addresses)
                if written <= 0 then false
                else
                    AidXgtEndpointSettings.removeForSystem (aid, systemId) |> ignore
                    true

/// C# 소비자(Promaker 속성 패널·저장, DSPilot)용 DsStore 확장.
[<Extension>]
type DsStorePlcEndpointExtensions =

    [<Extension>]
    static member TryReadXgtEndpoint(store: DsStore, systemId: Guid) : AidXgtConnectionInfo =
        PlcEndpointStore.tryReadXgt store systemId

    [<Extension>]
    static member TryReadLegacyUnassignedXgtEndpoint(store: DsStore) : AidXgtConnectionInfo =
        PlcEndpointStore.tryReadLegacyUnassignedXgt store

    [<Extension>]
    static member TryReadMicrexSxEndpoint(store: DsStore, systemId: Guid) : AidMicrexSxConnectionInfo =
        PlcEndpointStore.tryReadMicrexSx store systemId

    /// 반환 = 기록 여부. 비-LS 벤더(Mitsubishi)는 AID 표현이 없어 false.
    [<Extension>]
    static member EnsureXgtEndpoint
        (store: DsStore, systemId: Guid, vendor: PlcVendorChoice, profile: PlcVendorProfile, addresses: seq<string>) : bool =
        PlcEndpointStore.ensureXgt store systemId vendor profile addresses

    [<Extension>]
    static member EnsureMicrexSxEndpoint
        (store: DsStore, systemId: Guid, profile: PlcVendorProfile,
         ioMapPath: string, writableAreas: seq<string>, addresses: seq<string>) : bool =
        PlcEndpointStore.ensureMicrexSx store systemId profile ioMapPath writableAreas addresses
