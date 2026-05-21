namespace Ds2.SymbolImport.Matching

/// <summary>
/// 매직 스트링을 피하기 위한 애플리케이션 전역 상수
/// </summary>
module Constants =

    /// <summary>
    /// 결정할 수 없을 때 사용하는 기본 플로우 이름
    /// </summary>
    [<Literal>]
    let DefaultFlowName = "DefaultFlow"

    /// <summary>
    /// 결정할 수 없을 때 사용하는 기본 작업 이름
    /// </summary>
    [<Literal>]
    let DefaultWorkName = "DefaultWork"

    /// <summary>
    /// 결정할 수 없을 때 사용하는 기본 API 이름
    /// </summary>
    [<Literal>]
    let DefaultApi = "DefaultApi"

    /// <summary>
    /// 그룹화되지 않은 디바이스를 위한 그룹 이름
    /// </summary>
    [<Literal>]
    let NonGroupName = "NonGroup"

    /// <summary>
    /// 디바이스 이름 세그먼트에 사용되는 구분 문자
    /// </summary>
    let SegmentSeparators = [| '_'; '.'; '-' |]


