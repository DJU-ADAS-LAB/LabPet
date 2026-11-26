using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

/// <summary>기존 컨트롤과 명령을 유지하는 빠른 상호작용 패널의 조밀한 표현.</summary>
internal static class CompactPetOverlay
{
    internal const double PanelPadding = 6;
    internal const double FriendListMaximumHeight = 180;
    private static readonly ControlTemplate FlatButtonTemplate = CreateButtonTemplate();

    internal static void Attach(Border panel, Popup popup)
    {
        panel.Padding = new Thickness(PanelPadding);
        panel.Effect = null;
        panel.UseLayoutRounding = true;
        panel.SnapsToDevicePixels = true;
        panel.Loaded += (_, _) => Apply(panel);
        popup.Opened += (_, _) => Apply(panel);
        // 항목 재생성 때만 갱신하며 타이머나 매 프레임 트리 순회는 사용하지 않는다.
        foreach (var items in Descendants(panel).OfType<ItemsControl>())
            items.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (items.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                    panel.Dispatcher.BeginInvoke(() => Apply(panel));
            };
    }

    private static void Apply(Border panel)
    {
        foreach (var element in Descendants(panel).ToArray())
        {
            if (element is ScrollViewer scroll && element.TemplatedParent is null)
            {
                scroll.MaxHeight = FriendListMaximumHeight;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            if (element is Border border && border != panel && border.TemplatedParent is not Control)
            {
                border.Background = Brushes.Transparent;
                border.BorderThickness = new Thickness(0);
                border.Effect = null;
                if (border.DataContext is QuickOverlayEntry)
                {
                    border.Padding = new Thickness(0, 2, 0, 2);
                    border.Margin = new Thickness(0);
                }
                if (border.Name == "AppearanceMenuPanel")
                {
                    border.Padding = new Thickness(0, 3, 0, 0);
                    border.Margin = new Thickness(0);
                }
            }
            if (element is TextBlock text &&
                BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(QuickOverlayEntry.DisplayTitle))
            {
                CompactOverlayName.Attach(text);
            }
            if (element is ToggleButton toggle && toggle.Name == "AppearanceMenuToggle")
            {
                // 접힘 상태·내용·기존 토글 바인딩은 보존한다.
                toggle.Background = Brushes.Transparent;
                toggle.BorderThickness = new Thickness(0);
                toggle.Padding = new Thickness(0);
                toggle.ApplyTemplate();
                if (toggle.Template.FindName("ToggleBg", toggle) is Border toggleBackground)
                {
                    toggleBackground.Background = Brushes.Transparent;
                    toggleBackground.BorderThickness = new Thickness(0);
                }
            }
            if (element is Button button && button.Name != "PomodoroOverlayIndicatorButton")
            {
                button.Template = FlatButtonTemplate;
                button.Padding = new Thickness(0);
                button.BorderThickness = new Thickness(0);
                button.Background = Brushes.Transparent;
                if (button.DataContext is QuickOverlayEntry)
                {
                    // 전송 입력 버튼은 기존 크기를 유지한다.
                    if (button.Width == 26 && button.Height == 24)
                    {
                        button.FontSize = 15;
                        button.Margin = new Thickness(0);
                    }
                }
                else if (button.Content is string caption && caption == "열기")
                {
                    button.Content = "↗";
                    button.Width = 24;
                    button.FontSize = 15;
                    button.ToolTip = "통합 창 열기";
                }
            }
        }
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Surface");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        AddTrigger(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)), "Surface");
        AddTrigger(ButtonBase.IsPressedProperty, true, Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(32, 128, 128, 128)), "Surface");
        AddTrigger(UIElement.IsKeyboardFocusedProperty, true, Border.BorderThicknessProperty, new Thickness(1), "Surface");
        border.SetValue(Border.BorderBrushProperty, SystemColors.HighlightBrush);
        AddTrigger(UIElement.IsEnabledProperty, false, UIElement.OpacityProperty, 0.35, "Surface");
        template.Seal();
        return template;

        void AddTrigger(DependencyProperty property, object value, DependencyProperty target, object result, string name)
        {
            var trigger = new Trigger { Property = property, Value = value };
            trigger.Setters.Add(new Setter(target, result, name));
            template.Triggers.Add(trigger);
        }
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        if (root is FrameworkElement element) yield return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
