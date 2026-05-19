namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

type ConditionEntry = {
    RxWorkGuid: Guid
    ApiCallGuid: Guid option
    InputSpec: ValueSpec
}

/// CallCondition 트리 구조 보존 — isOR 플래그를 evaluate 단계까지 전달.
/// And/Or 중첩으로 사용자 모델의 `A | (B|C)` 같은 표현 정확히 평가.
/// 빈 And 는 true (= 조건 없음 통과), 빈 Or 는 false.
type ConditionExpression =
    | Leaf of ConditionEntry
    | And of ConditionExpression list
    | Or of ConditionExpression list

type SimIndex = {
    Store: DsStore
    AllWorkGuids: Guid list
    AllCallGuids: Guid list
    AllFlowGuids: Guid list
    WorkCanonicalGuids: Map<Guid, Guid>
    WorkCallGuids: Map<Guid, Guid list>
    mutable WorkStartPreds: Map<Guid, Guid list>
    mutable WorkPureStartPreds: Map<Guid, Guid list>
    mutable WorkResetPreds: Map<Guid, Guid list>
    mutable WorkDuration: Map<Guid, float>
    WorkSystemName: Map<Guid, string>
    WorkName: Map<Guid, string>
    WorkFlowGuid: Map<Guid, Guid>
    mutable CallStartPreds: Map<Guid, Guid list>
    CallWorkGuid: Map<Guid, Guid>
    CallApiCallGuids: Map<Guid, Guid list>
    CallAutoAuxConditions: Map<Guid, ConditionExpression>
    CallComAuxConditions: Map<Guid, ConditionExpression>
    CallSkipUnmatchConditions: Map<Guid, ConditionExpression>
    WorkReferenceGroups: Map<Guid, Guid list>
    WorkGroupSets: Map<Guid, Set<Guid>>
    CallCanonicalGuids: Map<Guid, Guid>
    CallReferenceGroups: Map<Guid, Guid list>
    ActiveSystemNames: Set<string>
    TickMs: int
    WorkTokenRole: Map<Guid, TokenRole>
    mutable WorkTokenSuccessors: Map<Guid, Guid list>
    TokenSourceGuids: Guid list
    TokenSinkGuids: Set<Guid>
    mutable TokenPathGuids: Set<Guid>
    CallRaceExclusions: Map<Guid, Set<Guid>>
    CallTypeMap: Map<Guid, CallType>
    CallTimeoutMap: Map<Guid, TimeSpan>
}

type SimIndexConnectionSnapshot = {
    WorkStartPreds: Map<Guid, Guid list>
    WorkPureStartPreds: Map<Guid, Guid list>
    WorkResetPreds: Map<Guid, Guid list>
    CallStartPreds: Map<Guid, Guid list>
    WorkTokenSuccessors: Map<Guid, Guid list>
    TokenPathGuids: Set<Guid>
}

type internal SimIndexBuildState = {
    mutable AllWorkGuids: Guid list
    mutable AllCallGuids: Guid list
    mutable AllFlowGuids: Guid list
    mutable WorkCallGuids: Map<Guid, Guid list>
    mutable WorkStartPreds: Map<Guid, Guid list>
    mutable WorkPureStartPreds: Map<Guid, Guid list>
    mutable WorkResetPreds: Map<Guid, Guid list>
    mutable WorkDuration: Map<Guid, float>
    mutable WorkSystemName: Map<Guid, string>
    mutable WorkName: Map<Guid, string>
    mutable WorkFlowGuid: Map<Guid, Guid>
    mutable CallStartPreds: Map<Guid, Guid list>
    mutable CallWorkGuid: Map<Guid, Guid>
    mutable CallApiCallGuids: Map<Guid, Guid list>
    mutable CallAutoAuxConditions: Map<Guid, ConditionExpression>
    mutable CallComAuxConditions: Map<Guid, ConditionExpression>
    mutable CallSkipUnmatchConditions: Map<Guid, ConditionExpression>
    mutable CallTypeMap: Map<Guid, CallType>
    mutable CallTimeoutMap: Map<Guid, TimeSpan>
}
