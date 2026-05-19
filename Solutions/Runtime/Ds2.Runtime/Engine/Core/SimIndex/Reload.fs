namespace Ds2.Runtime.Engine.Core

open System

module internal SimIndexReload =

    let snapshotConnections (index: SimIndex) : SimIndexConnectionSnapshot = {
        WorkStartPreds = index.WorkStartPreds
        WorkPureStartPreds = index.WorkPureStartPreds
        WorkResetPreds = index.WorkResetPreds
        CallStartPreds = index.CallStartPreds
        WorkTokenSuccessors = index.WorkTokenSuccessors
        TokenPathGuids = index.TokenPathGuids
    }

    let reloadConnections (index: SimIndex) =
        let previous = snapshotConnections index
        let rebuilt = SimIndexBuild.build index.Store index.TickMs
        index.WorkStartPreds <- rebuilt.WorkStartPreds
        index.WorkPureStartPreds <- rebuilt.WorkPureStartPreds
        index.WorkResetPreds <- rebuilt.WorkResetPreds
        index.CallStartPreds <- rebuilt.CallStartPreds
        index.WorkTokenSuccessors <- rebuilt.WorkTokenSuccessors
        index.TokenPathGuids <- rebuilt.TokenPathGuids
        previous, snapshotConnections index

    let reloadDurations (index: SimIndex) (skipGuids: Set<Guid>) =
        index.WorkDuration <-
            SimIndexAlgorithms.reloadDurations
                index.Store
                index.WorkCallGuids
                index.AllWorkGuids
                index.WorkDuration
                skipGuids
