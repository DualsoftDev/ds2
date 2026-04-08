namespace AasxEditor.Models;

/// <summary>
/// DB에 저장되는 AASX 파일 메타데이터
/// </summary>
public class AasxFileRecord
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public int ShellCount { get; set; }
    public int SubmodelCount { get; set; }
    public string? JsonContent { get; set; }
}

/// <summary>
/// DB에 저장되는 AAS 엔티티 (Shell, Submodel, Property, SMC 등)
/// </summary>
public class AasEntityRecord
{
    public long Id { get; set; }
    public long FileId { get; set; }
    public string EntityType { get; set; } = "";
    public string IdShort { get; set; } = "";
    public string? AasId { get; set; }
    public string JsonPath { get; set; } = "";
    public string? SemanticId { get; set; }
    public string? Value { get; set; }
    public string? ValueType { get; set; }
    public string? ParentJsonPath { get; set; }
    public string PropertiesJson { get; set; } = "{}";
}

/// <summary>
/// 검색 조건
/// </summary>
public class AasSearchQuery
{
    public string? Text { get; set; }
    public string? EntityType { get; set; }
    public string? SemanticId { get; set; }
    public long? FileId { get; set; }
}
