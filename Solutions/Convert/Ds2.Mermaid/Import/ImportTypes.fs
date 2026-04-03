namespace Ds2.Mermaid

/// 임포트할 대상 레벨
type ImportLevel =
    | SystemLevel   // Mermaid를 System 하위에 매핑 (3-depth: System > Flow > Work > Call)
    | FlowLevel     // Mermaid를 Flow 하위에 매핑
    | WorkLevel     // Mermaid를 Work 하위에 매핑

/// Mermaid 그래프의 depth 분석 결과
type DepthInfo = {
    HasSubgraphs: bool
    HasNestedSubgraphs: bool
    MaxDepth: int
    SubgraphCount: int
    TotalNodeCount: int
    GlobalEdgeCount: int
}

/// 프리뷰 정보 (다이얼로그에 표시)
type ImportPreview = {
    Level: ImportLevel
    FlowNames: string list
    WorkNames: string list
    CallNames: string list
    ArrowWorksCount: int
    ArrowCallsCount: int
    IgnoredEdges: (string * string) list
    Warnings: string list
}

/// 검증 결과
type ValidationResult =
    | MermaidValid
    | MermaidInvalid of errors: string list
