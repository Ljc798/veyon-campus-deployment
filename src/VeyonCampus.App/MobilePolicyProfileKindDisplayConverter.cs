using System.Globalization;
using Avalonia.Data.Converters;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class MobilePolicyProfileKindDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MobilePolicyProfileKind kind
            ? kind switch
            {
                MobilePolicyProfileKind.Website => "网站",
                MobilePolicyProfileKind.Application => "应用",
                MobilePolicyProfileKind.WebsiteApplication => "网站+应用",
                MobilePolicyProfileKind.System => "系统限制",
                _ => "其他"
            }
            : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("预设类型显示只用于读取。");
}
