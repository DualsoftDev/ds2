module Ds2.Core.Tests.PlcEndpointStoreTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels
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
