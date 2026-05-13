namespace Ds2.Core

open System

/// Work 계층에서 반복되는 공통 실행 속성.
type CommonWorkProperties<'T when 'T :> CommonWorkProperties<'T>>() =
    inherit PropertiesBase<'T>()

    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

/// Call 계층에서 반복되는 공통 실행 속성.
type CommonCallProperties<'T when 'T :> CommonCallProperties<'T>>() =
    inherit PropertiesBase<'T>()

    member val ObjectName: string = "" with get, set
    member val ActionName: string = "" with get, set
    member val RobotExecutable: string option = None with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val CallDirection: string option = None with get, set
