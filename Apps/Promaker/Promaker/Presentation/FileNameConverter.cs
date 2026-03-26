using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Promaker.Presentation;

/// <summary>
/// 파일 전체 경로에서 파일명만 추출하는 컨버터
/// </summary>
public class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string filePath && !string.IsNullOrEmpty(filePath))
        {
            try
            {
                return Path.GetFileName(filePath);
            }
            catch
            {
                return filePath;
            }
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
