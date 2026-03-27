using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ds2.Core;
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
