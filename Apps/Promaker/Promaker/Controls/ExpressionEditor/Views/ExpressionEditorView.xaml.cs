using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Expression;
using AAStoPLC.LadderEditor.Interaction;
using AAStoPLC.LadderEditor.Models;
using AAStoPLC.LadderEditor.Rendering;
using Promaker.Controls.ExpressionEditor.ViewModels;
using Promaker.Presentation;

namespace Promaker.Controls.ExpressionEditor.Views;

/// <summary>
/// 식 편집기 — 래더 / 텍스트(ST) 듀얼 + 강화 사용성 (Quick Insert / Format / 심볼 픽커 / 상태바).
/// </summary>
public partial class ExpressionEditorView : UserControl
{
    private readonly ObservableCollection<RungViewModel> _rungs = new();
    private readonly EditorContext _ldCtx = new() { GridCols = 14 };
    private CoilRungViewModel? _previewRung;
    private bool _suppressEditFeedback;
    private bool _suppressTextSync;

    private readonly DispatcherTimer _validateTimer;
    private string _lastCommittedText = "";

    private static readonly Brush DirtyBorder = Frozen(0xF2, 0xB1, 0x34);
    private static readonly Brush ErrorBorder = Frozen(0xE1, 0x5B, 0x5B);
    private static readonly Brush ErrorText   = Frozen(0xE1, 0x5B, 0x5B);
    private static readonly Brush WarnText    = Frozen(0xF2, 0xB1, 0x34);
    private static readonly Brush OkText      = Frozen(0x57, 0xC0, 0x6D);
    private Brush? _normalBorder;

    public ExpressionEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        _validateTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(300) };
        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); LiveValidate(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LdView.Context = _ldCtx;
        LdView.Rungs   = _rungs;
        LdView.Theme   = ThemeManager.CurrentTheme == AppTheme.Dark
            ? new DefaultDarkTheme() : new DefaultLightTheme();
        ThemeManager.ThemeChanged += SyncTheme;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= SyncTheme;
        if (LdView.Edit is { } edit)
        {
            edit.RungEdited += OnLadderEdited;
            edit.EditFailed += (_, t) => ShowStatus($"⚠ {t.Action} 실패: {t.Reason}", WarnText);
        }
        _normalBorder = TextBoxBorder.BorderBrush;
        ApplyMode();
        RefreshLd();
        UpdateStatusBar();
    }

    private void SyncTheme(AppTheme theme) =>
        LdView.Theme = theme == AppTheme.Dark ? new DefaultDarkTheme() : new DefaultLightTheme();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ExpressionEditorViewModel oldVm)
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is ExpressionEditorViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
            _ldCtx.SymbolProvider = new EditorSymbolProvider(newVm.SymbolProvider);
            RefreshLd();
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressEditFeedback) return;
        if (e.PropertyName is nameof(ExpressionEditorViewModel.LdLayoutForPreview)
            or nameof(ExpressionEditorViewModel.StPreview))
        {
            RefreshLd();
            SyncStTextFromVm();
        }
    }

    private void RefreshLd()
    {
        if (DataContext is not ExpressionEditorViewModel vm) return;
        var cond = CoilConditionConverter.ToCoilCondition(vm.Root);
        if (cond is null || cond.IsAlwaysTrue) cond = CoilCondition.NewVar("");
        if (_previewRung is null)
        {
            _previewRung = new CoilRungViewModel(cond, "OUT");
            _rungs.Clear();
            _rungs.Add(_previewRung);
        }
        else _previewRung.Condition = cond;
        SyncStTextFromVm();
    }

    private void SyncStTextFromVm()
    {
        if (_suppressTextSync) return;
        if (DataContext is not ExpressionEditorViewModel vm) return;
        _suppressTextSync = true;
        try
        {
            var text = vm.StPreview ?? "";
            StTextBox.Text = text;
            ApplyHighlight(text);
            _lastCommittedText = text;
            ClearStatus();
            ApplyBtn.IsEnabled = false;
            UpdateStatusBar();
        }
        finally { _suppressTextSync = false; }
    }

    private void ApplyHighlight(string text)
    {
        if (StHighlight is null) return;
        StHighlight.Inlines.Clear();
        StHighlight.Inlines.AddRange(StSyntaxHighlight.ToRuns(text));
    }

    private void OnLadderEdited(object? sender, RungEditedEventArgs args)
    {
        if (DataContext is not ExpressionEditorViewModel vm) return;
        if (_previewRung is null) return;
        var node = CoilConditionConverter.ToExprNode(_previewRung.Condition);
        if (node is null) return;
        _suppressEditFeedback = true;
        try { vm.Root = node; }
        finally { _suppressEditFeedback = false; }
        SyncStTextFromVm();
    }

    // ── 모드 토글 ──────────────────────────────────────────────────────────────

    private void ModeLadder_Checked(object sender, RoutedEventArgs e) => ApplyMode();
    private void ModeText_Checked(object sender, RoutedEventArgs e)   => ApplyMode();

    private void ApplyMode()
    {
        if (StTextBox is null || LdView is null) return;
        bool textMode = ModeText?.IsChecked == true;
        LdView.IsReadOnly = textMode;
        StTextBox.IsReadOnly          = !textMode;
        StTextBox.Visibility          = textMode ? Visibility.Visible : Visibility.Collapsed;
        StHighlightOverlay.Visibility = textMode ? Visibility.Collapsed : Visibility.Visible;
        ApplyBtn.Visibility           = textMode ? Visibility.Visible : Visibility.Collapsed;
        FormatBtn.Visibility          = textMode ? Visibility.Visible : Visibility.Collapsed;
        QuickInsertBar.Visibility     = textMode ? Visibility.Visible : Visibility.Collapsed;
        if (ModeHint is not null)
            ModeHint.Text = textMode
                ? "Ctrl+Enter 적용 · ESC 원복 · Ctrl+Space 심볼 · Alt+F Format"
                : "텍스트는 자동 갱신 — 키워드/괄호 색상 표시";
        if (!textMode)
        {
            ClearStatus();
            ApplyBtn.IsEnabled = false;
            TextBoxBorder.BorderBrush = _normalBorder;
            TextBoxBorder.BorderThickness = new Thickness(1);
            ApplyHighlight(StTextBox.Text);
        }
        UpdateStatusBar();
    }

    // ── 텍스트 변경 / 실시간 검증 ──────────────────────────────────────────────

    private void StTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextSync) return;
        if (StTextBox.IsReadOnly) return;
        bool dirty = (StTextBox.Text ?? "") != _lastCommittedText;
        ApplyBtn.IsEnabled = dirty;
        TextBoxBorder.BorderBrush = dirty ? DirtyBorder : _normalBorder;
        TextBoxBorder.BorderThickness = new Thickness(dirty ? 2 : 1);
        _validateTimer.Stop();
        if (dirty) _validateTimer.Start();
        else ClearStatus();
        UpdateStatusBar();
        UpdateAutoComplete();
    }

    // ── 인라인 자동완성 ───────────────────────────────────────────────────────

    private void UpdateAutoComplete()
    {
        if (DataContext is not ExpressionEditorViewModel vm) { HideAutoComplete(); return; }
        var (start, word) = StEditorOps.GetWordBeforeCaret(StTextBox);
        if (start < 0 || word.Length < 1) { HideAutoComplete(); return; }
        // 키워드는 자동완성 후보에서 제외 (자동 정규화 혼동 방지).
        if (IsReservedKeyword(word)) { HideAutoComplete(); return; }
        var all = new System.Collections.Generic.List<string> { "_ON", "_OFF" };
        try { all.AddRange(vm.SymbolProvider.GetCandidates().Select(c => c.Display)); } catch { }
        var distinct = all.Distinct(System.StringComparer.OrdinalIgnoreCase);
        var ranked = StEditorOps.RankAutoCompleteCandidates(distinct, word).Take(10).ToList();
        if (ranked.Count == 0 ||
            (ranked.Count == 1 && string.Equals(ranked[0], word, System.StringComparison.OrdinalIgnoreCase)))
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
        AutoCompletePopup.VerticalOffset = rect.Bottom + 2;
    }

    private void HideAutoComplete()
    {
        if (AutoCompletePopup is null) return;
        AutoCompletePopup.IsOpen = false;
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
    {
        AcceptAutoComplete();
    }

    private static bool IsReservedKeyword(string w)
    {
        var u = w.ToUpperInvariant();
        return u is "AND" or "OR" or "NOT" or "TRUE" or "FALSE" or "R_TRIG" or "F_TRIG";
    }

    private void StTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateStatusBar();
        // caret 이 식별자 영역을 벗어나면 popup 닫음.
        if (AutoCompletePopup is not null && AutoCompletePopup.IsOpen)
        {
            var (start, word) = StEditorOps.GetWordBeforeCaret(StTextBox);
            if (start < 0 || word.Length < 1) HideAutoComplete();
        }
    }

    private void LiveValidate()
    {
        if (StTextBox is null) return;
        var text = StTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) { ClearStatus(); UpdateValidationStatus(true, 0); return; }
        if (!CoilConditionParser.TryParse(text, out _, out var err))
        {
            ShowStatus("✕ " + err, ErrorText);
            TextBoxBorder.BorderBrush = ErrorBorder;
            TextBoxBorder.BorderThickness = new Thickness(2);
            UpdateValidationStatus(false, 0);
            return;
        }
        var unknown = UnknownSymbols(text);
        if (unknown.Count > 0)
            ShowStatus($"⚠ 파싱 OK · 미정의 {unknown.Count}건: {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? " …" : "")}", WarnText);
        else
            ShowStatus("✓ 파싱 OK — Ctrl+Enter 로 래더 반영", OkText);
        TextBoxBorder.BorderBrush = DirtyBorder;
        UpdateValidationStatus(true, unknown.Count);
    }

    private System.Collections.Generic.List<string> UnknownSymbols(string text)
    {
        var unknown = new System.Collections.Generic.List<string>();
        if (DataContext is not ExpressionEditorViewModel vm) return unknown;
        var sym = vm.SymbolProvider;
        foreach (var id in CoilConditionParser.ExtractIdentifiers(text))
        {
            if (string.Equals(id, "_ON", System.StringComparison.OrdinalIgnoreCase)
             || string.Equals(id, "_OFF", System.StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var hit = sym.GetCandidates().Any(c =>
                    string.Equals(c.Display, id, System.StringComparison.OrdinalIgnoreCase));
                if (!hit) unknown.Add(id);
            }
            catch { }
        }
        return unknown.Distinct().ToList();
    }

    private void ShowStatus(string text, Brush brush)
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

    // ── 키 입력 ────────────────────────────────────────────────────────────────

    private void StTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool alt  = (Keyboard.Modifiers & ModifierKeys.Alt)     == ModifierKeys.Alt;

        // 자동완성 popup 열려있으면 navigation 키를 먼저 가로챔.
        if (AutoCompletePopup is not null && AutoCompletePopup.IsOpen)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (AutoCompleteList.Items.Count == 0) return;
                int next = e.Key == Key.Down
                    ? System.Math.Min(AutoCompleteList.SelectedIndex + 1, AutoCompleteList.Items.Count - 1)
                    : System.Math.Max(AutoCompleteList.SelectedIndex - 1, 0);
                AutoCompleteList.SelectedIndex = next;
                AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                e.Handled = true; return;
            }
            if (e.Key == Key.Tab || (e.Key == Key.Enter && !ctrl))
            {
                AcceptAutoComplete();
                e.Handled = true; return;
            }
            if (e.Key == Key.Escape) { HideAutoComplete(); e.Handled = true; return; }
        }

        if ((ctrl && e.Key == Key.Enter) || e.Key == Key.F5) { CommitText(); e.Handled = true; return; }
        if (e.Key == Key.Escape)         { SyncStTextFromVm(); e.Handled = true; return; }
        if (alt && e.Key == Key.F)       { Format(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Space)  { OpenSymbolPicker(); e.Handled = true; return; }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e) => CommitText();
    private void FormatBtn_Click(object sender, RoutedEventArgs e) => Format();
    private void Revert_Click(object sender, RoutedEventArgs e) => SyncStTextFromVm();

    // ── Quick Insert ───────────────────────────────────────────────────────────

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

    // ── 심볼 픽커 ──────────────────────────────────────────────────────────────

    private void SymbolPicker_Click(object sender, RoutedEventArgs e) => OpenSymbolPicker();

    private void OpenSymbolPicker()
    {
        if (StTextBox.IsReadOnly) return;
        if (DataContext is not ExpressionEditorViewModel vm) return;
        var all = new System.Collections.Generic.List<string> { "_ON", "_OFF" };
        try { all.AddRange(vm.SymbolProvider.GetCandidates().Select(c => c.Display)); }
        catch { }
        var distinct = all.Distinct(System.StringComparer.OrdinalIgnoreCase)
                          .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                          .ToList();
        SymbolList.ItemsSource = distinct;
        SymbolFilter.Text = "";
        if (distinct.Count > 0) SymbolList.SelectedIndex = 0;
        SymbolPopup.IsOpen = true;
        SymbolFilter.Focus();
    }

    private void SymbolFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not ExpressionEditorViewModel vm) return;
        var all = new System.Collections.Generic.List<string> { "_ON", "_OFF" };
        try { all.AddRange(vm.SymbolProvider.GetCandidates().Select(c => c.Display)); }
        catch { }
        var distinct = all.Distinct(System.StringComparer.OrdinalIgnoreCase)
                          .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase);
        SymbolList.ItemsSource = StEditorOps.FilterSymbols(distinct, SymbolFilter.Text).ToList();
        if (SymbolList.Items.Count > 0) SymbolList.SelectedIndex = 0;
    }

    private void SymbolFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (SymbolList.Items.Count == 0) return;
            int next = e.Key == Key.Down
                ? System.Math.Min(SymbolList.SelectedIndex + 1, SymbolList.Items.Count - 1)
                : System.Math.Max(SymbolList.SelectedIndex - 1, 0);
            SymbolList.SelectedIndex = next;
            SymbolList.ScrollIntoView(SymbolList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitSymbolPick();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SymbolPopup.IsOpen = false;
            StTextBox.Focus();
            e.Handled = true;
        }
    }

    private void SymbolList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CommitSymbolPick();
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

    // ── Commit ────────────────────────────────────────────────────────────────

    private void CommitText()
    {
        if (StTextBox.IsReadOnly) return;
        if (DataContext is not ExpressionEditorViewModel vm) return;
        var text = StTextBox.Text ?? "";

        if (!CoilConditionParser.TryParse(text, out var cond, out var err) || cond is null)
        {
            ShowStatus("✕ " + err, ErrorText);
            TextBoxBorder.BorderBrush = ErrorBorder;
            TextBoxBorder.BorderThickness = new Thickness(2);
            // 에러 위치 자동 caret 점프.
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

        var node = CoilConditionConverter.ToExprNode(cond);
        if (node is null) return;

        _suppressEditFeedback = true;
        _suppressTextSync = true;
        try
        {
            vm.Root = node;
            _lastCommittedText = text;
            ShowStatus("✓ 적용됨", OkText);
            ApplyBtn.IsEnabled = false;
            TextBoxBorder.BorderBrush = _normalBorder;
            TextBoxBorder.BorderThickness = new Thickness(1);
        }
        finally { _suppressEditFeedback = false; _suppressTextSync = false; }
        UpdateStatusBar();
    }

    // ── 상태바 ────────────────────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        if (StatusModeLabel is null) return;
        bool textMode = ModeText?.IsChecked == true;
        StatusModeLabel.Text = textMode ? "✏ 텍스트 모드" : "🪜 래더 모드";

        if (textMode && StTextBox is not null)
        {
            var (line, col) = StEditorOps.GetCursorLineCol(StTextBox);
            StatusCursorLabel.Text = $"Ln {line}, Col {col}";
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

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>ExpressionEditor 의 IExpressionSymbolProvider 를 LadderEditor 의 ISymbolProvider 로 어댑트.</summary>
    private sealed class EditorSymbolProvider : AAStoPLC.LadderEditor.Models.ISymbolProvider
    {
        private static readonly string[] BuiltIn = { "_ON", "_OFF" };
        private readonly Promaker.Controls.ExpressionEditor.Providers.IExpressionSymbolProvider _inner;
        private readonly System.Collections.Generic.HashSet<string> _known;
        private readonly System.Collections.Generic.List<string> _candidates;

        public EditorSymbolProvider(Promaker.Controls.ExpressionEditor.Providers.IExpressionSymbolProvider inner)
        {
            _inner = inner;
            _candidates = BuiltIn
                .Concat(inner.GetCandidates().Select(c => c.Display))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            _known = new(_candidates, System.StringComparer.OrdinalIgnoreCase);
        }

        public System.Collections.Generic.IEnumerable<string> Search(string prefixOrSubstring) =>
            string.IsNullOrEmpty(prefixOrSubstring)
                ? _candidates
                : _candidates.Where(s => s.IndexOf(prefixOrSubstring, System.StringComparison.OrdinalIgnoreCase) >= 0);

        public bool IsKnown(string symbol) => _known.Contains(symbol);
    }
}
