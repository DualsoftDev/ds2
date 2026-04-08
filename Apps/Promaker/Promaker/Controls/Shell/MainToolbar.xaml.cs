using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ds2.Core;
using Ds2.Editor;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class MainToolbar : UserControl
{
    public MainToolbar()
    {
        InitializeComponent();
        Loaded += (_, _) => InitializeConnectPinStates();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void CloseSavePopup(object sender, RoutedEventArgs e) => SaveMenuToggle.IsChecked = false;
    private void CloseOpenPopup(object sender, RoutedEventArgs e) => OpenMenuToggle.IsChecked = false;
    private void CloseEditPopup(object sender, RoutedEventArgs e) => EditMenuToggle.IsChecked = false;

    private void ConnectType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && VM is { } vm)
        {
            vm.SelectedConnectArrowType = tag switch
            {
                "Reset" => ArrowType.Reset,
                "StartReset" => ArrowType.StartReset,
                "ResetReset" => ArrowType.ResetReset,
                "Group" => ArrowType.Group,
                _ => ArrowType.Start
            };
            ConnectTypeToggle.IsChecked = false;
        }
    }

    private void ConnectTypePopup_Opened(object sender, EventArgs e)
    {
        if (VM is not { } vm) return;

        var isWorkMode = vm.Canvas.ActiveTab is { } tab
            && EntityKindRules.isWorkArrowModeForTab(tab.Kind);

        var vis = isWorkMode ? Visibility.Visible : Visibility.Collapsed;
        ConnResetRadio.Visibility = vis;
        ConnStartResetRadio.Visibility = vis;
        ConnResetResetRadio.Visibility = vis;

        // Call 모드에서 Work 전용 타입이 선택돼 있으면 Start로 폴백
        if (!isWorkMode && vm.SelectedConnectArrowType is ArrowType.Reset or ArrowType.StartReset or ArrowType.ResetReset)
            vm.SelectedConnectArrowType = ArrowType.Start;

        var radio = vm.SelectedConnectArrowType switch
        {
            ArrowType.Reset => ConnResetRadio,
            ArrowType.StartReset => ConnStartResetRadio,
            ArrowType.ResetReset => ConnResetResetRadio,
            ArrowType.Group => ConnGroupRadio,
            _ => ConnStartRadio
        };
        radio.IsChecked = true;
    }

    private void ConnectPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tagStr }
            && Enum.TryParse<ArrowType>(tagStr, out var type))
        {
            ArrowTypeFrequencyTracker.TogglePin(type);
        }
    }

    private void InitializeConnectPinStates()
    {
        ConnStartPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Start);
        ConnResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Reset);
        ConnStartResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.StartReset);
        ConnResetResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.ResetReset);
        ConnGroupPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Group);
    }
}
