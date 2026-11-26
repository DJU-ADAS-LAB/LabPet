using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabPet.Client.Social;

namespace LabPet.Client.Views;

public partial class VisitorPetWindow
{
    private Button? _greetOverlayButton;
    private Button? _snowballOverlayButton;
    private StackPanel? _interactionStrip;

    private void InitializeInteractionOverlay()
    {
        if (DismissButton.Parent is not Canvas canvas) return;
        _interactionStrip = new StackPanel { Background = Brushes.Transparent, Width = DismissButton.Width + 8 };
        canvas.Children.Add(_interactionStrip);
        Panel.SetZIndex(_interactionStrip, 10);
        _greetOverlayButton = CreateInteractionButton("👋", "인사하기");
        _snowballOverlayButton = CreateInteractionButton("❄", "눈덩이 던지기");
        _greetOverlayButton.Click += PetFriendMenu_Click;
        _interactionStrip.Children.Add(_greetOverlayButton);
        _interactionStrip.Children.Add(_snowballOverlayButton);
        _interactionStrip.MouseEnter += VisitorSurface_MouseEnter;
        _interactionStrip.MouseLeave += VisitorSurface_MouseLeave;
        // 메뉴의 DataContext는 모든 화면이 공유하는 대상별 SnowballAction이다.
        _snowballOverlayButton.SetBinding(Button.CommandProperty,
            new Binding("DataContext.Command") { Source = SnowballMenuItem });
        _snowballOverlayButton.SetBinding(ToolTipProperty,
            new Binding("DataContext.Tooltip") { Source = SnowballMenuItem });
        _greetOverlayButton.SetBinding(IsEnabledProperty,
            new Binding("IsEnabled") { Source = PetFriendMenuItem });
        _interactionStrip.Opacity = 0;
        _interactionStrip.IsHitTestVisible = false;
    }

    private Button CreateInteractionButton(string icon, string label)
    {
        var button = new Button
        {
            Content = icon, ToolTip = label, Width = DismissButton.Width,
            Height = DismissButton.Height, Style = DismissButton.Style,
            Template = DismissButton.Template, Background = DismissButton.Background,
            Foreground = DismissButton.Foreground, BorderBrush = DismissButton.BorderBrush,
            BorderThickness = DismissButton.BorderThickness, FontSize = DismissButton.FontSize,
            Padding = DismissButton.Padding, Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 2, 4, 2)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    private void SetInteractionOverlayVisible(bool visible)
    {
        if (_interactionStrip is null) return;
        _interactionStrip.IsHitTestVisible = visible;
        _interactionStrip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(visible ? _petOpacity : 0, TimeSpan.FromMilliseconds(visible ? 160 : 200)));
        if (visible) VisitorContextMenu_Opened(this, new RoutedEventArgs());
    }

    private void PositionInteractionOverlay(double left, double top)
    {
        if (_interactionStrip is null) return;
        Canvas.SetLeft(_interactionStrip, left - 4);
        Canvas.SetTop(_interactionStrip, top + DismissButton.Height + 2);
    }
}
