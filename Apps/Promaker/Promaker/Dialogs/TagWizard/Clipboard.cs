using System.Collections.Generic;
using System.Linq;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    private record ClipboardEnvelope(string Type, string Json);

    private void CopyGridSelection(System.Windows.Controls.DataGrid grid)
    {
        try
        {
            object[] selected = grid.SelectedItems.Cast<object>().ToArray();
            if (selected.Length == 0) return;

            string typeTag;
            string payload;
            if (selected[0] is SignalPatternRow)
            {
                typeTag = "SignalPatternRow";
                var items = selected.Cast<SignalPatternRow>()
                    .Select(r => new SignalRowClipboardItem(
                        r.ApiName ?? "", r.Pattern ?? "", r.TargetFBPort ?? "",
                        r.SkipAddressAlloc, r.IsSpare, r.UserDataType ?? "",
                        Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(r.PreFbCondition)))
                    .ToList();
                payload = System.Text.Json.JsonSerializer.Serialize(items);
            }
            else if (selected[0] is AuxPortRow)
            {
                typeTag = "AuxPortRow";
                var items = selected.Cast<AuxPortRow>()
                    .Select(r => new AuxPortClipboardItem(
                        r.ApiName ?? "", r.TargetFBPort ?? "",
                        r.Kind ?? "DirectFB", r.AuxKind ?? "AutoAux",
                        Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(r.Condition)))
                    .ToList();
                payload = System.Text.Json.JsonSerializer.Serialize(items);
            }
            else return;

            var envelope = new ClipboardEnvelope(typeTag, payload);
            System.Windows.Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(envelope));
        }
        catch { /* clipboard 실패 시 silent */ }
    }

    private void PasteGridSelection(System.Windows.Controls.DataGrid grid)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var raw = System.Windows.Clipboard.GetText();
            var env = System.Text.Json.JsonSerializer.Deserialize<ClipboardEnvelope>(raw);
            if (env == null) return;

            var fb = Step2Section.GlobalFBTypeCombo?.SelectedItem as string ?? "";
            var sysType = _currentDeviceTemplateFile;

            // SignalPatternRow → IW/QW/MW 그리드끼리 호환.
            if (env.Type == "SignalPatternRow"
                && (grid == Step2Section.IwSignalGrid || grid == Step2Section.QwSignalGrid || grid == Step2Section.MwSignalGrid))
            {
                var sec = AllSections().FirstOrDefault(s => s.Grid == grid);
                if (sec == null) return;
                var items = System.Text.Json.JsonSerializer.Deserialize<List<SignalRowClipboardItem>>(env.Json) ?? new();
                foreach (var it in items)
                {
                    sec.Rows.Add(HookAutoSave(new SignalPatternRow
                    {
                        ApiName          = it.ApiName,
                        Pattern          = it.Pattern,
                        TargetFBType     = fb,
                        TargetFBPort     = it.TargetFBPort,
                        SkipAddressAlloc = it.SkipAddressAlloc,
                        IsSpare          = it.IsSpare,
                        UserDataType     = it.UserDataType,
                        PreFbCondition   = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(it.PreFbCondition),
                    }));
                }
                PersistCurrentPreset();
            }
            // AuxPortRow → AUX 그리드만.
            else if (env.Type == "AuxPortRow" && grid == Step2Section.AuxPortGrid)
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<AuxPortClipboardItem>>(env.Json) ?? new();
                var apiOpts = BuildAuxApiOptions(sysType);
                foreach (var it in items)
                {
                    _auxPortRows.Add(HookAutoSave(new AuxPortRow
                    {
                        ApiName      = it.ApiName,
                        TargetFBType = fb,
                        TargetFBPort = it.TargetFBPort,
                        Kind         = string.IsNullOrEmpty(it.Kind) ? "DirectFB" : it.Kind,
                        AuxKind      = string.IsNullOrEmpty(it.AuxKind) ? "AutoAux" : it.AuxKind,
                        Condition    = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(it.Condition),
                        ApiOptions   = apiOpts,
                    }));
                }
                PersistCurrentPreset();
            }
            // 타입 불일치 시 silent (서로 다른 그리드 type)
        }
        catch { }
    }
}
