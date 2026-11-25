using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

/// <summary>
/// 바탕화면 펫 우클릭 메뉴와 시스템 트레이 메뉴를 단일 소스에서 생성한다.
/// </summary>
internal static class PetContextMenuFactory
{
    private static readonly BooleanToVisibilityConverter BoolToVisibility = new();
    private static readonly Brush UpdateReadyForeground = CreateFrozenBrush("#FFFF5D8F");
    private static readonly Brush VersionSubtextForeground = CreateFrozenBrush("#FF9CA3AF");
    private static readonly Brush FeatureBadgeDotBrush = CreateFrozenBrush("#FFEF4444");

    public static ContextMenu Create(PetWindowViewModel viewModel, bool includeStartupAutoRun)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var menu = new ContextMenu
        {
            DataContext = viewModel
        };

        var updateReadyItem = new MenuItem
        {
            Command = viewModel.ApplyUpdateAndRestartCommand,
            FontWeight = FontWeights.Bold,
            Foreground = UpdateReadyForeground
        };
        updateReadyItem.SetBinding(MenuItem.HeaderProperty, new Binding(nameof(PetWindowViewModel.UpdateReadyMenuHeader))
        {
            Source = viewModel
        });
        updateReadyItem.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(PetWindowViewModel.IsUpdateReady))
        {
            Source = viewModel,
            Converter = BoolToVisibility
        });
        menu.Items.Add(updateReadyItem);

        menu.Items.Add(new MenuItem { Header = "🏠  LabPet 열기", Command = viewModel.OpenHubCommand });
        menu.Items.Add(new MenuItem { Header = "🎨  외형 · 이름 변경", Command = viewModel.OpenCustomizationCommand });
        menu.Items.Add(new MenuItem { Header = "💬  친구", Command = viewModel.OpenSocialCommand });
        menu.Items.Add(new MenuItem { Header = "📈  성장", Command = viewModel.OpenGrowthCommand });
        var pomodoroHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        pomodoroHeader.Children.Add(new TextBlock
        {
            Text = "⏱  뽀모도로",
            VerticalAlignment = VerticalAlignment.Center
        });
        var pomodoroBadgeDot = new Ellipse
        {
            Name = "PomodoroMenuRedDot",
            Width = 6,
            Height = 6,
            Fill = FeatureBadgeDotBrush,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        pomodoroBadgeDot.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(PetWindowViewModel.ShowPomodoroFeatureBadge))
        {
            Source = viewModel,
            Converter = BoolToVisibility
        });
        pomodoroHeader.Children.Add(pomodoroBadgeDot);
        menu.Items.Add(new MenuItem { Header = pomodoroHeader, Command = viewModel.OpenPomodoroCommand });
        menu.Items.Add(new MenuItem { Header = "🎮  코인 게임", Command = viewModel.OpenGamesCommand });

        menu.Items.Add(new Separator());

        menu.Items.Add(new MenuItem { Header = "🎄  크리스마스 트리", Command = viewModel.LaunchChristmasTreeCommand });

        // 클릭 투과 및 집중 모드 우클릭 메뉴 항목 제거 (관련 코드 주석 처리)
        // var clickThroughItem = new MenuItem
        // {
        //     Header = "🎯  클릭 투과 집중 모드",
        //     IsCheckable = true
        // };
        // clickThroughItem.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(PetWindowViewModel.IsClickThroughEnabled))
        // {
        //     Source = viewModel,
        //     Mode = BindingMode.TwoWay
        // });
        // menu.Items.Add(clickThroughItem);

        if (includeStartupAutoRun)
        {
            var autoRunItem = new MenuItem
            {
                Header = "Windows 시작 시 자동 실행",
                IsCheckable = true
            };
            autoRunItem.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(PetWindowViewModel.IsStartupAutoRunEnabled))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay
            });
            menu.Items.Add(autoRunItem);
        }

        menu.Items.Add(new MenuItem { Header = "📍  기본 위치로 복귀", Command = viewModel.ResetPositionCommand });

        menu.Items.Add(new Separator());

        menu.Items.Add(new MenuItem { Header = "⚙  설정", Command = viewModel.OpenSettingsCommand });

        var updateHeader = new StackPanel();
        updateHeader.Children.Add(new TextBlock { Text = "🔄  업데이트 확인" });
        var versionText = new TextBlock
        {
            FontSize = 10,
            Foreground = VersionSubtextForeground,
            Margin = new Thickness(20, 1, 0, 0)
        };
        versionText.SetBinding(TextBlock.TextProperty, new Binding(nameof(PetWindowViewModel.CurrentVersionText))
        {
            Source = viewModel
        });
        updateHeader.Children.Add(versionText);

        menu.Items.Add(new MenuItem
        {
            Header = updateHeader,
            Command = viewModel.CheckUpdatesCommand
        });

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "⏻  LabPet 종료", Command = viewModel.ExitApplicationCommand });

        return menu;
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
