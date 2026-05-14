using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Adapters;
using AAStoPLC.LadderEditor.Expression;
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
    private bool _isDirty;  // 사용자가 LadderEditor 에서 한 번이라도 편집했는지.
    private bool _suppressTextSync; // 텍스트박스 자동 갱신 재진입 차단.
    private string _lastCommittedText = ""; // dirty / commit 추적용.
    private readonly System.Windows.Threading.DispatcherTimer _validateTimer;
    private System.Windows.Media.Brush? _normalBorder;

    private static readonly System.Windows.Media.Brush DirtyBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xB1, 0x34));
    private static readonly System.Windows.Media.Brush ErrorBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0x5B, 0x5B));
    private static readonly System.Windows.Media.Brush ErrorText   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0x5B, 0x5B));
    private static readonly System.Windows.Media.Brush WarnText    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xB1, 0x34));
    private static readonly System.Windows.Media.Brush OkText      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x57, 0xC0, 0x6D));

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
        StatusText.Text   = "닫기 시 LadderEditor 변경 사항이 store 에 저장됩니다.";

        EditorView.Context = _ctx;
        EditorView.Rungs   = _rungs;
        SyncTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += SyncTheme;
        Closing += (_, _) => SaveBack();
        Closed  += (_, _) => ThemeManager.ThemeChanged -= SyncTheme;

        Refresh();
        ApplyMode();

        _validateTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(300) };
        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); LiveValidate(); };

        // 편집 발생 추적 — RungEdited / EditFailed 이벤트.
        Loaded += (_, _) =>
        {
            if (EditorView.Edit is { } edit)
            {
                edit.RungEdited += (_, _) => OnEdited();
                edit.EditFailed += (_, t) =>
                {
                    ShowStatus($"⚠ {t.Action} 실패: {t.Reason}", WarnText);
                    StatusText.Text = $"⚠ 편집 실패: {t.Reason}";
                };
            }
            if (TextBoxBorder is not null) _normalBorder = TextBoxBorder.BorderBrush;
        };
    }

    private void OnEdited()
    {
        _isDirty = true;
        if (_rung is not null)
        {
            UpdateSymbolProvider(_rung.Condition);
            SyncStTextFromRung();
        }
    }

    // ── 듀얼 모드 (래더 / 텍스트) ─────────────────────────────────────────────

    private void ModeLadder_Checked(object sender, RoutedEventArgs e) => ApplyMode();
    private void ModeText_Checked(object sender, RoutedEventArgs e)   => ApplyMode();

    private void ApplyMode()
    {
        if (StTextBox is null || EditorView is null) return;
        bool textMode = ModeText?.IsChecked == true;
        EditorView.IsReadOnly = textMode;
        StTextBox.IsReadOnly  = !textMode;
        StTextBox.Visibility         = textMode ? Visibility.Visible : Visibility.Collapsed;
        if (StHighlightOverlay is not null)
            StHighlightOverlay.Visibility = textMode ? Visibility.Collapsed : Visibility.Visible;
        if (ApplyBtn is not null)        ApplyBtn.Visibility        = textMode ? Visibility.Visible : Visibility.Collapsed;
        if (FormatBtn is not null)       FormatBtn.Visibility       = textMode ? Visibility.Visible : Visibility.Collapsed;
        if (QuickInsertBar is not null)  QuickInsertBar.Visibility  = textMode ? Visibility.Visible : Visibility.Collapsed;
        if (ModeHint is not null)
            ModeHint.Text = textMode
                ? "Ctrl+Enter 적용 · ESC 원복 · Ctrl+Space 심볼 · Alt+F Format"
                : "텍스트는 자동 갱신 — 키워드/괄호 색상 표시";
        if (!textMode)
        {
            ClearStatus();
            if (ApplyBtn is not null) ApplyBtn.IsEnabled = false;
            if (TextBoxBorder is not null)
            {
                TextBoxBorder.BorderBrush = _normalBorder ?? TextBoxBorder.BorderBrush;
                TextBoxBorder.BorderThickness = new Thickness(1);
            }
            ApplyHighlight(StTextBox.Text);
        }
        UpdateStatusBar();
    }

    private void ApplyHighlight(string text)
    {
        if (StHighlight is null) return;
        StHighlight.Inlines.Clear();
        StHighlight.Inlines.AddRange(StSyntaxHighlight.ToRuns(text));
    }

    private void SyncStTextFromRung()
    {
        if (_suppressTextSync || _rung is null || StTextBox is null) return;
        _suppressTextSync = true;
        try
        {
            var text = CoilConditionModule.toSt(_rung.Condition);
            StTextBox.Text = text;
            ApplyHighlight(text);
            _lastCommittedText = text;
            ClearStatus();
            if (ApplyBtn is not null) ApplyBtn.IsEnabled = false;
            if (TextBoxBorder is not null)
            {
                TextBoxBorder.BorderBrush = _normalBorder ?? TextBoxBorder.BorderBrush;
                TextBoxBorder.BorderThickness = new Thickness(1);
            }
        }
        finally { _suppressTextSync = false; }
    }

    private void StTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextSync) return;
        if (StTextBox.IsReadOnly) return;
        bool dirty = (StTextBox.Text ?? "") != _lastCommittedText;
        if (ApplyBtn is not null) ApplyBtn.IsEnabled = dirty;
        if (TextBoxBorder is not null)
        {
            TextBoxBorder.BorderBrush = dirty ? DirtyBorder : (_normalBorder ?? TextBoxBorder.BorderBrush);
            TextBoxBorder.BorderThickness = new Thickness(dirty ? 2 : 1);
        }
        _validateTimer.Stop();
        if (dirty) _validateTimer.Start();
        else ClearStatus();
        UpdateStatusBar();
        UpdateAutoComplete();
    }

    // ── 인라인 자동완성 ───────────────────────────────────────────────────────
    private void UpdateAutoComplete()
    {
        if (AutoCompletePopup is null || AutoCompleteList is null) return;
        var (start, word) = StEditorOps.GetWordBeforeCaret(StTextBox);
        if (start < 0 || word.Length < 1) { HideAutoComplete(); return; }
        if (IsReservedKeyword(word)) { HideAutoComplete(); return; }
        var all = new List<string> { "_ON", "_OFF" };
        all.AddRange(BuildDisplayNameToApiCallId().Keys);
        var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase);
        var ranked = StEditorOps.RankAutoCompleteCandidates(distinct, word).Take(10).ToList();
        if (ranked.Count == 0 ||
            (ranked.Count == 1 && string.Equals(ranked[0], word, StringComparison.OrdinalIgnoreCase)))
        { HideAutoComplete(); return; }
        AutoCompleteList.ItemsSource = ranked;
        AutoCompleteList.SelectedIndex = 0;
        PositionAutoCompletePopup();
        AutoCompletePopup.IsOpen = true;
    }

    private void PositionAutoCompletePopup()
    {
        var rect = StTextBox.GetRectFromCharacterIndex(StTextBox.SelectionStart);
        if (rect.IsEmpty) return;
        AutoCompletePopup.HorizontalOffset = rect.Left;
        AutoCompletePopup.VerticalOffset   = rect.Bottom + 2;
    }

    private void HideAutoComplete()
    {
        if (AutoCompletePopup is not null) AutoCompletePopup.IsOpen = false;
    }

    private void AcceptAutoComplete()
    {
        if (!AutoCompletePopup.IsOpen) return;
        if (AutoCompleteList.SelectedItem is not string pick) return;
        var (start, _) = StEditorOps.GetWordBeforeCaret(StTextBox);
        if (start < 0) { HideAutoComplete(); return; }
        HideAutoComplete();
        StEditorOps.ReplaceWordBeforeCaret(StTextBox, start, pick);
    }

    private void AutoCompleteList_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AcceptAutoComplete();

    private static bool IsReservedKeyword(string w)
    {
        var u = w.ToUpperInvariant();
        return u is "AND" or "OR" or "NOT" or "TRUE" or "FALSE" or "R_TRIG" or "F_TRIG";
    }

    private void LiveValidate()
    {
        if (StTextBox is null) return;
        var text = StTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) { ClearStatus(); UpdateValidationStatus(true, 0); return; }
        if (!CoilConditionParser.TryParse(text, out _, out var err))
        {
            ShowStatus("✕ " + err, ErrorText);
            if (TextBoxBorder is not null) { TextBoxBorder.BorderBrush = ErrorBorder; TextBoxBorder.BorderThickness = new Thickness(2); }
            UpdateValidationStatus(false, 0);
            return;
        }
        var unknown = UnknownSymbols(text);
        if (unknown.Count > 0)
            ShowStatus($"⚠ 파싱 OK · 미매핑 심볼 {unknown.Count}건 (Apply 시 leaf drop): {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? " …" : "")}", WarnText);
        else
            ShowStatus("✓ 파싱 OK — Ctrl+Enter 로 래더 반영", OkText);
        if (TextBoxBorder is not null) TextBoxBorder.BorderBrush = DirtyBorder;
        UpdateValidationStatus(true, unknown.Count);
    }

    private List<string> UnknownSymbols(string text)
    {
        var unknown = new List<string>();
        if (_rung is null) return unknown;
        var known = BuildDisplayNameToApiCallId();
        foreach (var id in CoilConditionParser.ExtractIdentifiers(text))
        {
            if (string.Equals(id, "_ON", StringComparison.OrdinalIgnoreCase)
             || string.Equals(id, "_OFF", StringComparison.OrdinalIgnoreCase)) continue;
            if (!known.ContainsKey(id)) unknown.Add(id);
        }
        return unknown.Distinct().ToList();
    }

    private void ShowStatus(string text, System.Windows.Media.Brush brush)
    {
        if (StStatus is null) return;
        StStatus.Text = text;
        StStatus.Foreground = brush;
    }

    private void ClearStatus()
    {
        if (StStatus is null) return;
        StStatus.Text = "";
    }

    private void StTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool alt  = (Keyboard.Modifiers & ModifierKeys.Alt)     == ModifierKeys.Alt;

        if (AutoCompletePopup is not null && AutoCompletePopup.IsOpen)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (AutoCompleteList.Items.Count == 0) return;
                int next = e.Key == Key.Down
                    ? Math.Min(AutoCompleteList.SelectedIndex + 1, AutoCompleteList.Items.Count - 1)
                    : Math.Max(AutoCompleteList.SelectedIndex - 1, 0);
                AutoCompleteList.SelectedIndex = next;
                AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                e.Handled = true; return;
            }
            if (e.Key == Key.Tab || (e.Key == Key.Enter && !ctrl))
            { AcceptAutoComplete(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { HideAutoComplete(); e.Handled = true; return; }
        }

        if ((ctrl && e.Key == Key.Enter) || e.Key == Key.F5) { CommitText(); e.Handled = true; return; }
        if (e.Key == Key.Escape)         { SyncStTextFromRung(); e.Handled = true; return; }
        if (alt && e.Key == Key.F)       { Format(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Space)  { OpenSymbolPicker(); e.Handled = true; return; }
    }

    private void StTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateStatusBar();
        if (AutoCompletePopup is not null && AutoCompletePopup.IsOpen)
        {
            var (start, word) = StEditorOps.GetWordBeforeCaret(StTextBox);
            if (start < 0 || word.Length < 1) HideAutoComplete();
        }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e) => CommitText();
    private void FormatBtn_Click(object sender, RoutedEventArgs e) => Format();
    private void Revert_Click(object sender, RoutedEventArgs e) => SyncStTextFromRung();

    // ── Quick Insert ──────────────────────────────────────────────────────────
    private void InsertAnd_Click(object sender, RoutedEventArgs e) => StEditorOps.InsertAtCaret(StTextBox, " AND ");
    private void InsertOr_Click(object sender, RoutedEventArgs e)  => StEditorOps.InsertAtCaret(StTextBox, " OR ");
    private void InsertNot_Click(object sender, RoutedEventArgs e) => StEditorOps.InsertAtCaret(StTextBox, "NOT ");
    private void InsertParen_Click(object sender, RoutedEventArgs e) => StEditorOps.InsertParenSnippet(StTextBox, "(", ")");
    private void InsertRTrig_Click(object sender, RoutedEventArgs e) => StEditorOps.InsertParenSnippet(StTextBox, "R_TRIG(", ")");
    private void InsertFTrig_Click(object sender, RoutedEventArgs e) => StEditorOps.InsertParenSnippet(StTextBox, "F_TRIG(", ")");

    private void Format()
    {
        if (StTextBox.IsReadOnly) return;
        var formatted = StEditorOps.Format(StTextBox.Text ?? "");
        if (formatted is null)
        {
            ShowStatus("✕ Format 실패: 파싱 오류 먼저 해결 필요", ErrorText);
            return;
        }
        StTextBox.Text = formatted;
        StTextBox.SelectionStart  = formatted.Length;
        StTextBox.SelectionLength = 0;
        ShowStatus("📐 정규 표기로 변환됨", OkText);
    }

    // ── 심볼 픽커 ─────────────────────────────────────────────────────────────
    private void SymbolPicker_Click(object sender, RoutedEventArgs e) => OpenSymbolPicker();

    private void OpenSymbolPicker()
    {
        if (StTextBox.IsReadOnly) return;
        var all = new List<string> { "_ON", "_OFF" };
        all.AddRange(BuildDisplayNameToApiCallId().Keys);
        var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        SymbolList.ItemsSource = distinct;
        SymbolFilter.Text = "";
        if (distinct.Count > 0) SymbolList.SelectedIndex = 0;
        SymbolPopup.IsOpen = true;
        SymbolFilter.Focus();
    }

    private void SymbolFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var all = new List<string> { "_ON", "_OFF" };
        all.AddRange(BuildDisplayNameToApiCallId().Keys);
        var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        SymbolList.ItemsSource = StEditorOps.FilterSymbols(distinct, SymbolFilter.Text).ToList();
        if (SymbolList.Items.Count > 0) SymbolList.SelectedIndex = 0;
    }

    private void SymbolFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (SymbolList.Items.Count == 0) return;
            int next = e.Key == Key.Down
                ? Math.Min(SymbolList.SelectedIndex + 1, SymbolList.Items.Count - 1)
                : Math.Max(SymbolList.SelectedIndex - 1, 0);
            SymbolList.SelectedIndex = next;
            SymbolList.ScrollIntoView(SymbolList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { CommitSymbolPick(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SymbolPopup.IsOpen = false; StTextBox.Focus(); e.Handled = true; }
    }

    private void SymbolList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitSymbolPick();
    private void SymbolList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitSymbolPick(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SymbolPopup.IsOpen = false; StTextBox.Focus(); e.Handled = true; }
    }

    private void CommitSymbolPick()
    {
        if (SymbolList.SelectedItem is not string sym) return;
        SymbolPopup.IsOpen = false;
        StTextBox.Focus();
        StEditorOps.InsertAtCaret(StTextBox, sym);
    }

    // ── 상태바 ────────────────────────────────────────────────────────────────
    private void UpdateStatusBar()
    {
        if (StatusModeLabel is null) return;
        bool textMode = ModeText?.IsChecked == true;
        StatusModeLabel.Text = textMode ? "✏ 텍스트 모드" : "🪜 래더 모드";
        if (textMode && StTextBox is not null)
        {
            var (ln, col) = StEditorOps.GetCursorLineCol(StTextBox);
            StatusCursorLabel.Text = $"Ln {ln}, Col {col}";
        }
        else StatusCursorLabel.Text = "";
        int leaves = StEditorOps.LeafCount(StTextBox?.Text ?? "");
        StatusLeafLabel.Text = $"leaf {leaves}개";
    }

    private void UpdateValidationStatus(bool ok, int warnCount)
    {
        if (StatusValidationLabel is null) return;
        if (!ok) { StatusValidationLabel.Text = "✕ 파싱 오류"; StatusValidationLabel.Foreground = ErrorText; }
        else if (warnCount > 0) { StatusValidationLabel.Text = $"⚠ 경고 {warnCount}"; StatusValidationLabel.Foreground = WarnText; }
        else { StatusValidationLabel.Text = "✓ OK"; StatusValidationLabel.Foreground = OkText; }
    }

    private void CommitText()
    {
        if (StTextBox is null || StTextBox.IsReadOnly) return;
        if (_rung is null) return;
        var text = StTextBox.Text ?? "";

        if (!CoilConditionParser.TryParse(text, out var cond, out var err) || cond is null)
        {
            ShowStatus("✕ " + err, ErrorText);
            if (TextBoxBorder is not null) { TextBoxBorder.BorderBrush = ErrorBorder; TextBoxBorder.BorderThickness = new Thickness(2); }
            int pos = StEditorOps.ExtractErrorPosition(err);
            if (pos >= 0 && pos <= text.Length)
            {
                StTextBox.SelectionStart = pos;
                StTextBox.SelectionLength = 0;
                StTextBox.Focus();
            }
            UpdateValidationStatus(false, 0);
            return;
        }

        _suppressTextSync = true;
        try
        {
            _rung.Condition = CoilConditionModule.simplify(cond);
            _isDirty = true;
            _lastCommittedText = text;
            UpdateSymbolProvider(_rung.Condition);
            ShowStatus("✓ 적용됨", OkText);
            if (ApplyBtn is not null) ApplyBtn.IsEnabled = false;
            if (TextBoxBorder is not null)
            {
                TextBoxBorder.BorderBrush = _normalBorder ?? TextBoxBorder.BorderBrush;
                TextBoxBorder.BorderThickness = new Thickness(1);
            }
        }
        finally { _suppressTextSync = false; }
        UpdateStatusBar();
    }

    /// <summary>편집된 CoilCondition 을 CallConditionTreeDto 로 재귀 매핑 후 ReplaceCallConditionTree 호출.
    /// 중첩 And/Or 구조 보존. 알 수 없는 leaf 는 무시.
    /// 안전장치: _isDirty=false 또는 매핑된 leaf 0 개면 skip — 기존 store 보존.
    /// </summary>
    private void SaveBack()
    {
        if (!_isDirty || _rung is null) return;
        var nameToId = BuildDisplayNameToApiCallId();

        var simp = CoilConditionModule.simplify(_rung.Condition);
        var dto = ToDto(simp, nameToId);
        if (dto is null) return;
        // 매핑된 leaf 한 개도 없으면 skip — 빈 트리 저장 방지.
        if (CountLeaves(dto) == 0) return;

        _host.TryAction(() => _store.ReplaceCallConditionTree(_callId, _condType, dto));
    }

    /// <summary>CoilCondition → CallConditionTreeDto 재귀 변환.
    /// per-leaf ContactKind, group IsInverted 모두 보존.
    /// ApiCall 매핑 안 되는 leaf (_ON/_OFF 등 raw 심볼) 는 RawSymbols 에 보존.</summary>
    private static CallConditionTreeDto? ToDto(CoilCondition c, Dictionary<string, Guid> nameToId)
    {
        // Leaf: 단일 leaf 를 isOR=false 그룹으로 감싸 단일화 처리.
        // Not(Raw "") 인버터 placeholder 는 leaf 로 처리.
        // Not(inner) 일반 NOT 은 IsInverted=true 그룹.
        bool isOr = false;
        bool isInverted = false;
        IEnumerable<CoilCondition> ops;

        switch (c)
        {
            case CoilCondition.Or o: isOr = true;  ops = o.operands; break;
            case CoilCondition.And a: isOr = false; ops = a.operands; break;
            case CoilCondition.Not n:
                // Not(Raw "") = inverter placeholder leaf.
                if (n.operand is CoilCondition.Raw r0 && string.IsNullOrEmpty(r0.expr))
                {
                    ops = new[] { c };
                    break;
                }
                // 일반 Not — 그룹에 IsInverted=true 부여, 내부를 single-op AND 로 펼침.
                isInverted = true;
                ops = new[] { n.operand };
                break;
            case CoilCondition.Var or CoilCondition.NegVar
                 or CoilCondition.Rising or CoilCondition.Falling
                 or CoilCondition.Raw:
                ops = new[] { c };
                break;
            default: return null;  // AlwaysTrue/False — drop
        }

        var apiCallIds = new List<Guid>();
        var apiCallKinds = new List<ContactKind>();
        var rawSymbols = new List<string>();
        var rawSymbolKinds = new List<ContactKind>();
        var children = new List<CallConditionTreeDto>();

        foreach (var op in ops)
        {
            if (TryClassifyLeaf(op, nameToId, out var leafId, out var leafKind))
            {
                apiCallIds.Add(leafId);
                apiCallKinds.Add(leafKind);
            }
            else if (CoilAst.IsLeaf(op) || op is CoilCondition.Raw)
            {
                // ApiCall 매핑 안 되는 leaf — RawSymbols 에 보존 (이전에는 drop 됐던 _ON/_OFF 등).
                if (TryExtractRawLeaf(op, out var rawName, out var rawKind)
                    && !string.IsNullOrEmpty(rawName))
                {
                    rawSymbols.Add(rawName);
                    rawSymbolKinds.Add(rawKind);
                }
            }
            else
            {
                var child = ToDto(op, nameToId);
                if (child is not null) children.Add(child);
            }
        }
        return new CallConditionTreeDto(isOr, isInverted,
                                         apiCallIds, apiCallKinds,
                                         rawSymbols, rawSymbolKinds,
                                         children);
    }

    /// <summary>매핑 안 된 leaf → (심볼 이름, ContactKind). 이름이 비었거나 분류 불가 시 false.</summary>
    private static bool TryExtractRawLeaf(CoilCondition c, out string name, out ContactKind kind)
    {
        switch (c)
        {
            case CoilCondition.Var v:      name = v.name;  kind = ContactKind.NoContact;     return true;
            case CoilCondition.NegVar nv:  name = nv.name; kind = ContactKind.NcContact;     return true;
            case CoilCondition.Rising rs:  name = rs.name; kind = ContactKind.RisingPulse;   return true;
            case CoilCondition.Falling fl: name = fl.name; kind = ContactKind.FallingPulse;  return true;
            case CoilCondition.Raw raw:    name = raw.expr; kind = ContactKind.NoContact;    return true;
            default:                        name = "";      kind = ContactKind.NoContact;    return false;
        }
    }

    /// <summary>단일 노드가 leaf 인지 분류 + ContactKind 결정.
    /// Not(Raw "") = Inverter placeholder.</summary>
    private static bool TryClassifyLeaf(
        CoilCondition c, Dictionary<string, Guid> nameToId,
        out Guid id, out ContactKind kind)
    {
        switch (c)
        {
            case CoilCondition.Var v:
                kind = ContactKind.NoContact;
                return nameToId.TryGetValue(v.name, out id);
            case CoilCondition.NegVar nv:
                kind = ContactKind.NcContact;
                return nameToId.TryGetValue(nv.name, out id);
            case CoilCondition.Rising rs:
                kind = ContactKind.RisingPulse;
                return nameToId.TryGetValue(rs.name, out id);
            case CoilCondition.Falling fl:
                kind = ContactKind.FallingPulse;
                return nameToId.TryGetValue(fl.name, out id);
            case CoilCondition.Not n
                when n.operand is CoilCondition.Raw r && string.IsNullOrEmpty(r.expr):
                kind = ContactKind.Inverter;
                id = Guid.Empty;
                return true;
            case CoilCondition.Raw raw when nameToId.TryGetValue(raw.expr, out id):
                kind = ContactKind.NoContact;
                return true;
            default:
                kind = ContactKind.NoContact;
                id = Guid.Empty;
                return false;
        }
    }

    private static int CountLeaves(CallConditionTreeDto d) =>
        d.ApiCallIds.Count + d.RawSymbols.Count + d.Children.Sum(CountLeaves);

    /// <summary>buildPreview 와 동일한 `{System.Name}.{ApiDef.Name}` 포맷 → ApiCall.Id lookup.</summary>
    private Dictionary<string, Guid> BuildDisplayNameToApiCallId()
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var ac in _store.ApiCalls.Values)
        {
            if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId)) continue;
            if (!_store.ApiDefs.TryGetValue(ac.ApiDefId.Value, out var def)) continue;
            if (!_store.Systems.TryGetValue(def.ParentId, out var sys)) continue;
            var key = $"{sys.Name}.{def.Name}";
            if (!map.ContainsKey(key)) map[key] = ac.Id;
        }
        return map;
    }

    /// <summary>이 Call 에 등록된 CallCondition 의 실제 ApiCall 심볼만 + 사용 여부 — Refresh 마다 갱신.</summary>
    private void UpdateSymbolProvider(CoilCondition cond)
    {
        // 현재 rung 에서 사용 중인 심볼 set (used).
        var usedSet = new HashSet<string>(
            CoilAst.Leaves(cond).Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);
        // 이 Call 에 실제로 등록된 CallCondition 들의 ApiCall display name 만 추출.
        var registered = new List<string>();
        if (_host.TryRef(() => _store.GetCallConditionsForPanel(_callId), out var conds))
        {
            foreach (var c in conds)
                CollectApiCallNames(c, registered);
        }
        // 현재 rung 에 있는데 conditions 에 안 보이는 leaf 도 포함 (drag 로 추가된 임시 심볼 등).
        foreach (var u in usedSet)
            if (!registered.Contains(u, StringComparer.OrdinalIgnoreCase))
                registered.Add(u);
        _ctx.SymbolProvider = new AnnotatedSymbolProvider(registered, usedSet);
    }

    private static void CollectApiCallNames(Ds2.Editor.CallConditionPanelItem c, List<string> acc)
    {
        foreach (var item in c.Items)
            if (!acc.Contains(item.ApiDefDisplayName, StringComparer.OrdinalIgnoreCase))
                acc.Add(item.ApiDefDisplayName);
        foreach (var child in c.Children) CollectApiCallNames(child, acc);
    }

    /// <summary>심볼 + 사용 상태 라벨. SymbolPopupEditor 가 \u2003 이후 상태 텍스트는 잘라냄.</summary>
    private sealed class AnnotatedSymbolProvider : ISymbolProvider
    {
        private static readonly string[] BuiltIn = { "_ON", "_OFF" };
        private const string SEP = "\u2003";  // em-space — 표시용 구분자
        private readonly List<string> _displays; // 정렬: 미사용 먼저
        private readonly HashSet<string> _known;

        public AnnotatedSymbolProvider(IEnumerable<string> all, HashSet<string> usedSet)
        {
            var allList = BuiltIn
                .Concat(all)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _known = new(allList, StringComparer.OrdinalIgnoreCase);

            // 미사용을 먼저 (사용자가 미배치 심볼을 우선 선택하도록 유도).
            string Decorate(string s) => usedSet.Contains(s)
                ? $"{s}{SEP}✓ 사용중"
                : $"{s}{SEP}○ 미사용";
            _displays = allList
                .OrderBy(s => usedSet.Contains(s) ? 1 : 0)
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(Decorate)
                .ToList();
        }

        public IEnumerable<string> Search(string prefixOrSubstring) =>
            string.IsNullOrEmpty(prefixOrSubstring)
                ? _displays
                : _displays.Where(s => s.IndexOf(prefixOrSubstring, StringComparison.OrdinalIgnoreCase) >= 0);

        public bool IsKnown(string symbol) => _known.Contains(symbol);
    }


    private void SyncTheme(AppTheme theme) =>
        EditorView.Theme = theme == AppTheme.Dark ? new DefaultDarkTheme() : new DefaultLightTheme();

    /// <summary>현재 store 상태 → CoilCondition → 단일 CoilRung 으로 표시.</summary>
    private void Refresh()
    {
        if (!_host.TryRef(() => _store.Calls[_callId], out var call)) return;
        var condOpt = ConditionExprBuilder.buildPreview(_store, call, _condType);
        var cond = FSharpOption<CoilCondition>.get_IsSome(condOpt)
            ? condOpt.Value : CoilCondition.AlwaysTrue;
        const string coilName = "OUT";

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
        UpdateSymbolProvider(cond);
        SyncStTextFromRung();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_rung is null) { StatusText.Text = "rung 없음"; return; }
        var nameToId = BuildDisplayNameToApiCallId();
        var leaves = CoilAst.Leaves(_rung.Condition).Select(l => l.Name).ToList();
        var matched = leaves.Where(n => nameToId.ContainsKey(n)).ToList();
        var unmatched = leaves.Where(n => !nameToId.ContainsKey(n)).ToList();

        _isDirty = true;
        SaveBack();
        _isDirty = false;

        var msg = $"✓ 저장 — leaf {leaves.Count}, 매핑 {matched.Count}, 미매핑 {unmatched.Count}";
        if (unmatched.Count > 0) msg += $" [미매핑: {string.Join(", ", unmatched.Take(3))}]";
        StatusText.Text = msg;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
