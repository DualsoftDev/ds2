using System.Windows;
using Ds2.UI.Core;

namespace Promaker.Dialogs;

public partial class ArrowTypeDialog : Window
{
    private static UiArrowType _lastWorkArrowType = UiArrowType.Start;
    private static UiArrowType _lastCallArrowType = UiArrowType.Start;

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

        Loaded += (_, _) => OkButton.Focus();
    }

    public UiArrowType SelectedArrowType { get; private set; } = UiArrowType.Start;

    private static UiArrowType NormalizeArrowTypeForMode(UiArrowType arrowType, bool isWorkMode)
    {
        if (arrowType == UiArrowType.None)
            return UiArrowType.Start;

        if (!isWorkMode && (arrowType == UiArrowType.Reset || arrowType == UiArrowType.StartReset || arrowType == UiArrowType.ResetReset))
            return UiArrowType.Start;

        return arrowType;
    }

    private void ApplySelection(UiArrowType arrowType)
    {
        StartRadio.IsChecked = false;
        ResetRadio.IsChecked = false;
        StartResetRadio.IsChecked = false;
        ResetResetRadio.IsChecked = false;
        GroupRadio.IsChecked = false;

        switch (arrowType)
        {
            case UiArrowType.Start:
                StartRadio.IsChecked = true;
                break;
            case UiArrowType.Reset:
                ResetRadio.IsChecked = true;
                break;
            case UiArrowType.StartReset:
                StartResetRadio.IsChecked = true;
                break;
            case UiArrowType.ResetReset:
                ResetResetRadio.IsChecked = true;
                break;
            case UiArrowType.Group:
                GroupRadio.IsChecked = true;
                break;
            default:
                StartRadio.IsChecked = true;
                break;
        }
    }

    private UiArrowType ReadSelectedArrowType()
    {
        if (GroupRadio.IsChecked == true)
            return UiArrowType.Group;

        if (_isWorkMode)
        {
            if (ResetRadio.IsChecked == true)
                return UiArrowType.Reset;

            if (StartResetRadio.IsChecked == true)
                return UiArrowType.StartReset;

            if (ResetResetRadio.IsChecked == true)
                return UiArrowType.ResetReset;
        }

        return UiArrowType.Start;
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
