using System.Globalization;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker;

namespace Promaker.Converters;

file static class ConverterHelpers
{
    private static readonly IReadOnlyDictionary<EntityKind, string> EntityBrushKeys =
        new Dictionary<EntityKind, string>
        {
            [EntityKind.Work] = "NodeWorkBackgroundBrush",
            [EntityKind.Call] = "NodeCallBackgroundBrush",
            [EntityKind.Flow] = "AccentBrush",
            [EntityKind.System] = "OrangeAccentBrush",
            [EntityKind.Project] = "GreenAccentBrush"
        };

    private static readonly IReadOnlyDictionary<EntityKind, string> EntityIcons =
        new Dictionary<EntityKind, string>
        {
            [EntityKind.Project] = "P",
            [EntityKind.System] = "S",
            [EntityKind.Flow] = "F",
            [EntityKind.Work] = "W",
            [EntityKind.Call] = "C",
            [EntityKind.ApiDef] = "A",
            [EntityKind.Button] = "B",
            [EntityKind.Lamp] = "L",
            [EntityKind.Condition] = "?",
            [EntityKind.Action] = "!"
        };

    public static Brush ResolveBrush(string key)
    {
        var app = Application.Current;
        return app?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public static string ResolveEntityBrushKey(EntityKind entityType) =>
        EntityBrushKeys.TryGetValue(entityType, out var key)
            ? key
            : "TertiaryBackgroundBrush";

    public static string ResolveEntityIcon(EntityKind entityType) =>
        EntityIcons.TryGetValue(entityType, out var icon)
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
        var kind = value is EntityKind ek ? ek : EntityKind.Project;
        var key = ConverterHelpers.ResolveEntityBrushKey(kind);
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
        => ConverterHelpers.ResolveEntityIcon(value is EntityKind ek ? ek : EntityKind.Project);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
