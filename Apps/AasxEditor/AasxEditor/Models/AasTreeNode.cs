namespace AasxEditor.Models;

public class AasTreeNode
{
    public string Label { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string Icon { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public bool IsExpanded { get; set; }
    public List<AasTreeNode> Children { get; set; } = [];

    /// <summary>
    /// 이 노드에 해당하는 AAS 객체의 주요 속성들 (Properties 패널용)
    /// </summary>
    public Dictionary<string, string?> Properties { get; set; } = [];
}
