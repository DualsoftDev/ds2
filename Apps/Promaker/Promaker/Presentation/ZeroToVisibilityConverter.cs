using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Promaker.Presentation;

/// <summary>
/// 숫자가 0이면 Visible, 0이 아니면 Collapsed로 변환하는 컨버터
/// </summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
        {
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
