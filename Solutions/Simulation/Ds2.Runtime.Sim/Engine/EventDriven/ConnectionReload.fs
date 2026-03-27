namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Engine.Scheduler

/// 화살표 변경 후 고아 Work/토큰 정리 로직
module internal ConnectionReload =

    type Context = {
        Index: SimIndex
        StateManager: StateManager
        Scheduler: EventScheduler
        IsActiveSystemWork: Guid -> bool
        EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
        TriggerWorkStateChanged: WorkStateChangedArgs -> unit
        /// duration 이벤트 취소 + mutable map 업데이트 콜백
        CancelDurationEvent: Guid -> unit
        /// paused duration 제거 콜백
        ClearPausedDuration: Guid -> unit
    }

    let private memberHasValidStart (index: SimIndex) (memberGuid: Guid) =
        let isSource =
            index.WorkTokenRole |> Map.tryFind memberGuid
            |> Option.map (fun r -> r.HasFlag(TokenRole.Source))
            |> Option.defaultValue false
        let preds = SimIndex.findOrEmpty memberGuid index.WorkStartPreds
        not preds.IsEmpty || isSource

    /// 화살표 삭제 후 선행 조건이 사라진 Going Work를 Ready로 되돌림
    let private revertOrphanedGoingWork (ctx: Context) (workGuid: Guid) =
        ctx.StateManager.ClearWorkPending(workGuid)
        ctx.CancelDurationEvent workGuid
        ctx.ClearPausedDuration workGuid
        // 토큰 회수
        match ctx.StateManager.GetWorkToken(workGuid) with
        | Some token ->
            ctx.StateManager.SetWorkToken(workGuid, None)
            ctx.EmitTokenEvent Discard token workGuid None
        | None -> ()
        // Call 상태 초기화
        for callGuid in SimIndex.findOrEmpty workGuid ctx.Index.WorkCallGuids do
            ctx.StateManager.ClearCallPending(callGuid)
            if ctx.StateManager.GetCallState(callGuid) <> Status4.Ready then
                ctx.StateManager.ForceCallState(callGuid, Status4.Ready)
        // Work → Ready
        ctx.StateManager.ClearMinDuration(workGuid)
        ctx.StateManager.ForceWorkState(workGuid, Status4.Ready)
        let wName = ctx.Index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid)
        let clock = TimeSpan.FromMilliseconds(float ctx.Scheduler.CurrentTimeMs)
        ctx.TriggerWorkStateChanged({
            WorkGuid = workGuid; WorkName = wName
            PreviousState = Status4.Going; NewState = Status4.Ready; Clock = clock })

    let removeScheduledConditionEvents (ctx: Context) =
        ctx.Scheduler.RemoveWhere(fun event ->
            match event.EventType with
            | ScheduledEventType.EvaluateConditions -> true
            | _ -> false)
        |> ignore

    /// 새 topology에서 선행 노드가 사라진 Going Work를 되돌림 (ReferenceOf 그룹 인식)
    let invalidateOrphanedGoingWorks (ctx: Context) =
        let processedCanonicals = Collections.Generic.HashSet<Guid>()
        for workGuid in ctx.Index.AllWorkGuids do
            let canonical = SimIndex.canonicalWorkGuid ctx.Index workGuid
            if not (processedCanonicals.Contains(canonical))
               && ctx.IsActiveSystemWork workGuid
               && ctx.StateManager.GetWorkState(workGuid) = Status4.Going then
                processedCanonicals.Add(canonical) |> ignore
                let groupMembers = SimIndex.referenceGroupOf ctx.Index workGuid
                // 그룹 내 어느 멤버든 유효한 선행 노드가 있으면 전체 그룹 유지
                let anyMemberJustified = groupMembers |> List.exists (memberHasValidStart ctx.Index)
                if not anyMemberJustified then
                    // 토큰을 가지고 있고 아직 토큰 경로에 남아있으면 보존
                    // → 완료 후 shiftToken이 새 topology의 후속자로 자연 이동
                    let hasToken = ctx.StateManager.GetWorkToken(canonical) |> Option.isSome
                    let stillInPath = ctx.Index.TokenPathGuids.Contains canonical
                    if not (hasToken && stillInPath) then
                        revertOrphanedGoingWork ctx canonical

    let invalidateOrphanedTokenHolders (ctx: Context) =
        for workGuid in ctx.Index.AllWorkGuids do
            if ctx.IsActiveSystemWork workGuid
               && not (ctx.Index.TokenPathGuids.Contains workGuid)
               && not (ctx.Index.TokenSinkGuids.Contains workGuid) then
                let workState = ctx.StateManager.GetWorkState(workGuid)
                // active 상태(Going/Finish/Homing) work의 토큰은 보존 — 완료 시 자연 처리됨
                if workState = Status4.Ready then
                    match ctx.StateManager.GetWorkToken(workGuid) with
                    | Some token ->
                        ctx.StateManager.SetWorkToken(workGuid, None)
                        ctx.EmitTokenEvent Discard token workGuid None
                    | None -> ()
