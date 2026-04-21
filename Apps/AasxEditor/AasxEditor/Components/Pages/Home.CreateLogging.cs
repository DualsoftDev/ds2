using AasCore.Aas3_1;
using Env = AasCore.Aas3_1.Environment;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    private const string LoggingSubmodelIdShort = "SequenceLogging";
    private const string ModelSubmodelIdShort = "SequenceModel";
    private const string SystemPropertiesIdShort = "SystemProperties";
    private const string ActiveSystemsIdShort = "ActiveSystems";
    private const string ProjectSmcIdShort = "Project";
    private const string LoggingSemanticUri = "https://dualsoft.com/aas/submodel/logging";

    private bool _createLoggingBannerDismissed;
    private bool _showCreateLoggingModal;
    private List<SystemPickerItem> _systemPickerItems = [];

    private sealed class SystemPickerItem
    {
        public string Label { get; set; } = "";
        public Guid Guid { get; set; }
        public bool Selected { get; set; } = true;
    }

    private bool HasSequenceLoggingSubmodel
        => _currentEnv?.Submodels?.Any(sm => sm.IdShort == LoggingSubmodelIdShort) == true;

    private bool HasSequenceModelSubmodel
        => _currentEnv?.Submodels?.Any(sm => sm.IdShort == ModelSubmodelIdShort) == true;

    private bool ShouldShowCreateLoggingBanner
        => _contentLoaded
           && _currentEnv is not null
           && HasSequenceModelSubmodel
           && !HasSequenceLoggingSubmodel
           && !_createLoggingBannerDismissed;

    private void OnDismissCreateLoggingBanner() => _createLoggingBannerDismissed = true;

    private void OnOpenCreateLoggingModal()
    {
        _systemPickerItems = GetActiveSystemsFromEnv()
            .Select(s => new SystemPickerItem { Label = s.Label, Guid = s.Guid, Selected = true })
            .ToList();
        _showCreateLoggingModal = true;
    }

    private void OnCancelCreateLogging() => _showCreateLoggingModal = false;

    private void OnTogglePickerItem(SystemPickerItem item) => item.Selected = !item.Selected;

    private void OnPickerSelectAll()
    {
        foreach (var it in _systemPickerItems) it.Selected = true;
    }

    private void OnPickerDeselectAll()
    {
        foreach (var it in _systemPickerItems) it.Selected = false;
    }

    private async Task OnConfirmCreateLogging()
    {
        if (_currentEnv is null) { _showCreateLoggingModal = false; return; }

        var selected = _systemPickerItems.Where(i => i.Selected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("하나 이상의 System을 선택하세요", "error");
            return;
        }

        PushUndo("SequenceLogging 서브모델 추가");

        try
        {
            InjectSequenceLoggingSubmodel(_currentEnv, selected.Select(s => s.Guid).ToList());
            var json = Converter.EnvironmentToJson(_currentEnv);
            _currentEnv = Converter.JsonToEnvironment(json);
            await SyncJsonToEditorAsync(json);
            RebuildTree();
            _showCreateLoggingModal = false;
            _createLoggingBannerDismissed = false;
            SetStatus($"SequenceLogging 서브모델 추가됨 ({selected.Count} System)", "success");
        }
        catch (Exception ex)
        {
            SetStatus($"SequenceLogging 생성 실패: {ex.Message}", "error");
        }
    }

    private List<(string Label, Guid Guid)> GetActiveSystemsFromEnv()
    {
        var result = new List<(string, Guid)>();
        if (_currentEnv?.Submodels is null) return result;

        var modelSm = _currentEnv.Submodels.FirstOrDefault(sm => sm.IdShort == ModelSubmodelIdShort);
        if (modelSm?.SubmodelElements is null) return result;

        var projectSmc = modelSm.SubmodelElements
            .OfType<SubmodelElementCollection>()
            .FirstOrDefault(e => e.IdShort == ProjectSmcIdShort);
        if (projectSmc?.Value is null) return result;

        var activeSystemsSml = projectSmc.Value
            .OfType<SubmodelElementList>()
            .FirstOrDefault(e => e.IdShort == ActiveSystemsIdShort);
        if (activeSystemsSml?.Value is null) return result;

        foreach (var elem in activeSystemsSml.Value.OfType<SubmodelElementCollection>())
        {
            var guid = TryExtractSystemGuid(elem);
            if (guid is null) continue;
            var name = TryExtractSystemName(elem) ?? elem.IdShort ?? guid.Value.ToString();
            result.Add((name, guid.Value));
        }
        return result;
    }

    private static Guid? TryExtractSystemGuid(SubmodelElementCollection smc)
    {
        if (smc.IdShort is { } id && id.StartsWith("System_", StringComparison.Ordinal))
        {
            var g = id["System_".Length..];
            if (Guid.TryParseExact(g, "N", out var parsed)) return parsed;
            if (Guid.TryParse(g, out parsed)) return parsed;
        }
        if (smc.Value is not null)
        {
            var p = smc.Value.OfType<Property>().FirstOrDefault(x =>
                x.IdShort is "Guid" or "Id" or "SystemGuid");
            if (p?.Value is { } v && Guid.TryParse(v, out var g)) return g;
        }
        return null;
    }

    private static string? TryExtractSystemName(SubmodelElementCollection smc)
    {
        if (smc.Value is null) return null;
        var p = smc.Value.OfType<Property>().FirstOrDefault(x => x.IdShort == "Name");
        return p?.Value;
    }

    private static void InjectSequenceLoggingSubmodel(Env env, List<Guid> systemGuids)
    {
        var loggingId = DeriveLoggingSubmodelId(env);

        var semanticRef = new Reference(
            ReferenceTypes.ExternalReference,
            new List<IKey> { new Key(KeyTypes.GlobalReference, LoggingSemanticUri) });

        var sysPropsSmc = new SubmodelElementCollection
        {
            IdShort = SystemPropertiesIdShort,
            Value = new List<ISubmodelElement>()
        };

        foreach (var guid in systemGuids)
        {
            var errDefs = new SubmodelElementList(
                typeValueListElement: AasSubmodelElements.Property,
                valueTypeListElement: DataTypeDefXsd.String)
            {
                IdShort = "ErrorDefinitions",
                Value = new List<ISubmodelElement>()
            };

            var systemSmc = new SubmodelElementCollection
            {
                IdShort = "System_" + guid.ToString("N"),
                Value = new List<ISubmodelElement> { errDefs }
            };
            sysPropsSmc.Value.Add(systemSmc);
        }

        var loggingSm = new Submodel(id: loggingId)
        {
            IdShort = LoggingSubmodelIdShort,
            SemanticId = semanticRef,
            SubmodelElements = new List<ISubmodelElement> { sysPropsSmc }
        };

        env.Submodels ??= new List<ISubmodel>();
        env.Submodels.Add(loggingSm);

        if (env.AssetAdministrationShells is { Count: > 0 } shells)
        {
            var shell = shells[0];
            shell.Submodels ??= new List<IReference>();
            var smRef = new Reference(
                ReferenceTypes.ModelReference,
                new List<IKey> { new Key(KeyTypes.Submodel, loggingId) });
            shell.Submodels.Add(smRef);
        }
    }

    private static string DeriveLoggingSubmodelId(Env env)
    {
        var modelSm = env.Submodels?.FirstOrDefault(sm => sm.IdShort == ModelSubmodelIdShort);
        if (modelSm?.Id is { } baseId && Guid.TryParse(baseId, out var g))
        {
            var bytes = g.ToByteArray();
            bytes[15] = (byte)(bytes[15] + 4);
            return new Guid(bytes).ToString();
        }
        return Guid.NewGuid().ToString();
    }
}
