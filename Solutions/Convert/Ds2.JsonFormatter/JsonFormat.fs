namespace Ds2.JsonFormatter

/// JSON 포맷 정보 (외부 시스템 연동용 상수)
module JsonFormat =

    /// 현재 포맷 버전
    [<Literal>]
    let FormatVersion = "2.0"

    /// ArrowType 정수 매핑
    let arrowTypeUnspecified = 0
    let arrowTypeStart = 1
    let arrowTypeReset = 2
    let arrowTypeStartReset = 3
    let arrowTypeResetReset = 4
    let arrowTypeGroup = 5

    /// Status4 정수 매핑
    let status4Ready = 0
    let status4Going = 1
    let status4Finish = 2
    let status4Homing = 3

    /// CallType 정수 매핑
    let callTypeWaitForCompletion = 0
    let callTypeSkipIfCompleted = 1

    /// TokenRole 정수 매핑 (Flags)
    let tokenRoleNone = 0
    let tokenRoleSource = 1
    let tokenRoleIgnore = 2
    let tokenRoleSink = 4

    /// CallConditionType 정수 매핑
    let callConditionAutoAux = 0
    let callConditionComAux = 1
    let callConditionSkipUnmatch = 2

    /// FlowTag 정수 매핑
    let flowTagReady = 0
    let flowTagDrive = 1
    let flowTagPause = 2

    /// ValueSpec Case 문자열
    let valueSpecUndefined = "UndefinedValue"
    let valueSpecBool = "BoolValue"
    let valueSpecInt8 = "Int8Value"
    let valueSpecInt16 = "Int16Value"
    let valueSpecInt32 = "Int32Value"
    let valueSpecInt64 = "Int64Value"
    let valueSpecUInt8 = "UInt8Value"
    let valueSpecUInt16 = "UInt16Value"
    let valueSpecUInt32 = "UInt32Value"
    let valueSpecUInt64 = "UInt64Value"
    let valueSpecFloat32 = "Float32Value"
    let valueSpecFloat64 = "Float64Value"
    let valueSpecString = "StringValue"

    /// ValueSpec Inner Case 문자열
    let valueSpecInnerUndefined = "Undefined"
    let valueSpecInnerSingle = "Single"
    let valueSpecInnerMultiple = "Multiple"
    let valueSpecInnerRanges = "Ranges"

    /// BoundType 문자열
    let boundTypeOpen = "Open"
    let boundTypeClosed = "Closed"

    /// TokenValue Case 문자열
    let tokenValueIntToken = "IntToken"
