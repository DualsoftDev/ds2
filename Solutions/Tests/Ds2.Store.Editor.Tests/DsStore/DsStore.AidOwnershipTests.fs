module Ds2.Store.Editor.Tests.DsStoreAidOwnershipTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// =============================================================================
// AID 소유는 Project 엔티티가 아니라 DsStore 다 (2026-09-22 이관).
// 이유·배경은 DsStore.AssetInterfaces 주석 참조 — 여기서는 ①구버전 파일 흡수
// ②신규 포맷 왕복 ③Project 스냅샷에서 빠졌는지(성능 계약) 를 못 박는다.
// =============================================================================

let private aidWith (systemId: Guid) (addressCount: int) =
    let aid = AssetInterfacesDescription()
    let request =
        AidXgtConnectionInfo(
            "", "LsXgi", "tcp", "192.168.0.10", 2004, "",
            false, 0uy, 0uy, 1000, 100, None)
    let addresses = [ for i in 1 .. addressCount -> $"%%MX0.{i}" ]
    AidXgtEndpointSettings.ensureBindingForSystem (aid, systemId, request, addresses) |> ignore
    aid

let private interactionCount (aid: AssetInterfacesDescription) =
    aid.Interfaces
    |> Seq.sumBy (fun b -> match b with Xgt (_, xs) -> List.length xs | _ -> 0)

/// 구버전 .sdf/.json 은 AID 를 프로젝트 안(`assetInterfaces`)에 싣고 있다.
/// 로드하면 store 소유로 옮겨지고 Project 쪽은 비워져야 한다.
[<Fact>]
let ``legacy project-owned AID is migrated to the store on load`` () =
    let legacy = createStore ()
    let project = addProject legacy "P"
    let system = addSystem legacy "Sys" project.Id true
    // 구버전 배치 재현 — 그때는 이 자리가 AID 의 정본이었다.
    legacy.Projects.[project.Id].LegacyAssetInterfaces <- Some (aidWith system.Id 5)
    let json = Ds2.Serialization.JsonConverter.serialize legacy
    Assert.Contains("assetInterfaces", json)

    let restored = createStore ()
    restored.ReplaceStore(Ds2.Serialization.JsonConverter.deserialize<DsStore> json)

    let migrated = restored.TryGetAssetInterfaces project.Id
    Assert.True(migrated.IsSome, "구버전 AID 가 store 로 넘어오지 않았다")
    Assert.Equal(5, interactionCount migrated.Value)
    // 레거시 자리는 비워 둔다 — 안 비우면 다음 저장에 옛 배치가 되살아나 두 벌이 된다.
    Assert.True(restored.Projects.[project.Id].LegacyAssetInterfaces.IsNone)

/// 신규 포맷: AID 는 store 최상위에 실리고, 프로젝트 안에는 그 키가 없다.
[<Fact>]
let ``store-owned AID round-trips and leaves no legacy key in the project`` () =
    let store = createStore ()
    let project = addProject store "P"
    let system = addSystem store "Sys" project.Id true
    store.SetAssetInterfaces(project.Id, aidWith system.Id 3)

    let json = Ds2.Serialization.JsonConverter.serialize store
    let restored = createStore ()
    restored.ReplaceStore(Ds2.Serialization.JsonConverter.deserialize<DsStore> json)

    let roundTripped = restored.TryGetAssetInterfaces project.Id
    Assert.True(roundTripped.IsSome)
    Assert.Equal(3, interactionCount roundTripped.Value)
    Assert.True(restored.Projects.[project.Id].LegacyAssetInterfaces.IsNone)

/// 성능 계약 — AID 는 Project 엔티티의 undo 스냅샷(=JSON 왕복 복제)에 더 이상 실리지 않는다.
/// 이게 깨지면 시스템 추가/삭제/가져오기가 다시 AID 크기에 비례해 느려진다.
[<Fact>]
let ``project undo snapshot no longer carries the AID payload`` () =
    let store = createStore ()
    let project = addProject store "P"
    let system = addSystem store "Sys" project.Id true
    let before = (DeepCopyHelper.backupEntityAs project |> Ds2.Serialization.JsonConverter.serialize).Length

    store.SetAssetInterfaces(project.Id, aidWith system.Id 2000)
    let after = (DeepCopyHelper.backupEntityAs project |> Ds2.Serialization.JsonConverter.serialize).Length

    Assert.Equal(before, after)

/// 가져오기/삭제가 Project 를 mutate 해도 store 의 AID 는 그대로여야 한다
/// (undo 스냅샷이 Project 만 되돌리므로 AID 가 딸려 돌아가면 접속 설정이 유실된다).
[<Fact>]
let ``project mutation and undo leave the store AID untouched`` () =
    let store = createStore ()
    let project = addProject store "P"
    let system = addSystem store "Sys" project.Id true
    store.SetAssetInterfaces(project.Id, aidWith system.Id 4)

    let added = addSystem store "Sys2" project.Id true
    Assert.Contains(added.Id, store.Projects.[project.Id].ActiveSystemIds)
    store.Undo()

    let aid = store.TryGetAssetInterfaces project.Id
    Assert.True(aid.IsSome)
    Assert.Equal(4, interactionCount aid.Value)
