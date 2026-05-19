using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using Ds2.Core;
using Promaker.Presentation;

namespace Promaker.Dialogs;

public partial class ArrowTypeDialog : Window
{
    private static ArrowType _lastWorkArrowType = ArrowType.Start;
    private static ArrowType _lastCallArrowType = ArrowType.Start;

    private readonly bool _isWorkMode;

    public ArrowTypeDialog(bool isWorkMode)
    {
        InitializeComponent();

        _isWorkMode = isWorkMode;

        if (!_isWorkMode)
        {
            ResetRadio.Visibility = Visibility.Collapsed;
            StartResetRadio.Visibility = Visibility.Collapsed;
            ResetResetRadio.Visibility = Visibility.Collapsed;
        }

        var initialType = NormalizeArrowTypeForMode(_isWorkMode ? _lastWorkArrowType : _lastCallArrowType, _isWorkMode);
        SelectedArrowType = initialType;
        ApplySelection(initialType);
        InitializePinStates();

        Loaded += (_, _) => OkButton.Focus();
    }

    public ArrowType SelectedArrowType { get; private set; } = ArrowType.Start;

    private static ArrowType NormalizeArrowTypeForMode(ArrowType arrowType, bool isWorkMode)
    {
        if (arrowType == ArrowType.Unspecified)
            return ArrowType.Start;

        if (!isWorkMode && (arrowType == ArrowType.Reset || arrowType == ArrowType.StartReset || arrowType == ArrowType.ResetReset))
            return ArrowType.Start;

        return arrowType;
    }

    private void ApplySelection(ArrowType arrowType)
    {
        StartRadio.IsChecked = false;
        ResetRadio.IsChecked = false;
        StartResetRadio.IsChecked = false;
        ResetResetRadio.IsChecked = false;
        GroupRadio.IsChecked = false;

        switch (arrowType)
        {
            case ArrowType.Start:
                StartRadio.IsChecked = true;
                break;
            case ArrowType.Reset:
                ResetRadio.IsChecked = true;
                break;
            case ArrowType.StartReset:
                StartResetRadio.IsChecked = true;
                break;
            case ArrowType.ResetReset:
                ResetResetRadio.IsChecked = true;
                break;
            case ArrowType.Group:
                GroupRadio.IsChecked = true;
                break;
            default:
                StartRadio.IsChecked = true;
                break;
        }
    }

    private ArrowType ReadSelectedArrowType()
    {
        if (GroupRadio.IsChecked == true)
            return ArrowType.Group;

        if (_isWorkMode)
        {
            if (ResetRadio.IsChecked == true)
                return ArrowType.Reset;

            if (StartResetRadio.IsChecked == true)
                return ArrowType.StartReset;

            if (ResetResetRadio.IsChecked == true)
                return ArrowType.ResetReset;
        }

        return ArrowType.Start;
    }

    private void InitializePinStates()
    {
        StartPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Start);
        ResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Reset);
        StartResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.StartReset);
        ResetResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.ResetReset);
        GroupPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Group);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tagStr }
            && Enum.TryParse<ArrowType>(tagStr, out var type))
        {
            ArrowTypeFrequencyTracker.TogglePin(type);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedArrowType = NormalizeArrowTypeForMode(ReadSelectedArrowType(), _isWorkMode);

        if (_isWorkMode)
            _lastWorkArrowType = SelectedArrowType;
        else
            _lastCallArrowType = SelectedArrowType;

        DialogResult = true;
    }
}
