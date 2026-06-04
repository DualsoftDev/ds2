namespace Ds2.Mermaid

#nowarn "44" // [Obsolete] mapToFlow/FlowFlat/Work — UI 진입점은 제거됨, 테스트 호환 유지

module MermaidMapper =

    let mapArrowType label = MermaidMapperCommon.mapArrowType label
    let mapToFlow store flowId systemId projectId graph = MermaidMapperTargets.mapToFlow store flowId systemId projectId graph
    let mapToFlowFlat store flowId systemId graph = MermaidMapperTargets.mapToFlowFlat store flowId systemId graph
    let mapToWork store workId projectId graph = MermaidMapperTargets.mapToWork store workId projectId graph
    let mapToSystem store projectId graph = MermaidMapperTargets.mapToSystem store projectId graph
    /// Plan + callIndex 페어 — IoTag sidecar 바인딩용. `loadProjectFromFile` 가 호출.
    let mapToSystemEx store projectId graph = MermaidMapperTargets.mapToSystemEx store projectId graph
    let buildPreview graph level = MermaidMapperTargets.buildPreview graph level
