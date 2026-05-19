module Ds2.Store.Editor.Tests.EngineAwareHomingPlanTests

open System
open System.Collections.Generic
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO
open Ds2.Runtime.Model
open Xunit

let private newGuid () = Guid.NewGuid()

/// 테스트용 SignalMapping — 주요 필드만 채우고 나머지는 기본.
let private mapping (apiCallGuid: Guid) (callGuid: Guid) (outAddr: string) (inAddr: string) (txWork: Guid option) (rxWork: Guid option) : SignalMapping = {
    ApiCallGuid = apiCallGuid
    CallGuid = callGuid
    TxWorkGuid = txWork
    RxWorkGuid = rxWork
    OutAddress = outAddr
    InAddress = inAddr
}

/// SignalIOMap 을 mappings 리스트로부터 빌드.
let private iomFromMappings (mappings: SignalMapping list) : SignalIOMap =
    let outMap =
        mappings
        |> List.filter (fun m -> not (String.IsNullOrEmpty m.OutAddress))
        |> List.groupBy (fun m -> m.OutAddress)
        |> Map.ofList
    let inMap =
        mappings
        |> List.filter (fun m -> not (String.IsNullOrEmpty m.InAddress))
        |> List.groupBy (fun m -> m.InAddress)
        |> Map.ofList
    let callMap =
        mappings
        |> List.groupBy (fun m -> m.CallGuid)
        |> Map.ofList
    let txMap =
        mappings
        |> List.choose (fun m -> m.TxWorkGuid |> Option.map (fun g -> g, m.OutAddress))
        |> List.filter (fun (_, oa) -> not (String.IsNullOrEmpty oa))
        |> List.groupBy fst
        |> List.map (fun (g, pairs) -> g, pairs |> List.map snd |> List.distinct)
        |> Map.ofList
    let rxMap =
        mappings
        |> List.choose (fun m -> m.RxWorkGuid |> Option.map (fun g -> g, m.InAddress))
        |> List.filter (fun (_, ia) -> not (String.IsNullOrEmpty ia))
        |> List.groupBy fst
        |> List.map (fun (g, pairs) -> g, pairs |> List.map snd |> List.distinct)
        |> Map.ofList
    {
        Mappings = mappings
        OutAddressToMappings = outMap
        InAddressToMappings = inMap
        CallToMappings = callMap
        TxWorkToOutAddresses = txMap
        RxWorkToInAddresses = rxMap
    }

let private inValuesOf (pairs: (string * bool) list) : IReadOnlyDictionary<string, bool> =
    let d = Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    for k, v in pairs do d.[k] <- v
    d :> IReadOnlyDictionary<_, _>

let private emptyCtx : EngineAwareHomingPlan.Context = {
    AllWorkGuids = []
    WorkName = Map.empty
    WorkResetPreds = Map.empty
    CallComAuxConditions = Map.empty
}

let private emptyState () = SimState.create 10 [] [] []

[<Fact>]
let ``empty context produces empty plan`` () =
    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            emptyCtx
            (iomFromMappings [])
            (emptyState ())
            (inValuesOf [])
            Set.empty
            Set.empty

    Assert.Empty(plan.Candidates)
    Assert.Empty(plan.Passed)
    Assert.Empty(plan.BlockedCallGuids)
    Assert.Empty(plan.OutsToFire)
    Assert.Empty(plan.WorksMissingResetPreds)

[<Fact>]
let ``finish target with IN false produces fire candidate via call mapping`` () =
    let workA = newGuid()
    let callA = newGuid()
    let apiCallA = newGuid()
    let iom =
        iomFromMappings
            [ mapping apiCallA callA "O1" "I1" (Some workA) (Some workA) ]
    let ctx = { emptyCtx with
                  AllWorkGuids = [ workA ]
                  WorkName = Map.ofList [ workA, "A" ] }

    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            ctx iom (emptyState ()) (inValuesOf [ "I1", false ])
            (Set.singleton workA) Set.empty

    Assert.Equal(1, plan.Candidates.Length)
    let cand = plan.Candidates.[0]
    Assert.Equal("O1", cand.OutAddress)
    Assert.Equal(callA, cand.CallGuid)
    Assert.Equal("A", cand.WorkName)
    Assert.Contains("FIRE", cand.Reason)
    Assert.Equal<string seq>([| "O1" |], plan.OutsToFire)

[<Fact>]
let ``finish target with IN true produces no candidate`` () =
    let workA = newGuid()
    let callA = newGuid()
    let iom = iomFromMappings [ mapping (newGuid()) callA "O1" "I1" (Some workA) (Some workA) ]
    let ctx = { emptyCtx with AllWorkGuids = [ workA ]; WorkName = Map.ofList [ workA, "A" ] }

    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            ctx iom (emptyState ()) (inValuesOf [ "I1", true ])
            (Set.singleton workA) Set.empty

    Assert.Empty(plan.Candidates)

[<Fact>]
let ``ready target with IN true and reset partner produces reset candidate via partner's out`` () =
    let workB = newGuid()  // ready target
    let partnerA = newGuid()  // reset partner — partnerA 의 OUT 이 발사됨
    let callP = newGuid()
    let iom =
        iomFromMappings
            [ mapping (newGuid()) callP "Op" ""  (Some partnerA) None
              mapping (newGuid()) (newGuid()) ""   "Ib" None (Some workB) ]
    let ctx = { emptyCtx with
                  AllWorkGuids = [ workB; partnerA ]
                  WorkName = Map.ofList [ workB, "B"; partnerA, "A" ]
                  WorkResetPreds = Map.ofList [ workB, [ partnerA ] ] }

    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            ctx iom (emptyState ()) (inValuesOf [ "Ib", true ])
            Set.empty (Set.singleton workB)

    Assert.Equal(1, plan.Candidates.Length)
    let cand = plan.Candidates.[0]
    Assert.Equal("Op", cand.OutAddress)
    Assert.Equal(callP, cand.CallGuid)
    Assert.Equal("B", cand.WorkName)
    Assert.Contains("RESET", cand.Reason)

[<Fact>]
let ``ready target without reset partner is reported in WorksMissingResetPreds`` () =
    let workB = newGuid()
    let iom = iomFromMappings [ mapping (newGuid()) (newGuid()) "" "Ib" None (Some workB) ]
    let ctx = { emptyCtx with
                  AllWorkGuids = [ workB ]
                  WorkName = Map.ofList [ workB, "B" ] }

    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            ctx iom (emptyState ()) (inValuesOf [ "Ib", true ])
            Set.empty (Set.singleton workB)

    Assert.Empty(plan.Candidates)
    Assert.Equal(1, plan.WorksMissingResetPreds.Length)
    let (g, n) = plan.WorksMissingResetPreds.[0]
    Assert.Equal(workB, g)
    Assert.Equal("B", n)

[<Fact>]
let ``ComAux false expression blocks call and dedups blocked guid`` () =
    let workA = newGuid()
    let workC = newGuid()
    let callA = newGuid()
    let iom =
        iomFromMappings
            [ mapping (newGuid()) callA "O1" "I1" (Some workA) (Some workA)
              mapping (newGuid()) callA "O2" "I2" (Some workC) (Some workC) ]
    let blockingExpr = ConditionExpression.Or []  // 빈 Or = false (= ComAux 미충족)
    let ctx = { emptyCtx with
                  AllWorkGuids = [ workA; workC ]
                  WorkName = Map.ofList [ workA, "A"; workC, "C" ]
                  CallComAuxConditions = Map.ofList [ callA, blockingExpr ] }

    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            ctx iom (emptyState ()) (inValuesOf [ "I1", false; "I2", false ])
            (Set.ofList [ workA; workC ]) Set.empty

    Assert.Equal(2, plan.Candidates.Length)
    Assert.Empty(plan.Passed)
    Assert.Equal<Guid seq>([| callA |], plan.BlockedCallGuids)
    Assert.Empty(plan.OutsToFire)

[<Fact>]
let ``OUT addresses are case-insensitive deduped`` () =
    let workA = newGuid()
    let workB = newGuid()
    let callA = newGuid()
    let callB = newGuid()
    let iom =
        iomFromMappings
            [ mapping (newGuid()) callA "O1" "I1" (Some workA) (Some workA)
              mapping (newGuid()) callB "o1" "I2" (Some workB) (Some workB) ]
    let ctx = { emptyCtx with
                  AllWorkGuids = [ workA; workB ]
                  WorkName = Map.ofList [ workA, "A"; workB, "B" ] }

    let plan =
        EngineAwareHomingPlan.buildPlanWithTargets
            ctx iom (emptyState ()) (inValuesOf [ "I1", false; "I2", false ])
            (Set.ofList [ workA; workB ]) Set.empty

    Assert.Equal(2, plan.Candidates.Length)
    Assert.Equal(1, plan.OutsToFire.Length)
