using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ds2.Core;
using Ds2.UI.Frontend;

namespace Ds2.UI.Frontend.Converters;

file static class ConverterHelpers
{
    public static Brush ResolveBrush(string key)
    {
        var app = Application.Current;
        return app?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
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
        var entityType = value?.ToString();
        var key =
            EntityTypes.Is(entityType, EntityTypes.Work) ? "NodeWorkBackgroundBrush" :
            EntityTypes.Is(entityType, EntityTypes.Call) ? "NodeCallBackgroundBrush" :
            EntityTypes.Is(entityType, EntityTypes.Flow) ? "AccentBrush" :
            EntityTypes.Is(entityType, EntityTypes.System) ? "OrangeAccentBrush" :
            EntityTypes.Is(entityType, EntityTypes.Project) ? "GreenAccentBrush" :
            "TertiaryBackgroundBrush";

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
        var key = value is ArrowType at
            ? at switch
            {
                ArrowType.Start => "GreenAccentBrush",
                ArrowType.Reset => "OrangeAccentBrush",
                ArrowType.StartReset => "RedAccentBrush",
                ArrowType.ResetReset => "OrangeAccentBrush",
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
        => value is ArrowType at && (at == ArrowType.Reset || at == ArrowType.ResetReset)
            ? Dashed
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EntityTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var entityType = value?.ToString();
        return
            EntityTypes.Is(entityType, EntityTypes.Project) ? "P" :
            EntityTypes.Is(entityType, EntityTypes.System) ? "S" :
            EntityTypes.Is(entityType, EntityTypes.Flow) ? "F" :
            EntityTypes.Is(entityType, EntityTypes.Work) ? "W" :
            EntityTypes.Is(entityType, EntityTypes.Call) ? "C" :
            EntityTypes.Is(entityType, EntityTypes.ApiDef) ? "A" :
            EntityTypes.Is(entityType, EntityTypes.Button) ? "B" :
            EntityTypes.Is(entityType, EntityTypes.Lamp) ? "L" :
            EntityTypes.Is(entityType, EntityTypes.Condition) ? "?" :
            EntityTypes.Is(entityType, EntityTypes.Action) ? "!" :
            "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
