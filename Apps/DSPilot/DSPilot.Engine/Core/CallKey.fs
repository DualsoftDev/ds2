namespace DSPilot.Engine

open System

/// Call을 고유하게 식별하기 위한 복합 키
/// FlowName과 CallName만으로 고유 키를 구성 (WorkName 불필요)
[<Struct>]
type CallKey =
    { FlowName: string
      CallName: string }

    /// 생성자
    static member Create(flowName: string, callName: string) =
        { FlowName = flowName
          CallName = callName }

    /// 문자열 표현 (로깅용)
    override this.ToString() =
        sprintf "%s/%s" this.FlowName this.CallName

    /// 해시 키 생성 (Dictionary 키로 사용)
    member this.ToHashKey() =
        sprintf "%s|%s" this.FlowName this.CallName
