using AasCore.Aas3_0;
using AasxEditor.Models;
using Env = AasCore.Aas3_0.Environment;

namespace AasxEditor.Services;

public class AasTreeBuilderService
{
    public List<AasTreeNode> BuildTree(Env env)
    {
        var root = new List<AasTreeNode>();

        // Asset Administration Shells
        if (env.AssetAdministrationShells is { Count: > 0 } shells)
        {
            foreach (var (shell, si) in shells.Select((s, i) => (s, i)))
            {
                var shellNode = new AasTreeNode
                {
                    Label = shell.IdShort ?? $"Shell [{si}]",
                    NodeType = "Shell",
                    Icon = "S",
                    JsonPath = $"assetAdministrationShells[{si}]",
                    IsExpanded = true,
                    Properties = BuildShellProperties(shell)
                };

                // Asset Information
                if (shell.AssetInformation is { } ai)
                {
                    shellNode.Children.Add(new AasTreeNode
                    {
                        Label = "AssetInformation",
                        NodeType = "AssetInfo",
                        Icon = "A",
                        JsonPath = $"assetAdministrationShells[{si}].assetInformation",
                        Properties = BuildAssetInfoProperties(ai)
                    });
                }

                root.Add(shellNode);
            }
        }

        // Submodels
        if (env.Submodels is { Count: > 0 } submodels)
        {
            var submodelsGroup = new AasTreeNode
            {
                Label = $"Submodels ({submodels.Count})",
                NodeType = "Group",
                Icon = "G",
                JsonPath = "submodels",
                IsExpanded = true
            };

            foreach (var (sm, smi) in submodels.Select((s, i) => (s, i)))
            {
                var smNode = BuildSubmodelNode(sm, smi);
                submodelsGroup.Children.Add(smNode);
            }

            root.Add(submodelsGroup);
        }

        // Concept Descriptions
        if (env.ConceptDescriptions is { Count: > 0 } cds)
        {
            var cdsGroup = new AasTreeNode
            {
                Label = $"ConceptDescriptions ({cds.Count})",
                NodeType = "Group",
                Icon = "G",
                JsonPath = "conceptDescriptions"
            };

            foreach (var (cd, cdi) in cds.Select((c, i) => (c, i)))
            {
                cdsGroup.Children.Add(new AasTreeNode
                {
                    Label = cd.IdShort ?? cd.Id ?? $"CD [{cdi}]",
                    NodeType = "ConceptDescription",
                    Icon = "C",
                    JsonPath = $"conceptDescriptions[{cdi}]",
                    Properties = new Dictionary<string, string?>
                    {
                        ["idShort"] = cd.IdShort,
                        ["id"] = cd.Id
                    }
                });
            }

            root.Add(cdsGroup);
        }

        return root;
    }

    private AasTreeNode BuildSubmodelNode(ISubmodel sm, int index)
    {
        var node = new AasTreeNode
        {
            Label = sm.IdShort ?? $"Submodel [{index}]",
            NodeType = "Submodel",
            Icon = "M",
            JsonPath = $"submodels[{index}]",
            IsExpanded = true,
            Properties = BuildSubmodelProperties(sm)
        };

        if (sm.SubmodelElements is { Count: > 0 } elements)
        {
            foreach (var (elem, ei) in elements.Select((e, i) => (e, i)))
            {
                var childPath = $"submodels[{index}].submodelElements[{ei}]";
                node.Children.Add(BuildElementNode(elem, childPath));
            }
        }

        return node;
    }

    private AasTreeNode BuildElementNode(ISubmodelElement elem, string basePath)
    {
        var node = new AasTreeNode
        {
            Label = elem.IdShort ?? "(unnamed)",
            NodeType = GetElementTypeName(elem),
            Icon = GetElementIcon(elem),
            JsonPath = basePath,
            Properties = BuildElementProperties(elem)
        };

        // SubmodelElementCollection
        if (elem is SubmodelElementCollection smc && smc.Value is { Count: > 0 })
        {
            foreach (var (child, ci) in smc.Value.Select((c, i) => (c, i)))
            {
                node.Children.Add(BuildElementNode(child, $"{basePath}.value[{ci}]"));
            }
        }

        // SubmodelElementList
        if (elem is SubmodelElementList sml && sml.Value is { Count: > 0 })
        {
            foreach (var (child, ci) in sml.Value.Select((c, i) => (c, i)))
            {
                node.Children.Add(BuildElementNode(child, $"{basePath}.value[{ci}]"));
            }
        }

        // Entity
        if (elem is Entity ent && ent.Statements is { Count: > 0 })
        {
            foreach (var (stmt, si) in ent.Statements.Select((s, i) => (s, i)))
            {
                node.Children.Add(BuildElementNode(stmt, $"{basePath}.statements[{si}]"));
            }
        }

        // AnnotatedRelationshipElement
        if (elem is AnnotatedRelationshipElement are && are.Annotations is { Count: > 0 })
        {
            foreach (var (ann, ai) in are.Annotations.Select((a, i) => (a, i)))
            {
                node.Children.Add(BuildElementNode(ann, $"{basePath}.annotations[{ai}]"));
            }
        }

        return node;
    }

    private static string GetElementTypeName(ISubmodelElement elem) => elem switch
    {
        Property => "Property",
        MultiLanguageProperty => "MLP",
        SubmodelElementCollection => "SMC",
        SubmodelElementList => "SML",
        AasCore.Aas3_0.File => "File",
        Blob => "Blob",
        AasCore.Aas3_0.Range => "Range",
        ReferenceElement => "Ref",
        RelationshipElement => "Rel",
        AnnotatedRelationshipElement => "ARel",
        Entity => "Entity",
        Operation => "Op",
        Capability => "Cap",
        BasicEventElement => "Event",
        _ => "Elem"
    };

    private static string GetElementIcon(ISubmodelElement elem) => elem switch
    {
        Property => "P",
        MultiLanguageProperty => "L",
        SubmodelElementCollection => "{}",
        SubmodelElementList => "[]",
        AasCore.Aas3_0.File => "F",
        Operation => "Op",
        _ => "E"
    };

    private static Dictionary<string, string?> BuildShellProperties(IAssetAdministrationShell shell) => new()
    {
        ["idShort"] = shell.IdShort,
        ["id"] = shell.Id,
        ["assetKind"] = shell.AssetInformation?.AssetKind.ToString(),
        ["globalAssetId"] = shell.AssetInformation?.GlobalAssetId
    };

    private static Dictionary<string, string?> BuildAssetInfoProperties(IAssetInformation ai) => new()
    {
        ["assetKind"] = ai.AssetKind.ToString(),
        ["globalAssetId"] = ai.GlobalAssetId,
        ["assetType"] = ai.AssetType
    };

    private static Dictionary<string, string?> BuildSubmodelProperties(ISubmodel sm) => new()
    {
        ["idShort"] = sm.IdShort,
        ["id"] = sm.Id,
        ["kind"] = sm.Kind?.ToString(),
        ["semanticId"] = FormatReference(sm.SemanticId),
        ["elements"] = sm.SubmodelElements?.Count.ToString() ?? "0"
    };

    private static Dictionary<string, string?> BuildElementProperties(ISubmodelElement elem)
    {
        var props = new Dictionary<string, string?>
        {
            ["idShort"] = elem.IdShort,
            ["type"] = GetElementTypeName(elem),
            ["semanticId"] = FormatReference(elem.SemanticId)
        };

        switch (elem)
        {
            case Property p:
                props["valueType"] = p.ValueType.ToString();
                props["value"] = p.Value;
                break;
            case MultiLanguageProperty mlp:
                var firstLang = mlp.Value?.FirstOrDefault();
                props["value"] = firstLang is not null ? $"[{firstLang.Language}] {firstLang.Text}" : null;
                break;
            case AasCore.Aas3_0.File f:
                props["contentType"] = f.ContentType;
                props["value"] = f.Value;
                break;
            case Blob b:
                props["contentType"] = b.ContentType;
                props["size"] = b.Value?.Length.ToString();
                break;
            case AasCore.Aas3_0.Range r:
                props["valueType"] = r.ValueType.ToString();
                props["min"] = r.Min;
                props["max"] = r.Max;
                break;
            case SubmodelElementCollection smc:
                props["children"] = smc.Value?.Count.ToString() ?? "0";
                break;
            case SubmodelElementList sml:
                props["typeValueListElement"] = sml.TypeValueListElement.ToString();
                props["children"] = sml.Value?.Count.ToString() ?? "0";
                break;
            case Operation op:
                props["inputVars"] = op.InputVariables?.Count.ToString() ?? "0";
                props["outputVars"] = op.OutputVariables?.Count.ToString() ?? "0";
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
}
