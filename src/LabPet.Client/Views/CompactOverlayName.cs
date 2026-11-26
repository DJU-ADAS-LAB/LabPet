using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

/// <summary>이름을 열 너비 안에서 한 줄 또는 두 줄로 표시하며 원본 이름은 툴팁에 보존한다.</summary>
internal sealed class CompactOverlayName(TextBlock text, bool tooltip) : IMultiValueConverter
{
    internal static void Attach(TextBlock text)
    {
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.LineHeight = 12;
        text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        text.MaxHeight = 24;
        text.SetBinding(TextBlock.TextProperty, CreateBinding(text, false));
        text.SetBinding(FrameworkElement.ToolTipProperty, CreateBinding(text, true));
        text.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                BindingOperations.GetMultiBindingExpression(text, TextBlock.TextProperty)?.UpdateTarget();
        };
    }

    private static MultiBinding CreateBinding(TextBlock text, bool tooltip)
    {
        var binding = new MultiBinding { Converter = new CompactOverlayName(text, tooltip) };
        binding.Bindings.Add(new Binding(nameof(QuickOverlayEntry.DisplayTitle)));
        binding.Bindings.Add(new Binding());
        binding.Bindings.Add(new Binding(nameof(FrameworkElement.ActualWidth)) { Source = text });
        return binding;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[1] is not QuickOverlayEntry entry)
            return string.Empty;
        var owner = entry.Friend?.OwnerName ?? entry.Peer?.OwnerName ?? string.Empty;
        var pet = entry.Friend?.PetName ?? entry.Peer?.PetName ?? string.Empty;
        var full = string.IsNullOrWhiteSpace(owner) ? pet
            : string.IsNullOrWhiteSpace(pet) ? owner : $"{owner} ({pet})";
        if (string.IsNullOrWhiteSpace(full)) full = entry.DisplayTitle;
        var width = text.ActualWidth > 0 ? text.ActualWidth : text.Width;
        if (tooltip || !double.IsFinite(width) || width <= 0) return full;
        var measured = new FormattedText(full, culture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(pet) ? full : $"{owner}\n({pet})";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
