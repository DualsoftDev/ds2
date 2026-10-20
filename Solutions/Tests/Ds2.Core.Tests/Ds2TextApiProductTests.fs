module Ds2.Core.Tests.Ds2TextApiProductTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Serialization
open Ds2.Text
open Xunit

// 2026-10-20 Q43 — api 하나 = 제품 하나(`api X { …; product N; }`). SDF 가 그 번호를 싣고(ApiDef.TokenSpecId),
// 작성기가 api 블록에 `product N;` 으로 되쓴다. 전에는 Core 가 이 필드를 몰라 적재 때 버렸고, 제품이 둘 이상인 모델은
// 작성기 출력이 다시 서지 않았다(Studio `ds2.api.product-missing`, 가이드 왕복 C6).

let private model (specId: int option) =
    let store = DsStore()
    let project = Project("P")
    let system = DsSystem("S")
    let flow = Flow("F", system.Id)
    let input = Work("F", "투입", flow.Id)
    store.Projects.[project.Id] <- project
    store.Systems.[system.Id] <- system
    store.Flows.[flow.Id] <- flow
    store.Works.[input.Id] <- input
    project.ActiveSystemIds.Add system.Id
    project.TokenSpecs.Add({ Id = 1; Label = "기본"; Fields = Map.ofList [ "옵션", "false" ]; WorkId = None })
    project.TokenSpecs.Add({ Id = 2; Label = "옵션"; Fields = Map.ofList [ "옵션", "true" ]; WorkId = None })
    let api = ApiDef("옵션투입", system.Id)
    api.TxGuid <- Some input.Id
    api.RxGuid <- Some input.Id
    api.TokenSpecId <- specId
    store.ApiDefs.[api.Id] <- api
    store, project, system, api

let private apiBlock (text: string) =
    let start = text.IndexOf("api 옵션투입")
    Assert.True(start >= 0, text)
    text.Substring(start, text.IndexOf('}', start) - start)

[<Fact>]
let ``api product is written inside its api block`` () =
    let store, project, _, _ = model (Some 2)
    let block = apiBlock (Ds2TextWriter.write store project.Id)
    Assert.Contains("product 2;", block)
    Assert.Equal(1, block.Split("product ").Length - 1)

[<Fact>]
let ``api without product writes no product line`` () =
    let store, project, _, _ = model None
    Assert.DoesNotContain("product", apiBlock (Ds2TextWriter.write store project.Id))

[<Fact>]
let ``api product survives the SDF JSON round trip`` () =
    let store, project, _, api = model (Some 2)
    let json = JsonConverter.serialize store
    Assert.Contains("\"tokenSpecId\": 2", json)
    let actual = JsonConverter.deserialize<DsStore> json
    Assert.Equal(Some 2, actual.ApiDefs.[api.Id].TokenSpecId)
    Assert.Equal(Ds2TextWriter.write store project.Id, Ds2TextWriter.write actual project.Id)

[<Fact>]
let ``product on a sensor is refused, not dropped`` () =
    let store, project, _, api = model (Some 1)
    api.TxGuid <- None
    let ex = Assert.ThrowsAny<exn>(fun () -> Ds2TextWriter.write store project.Id |> ignore)
    Assert.Contains("DS2TEXT_API_PRODUCT", ex.Message)
