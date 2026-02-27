using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Ds2.UI.Frontend.Controls;

public partial class ConditionSectionControl : UserControl
{
    public static readonly DependencyProperty HeaderTextProperty =
        DependencyProperty.Register(
            nameof(HeaderText),
            typeof(string),
            typeof(ConditionSectionControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AddToolTipProperty =
        DependencyProperty.Register(
            nameof(AddToolTip),
            typeof(string),
            typeof(ConditionSectionControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AddCommandProperty =
        DependencyProperty.Register(
            nameof(AddCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AddCommandParameterProperty =
        DependencyProperty.Register(
            nameof(AddCommandParameter),
            typeof(object),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveConditionCommandProperty =
        DependencyProperty.Register(
            nameof(RemoveConditionCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ToggleConditionIsOrCommandProperty =
        DependencyProperty.Register(
            nameof(ToggleConditionIsOrCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ToggleConditionIsRisingCommandProperty =
        DependencyProperty.Register(
            nameof(ToggleConditionIsRisingCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AddConditionApiCallCommandProperty =
        DependencyProperty.Register(
            nameof(AddConditionApiCallCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveConditionApiCallCommandProperty =
        DependencyProperty.Register(
            nameof(RemoveConditionApiCallCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty EditConditionApiCallSpecCommandProperty =
        DependencyProperty.Register(
            nameof(EditConditionApiCallSpecCommand),
            typeof(ICommand),
            typeof(ConditionSectionControl),
            new PropertyMetadata(null));

    public ConditionSectionControl()
    {
        InitializeComponent();
    }

    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    public string AddToolTip
    {
        get => (string)GetValue(AddToolTipProperty);
        set => SetValue(AddToolTipProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? AddCommand
    {
        get => (ICommand?)GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public object? AddCommandParameter
    {
        get => GetValue(AddCommandParameterProperty);
        set => SetValue(AddCommandParameterProperty, value);
    }

    public ICommand? RemoveConditionCommand
    {
        get => (ICommand?)GetValue(RemoveConditionCommandProperty);
        set => SetValue(RemoveConditionCommandProperty, value);
    }

    public ICommand? ToggleConditionIsOrCommand
    {
        get => (ICommand?)GetValue(ToggleConditionIsOrCommandProperty);
        set => SetValue(ToggleConditionIsOrCommandProperty, value);
    }

    public ICommand? ToggleConditionIsRisingCommand
    {
        get => (ICommand?)GetValue(ToggleConditionIsRisingCommandProperty);
        set => SetValue(ToggleConditionIsRisingCommandProperty, value);
    }

    public ICommand? AddConditionApiCallCommand
    {
        get => (ICommand?)GetValue(AddConditionApiCallCommandProperty);
        set => SetValue(AddConditionApiCallCommandProperty, value);
    }

    public ICommand? RemoveConditionApiCallCommand
    {
        get => (ICommand?)GetValue(RemoveConditionApiCallCommandProperty);
        set => SetValue(RemoveConditionApiCallCommandProperty, value);
    }

    public ICommand? EditConditionApiCallSpecCommand
    {
        get => (ICommand?)GetValue(EditConditionApiCallSpecCommandProperty);
        set => SetValue(EditConditionApiCallSpecCommandProperty, value);
    }
}
