using System.Globalization;
using Avalonia.Data.Converters;

namespace VeyonCampus.App;

public sealed class LocalDateTimeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture),
            DateTime timestamp => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture),
            _ => ""
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("日期显示只用于读取。");
}
