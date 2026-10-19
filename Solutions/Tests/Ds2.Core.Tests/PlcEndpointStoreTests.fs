module Ds2.Core.Tests.PlcEndpointStoreTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels
open Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes
open Xunit

/// Project 1 + active System 1 인 store. Core 테스트라 편집기 없이 엔티티를 직접 넣는다.
let private storeWithSystem (systemName: string) =
    let store = DsStore()
    let project = Project("P")
    let system = DsSystem(systemName)
    project.ActiveSystemIds.Add system.Id
    store.Projects.[project.Id] <- project
    store.Systems.[system.Id] <- system
    store, project, system

let private lsProfile ip port =
    let p = PlcVendorProfile.Defaults PlcVendorChoice.LsXgi
    p.IpAddress <- ip
    p.Port <- port
    p

/// 편집기가 접속을 저장하면 AID 가 없는 모델에도 AID 와 XGT endpoint 가 생기고, 되읽으면 같은 값이 나온다.
/// bcf9121b 가 "생성" 경로를 빠뜨려 XGT 바인딩 없는 모델이 PLC IP 를 넣어도 반영 안 되던 구멍을 지킨다.
[<Fact>]
let ``EnsureXgtEndpoint creates the AID and the endpoint on a model without one`` () =
    let store, project, system = storeWithSystem "Press"
    Assert.Empty store.AssetInterfaces

    let ok = store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgi, lsProfile "10.0.0.5" 2004, [ "%IX0.0"; "%QX0.0" ])

    Assert.True ok
    Assert.True(store.AssetInterfaces.ContainsKey project.Id)
    let conn = store.TryReadXgtEndpoint system.Id
    Assert.NotNull conn
    Assert.Equal("LsXgi", conn.Vendor)
    Assert.Equal("10.0.0.5", conn.IpAddress)
    Assert.Equal(2004, conn.Port)
    Assert.Equal(system.Id, conn.SystemId.Value)

/// 한 System 은 PLC 1대다 — 벤더를 SX 로 바꿔 저장하면 XGT endpoint 가 사라지고, LS 로 되돌리면 SX 가 사라진다.
/// 둘이 남으면 수집기가 AID 에서 연결을 하나 더 만든다("1대만 설정했는데 2대").
[<Fact>]
let ``switching vendor between LS and MicrexSx leaves exactly one endpoint`` () =
    let store, _, system = storeWithSystem "Weld"
    Assert.True(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgk, lsProfile "10.0.0.5" 2004, [ "%IX0.0" ]))

    let sx = PlcVendorProfile.Defaults PlcVendorChoice.MicrexSx
    sx.IpAddress <- "10.0.0.9"
    Assert.True(store.EnsureMicrexSxEndpoint(system.Id, sx, "", [ "M1" ], [ "M1.2000.0" ]))
    Assert.Null(store.TryReadXgtEndpoint system.Id)
    let sxConn = store.TryReadMicrexSxEndpoint system.Id
    Assert.NotNull sxConn
    Assert.Equal(509, sxConn.Port)
    Assert.Equal<string>([| "M1" |], sxConn.WritableAreas)

    Assert.True(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgk, lsProfile "10.0.0.5" 2004, [ "%IX0.0" ]))
    Assert.Null(store.TryReadMicrexSxEndpoint system.Id)
    Assert.NotNull(store.TryReadXgtEndpoint system.Id)

/// 쓸 수 없는 요청은 조용히 성공하지 않는다 — Mitsubishi 는 AID 표현이 없고, 모르는 System 은 소유 Project 가 없다.
[<Fact>]
let ``unsupported vendor and unknown system are refused without touching the model`` () =
    let store, _, system = storeWithSystem "Any"
    Assert.False(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.Mitsubishi, lsProfile "10.0.0.5" 5007, [ "%IX0.0" ]))
    Assert.False(store.EnsureXgtEndpoint(Guid.NewGuid(), PlcVendorChoice.LsXgi, lsProfile "10.0.0.5" 2004, [ "%IX0.0" ]))
    Assert.Null(store.TryReadXgtEndpoint(Guid.NewGuid()))

/// 접속 정보는 주소(ApiDef I/O)와 무관하게 저장된다 — 새 프로젝트에서 I/O 를 붙이기 전에 PLC IP 부터 넣는 순서가
/// 자연스럽다. 종전엔 주소 0개를 "쓸 게 없다" 로 보고 저장 실패를 돌려줘 "PLC 접속이 저장되지 않는다" 로 보고됐다.
[<Fact>]
let ``EnsureXgtEndpoint stores the connection even when the system has no addresses yet`` () =
    let store, _, system = storeWithSystem "Fresh"

    Assert.True(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgb, lsProfile "10.0.0.11" 2004, []))

    let conn = store.TryReadXgtEndpoint system.Id
    Assert.NotNull conn
    Assert.Equal("10.0.0.11", conn.IpAddress)
    Assert.Equal("LsXgb", conn.Vendor)

/// 요청이 성립하지 않아(빈 IP) 기록이 안 됐으면 모델도 그대로다 — 실패한 저장이 빈 AID 를 남기면 그 다음 AASX
/// 저장에 "Interfaces 0개" 인 AID 서브모델이 실리고, 되읽기는 무주인 endpoint 탐색까지 꼬인다.
[<Fact>]
let ``a refused write leaves no empty AID behind`` () =
    let store, _, system = storeWithSystem "NoIp"

    Assert.False(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgi, lsProfile "" 2004, [ "%IX0.0" ]))

    Assert.Empty store.AssetInterfaces
    Assert.Null(store.TryReadXgtEndpoint system.Id)

/// 구버전 파일의 endpoint 는 소유 System 이 없다. 단일 System 프로젝트에선 그 하나를 표시 폴백으로 보여 주고,
/// 저장하면 새 endpoint 를 만들지 않고 그것을 귀속(claim)시킨다. 둘 이상이면 어느 것도 "구버전" 으로 고르지 않는다.
[<Fact>]
let ``a single unowned endpoint is shown as legacy and claimed on save, two are never guessed`` () =
    let store, project, system = storeWithSystem "Line"
    Assert.True(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgi, lsProfile "10.0.0.5" 2004, [ "%IX0.0" ]))
    let aid = (store.TryGetAssetInterfaces project.Id).Value
    let unown binding =
        match binding with
        | Xgt (endpoint, interactions) -> Xgt ({ endpoint with SystemId = None }, interactions)
        | other -> other
    aid.Interfaces.[0] <- unown aid.Interfaces.[0]

    Assert.Null(store.TryReadXgtEndpoint system.Id)
    let legacy = store.TryReadLegacyUnassignedXgtEndpoint()
    Assert.NotNull legacy
    Assert.Equal("10.0.0.5", legacy.IpAddress)

    Assert.True(store.EnsureXgtEndpoint(system.Id, PlcVendorChoice.LsXgi, lsProfile "10.0.0.7" 2004, []))
    Assert.Single aid.Interfaces |> ignore
    Assert.Null(store.TryReadLegacyUnassignedXgtEndpoint())
    Assert.Equal("10.0.0.7", (store.TryReadXgtEndpoint system.Id).IpAddress)

    aid.Interfaces.[0] <- unown aid.Interfaces.[0]
    aid.Interfaces.Add(unown aid.Interfaces.[0])
    Assert.Null(store.TryReadLegacyUnassignedXgtEndpoint())
