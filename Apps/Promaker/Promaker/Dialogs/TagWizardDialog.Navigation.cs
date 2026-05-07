using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core.Store;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    public void OpenAtFBTagMapForSystemType(string? systemType)
    {
        MoveToStep(2);
        if (string.IsNullOrWhiteSpace(systemType))
            return;

        try { LoadDeviceTemplate(systemType); }
        catch { }
    }

    private void ShowSummary()
    {
        var summary = WizardSummaryBuilder.Build(
            _store, _successCount, _ioRows.Count, _dummyRows.Count, SignalTemplateWarnings);
        SummarySignalStats.Text = summary.SignalStats;
        CompletionSummaryText.Text = summary.CompletionStatus;
        OpenMigrationButton.Visibility =
            summary.HasCallStructureViolations ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenMigration_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MultiDeviceCallMigrationDialog(_store) { Owner = this };
        dlg.ShowDialog();
        ShowSummary();
    }

    private void ChunkedToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb)
            return;

        var sec = AllSections().FirstOrDefault(s => s.ChunkedToggle == cb);
        if (sec != null)
            ApplyChunkedView(sec, cb.IsChecked == true);
    }

    private void RefreshChunkedViewsIfActive()
    {
        foreach (var sec in AllSections())
        {
            if (sec.ChunkedToggle?.IsChecked == true)
                ApplyChunkedView(sec, true);
        }
    }

    private static void ApplyChunkedView(SignalSectionInfo sec, bool chunked)
    {
        sec.Chunks.Clear();
        if (!chunked)
        {
            sec.Grid.Visibility = Visibility.Visible;
            sec.ChunkedView.Visibility = Visibility.Collapsed;
            return;
        }

        var ioRows = sec.Rows.Where(r => !r.SkipAddressAlloc).ToList();
        for (int start = 0; start < ioRows.Count; start += ChunkSize)
        {
            var slice = new ObservableCollection<IndexedPatternRow>();
            for (int i = start; i < ioRows.Count && i < start + ChunkSize; i++)
                slice.Add(new IndexedPatternRow(i / 16, i % 16, ioRows[i].Pattern ?? ""));
            sec.Chunks.Add(slice);
        }

        sec.Grid.Visibility = Visibility.Collapsed;
        sec.ChunkedView.Visibility = Visibility.Visible;
    }

    private void InitializeStep1()
    {
        var flows = Queries.allFlows(_store);
        var works = flows.SelectMany(f => Queries.worksOf(f.Id, _store)).ToArray();
        var calls = works.SelectMany(w => Queries.callsOf(w.Id, _store)).ToArray();
        var allApiCalls = Queries.allApiCalls(_store);

        FlowCountText.Text = $"{flows.Length}";
        WorkCountText.Text = $"{works.Length}";
        CallCountText.Text = $"{calls.Length}";
        DeviceCountText.Text = $"{allApiCalls.Length}";

        LoadTemplateFileList();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            LoadTemplateFileList();
            MoveToStep(2);
        }
        else if (_currentStep == 2)
        {
            if (!ValidateSignalTemplates())
                return;
            if (!GenerateSignals())
                return;

            MoveToStep(3);
        }
        else if (_currentStep == 3)
        {
            if (!ConfirmAndApplyPatterns())
                return;

            MoveToStep(4);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            MoveToStep(_currentStep - 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void MoveToStep(int step)
    {
        _currentStep = step;

        Step1Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Content.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (step == 4)
            ShowSummary();

        UpdateStepIndicators();
        UpdateButtons();
    }

    private void UpdateStepIndicators()
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var greenBrush = (Brush)FindResource("GreenAccentBrush");
        var secondaryBrush = (Brush)FindResource("SecondaryBackgroundBrush");
        var lightText = (Brush)FindResource("AlwaysLightTextBrush");
        var secondaryText = (Brush)FindResource("SecondaryTextBrush");

        ApplyStepStyle(Step1Bar, Step1Text, 1, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step2Bar, Step2Text, 2, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step3Bar, Step3Text, 3, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step4Bar, Step4Text, 4, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
    }

    private void ApplyStepStyle(
        Border bar,
        TextBlock text,
        int stepNumber,
        Brush accentBrush,
        Brush greenBrush,
        Brush secondaryBrush,
        Brush lightText,
        Brush secondaryText)
    {
        if (_currentStep == stepNumber)
        {
            bar.Background = accentBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > stepNumber)
        {
            bar.Background = greenBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.Normal;
        }
        else
        {
            bar.Background = secondaryBrush;
            text.Foreground = secondaryText;
            text.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateButtons()
    {
        BackButton.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (_currentStep < 4)
        {
            NextButton.Visibility = Visibility.Visible;
            if (_currentStep == 1)
                NextButton.Content = "다음 ▶";
            else if (_currentStep == 2)
                NextButton.Content = "신호 생성 ▶";
            else if (_currentStep == 3)
                NextButton.Content = "적용 ▶";

            CloseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            NextButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
    }
}
