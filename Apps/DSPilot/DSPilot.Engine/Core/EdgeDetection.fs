namespace DSPilot.Engine.Core

open System
open System.Collections.Generic

/// Edge type (Rising or Falling) - enum for C# interop
type EdgeType =
    | RisingEdge = 0
    | FallingEdge = 1

/// Edge event detected from PLC tag
type EdgeEvent =
    { TagName: string
      EdgeType: EdgeType
      Timestamp: DateTime
      Source: string }

/// PLC Tag event (raw data from PLC)
type PlcTagEvent =
    { TagName: string
      Value: bool
      Timestamp: DateTime
      Source: string }

/// Tag state tracker for edge detection
type TagStateTracker() =
    let lastStates = Dictionary<string, bool>()

    /// Detect edge from PLC tag event
    member this.DetectEdge(event: PlcTagEvent) : EdgeEvent option =
        match lastStates.TryGetValue(event.TagName) with
        | true, prevValue ->
            // Update state
            lastStates.[event.TagName] <- event.Value

            // Detect edge
            if not prevValue && event.Value then
                // Rising Edge (0 -> 1)
                Some { TagName = event.TagName
                       EdgeType = EdgeType.RisingEdge
                       Timestamp = event.Timestamp
                       Source = event.Source }
            elif prevValue && not event.Value then
                // Falling Edge (1 -> 0)
                Some { TagName = event.TagName
                       EdgeType = EdgeType.FallingEdge
                       Timestamp = event.Timestamp
                       Source = event.Source }
            else
                // No edge (value unchanged)
                None
        | false, _ ->
            // First time seeing this tag, initialize state
            lastStates.[event.TagName] <- event.Value
            None

    /// Clear all tracked states
    member this.Clear() =
        lastStates.Clear()

    /// Get current state of a tag
    member this.GetState(tagName: string) : bool option =
        match lastStates.TryGetValue(tagName) with
        | true, value -> Some value
        | false, _ -> None
