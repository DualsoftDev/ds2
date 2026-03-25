namespace Ds2.Mermaid

module MermaidMapper =

    let mapArrowType label = MermaidMapperCommon.mapArrowType label
    let mapToFlow store flowId systemId projectId graph = MermaidMapperTargets.mapToFlow store flowId systemId projectId graph
    let mapToFlowFlat store flowId systemId graph = MermaidMapperTargets.mapToFlowFlat store flowId systemId graph
    let mapToWork store workId projectId graph = MermaidMapperTargets.mapToWork store workId projectId graph
    let mapToSystem store projectId graph = MermaidMapperTargets.mapToSystem store projectId graph
    let buildPreview graph level = MermaidMapperTargets.buildPreview graph level
