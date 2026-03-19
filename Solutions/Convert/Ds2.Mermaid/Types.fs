namespace Ds2.Mermaid

/// Mermaid 그래프 방향
type MermaidDirection =
    | TD  // Top-Down
    | LR  // Left-Right
    | RL  // Right-Left
    | BT  // Bottom-Top

/// Mermaid 화살표 스타일
type MermaidArrowStyle =
    | Solid   // -->
    | Dashed  // -.->

/// 화살표 라벨 (의미 있는 타입)
type ArrowLabel =
    | NoLabel
    | Interlock
    | SelfReset
    | StartReset
    | StartEdge
    | ResetEdge
    | AutoPre
    | ResetReset
    | Group
    | Custom of string

/// Mermaid 노드 정의
type MermaidNode = {
    /// 노드 ID (예: "CARTYPE_차종초기화_CAR_BT_SV_ON")
    Id: string
    /// 표시 라벨 (예: "CAR_BT_SV.ON")
    Label: string
    /// commonPre에서 추출한 조건 목록 (Legacy Ev2: 노드 ID 리스트)
    CommonConditions: string list
    /// AutoAux 조건 소스 Call 이름 목록
    AutoAuxConditionRefs: string list
    /// ComAux 조건 소스 Call 이름 목록
    ComAuxConditionRefs: string list
    /// SkipUnmatch 조건 소스 Call 이름 목록
    SkipUnmatchConditionRefs: string list
}

/// Mermaid 엣지 정의
type MermaidEdge = {
    /// 소스 노드 ID
    SourceId: string
    /// 타겟 노드 ID
    TargetId: string
    /// 화살표 스타일 (실선/점선)
    Style: MermaidArrowStyle
    /// 화살표 라벨
    Label: ArrowLabel
}

/// Mermaid 서브그래프 정의 (중첩 지원)
type MermaidSubgraph = {
    /// 서브그래프 ID (예: "CARTYPE_차종초기화")
    Id: string
    /// 표시 이름 (["..."]에서 추출)
    DisplayName: string option
    /// 서브그래프 내 노드들
    Nodes: MermaidNode list
    /// 서브그래프 내부 엣지들
    InternalEdges: MermaidEdge list
    /// 중첩 서브그래프 (System > Flow > Work 등)
    Children: MermaidSubgraph list
    /// Passive(Device) System 여부
    IsPassive: bool
}

/// 파싱된 Mermaid 그래프 전체
type MermaidGraph = {
    /// 그래프 방향
    Direction: MermaidDirection
    /// 모든 서브그래프 (중첩 구조 지원)
    Subgraphs: MermaidSubgraph list
    /// 서브그래프 밖의 글로벌 노드 (1-depth 구조)
    GlobalNodes: MermaidNode list
    /// 서브그래프 간 또는 글로벌 엣지
    GlobalEdges: MermaidEdge list
    /// autoPre 점선 엣지 (조건 연결)
    AutoPreEdges: MermaidEdge list
}

/// 파싱 오류 타입
type ParseError =
    | InvalidGraphDirection of string
    | UnclosedSubgraph of string
    | UnknownNode of string
    | InvalidArrowSyntax of content: string * lineNumber: int
    | DuplicateNodeId of string
    | EmptyGraph

module ParseError =
    let toString = function
        | InvalidGraphDirection dir -> $"Invalid graph direction: {dir}"
        | UnclosedSubgraph name -> $"Unclosed subgraph: {name}"
        | UnknownNode id -> $"Unknown node referenced: {id}"
        | InvalidArrowSyntax (content, line) -> $"Invalid arrow syntax at line {line}: {content}"
        | DuplicateNodeId id -> $"Duplicate node ID: {id}"
        | EmptyGraph -> "Empty graph: no subgraphs or nodes found"
