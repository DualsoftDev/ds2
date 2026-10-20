namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core

module internal TokenFlow =
    type Context = {
        Index: SimIndex
        StateManager: StateManager
        CurrentTimeMs: unit -> int64
        TriggerTokenEvent: TokenEventArgs -> unit
    }

    let workNameOf (ctx: Context) guid =
        ctx.Index.WorkName |> Map.tryFind guid |> Option.defaultValue (string guid)

    let canReceiveToken (ctx: Context) workGuid =
        ctx.StateManager.GetWorkState(workGuid) = Status4.Ready
        && (SimState.getWorkToken workGuid (ctx.StateManager.GetState()) |> Option.isNone)

    let emitTokenEvent (ctx: Context) kind token workGuid (targetGuid: Guid option) =
        let clock = TimeSpan.FromMilliseconds(float (ctx.CurrentTimeMs()))
        ctx.TriggerTokenEvent({
            Kind = kind
            Token = token
            WorkGuid = workGuid
            WorkName = workNameOf ctx workGuid
            TargetWorkGuid = targetGuid
            TargetWorkName = targetGuid |> Option.map (workNameOf ctx)
            Clock = clock })

    let shiftToken (ctx: Context) (workGuid: Guid) (token: TokenValue) =
        let completeTokenAtWork () =
            ctx.StateManager.SetWorkToken(workGuid, None)
            ctx.StateManager.AddCompletedToken(token)
            emitTokenEvent ctx Complete token workGuid None

        // Group sync: workGuid 또는 그 reference group 의 멤버가 Group arrow 그룹에 속해 있으면
        // 그룹 모든 멤버 Finish 까지 shift 차단.
        // - 정상 모델 (`A→Group[B,C,D]→E`): token 이 멤버 모두에 분배되어 각자 Finish 시점에
        //   shiftToken 이 발화하지만 그룹 미완료 멤버가 있으면 차단 → token holding.
        //   마지막 멤버 Finish 시 통과 + 다른 멤버의 token 도 함께 clear (다음 사이클 보장).
        // - 잘못된 모델 (`A→C / Group[B, C(REF), D]`): token 이 C (entry) 만 받아 단독 Finish 후
        //   shift 시도 → C 의 reference group ([C, C_REF]) 중 C_REF 가 그룹 멤버 → 그룹 다른
        //   멤버 (B, D) 가 시작도 못 했으므로 영구 차단 (deadlock 노출 → 모델 오류 표면화).
        let groupSet =
            SimIndex.referenceGroupOf ctx.Index workGuid
            |> List.collect (fun g -> SimIndex.workGroupOf ctx.Index g |> Set.toList)
            |> Set.ofList
        let refGroup = SimIndex.referenceGroupOf ctx.Index workGuid |> Set.ofList
        let waitingForGroup =
            if Set.isEmpty groupSet then false
            else
                groupSet
                |> Set.exists (fun g ->
                    not (Set.contains g refGroup)
                    && ctx.StateManager.GetWorkState(g) <> Status4.Finish)

        // 원본/REF Work 의 outgoing edge 가 각자 다를 수 있으므로 (예: 원본은 Work2 로,
        // REF 는 Work4 로 연결) reference group 전체의 successors 합집합을 사용해 token 분기.
        // canonical workGuid 의 successors 만 보면 REF 후속 Work 가 token 못 받아 시작 안 됨.
        let receivableSuccessors () =
            let groupGuids = SimIndex.referenceGroupOf ctx.Index workGuid
            groupGuids
            |> List.collect (fun g ->
                ctx.Index.WorkTokenSuccessors
                |> Map.tryFind g
                |> Option.defaultValue [])
            |> List.distinct
            |> List.filter (canReceiveToken ctx)

        let shiftTokenToTargets targetGuids =
            for targetGuid in targetGuids do
                ctx.StateManager.SetWorkToken(targetGuid, Some token)
                ctx.StateManager.SetWorkTokenFrom(targetGuid, workGuid)
                emitTokenEvent ctx Shift token workGuid (Some targetGuid)
            ctx.StateManager.SetWorkToken(workGuid, None)
            // 그룹 멤버 token 도 clear — 다음 사이클에서 멤버들이 다시 token 받을 수 있게.
            if not (Set.isEmpty groupSet) then
                for peer in groupSet do
                    if peer <> workGuid then
                        match SimState.getWorkToken peer (ctx.StateManager.GetState()) with
                        | Some peerToken ->
                            ctx.StateManager.SetWorkToken(peer, None)
                            emitTokenEvent ctx Complete peerToken peer None
                        | None -> ()

        // FS1 CAT-11-R06 — 같은 제품이 갈라졌다 다시 합치면 동기화이지 두 제품이 아니다. 뒤 Work 가 이미
        // **같은 토큰**을 받았으면 이 가지의 사본은 그쪽으로 합친다. 막으면 이 Work 는 토큰을 쥔 채 다시 준비(homing)를
        // 끝내지 못해 영구히 선다 — 늦게 끝난 가지마다 하나씩(V&V 20·23, #8934: n 갈래 중 n-1 정지).
        let mergeTarget () =
            SimIndex.referenceGroupOf ctx.Index workGuid
            |> List.collect (fun g -> ctx.Index.WorkTokenSuccessors |> Map.tryFind g |> Option.defaultValue [])
            |> List.distinct
            |> List.tryFind (fun g -> SimState.getWorkToken g (ctx.StateManager.GetState()) = Some token)

        if waitingForGroup then
            // 그룹 미완료 — token holding. 다른 멤버 Finish 시 다시 shiftToken 호출됨.
            emitTokenEvent ctx Blocked token workGuid None
        elif ctx.Index.TokenSinkGuids.Contains(workGuid) then
            completeTokenAtWork ()
        else
            match receivableSuccessors () with
            | [] ->
                match mergeTarget () with
                | Some target ->
                    ctx.StateManager.SetWorkToken(workGuid, None)
                    emitTokenEvent ctx Shift token workGuid (Some target)
                | None -> emitTokenEvent ctx Blocked token workGuid None
            | targetGuids -> shiftTokenToTargets targetGuids

    /// Seed 대상 Work에 대응하는 origin label 결정.
    /// TokenSpec에 매칭되는 항목이 있으면 그 Label, 없으면 Work 이름.
    let resolveSeedOriginLabel (ctx: Context) (sourceWorkGuid: Guid) =
        let canonicalSourceWorkGuid = SimIndex.canonicalWorkGuid ctx.Index sourceWorkGuid
        Queries.getTokenSpecs ctx.Index.Store
        |> List.tryFind (fun spec ->
            spec.WorkId
            |> Option.map (fun workGuid -> SimIndex.canonicalWorkGuid ctx.Index workGuid = canonicalSourceWorkGuid)
            |> Option.defaultValue false)
        |> Option.map (fun spec -> spec.Label)
        |> Option.defaultWith (fun () -> workNameOf ctx canonicalSourceWorkGuid)

    /// #8938 — 새 토큰의 제품 사양. 차례대로:
    ///  1. 그 소스를 지령하는 api 의 제품(2026-10-20 Q43 `api X { …; product N; }` → ApiDef.TokenSpecId). 그런 api 가 여럿이고
    ///     제품이 서로 다르면 F5 는 어느 지령인지 모르므로 미정이다.
    ///  2. 출처 Work 가 맞는 사양(`product N "…" from …`).
    ///  3. 사양이 하나뿐이면 그것.
    /// 미정이면 None — 그 토큰의 `token.필드` 조건은 거짓이다(건너뛰지 않는다).
    let resolveSeedSpecId (ctx: Context) (sourceWorkGuid: Guid) : int option =
        let canonicalSourceWorkGuid = SimIndex.canonicalWorkGuid ctx.Index sourceWorkGuid
        let specs = Queries.getTokenSpecs ctx.Index.Store
        let known id = specs |> List.exists (fun spec -> spec.Id = id)
        let fromApi =
            ctx.Index.Store.ApiDefs.Values
            |> Seq.filter (fun api ->
                api.TxGuid
                |> Option.map (fun workGuid -> SimIndex.canonicalWorkGuid ctx.Index workGuid = canonicalSourceWorkGuid)
                |> Option.defaultValue false)
            |> Seq.choose (fun api -> api.TokenSpecId)
            |> Seq.distinct
            |> Seq.toList
        let fromSource =
            specs |> List.tryFind (fun spec ->
                spec.WorkId
                |> Option.map (fun workGuid -> SimIndex.canonicalWorkGuid ctx.Index workGuid = canonicalSourceWorkGuid)
                |> Option.defaultValue false)
        match fromApi, fromSource, specs with
        | [ id ], _, _ when known id -> Some id
        | _ :: _ :: _, _, _ -> None
        | _, Some spec, _ -> Some spec.Id
        | _, None, [ only ] -> Some only.Id
        | _ -> None

    let onWorkFinish (ctx: Context) (workGuid: Guid) =
        let tryGetActiveToken () =
            SimState.getWorkToken workGuid (ctx.StateManager.GetState())

        let isIgnoreRole () =
            match ctx.Index.WorkTokenRole |> Map.tryFind workGuid with
            | Some role -> role.HasFlag(TokenRole.Ignore)
            | None -> false

        let completeTokenAtWork token =
            ctx.StateManager.SetWorkToken(workGuid, None)
            ctx.StateManager.AddCompletedToken(token)
            emitTokenEvent ctx Complete token workGuid None

        match tryGetActiveToken () with
        | None -> ()
        | Some token when isIgnoreRole () ->
            completeTokenAtWork token
        | Some token ->
            shiftToken ctx workGuid token
