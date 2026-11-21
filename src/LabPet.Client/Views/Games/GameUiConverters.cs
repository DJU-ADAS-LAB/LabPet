using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LabPet.Client.Views.Games;

/// <summary>
/// enum 값을 ConverterParameter 문자열과 비교해 bool을 반환한다. (라디오/필 버튼 바인딩용)
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            var raw = parameter.ToString();
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlying.IsEnum && Enum.TryParse(underlying, raw, ignoreCase: true, out var parsedEnum))
            {
                return parsedEnum;
            }

            if (underlying == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                return parsedInt;
            }

            if (underlying == typeof(bool) && bool.TryParse(raw, out var parsedBool))
            {
                return parsedBool;
            }
        }

        return Binding.DoNothing;
    }
}

/// <summary>
/// enum 값이 지정한 상태 이름(또는 '|' 구분 목록)과 일치하면 Visible, 아니면 Collapsed를 반환한다.
/// </summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return Visibility.Collapsed;
        }

        var actual = value.ToString();
        var targets = parameter.ToString()!.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var target in targets)
        {
            if (string.Equals(actual, target, StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// bool 값이 false일 때 Visible, true일 때 Collapsed를 반환한다.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// null 여부 또는 빈 문자열 여부에 따라 Visibility를 반환한다.
/// ConverterParameter가 "Invert"이면 비어 있을 때 Visible을 반환한다.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

        if (string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
