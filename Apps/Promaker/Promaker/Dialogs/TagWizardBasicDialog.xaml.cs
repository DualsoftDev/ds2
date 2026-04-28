using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Dialogs;

/// <summary>
/// 기본 모드 TAG Wizard — 심볼 매크로와 Flow 선두 주소만으로 ApiCall.InTag/OutTag 일괄 생성.
/// SystemType · FB · AUX 등의 복잡도 없음. PLC 생성 연계는 고급 모드 사용.
/// </summary>
public partial class TagWizardBasicDialog : Window
{
    private readonly DsStore _store;
    private readonly ObservableCollection<FlowBaseBasicRow> _flowRows = new();
    private readonly ObservableCollection<PreviewBasicRow> _previewRows = new();

    public TagWizardBasicDialog(DsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));

        FlowBaseGrid.ItemsSource = _flowRows;
        PreviewGrid.ItemsSource   = _previewRows;

        LoadFlows();
        RefreshPreview();

        // 매크로 TextBox 입력 시 미리보기 디바운스 갱신
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); RefreshPreview(); };
        IwMacroBox.TextChanged += (_, _) => QueuePreviewRefresh();
        QwMacroBox.TextChanged += (_, _) => QueuePreviewRefresh();
        MwMacroBox.TextChanged += (_, _) => QueuePreviewRefresh();
    }

    // ── Flow 로드 ────────────────────────────────────────────────────────────

    private void LoadFlows()
    {
        _flowRows.Clear();
        var projects = Queries.allProjects(_store);
        if (projects.IsEmpty) return;

        var activeFlows = Queries.activeSystemsOf(projects.Head.Id, _store)
            .SelectMany(sys => Queries.flowsOf(sys.Id, _store))
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int i = 0;
        foreach (var name in activeFlows)
        {
            var baseAddr = (i * 1000).ToString();
            _flowRows.Add(new FlowBaseBasicRow
            {
                FlowName = name,
                IW_Base  = baseAddr,
                QW_Base  = baseAddr,
                MW_Base  = baseAddr,
            });
            i++;
        }

        StatusText.Text = activeFlows.Count > 0
            ? $"{activeFlows.Count}개 Flow 로드됨"
            : "Active 시스템 Flow 가 없습니다.";
    }

    private void ReloadFlows_Click(object sender, RoutedEventArgs e)
    {
        LoadFlows();
        RefreshPreview();
    }

    // ── 미리보기 ────────────────────────────────────────────────────────────

    private readonly System.Windows.Threading.DispatcherTimer _previewDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void QueuePreviewRefresh()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        _previewRows.Clear();

        var iwMacro = IwMacroBox.Text ?? "";
        var qwMacro = QwMacroBox.Text ?? "";
        var mwMacro = MwMacroBox.Text ?? "";

        // Flow → base offsets
        var flowMap = _flowRows.ToDictionary(r => r.FlowName, r => r, StringComparer.OrdinalIgnoreCase);
        var flowCounter = new Dictionary<string, (int iw, int qw, int mw)>(StringComparer.OrdinalIgnoreCase);

        foreach (var apiCall in _store.ApiCalls.Values)
        {
            // Resolve Flow/Device/Api names from ApiCall
            var (flowName, deviceAlias, apiName) = ResolveContext(apiCall);
            if (string.IsNullOrWhiteSpace(flowName)) continue;

            var inTag  = string.IsNullOrEmpty(iwMacro) ? "" : Expand(iwMacro, flowName, deviceAlias, apiName);
            var outTag = string.IsNullOrEmpty(qwMacro) ? "" : Expand(qwMacro, flowName, deviceAlias, apiName);

            // 주소 할당 — Flow 별 카운터 증가 (bit 단위)
            string inAddr = "", outAddr = "";
            if (!flowCounter.TryGetValue(flowName, out var cnt)) cnt = (0, 0, 0);
            if (flowMap.TryGetValue(flowName, out var fb))
            {
                if (!string.IsNullOrEmpty(inTag) && int.TryParse(fb.IW_Base, out var iwBase))
                {
                    inAddr = $"%IW{iwBase}.{cnt.iw / 16}.{cnt.iw % 16}";
                    cnt.iw++;
                }
                if (!string.IsNullOrEmpty(outTag) && int.TryParse(fb.QW_Base, out var qwBase))
                {
                    outAddr = $"%QW{qwBase}.{cnt.qw / 16}.{cnt.qw % 16}";
                    cnt.qw++;
                }
            }
            flowCounter[flowName] = cnt;

            _previewRows.Add(new PreviewBasicRow
            {
                CallId       = ResolveParentCallId(apiCall),
                ApiCallId    = apiCall.Id,
                Flow         = flowName,
                Device       = deviceAlias,
                Api          = apiName,
                InTag        = inTag,
                InAddress    = inAddr,
                OutTag       = outTag,
                OutAddress   = outAddr,
            });
        }

        StatusText.Text = $"미리보기 {_previewRows.Count}개 생성됨";
    }

    private (string flow, string device, string api) ResolveContext(ApiCall apiCall)
    {
        // Flow: OriginFlowId 우선, 없으면 parent Call → Work → Flow 체인
        string flow = "";
        if (apiCall.OriginFlowId != null && Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(apiCall.OriginFlowId))
        {
            var f = Queries.getFlow(apiCall.OriginFlowId.Value, _store);
            if (f != null && Microsoft.FSharp.Core.FSharpOption<Flow>.get_IsSome(f)) flow = f.Value.Name;
        }

        var parentCall = _store.Calls.Values.FirstOrDefault(c =>
            c.ApiCalls != null && c.ApiCalls.Any(ac => ac.Id == apiCall.Id));
        if (string.IsNullOrEmpty(flow) && parentCall != null)
        {
            var workOpt = Queries.getWork(parentCall.ParentId, _store);
            if (workOpt != null && Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(workOpt))
            {
                var flowOpt = Queries.getFlow(workOpt.Value.ParentId, _store);
                if (flowOpt != null && Microsoft.FSharp.Core.FSharpOption<Flow>.get_IsSome(flowOpt))
                    flow = flowOpt.Value.Name;
            }
        }

        // Device: parent Call.DevicesAlias
        string device = parentCall?.DevicesAlias ?? "";

        // Api: ApiDef 이름
        string api = "";
        if (apiCall.ApiDefId != null && Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId))
        {
            var adOpt = Queries.getApiDef(apiCall.ApiDefId.Value, _store);
            if (adOpt != null && Microsoft.FSharp.Core.FSharpOption<ApiDef>.get_IsSome(adOpt))
                api = adOpt.Value.Name;
        }

        return (flow, device, api);
    }

    private Guid ResolveParentCallId(ApiCall apiCall) =>
        _store.Calls.Values.FirstOrDefault(c =>
            c.ApiCalls != null && c.ApiCalls.Any(ac => ac.Id == apiCall.Id))?.Id ?? Guid.Empty;

    private static string Expand(string macro, string flow, string device, string api) =>
        (macro ?? "")
            .Replace("$(F)", flow  ?? "")
            .Replace("$(D)", device?? "")
            .Replace("$(A)", api   ?? "");

    // ── 적용 ────────────────────────────────────────────────────────────────

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_previewRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "적용할 미리보기 행이 없습니다.",
                "TAG Wizard (기본)",
                MessageBoxButton.OK,
                "ℹ");
            return;
        }

        var confirm = DialogHelpers.ShowThemedMessageBox(
            $"{_previewRows.Count}개 ApiCall 의 InTag/OutTag 에 덮어쓰기를 실행합니다.\n" +
            "기존 수동 설정값은 모두 새 매크로 결과로 교체됩니다.\n\n계속하시겠습니까?",
            "TAG Wizard (기본) — 적용 확인",
            MessageBoxButton.YesNo,
            "⚠");
        if (confirm != MessageBoxResult.Yes) return;

        int ok = 0, fail = 0;
        foreach (var r in _previewRows)
        {
            if (r.CallId == Guid.Empty || r.ApiCallId == Guid.Empty) { fail++; continue; }
            try
            {
                _store.UpdateApiCallIoTags(
                    r.CallId, r.ApiCallId,
                    new IOTag(r.OutTag ?? "", r.OutAddress ?? "", ""),
                    new IOTag(r.InTag  ?? "", r.InAddress  ?? "", ""));
                ok++;
            }
            catch { fail++; }
        }

        DialogHelpers.ShowThemedMessageBox(
            $"✓ {ok}개 ApiCall 적용 완료" + (fail > 0 ? $" / {fail}개 실패" : ""),
            "TAG Wizard (기본)",
            MessageBoxButton.OK,
            "✓");
        DialogResult = true;
        Close();
    }
}

public class FlowBaseBasicRow : INotifyPropertyChanged
{
    private string _flow = "", _iw = "", _qw = "", _mw = "";
    public string FlowName { get => _flow; set { _flow = value ?? ""; Notify(nameof(FlowName)); } }
    public string IW_Base  { get => _iw;   set { _iw   = value ?? ""; Notify(nameof(IW_Base));  } }
    public string QW_Base  { get => _qw;   set { _qw   = value ?? ""; Notify(nameof(QW_Base));  } }
    public string MW_Base  { get => _mw;   set { _mw   = value ?? ""; Notify(nameof(MW_Base));  } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class PreviewBasicRow
{
    public Guid   CallId     { get; init; }
    public Guid   ApiCallId  { get; init; }
    public string Flow       { get; init; } = "";
    public string Device     { get; init; } = "";
    public string Api        { get; init; } = "";
    public string InTag      { get; init; } = "";
    public string InAddress  { get; init; } = "";
    public string OutTag     { get; init; } = "";
    public string OutAddress { get; init; } = "";
}
