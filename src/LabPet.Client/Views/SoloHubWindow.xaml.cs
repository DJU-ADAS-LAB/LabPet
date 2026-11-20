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

    private sealed class AnimatedLadderTrack
    {
        public required Polyline Line { get; init; }
        public required Ellipse Marker { get; init; }
        public required List<Point> FullPoints { get; init; }
        public required double TotalLength { get; init; }
    }

    private readonly SoloHubViewModel _viewModel;
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly DispatcherTimer _ladderAnimTimer;
    private readonly List<AnimatedLadderTrack> _activeTracks = [];
    private double _ladderAnimProgress;

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

        _ladderAnimTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _ladderAnimTimer.Tick += OnLadderAnimTick;

        Closed += (_, _) =>
        {
            _uiRefreshTimer.Stop();
            _ladderAnimTimer.Stop();
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
        Dispatcher.BeginInvoke(() => DrawAndAnimateLadderBoard(board));
    }

    private void DrawAndAnimateLadderBoard(LadderBoard board)
    {
        _ladderAnimTimer.Stop();
        _activeTracks.Clear();
        LadderCanvas.Children.Clear();

        var w = LadderCanvas.ActualWidth > 40 ? LadderCanvas.ActualWidth : 390;
        var h = LadderCanvas.ActualHeight > 40 ? LadderCanvas.ActualHeight : 200;

        var count = board.Participants.Count;
        const double padX = 28.0;
        const double topY = 24.0;
        var bottomY = h - 24.0;
        var colStep = count > 1 ? (w - padX * 2) / (count - 1) : 0;
        var levelStep = (bottomY - topY) / board.Levels;

        double ColX(double col) => padX + col * colStep;
        double LevelY(double lv) => topY + lv * levelStep;

        var railBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219));

        // 1. 기본 세로줄 & 상단 참여자 / 하단 결과 라벨
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

        // 3. 각 참여자별 동시 하강 경로 애니메이션 트랙 준비
        var traces = LabLadderEngine.TraceAll(board);
        for (var i = 0; i < traces.Count; i++)
        {
            var trace = traces[i];
            var color = LadderPathColors[i % LadderPathColors.Length];
            var fullPoints = trace.Path.Select(pt => new Point(ColX(pt.Column), LevelY(pt.Level))).ToList();
            var totalLen = 0.0;
            for (var seg = 1; seg < fullPoints.Count; seg++)
            {
                totalLen += (fullPoints[seg] - fullPoints[seg - 1]).Length;
            }

            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)),
                StrokeThickness = 2.6,
                StrokeLineJoin = PenLineJoin.Round
            };
            var marker = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(color),
                Stroke = Brushes.White,
                StrokeThickness = 1.2
            };

            LadderCanvas.Children.Add(poly);
            LadderCanvas.Children.Add(marker);

            _activeTracks.Add(new AnimatedLadderTrack
            {
                Line = poly,
                Marker = marker,
                FullPoints = fullPoints,
                TotalLength = Math.Max(1.0, totalLen)
            });
        }

        _ladderAnimProgress = 0.0;
        _ladderAnimTimer.Start();
    }

    private void OnLadderAnimTick(object? sender, EventArgs e)
    {
        // 약 1.6초 동안 위에서 아래로 동시 하강
        _ladderAnimProgress = Math.Min(1.0, _ladderAnimProgress + 0.011);

        foreach (var track in _activeTracks)
        {
            var targetDist = track.TotalLength * _ladderAnimProgress;
            var pts = new PointCollection { track.FullPoints[0] };
            var head = track.FullPoints[0];
            var accum = 0.0;

            for (var i = 1; i < track.FullPoints.Count; i++)
            {
                var p0 = track.FullPoints[i - 1];
                var p1 = track.FullPoints[i];
                var segLen = (p1 - p0).Length;
                if (accum + segLen <= targetDist)
                {
                    pts.Add(p1);
                    head = p1;
                    accum += segLen;
                }
                else
                {
                    var remain = Math.Max(0, targetDist - accum);
                    var ratio = segLen > 0 ? remain / segLen : 0;
                    head = new Point(p0.X + (p1.X - p0.X) * ratio, p0.Y + (p1.Y - p0.Y) * ratio);
                    pts.Add(head);
                    break;
                }
            }

            track.Line.Points = pts;
            Canvas.SetLeft(track.Marker, head.X - 4);
            Canvas.SetTop(track.Marker, head.Y - 4);
        }

        if (_ladderAnimProgress >= 1.0)
        {
            _ladderAnimTimer.Stop();
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
