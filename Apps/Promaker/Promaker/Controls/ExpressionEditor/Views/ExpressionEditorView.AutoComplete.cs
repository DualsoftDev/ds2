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

public partial class ExpressionEditorView
{
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

}
