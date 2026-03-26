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
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (VM is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateConnectIcon(vm.SelectedConnectArrowType);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedConnectArrowType) && VM is { } vm)
        {
            UpdateConnectIcon(vm.SelectedConnectArrowType);
        }
    }

    private void UpdateConnectIcon(ArrowType arrowType)
    {
        // Hide all icons
        ConnectIconStart.Visibility = Visibility.Collapsed;
        ConnectIconReset.Visibility = Visibility.Collapsed;
        ConnectIconStartReset.Visibility = Visibility.Collapsed;
        ConnectIconResetReset.Visibility = Visibility.Collapsed;
        ConnectIconGroup.Visibility = Visibility.Collapsed;

        // Show selected icon
        switch (arrowType)
        {
            case ArrowType.Start:
                ConnectIconStart.Visibility = Visibility.Visible;
                break;
            case ArrowType.Reset:
                ConnectIconReset.Visibility = Visibility.Visible;
                break;
            case ArrowType.StartReset:
                ConnectIconStartReset.Visibility = Visibility.Visible;
                break;
            case ArrowType.ResetReset:
                ConnectIconResetReset.Visibility = Visibility.Visible;
                break;
            case ArrowType.Group:
                ConnectIconGroup.Visibility = Visibility.Visible;
                break;
        }
    }

    private void CloseSavePopup(object sender, RoutedEventArgs e) => SaveMenuToggle.IsChecked = false;
    private void CloseEditPopup(object sender, RoutedEventArgs e) => EditMenuToggle.IsChecked = false;
    private void CloseUtilPopup(object sender, RoutedEventArgs e) => UtilMenuToggle.IsChecked = false;

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
