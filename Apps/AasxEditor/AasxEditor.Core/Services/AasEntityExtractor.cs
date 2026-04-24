using System.Text.Json;
using AasCore.Aas3_1;
using AasxEditor.Models;
using Env = AasCore.Aas3_1.Environment;
using File = AasCore.Aas3_1.File;
using Range = AasCore.Aas3_1.Range;

namespace AasxEditor.Services;

/// <summary>
/// AAS Environment에서 DB 저장용 엔티티 레코드를 추출
/// </summary>
public class AasEntityExtractor
{
    public List<AasEntityRecord> Extract(Env env)
    {
        var records = new List<AasEntityRecord>();

        if (env.AssetAdministrationShells is { Count: > 0 } shells)
        {
            foreach (var (shell, si) in shells.Select((s, i) => (s, i)))
            {
                var path = $"assetAdministrationShells[{si}]";
                records.Add(new AasEntityRecord
                {
                    EntityType = "Shell",
                    IdShort = shell.IdShort ?? $"Shell [{si}]",
                    AasId = shell.Id,
                    JsonPath = path,
                    SemanticId = null,
                    PropertiesJson = ToJson(new
                    {
                        shell.IdShort,
                        shell.Id,
                        AssetKind = shell.AssetInformation?.AssetKind.ToString(),
                        shell.AssetInformation?.GlobalAssetId
                    })
                });
            }
        }

        if (env.Submodels is { Count: > 0 } submodels)
        {
            foreach (var (sm, smi) in submodels.Select((s, i) => (s, i)))
            {
                var smPath = $"submodels[{smi}]";
                records.Add(new AasEntityRecord
                {
                    EntityType = "Submodel",
                    IdShort = sm.IdShort ?? $"Submodel [{smi}]",
                    AasId = sm.Id,
                    JsonPath = smPath,
                    SemanticId = FormatReference(sm.SemanticId),
                    PropertiesJson = ToJson(new
                    {
                        sm.IdShort,
                        sm.Id,
                        Kind = sm.Kind?.ToString(),
                        SemanticId = FormatReference(sm.SemanticId),
                        Elements = sm.SubmodelElements?.Count ?? 0
                    })
                });

                if (sm.SubmodelElements is { Count: > 0 } elements)
                {
                    foreach (var (elem, ei) in elements.Select((e, i) => (e, i)))
                    {
                        ExtractElement(records, elem, $"{smPath}.submodelElements[{ei}]", smPath);
                    }
                }
            }
        }

        if (env.ConceptDescriptions is { Count: > 0 } cds)
        {
            foreach (var (cd, cdi) in cds.Select((c, i) => (c, i)))
            {
                records.Add(new AasEntityRecord
                {
                    EntityType = "ConceptDescription",
                    IdShort = cd.IdShort ?? cd.Id ?? $"CD [{cdi}]",
                    AasId = cd.Id,
                    JsonPath = $"conceptDescriptions[{cdi}]",
                    SemanticId = null,
                    PropertiesJson = ToJson(new { cd.IdShort, cd.Id })
                });
            }
        }

        return records;
    }

    private void ExtractElement(List<AasEntityRecord> records, ISubmodelElement elem, string path, string parentPath)
    {
        var record = new AasEntityRecord
        {
            EntityType = GetTypeName(elem),
            IdShort = elem.IdShort ?? "(unnamed)",
            JsonPath = path,
            ParentJsonPath = parentPath,
            SemanticId = FormatReference(elem.SemanticId)
        };

        switch (elem)
        {
            case Property p:
                record.Value = p.Value;
                record.ValueType = p.ValueType.ToString();
                break;
            case MultiLanguageProperty mlp:
                var first = mlp.Value?.FirstOrDefault();
                record.Value = first is not null ? $"[{first.Language}] {first.Text}" : null;
                break;
            case File f:
                record.Value = f.Value;
                record.ValueType = f.ContentType;
                break;
            case Range r:
                record.Value = $"{r.Min} ~ {r.Max}";
                record.ValueType = r.ValueType.ToString();
                break;
        }

        record.PropertiesJson = ToJson(BuildProps(elem));
        records.Add(record);

        // 재귀: 자식 요소
        IEnumerable<(ISubmodelElement child, int ci)>? children = elem switch
        {
            SubmodelElementCollection smc when smc.Value is { Count: > 0 } =>
                smc.Value.Select((c, i) => (c, i)),
            SubmodelElementList sml when sml.Value is { Count: > 0 } =>
                sml.Value.Select((c, i) => (c, i)),
            Entity ent when ent.Statements is { Count: > 0 } =>
                ent.Statements.Select((s, i) => ((ISubmodelElement)s, i)),
            AnnotatedRelationshipElement are when are.Annotations is { Count: > 0 } =>
                are.Annotations.Select((a, i) => ((ISubmodelElement)a, i)),
            _ => null
        };

        if (children is not null)
        {
            var childProp = elem switch
            {
                Entity => "statements",
                AnnotatedRelationshipElement => "annotations",
                _ => "value"
            };

            foreach (var (child, ci) in children)
            {
                ExtractElement(records, child, $"{path}.{childProp}[{ci}]", path);
            }
        }
    }

    private static string GetTypeName(ISubmodelElement elem) => elem switch
    {
        Property => "Property",
        MultiLanguageProperty => "MLP",
        SubmodelElementCollection => "SMC",
        SubmodelElementList => "SML",
        File => "File",
        Blob => "Blob",
        Range => "Range",
        ReferenceElement => "Ref",
        RelationshipElement => "Rel",
        AnnotatedRelationshipElement => "ARel",
        Entity => "Entity",
        Operation => "Op",
        Capability => "Cap",
        BasicEventElement => "Event",
        _ => "Elem"
    };

    private static Dictionary<string, object?> BuildProps(ISubmodelElement elem)
    {
        var props = new Dictionary<string, object?>
        {
            ["idShort"] = elem.IdShort,
            ["type"] = GetTypeName(elem),
            ["semanticId"] = FormatReference(elem.SemanticId)
        };

        switch (elem)
        {
            case Property p:
                props["valueType"] = p.ValueType.ToString();
                props["value"] = p.Value;
                break;
            case MultiLanguageProperty mlp:
                var first = mlp.Value?.FirstOrDefault();
                props["value"] = first is not null ? $"[{first.Language}] {first.Text}" : null;
                break;
            case File f:
                props["contentType"] = f.ContentType;
                props["value"] = f.Value;
                break;
            case Range r:
                props["valueType"] = r.ValueType.ToString();
                props["min"] = r.Min;
                props["max"] = r.Max;
                break;
            case SubmodelElementCollection smc:
                props["children"] = smc.Value?.Count ?? 0;
                break;
            case SubmodelElementList sml:
                props["typeValueListElement"] = sml.TypeValueListElement.ToString();
                props["children"] = sml.Value?.Count ?? 0;
                break;
            case Operation op:
                props["inputVars"] = op.InputVariables?.Count ?? 0;
                props["outputVars"] = op.OutputVariables?.Count ?? 0;
                break;
            case Entity ent:
                props["entityType"] = ent.EntityType.ToString();
                break;
        }

        return props;
    }

    private static string? FormatReference(IReference? reference)
    {
        if (reference?.Keys is not { Count: > 0 } keys) return null;
        return string.Join(" / ", keys.Select(k => k.Value));
    }

    private static string ToJson(object obj) =>
        JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
}
