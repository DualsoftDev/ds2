namespace DSPilot.Engine

open System

/// Call을 고유하게 식별하기 위한 복합 키
/// DB 스키마: UNIQUE(CallName, FlowName, WorkName)와 일치
[<Struct>]
type CallKey =
    { FlowName: string
      CallName: string
      WorkName: string option }

    /// FlowName과 CallName을 사용한 생성
    static member Create(flowName: string, callName: string) =
        { FlowName = flowName
          CallName = callName
          WorkName = None }

    /// FlowName, CallName, WorkName을 모두 사용한 생성
    static member CreateFull(flowName: string, callName: string, workName: string option) =
        { FlowName = flowName
          CallName = callName
          WorkName = workName }

    /// 문자열 표현 (로깅용)
    override this.ToString() =
        match this.WorkName with
        | Some wn -> sprintf "%s/%s/%s" this.FlowName wn this.CallName
        | None -> sprintf "%s/%s" this.FlowName this.CallName

    /// 해시 키 생성 (Dictionary 키로 사용)
    member this.ToHashKey() =
        match this.WorkName with
        | Some wn -> sprintf "%s|%s|%s" this.FlowName wn this.CallName
        | None -> sprintf "%s|%s" this.FlowName this.CallName
