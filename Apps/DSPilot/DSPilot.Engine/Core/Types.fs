namespace DSPilot.Engine

open Ds2.Core

/// 색상 클래스를 문자열로 변환
module ColorClassHelper =
    let toString = LoggingHelpers.colorClassToString

/// Tag 매칭 모드
type TagMatchMode =
    | ByName      // Tag 이름으로 매칭
    | ByAddress   // Tag 주소로 매칭

/// Call 매핑 정보
[<CLIMutable>]
type CallMappingInfo = {
    Call: Ds2.Core.Call
    ApiCall: Ds2.Core.ApiCall
    IsInTag: bool
    FlowName: string
}
