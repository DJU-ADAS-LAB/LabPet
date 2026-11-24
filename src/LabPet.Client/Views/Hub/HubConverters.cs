using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LabPet.Client.Views.Hub;

/// <summary>
/// 리소스 키 문자열을 Application 리소스의 Geometry로 변환한다.
/// </summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrWhiteSpace(key))
        {
            return Application.Current?.TryFindResource(key) as Geometry;
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
