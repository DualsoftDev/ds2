using System;
using System.Collections.ObjectModel;
using System.Windows;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Models;
using AAStoPLC.LadderEditor.Rendering;
using AAStoPLC.Pipeline;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Dialogs;

/// <summary>
/// Call 조건 편집 다이얼로그 — 트리 입력 UI 제거 후 LadderEditor 단독 화면.
/// 현재는 표시 + 인터랙션 만 — store 로의 역반영(save-back) 은 후속 작업.
/// 변경 진입점은 그대로 Call drag-drop / 별도 ApiCall 추가 다이얼로그 사용.
/// </summary>
public partial class ConditionEditDialog : Window
{
    private readonly DsStore _store;
    private readonly MainViewModel.PropertyPanelHost _host;
    private readonly Guid _callId;
    private readonly CallConditionType _condType;
    private readonly ObservableCollection<RungViewModel> _rungs = new();
    private readonly EditorContext _ctx = new() { GridCols = 14 };
    private CoilRungViewModel? _rung;

    public ConditionEditDialog(
        DsStore store,
        MainViewModel.PropertyPanelHost host,
        Guid callId,
        CallConditionType condType)
    {
        InitializeComponent();
        _store = store;
        _host = host;
        _callId = callId;
        _condType = condType;
        SectionTitle.Text = $"{condType} 조건 편집";
        StatusText.Text   = "트리 입력 제거 — LadderEditor 단독 (편집 결과 store 역반영은 후속).";

        EditorView.Context = _ctx;
        EditorView.Rungs   = _rungs;
        SyncTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += SyncTheme;
        Closed += (_, _) => ThemeManager.ThemeChanged -= SyncTheme;

        Refresh();
    }

    private void SyncTheme(AppTheme theme) =>
        EditorView.Theme = theme == AppTheme.Dark ? new DefaultDarkTheme() : new DefaultLightTheme();

    /// <summary>현재 store 상태 → CoilCondition → 단일 CoilRung 으로 표시.</summary>
    private void Refresh()
    {
        if (!_host.TryRef(() => _store.Calls[_callId], out var call)) return;
        var (condOpt, outputRising) = ConditionExprBuilder.buildPreview(_store, call, _condType);
        var cond = FSharpOption<CoilCondition>.get_IsSome(condOpt)
            ? condOpt.Value : CoilCondition.AlwaysTrue;
        string coilName = outputRising ? "R:OUT" : "OUT";

        if (_rung is null)
        {
            _rung = new CoilRungViewModel(cond, coilName);
            _rungs.Clear();
            _rungs.Add(_rung);
        }
        else
        {
            _rung.Condition = cond;
            _rung.CoilBit   = coilName;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
