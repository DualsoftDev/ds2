/// AID(AssetInterfacesDescription) 테스트 공용 — AASX 왕복 테스트(ds2)와 게이트웨이 조립 테스트(Hub)가 같이 쓴다.
module Ds2.TestKit.AidTestHelpers

open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.StandardSubmodels
open Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes
open Xunit

/// SubmodelElementCollection 을 재귀로 펼친다.
let rec descendants (items: seq<ISubmodelElement>) = seq {
    for item in items do
        yield item
        match item with
        | :? SubmodelElementCollection as smc when not (isNull smc.Value) ->
            yield! descendants smc.Value
        | _ -> ()
}

/// XGT 바인딩용 interaction 하나 — idShort · href · signalId 만 다르고 나머지는 상수.
let xgtInteraction idShort href signalId : OpcUaInteraction = {
    IdShort = idShort
    SemanticId = SemanticId $"urn:dualsoft:test:{idShort}"
    ValueType = XsBoolean
    Unit = None
    Href = href
    SignalId = SignalId signalId
}

let assertError (result: Result<'a, string>) =
    match result with
    | Error _ -> ()
    | Ok value -> Assert.Fail($"expected Error but got Ok {value}")
