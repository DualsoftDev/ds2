using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ds2.Core;

namespace Ds2.UI.Frontend.Converters;

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
        var key = value?.ToString() switch
        {
            "Work" => "NodeWorkBackgroundBrush",
            "Call" => "NodeCallBackgroundBrush",
            "Flow" => "AccentBrush",
            "System" => "OrangeAccentBrush",
            "Project" => "GreenAccentBrush",
            _ => "TertiaryBackgroundBrush"
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InvCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
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

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ArrowTypeToDashConverter : IValueConverter
{
    private static readonly DoubleCollection Dashed = [4, 2];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ArrowType at && (at == ArrowType.Reset || at == ArrowType.ResetReset)
            ? Dashed
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EntityTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() switch
        {
            "Project" => "P",
            "System" => "S",
            "Flow" => "F",
            "Work" => "W",
            "Call" => "C",
            "ApiDef" => "A",
            "Button" => "B",
            "Lamp" => "L",
            "Condition" => "?",
            "Action" => "!",
            _ => "?"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}