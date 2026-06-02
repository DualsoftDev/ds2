using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using Promaker.Presentation;
using MahApps.Metro.IconPacks;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;

namespace Promaker.Dialogs.ConfigEditor;

public partial class ConfigEditorWindow : Window
{
    private readonly ConfigEditorViewModel _vm;
    private bool _suppressSync;

    private readonly SearchHighlightRenderer _renderer = new();
    private List<(int off, int len)> _matches = new();
    private int _currentIndex = -1;

    public ConfigEditorWindow(ConfigEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;

        // 저장 완료 시 창 닫기
        viewModel.CloseRequested += () => Close();

        // 검색 하이라이트 렌더러 + 테마별(라이트/다크) JSON highlighting·에디터 색
        JsonEditor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        ApplyEditorTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += ApplyEditorTheme;
        Closed += (_, _) => ThemeManager.ThemeChanged -= ApplyEditorTheme;

        // TextEditor.Text 는 DependencyProperty 가 아니라 RawJson(ObservableProperty) 과 양방향 동기화.
        JsonEditor.Text = viewModel.RawJson ?? string.Empty;
        JsonEditor.TextChanged += OnEditorTextChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Ctrl+F = 찾기, Ctrl+H = 바꾸기, Esc = 닫기 (VSCode 식).
        JsonEditor.PreviewKeyDown += OnEditorPreviewKeyDown;
    }

    private static IHighlightingDefinition? LoadHighlighting(string resourceName)
    {
        using var stream = typeof(ConfigEditorWindow).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    /// <summary>라이트/다크 테마에 맞춰 에디터 배경·글자·줄번호·JSON highlighting 을 전환.</summary>
    private void ApplyEditorTheme(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark;
        JsonEditor.Background = HexBrush(dark ? "#1E1E1E" : "#FFFFFF");
        JsonEditor.Foreground = HexBrush(dark ? "#D4D4D4" : "#1E1E1E");
        JsonEditor.LineNumbersForeground = HexBrush(dark ? "#858585" : "#237893");
        JsonEditor.SyntaxHighlighting = LoadHighlighting(dark ? "JsonHighlighting.xshd" : "JsonHighlighting.Light.xshd");
    }

    private static Brush HexBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ── RawJson 양방향 동기화 ────────────────────────────────────────
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressSync) return;
        _suppressSync = true;
        _vm.RawJson = JsonEditor.Text;
        _suppressSync = false;
        if (FindReplaceOverlay.Visibility == Visibility.Visible)
            DoSearch();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSync || e.PropertyName != nameof(ConfigEditorViewModel.RawJson))
            return;
        var text = _vm.RawJson ?? string.Empty;
        if (JsonEditor.Text == text) return;
        _suppressSync = true;
        JsonEditor.Text = text;
        _suppressSync = false;
    }

    // ── 오버레이 표시/숨김 ──────────────────────────────────────────
    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && e.Key == Key.F) { ShowOverlay(replaceMode: false); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { ShowOverlay(replaceMode: true); e.Handled = true; }
        else if (e.Key == Key.Escape && FindReplaceOverlay.Visibility == Visibility.Visible) { CloseOverlay(); e.Handled = true; }
    }

    private void ShowOverlay(bool replaceMode)
    {
        FindReplaceOverlay.Visibility = Visibility.Visible;
        ExpandReplaceBtn.IsChecked = replaceMode;
        SetReplaceMode(replaceMode);

        // 에디터에서 한 줄 선택돼 있으면 찾기어로 가져온다 (VSCode 식).
        if (!string.IsNullOrEmpty(JsonEditor.SelectedText) && !JsonEditor.SelectedText.Contains('\n'))
            FindBox.Text = JsonEditor.SelectedText;

        FindBox.Focus();
        FindBox.SelectAll();
        DoSearch();
    }

    private void CloseOverlay()
    {
        FindReplaceOverlay.Visibility = Visibility.Collapsed;
        _matches = new();
        _currentIndex = -1;
        _renderer.Matches.Clear();
        _renderer.Current = null;
        Invalidate();
        JsonEditor.Focus();
    }

    private void OnCloseOverlay(object sender, RoutedEventArgs e) => CloseOverlay();

    private void OnToggleReplaceRow(object sender, RoutedEventArgs e)
        => SetReplaceMode(ExpandReplaceBtn.IsChecked == true);

    private void SetReplaceMode(bool on)
    {
        ReplaceRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ExpandIcon.Kind = on ? PackIconMaterialKind.ChevronDown : PackIconMaterialKind.ChevronRight;
    }

    // ── 검색 ────────────────────────────────────────────────────────
    private void OnFindTextChanged(object sender, RoutedEventArgs e) => DoSearch();
    private void OnSearchOptionChanged(object sender, RoutedEventArgs e) => DoSearch();

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) MovePrev();
            else MoveNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { CloseOverlay(); e.Handled = true; }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ReplaceCurrent(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseOverlay(); e.Handled = true; }
    }

    private void DoSearch()
    {
        var pattern = FindBox.Text;
        _matches = string.IsNullOrEmpty(pattern) ? new() : FindMatches(JsonEditor.Document.Text, pattern);

        _renderer.Matches.Clear();
        foreach (var (off, len) in _matches)
            _renderer.Matches.Add(new TextSegment { StartOffset = off, Length = len });

        if (_matches.Count == 0)
            _currentIndex = -1;
        else
        {
            _currentIndex = _matches.FindIndex(m => m.off >= JsonEditor.CaretOffset);
            if (_currentIndex < 0) _currentIndex = 0;
        }
        SelectCurrent();
        UpdateResultText();
        Invalidate();
    }

    private List<(int, int)> FindMatches(string text, string pattern)
    {
        var result = new List<(int, int)>();
        if (RegexBtn.IsChecked == true)
        {
            var opts = MatchCaseBtn.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
            try
            {
                foreach (Match m in Regex.Matches(text, pattern, opts))
                    if (m.Length > 0) result.Add((m.Index, m.Length));
            }
            catch (ArgumentException) { /* 잘못된 정규식 — 매치 없음 처리 */ }
        }
        else
        {
            var cmp = MatchCaseBtn.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var i = 0;
            while ((i = text.IndexOf(pattern, i, cmp)) >= 0)
            {
                if (WholeWordBtn.IsChecked != true || IsWholeWord(text, i, pattern.Length))
                    result.Add((i, pattern.Length));
                i += pattern.Length;
            }
        }
        return result;
    }

    private static bool IsWholeWord(string text, int start, int len)
    {
        var leftOk = start == 0 || (!char.IsLetterOrDigit(text[start - 1]) && text[start - 1] != '_');
        var end = start + len;
        var rightOk = end >= text.Length || (!char.IsLetterOrDigit(text[end]) && text[end] != '_');
        return leftOk && rightOk;
    }

    private void MoveNext()
    {
        if (_matches.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _matches.Count;
        SelectCurrent(); UpdateResultText(); Invalidate();
    }

    private void MovePrev()
    {
        if (_matches.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _matches.Count) % _matches.Count;
        SelectCurrent(); UpdateResultText(); Invalidate();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => MoveNext();
    private void OnFindPrev(object sender, RoutedEventArgs e) => MovePrev();

    private void SelectCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _matches.Count) { _renderer.Current = null; return; }
        var (off, len) = _matches[_currentIndex];
        _renderer.Current = new TextSegment { StartOffset = off, Length = len };
        JsonEditor.Select(off, len);
        JsonEditor.ScrollTo(JsonEditor.Document.GetLineByOffset(off).LineNumber, 0);
    }

    private void UpdateResultText()
        => ResultText.Text = _matches.Count == 0 ? "결과 없음" : $"{_currentIndex + 1} / {_matches.Count}";

    private void Invalidate()
        => JsonEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

    // ── 바꾸기 ──────────────────────────────────────────────────────
    private void OnReplaceCurrent(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void ReplaceCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;
        var (off, len) = _matches[_currentIndex];
        JsonEditor.Document.Replace(off, len, ReplaceBox.Text);
        JsonEditor.CaretOffset = off + ReplaceBox.Text.Length;
        // 텍스트 변경으로 OnEditorTextChanged → DoSearch 가 호출되지만, caret 기준 다음 매치를 잡도록 한 번 더.
        DoSearch();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var pattern = FindBox.Text;
        if (string.IsNullOrEmpty(pattern)) return;
        var text = JsonEditor.Document.Text;
        string result;

        if (RegexBtn.IsChecked == true)
        {
            var opts = MatchCaseBtn.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
            try { result = Regex.Replace(text, pattern, ReplaceBox.Text, opts); }
            catch (ArgumentException) { return; }
        }
        else if (WholeWordBtn.IsChecked == true)
        {
            // 단어 단위 — 매치 위치를 뒤에서부터 치환 (offset 안 깨지게).
            var matches = FindMatches(text, pattern);
            var sb = new StringBuilder(text);
            for (var k = matches.Count - 1; k >= 0; k--)
                sb.Remove(matches[k].Item1, matches[k].Item2).Insert(matches[k].Item1, ReplaceBox.Text);
            result = sb.ToString();
        }
        else
        {
            result = MatchCaseBtn.IsChecked == true
                ? text.Replace(pattern, ReplaceBox.Text)
                : ReplaceIgnoreCase(text, pattern, ReplaceBox.Text);
        }

        JsonEditor.Document.Text = result;
        DoSearch();
    }

    private static string ReplaceIgnoreCase(string text, string pattern, string replacement)
    {
        var sb = new StringBuilder();
        int i = 0, idx;
        while ((idx = text.IndexOf(pattern, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            sb.Append(text, i, idx - i).Append(replacement);
            i = idx + pattern.Length;
        }
        sb.Append(text, i, text.Length - i);
        return sb.ToString();
    }
}
