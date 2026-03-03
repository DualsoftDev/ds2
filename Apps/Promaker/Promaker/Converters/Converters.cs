using System.Globalization;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ds2.UI.Core;
using Promaker;

namespace Promaker.Converters;

file static class ConverterHelpers
{
    private static readonly IReadOnlyDictionary<string, string> EntityBrushKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EntityTypes.Work] = "NodeWorkBackgroundBrush",
            [EntityTypes.Call] = "NodeCallBackgroundBrush",
            [EntityTypes.Flow] = "AccentBrush",
            [EntityTypes.System] = "OrangeAccentBrush",
            [EntityTypes.Project] = "GreenAccentBrush"
        };

    private static readonly IReadOnlyDictionary<string, string> EntityIcons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EntityTypes.Project] = "P",
            [EntityTypes.System] = "S",
            [EntityTypes.Flow] = "F",
            [EntityTypes.Work] = "W",
            [EntityTypes.Call] = "C",
            [EntityTypes.ApiDef] = "A",
            [EntityTypes.Button] = "B",
            [EntityTypes.Lamp] = "L",
            [EntityTypes.Condition] = "?",
            [EntityTypes.Action] = "!"
        };

    public static Brush ResolveBrush(string key)
    {
        var app = Application.Current;
        return app?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public static string ResolveEntityBrushKey(string? entityType) =>
        entityType is not null && EntityBrushKeys.TryGetValue(entityType, out var key)
            ? key
            : "TertiaryBackgroundBrush";

    public static string ResolveEntityIcon(string? entityType) =>
        entityType is not null && EntityIcons.TryGetValue(entityType, out var icon)
            ? icon
            : "?";
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class EntityTypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = ConverterHelpers.ResolveEntityBrushKey(value?.ToString());
        return ConverterHelpers.ResolveBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InvCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

public sealed class ArrowTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is UiArrowType at
            ? at switch
            {
                UiArrowType.Start => "GreenAccentBrush",
                UiArrowType.Reset => "OrangeAccentBrush",
                UiArrowType.StartReset => "RedAccentBrush",
                UiArrowType.ResetReset => "OrangeAccentBrush",
                _ => "SecondaryTextBrush"
            }
            : "SecondaryTextBrush";

        return ConverterHelpers.ResolveBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class ArrowTypeToDashConverter : IValueConverter
{
    private static readonly DoubleCollection Dashed = [4, 2];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is UiArrowType at && (at == UiArrowType.Reset || at == UiArrowType.ResetReset)
            ? Dashed
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EntityTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ConverterHelpers.ResolveEntityIcon(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
