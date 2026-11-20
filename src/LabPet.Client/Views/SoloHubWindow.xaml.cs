using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LabPet.Client.Games;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

public partial class SoloHubWindow : Window
{
    private static readonly Color[] LadderPathColors =
    [
        Color.FromRgb(255, 93, 143),
        Color.FromRgb(79, 70, 229),
        Color.FromRgb(16, 185, 129),
        Color.FromRgb(245, 158, 11),
        Color.FromRgb(14, 165, 233),
        Color.FromRgb(168, 85, 247),
        Color.FromRgb(239, 68, 68),
        Color.FromRgb(20, 184, 166)
    ];

    private readonly SoloHubViewModel _viewModel;
    private readonly DispatcherTimer _uiRefreshTimer;

    public SoloHubWindow(SoloHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.Games.LadderBoardGenerated += OnLadderBoardGenerated;
        _viewModel.Games.PropertyChanged += OnGamesPropertyChanged;

        _uiRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _uiRefreshTimer.Tick += (_, _) =>
        {
            _viewModel.RefreshAudioUi();
            _viewModel.RefreshProgressUi();
        };
        _uiRefreshTimer.Start();

        Closed += (_, _) =>
        {
            _uiRefreshTimer.Stop();
            _viewModel.Games.LadderBoardGenerated -= OnLadderBoardGenerated;
            _viewModel.Games.PropertyChanged -= OnGamesPropertyChanged;
        };
    }

    private void OnGamesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoinGamesViewModel.IsTypingRunning) && _viewModel.Games.IsTypingRunning)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                TypingInputBox.Focus();
                Keyboard.Focus(TypingInputBox);
            });
        }
    }

    private void OnLadderBoardGenerated(object? sender, LadderBoard board)
    {
        Dispatcher.BeginInvoke(() => DrawLadderBoard(board));
    }

    private void DrawLadderBoard(LadderBoard board)
    {
        LadderCanvas.Children.Clear();
        var w = LadderCanvas.ActualWidth > 40 ? LadderCanvas.ActualWidth : 390;
        var h = LadderCanvas.ActualHeight > 40 ? LadderCanvas.ActualHeight : 210;

        var count = board.Participants.Count;
        const double padX = 28.0;
        const double topY = 24.0;
        var bottomY = h - 24.0;
        var colStep = count > 1 ? (w - padX * 2) / (count - 1) : 0;
        var levelStep = (bottomY - topY) / board.Levels;

        double ColX(double col) => padX + col * colStep;
        double LevelY(double lv) => topY + lv * levelStep;

        var railBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219));

        // 1. 기본 세로줄 & 상단 참여자/하단 결과 라벨
        for (var col = 0; col < count; col++)
        {
            var x = ColX(col);
            LadderCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = topY,
                X2 = x,
                Y2 = bottomY,
                Stroke = railBrush,
                StrokeThickness = 2.5
            });

            var topLabel = new TextBlock
            {
                Text = board.Participants[col],
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(LadderPathColors[col % LadderPathColors.Length]),
                Width = 64,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Canvas.SetLeft(topLabel, x - 32);
            Canvas.SetTop(topLabel, 3);
            LadderCanvas.Children.Add(topLabel);

            var botLabel = new TextBlock
            {
                Text = board.Prizes[col],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Width = 72,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Canvas.SetLeft(botLabel, x - 36);
            Canvas.SetTop(botLabel, bottomY + 4);
            LadderCanvas.Children.Add(botLabel);
        }

        // 2. 가로줄(사다리 발판)
        foreach (var rung in board.Rungs)
        {
            var y = LevelY(rung.Level);
            LadderCanvas.Children.Add(new Line
            {
                X1 = ColX(rung.LeftColumn),
                Y1 = y,
                X2 = ColX(rung.LeftColumn + 1),
                Y2 = y,
                Stroke = railBrush,
                StrokeThickness = 2.5
            });
        }

        // 3. 각 참여자 경로 하이라이트 (첫 번째 참여자 '나' 경로를 굵게 강조)
        var traces = LabLadderEngine.TraceAll(board);
        for (var i = traces.Count - 1; i >= 0; i--)
        {
            var trace = traces[i];
            var color = LadderPathColors[i % LadderPathColors.Length];
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(i == 0 ? color : Color.FromArgb(145, color.R, color.G, color.B)),
                StrokeThickness = i == 0 ? 3.2 : 1.8,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var pt in trace.Path)
            {
                poly.Points.Add(new Point(ColX(pt.Column), LevelY(pt.Level)));
            }

            LadderCanvas.Children.Add(poly);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
