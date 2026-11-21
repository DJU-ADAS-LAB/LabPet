using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LabPet.Client.Games;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Games;

public partial class LadderGameView : UserControl
{
    private static readonly Color[] TrackPalette =
    [
        Color.FromRgb(255, 77, 130),
        Color.FromRgb(79, 70, 229),
        Color.FromRgb(16, 185, 129),
        Color.FromRgb(245, 158, 11),
        Color.FromRgb(14, 165, 233),
        Color.FromRgb(168, 85, 247),
        Color.FromRgb(239, 68, 68),
        Color.FromRgb(20, 184, 166)
    ];

    private static readonly SolidColorBrush RailBrush = CreateFrozenBrush(Color.FromRgb(218, 222, 230));
    private static readonly SolidColorBrush HiddenPrizeBgBrush = CreateFrozenBrush(Color.FromRgb(242, 244, 246));
    private static readonly SolidColorBrush HiddenPrizeFgBrush = CreateFrozenBrush(Color.FromRgb(107, 114, 128));
    private static readonly SolidColorBrush HeaderBgBrush = CreateFrozenBrush(Color.FromRgb(255, 255, 255));
    private static readonly SolidColorBrush HeaderBorderBrush = CreateFrozenBrush(Color.FromRgb(232, 236, 242));

    private sealed class AnimatedLadderTrack
    {
        public required int StartColumn { get; init; }
        public required int EndColumn { get; init; }
        public required string Prize { get; init; }
        public required SolidColorBrush TrackBrush { get; init; }
        public required Polyline Line { get; init; }
        public required Ellipse Marker { get; init; }
        public required List<Point> FullPoints { get; init; }
        public required double TotalLength { get; init; }
        public bool Arrived { get; set; }
    }

    private readonly DispatcherTimer _ladderAnimTimer;
    private readonly List<AnimatedLadderTrack> _activeTracks = [];
    private readonly List<Border> _bottomPrizePills = [];
    private readonly List<TextBlock> _bottomPrizeTexts = [];
    private CoinGamesViewModel? _boundVm;
    private LadderBoard? _currentBoard;
    private double _ladderElapsedDist;
    private double _ladderStepDist;
    private double _maxTrackLength = 1.0;

    public LadderGameView()
    {
        InitializeComponent();
        _ladderAnimTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _ladderAnimTimer.Tick += OnLadderAnimTick;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SaveProgressAndStopIfRunning();
        DetachVm();
        _boundVm = e.NewValue as CoinGamesViewModel;
        if (IsLoaded && IsVisible && _boundVm is not null)
        {
            AttachVmAndSync();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            DetachVm();
            AttachVmAndSync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SaveProgressAndStopIfRunning();
        DetachVm();
    }

    private void SaveProgressAndStopIfRunning()
    {
        // 창을 닫거나 다른 화면으로 전환해도 진행률만 보존하고 절대 강제 공개·정산하지 않는다.
        if (_boundVm is { IsLadderAnimating: true } && _ladderAnimTimer.IsEnabled && _maxTrackLength > 0)
        {
            _boundVm.SaveLadderAnimationProgress(_ladderElapsedDist / _maxTrackLength);
        }

        StopAndClearCanvas();
    }

    private void AttachVmAndSync()
    {
        if (_boundVm is null)
        {
            return;
        }

        _boundVm.LadderBoardGenerated += OnLadderBoardGenerated;
        _boundVm.LadderAnimationCancelled += OnLadderAnimationCancelled;
        _boundVm.PropertyChanged += OnViewModelPropertyChanged;

        if (IsLoaded && IsVisible && _boundVm.IsLadderAnimating && _boundVm.ActiveLadderBoard is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsLoaded && IsVisible && _boundVm is { IsLadderAnimating: true } vm && vm.ActiveLadderBoard is { } board)
                {
                    DrawAndAnimateLadderBoard(
                        board,
                        vm.ActiveLadderAnimationProgress,
                        vm.ActiveLadderRevealedColumns,
                        vm.ActiveLadderParticipantPortraits);
                }
            });
        }
    }

    private void DetachVm()
    {
        if (_boundVm is null)
        {
            return;
        }

        _boundVm.LadderBoardGenerated -= OnLadderBoardGenerated;
        _boundVm.LadderAnimationCancelled -= OnLadderAnimationCancelled;
        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm is null || !IsLoaded || !IsVisible)
        {
            return;
        }

        if (e.PropertyName == nameof(CoinGamesViewModel.LadderStage)
            && _boundVm.LadderStage == LadderStage.Running
            && _boundVm.IsLadderAnimating
            && _boundVm.ActiveLadderBoard is { } board
            && _activeTracks.Count == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsLoaded && IsVisible && _boundVm is { IsLadderAnimating: true } && _activeTracks.Count == 0)
                {
                    DrawAndAnimateLadderBoard(
                        board,
                        _boundVm.ActiveLadderAnimationProgress,
                        _boundVm.ActiveLadderRevealedColumns,
                        _boundVm.ActiveLadderParticipantPortraits);
                }
            });
        }
    }

    private void OnLadderBoardGenerated(object? sender, LadderBoard board)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsLoaded && IsVisible && _boundVm is { IsLadderAnimating: true })
            {
                DrawAndAnimateLadderBoard(
                    board,
                    _boundVm.ActiveLadderAnimationProgress,
                    _boundVm.ActiveLadderRevealedColumns,
                    _boundVm.ActiveLadderParticipantPortraits);
            }
        });
    }

    private void OnLadderAnimationCancelled(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(StopAndClearCanvas);

    private void LadderCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || _boundVm is not { IsLadderAnimating: true } || _currentBoard is null || _activeTracks.Count == 0)
        {
            return;
        }

        var currentRatio = _maxTrackLength > 0 ? Math.Clamp(_ladderElapsedDist / _maxTrackLength, 0.0, 0.999) : 0.0;
        DrawAndAnimateLadderBoard(
            _currentBoard,
            currentRatio,
            _boundVm.ActiveLadderRevealedColumns,
            _boundVm.ActiveLadderParticipantPortraits);
    }

    private void StopAndClearCanvas()
    {
        _ladderAnimTimer.Stop();
        _activeTracks.Clear();
        _bottomPrizePills.Clear();
        _bottomPrizeTexts.Clear();
        _currentBoard = null;
        LadderCanvas.Children.Clear();
    }

    private void DrawAndAnimateLadderBoard(
        LadderBoard board,
        double resumeProgress,
        IReadOnlyList<bool> revealedColumns,
        IReadOnlyList<ImageSource?> portraits)
    {
        _ladderAnimTimer.Stop();
        _activeTracks.Clear();
        _bottomPrizePills.Clear();
        _bottomPrizeTexts.Clear();
        _currentBoard = board;
        LadderCanvas.Children.Clear();

        var w = LadderCanvas.ActualWidth > 60 ? LadderCanvas.ActualWidth : 640;
        var h = LadderCanvas.ActualHeight > 80 ? LadderCanvas.ActualHeight : 320;
        var count = Math.Max(2, board.Participants.Count);

        var roughStep = (w - 92.0) / Math.Max(1, count - 1);
        var padX = Math.Clamp(roughStep * 0.45, 36.0, 62.0);
        var chipWidth = Math.Clamp((w - padX * 1.4) / count - 6.0, 52.0, 108.0);
        var topY = 44.0;
        var bottomY = Math.Max(topY + 80.0, h - 40.0);
        var colStep = count > 1 ? (w - padX * 2) / (count - 1) : 0;
        var levelStep = (bottomY - topY) / board.Levels;

        double ColX(double col) => padX + col * colStep;
        double LevelY(double lv) => topY + lv * levelStep;

        // 1. 세로줄 & 상단 참가자 칩(미니 초상 포함) + 하단 "?" 비공개 칩
        for (var col = 0; col < count; col++)
        {
            var x = ColX(col);
            var trackColor = TrackPalette[col % TrackPalette.Length];
            var trackBrush = CreateFrozenBrush(trackColor);

            LadderCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = topY,
                X2 = x,
                Y2 = bottomY,
                Stroke = RailBrush,
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            var portrait = col < portraits.Count ? portraits[col] : null;
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (portrait is not null && chipWidth >= 70)
            {
                headerPanel.Children.Add(new Image
                {
                    Source = portrait,
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(0, 0, 4, 0),
                    Stretch = Stretch.Uniform
                });
            }
            else
            {
                headerPanel.Children.Add(new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = trackBrush,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            headerPanel.Children.Add(new TextBlock
            {
                Text = board.Participants[col],
                FontSize = count >= 7 ? 10.5 : 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = trackBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = chipWidth - (portrait is not null && chipWidth >= 70 ? 28 : 20),
                VerticalAlignment = VerticalAlignment.Center
            });

            var topChip = new Border
            {
                Width = chipWidth,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = HeaderBgBrush,
                BorderBrush = HeaderBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = board.Participants[col],
                Child = headerPanel
            };
            Canvas.SetLeft(topChip, Math.Clamp(x - chipWidth / 2.0, 2, Math.Max(2, w - chipWidth - 2)));
            Canvas.SetTop(topChip, 6);
            LadderCanvas.Children.Add(topChip);

            var botText = new TextBlock
            {
                Text = "?",
                FontSize = count >= 7 ? 10.5 : 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = HiddenPrizeFgBrush,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var botPill = new Border
            {
                Width = chipWidth,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = HiddenPrizeBgBrush,
                BorderBrush = HeaderBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                Child = botText
            };
            Canvas.SetLeft(botPill, Math.Clamp(x - chipWidth / 2.0, 2, Math.Max(2, w - chipWidth - 2)));
            Canvas.SetTop(botPill, bottomY + 7);
            LadderCanvas.Children.Add(botPill);
            _bottomPrizePills.Add(botPill);
            _bottomPrizeTexts.Add(botText);
        }

        // 2. 가로줄
        foreach (var rung in board.Rungs)
        {
            var y = LevelY(rung.Level);
            LadderCanvas.Children.Add(new Line
            {
                X1 = ColX(rung.LeftColumn),
                Y1 = y,
                X2 = ColX(rung.LeftColumn + 1),
                Y2 = y,
                Stroke = RailBrush,
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        // 3. 경로 애니메이션 트랙 생성
        var traces = LabLadderEngine.TraceAll(board);
        var maxLength = 1.0;
        for (var i = 0; i < traces.Count; i++)
        {
            var trace = traces[i];
            var trackColor = TrackPalette[i % TrackPalette.Length];
            var trackBrush = CreateFrozenBrush(trackColor);
            var lineBrush = CreateFrozenBrush(Color.FromArgb(225, trackColor.R, trackColor.G, trackColor.B));

            var fullPoints = trace.Path.Select(pt => new Point(ColX(pt.Column), LevelY(pt.Level))).ToList();
            var totalLen = 0.0;
            for (var seg = 1; seg < fullPoints.Count; seg++)
            {
                totalLen += (fullPoints[seg] - fullPoints[seg - 1]).Length;
            }

            totalLen = Math.Max(1.0, totalLen);
            if (totalLen > maxLength)
            {
                maxLength = totalLen;
            }

            var poly = new Polyline
            {
                Stroke = lineBrush,
                StrokeThickness = 3.0,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            var marker = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = trackBrush,
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };

            LadderCanvas.Children.Add(poly);
            LadderCanvas.Children.Add(marker);

            var alreadyRevealed = i < revealedColumns.Count && revealedColumns[i];
            var track = new AnimatedLadderTrack
            {
                StartColumn = trace.StartColumn,
                EndColumn = trace.EndColumn,
                Prize = trace.Prize,
                TrackBrush = trackBrush,
                Line = poly,
                Marker = marker,
                FullPoints = fullPoints,
                TotalLength = totalLen,
                Arrived = alreadyRevealed
            };
            _activeTracks.Add(track);

            if (alreadyRevealed)
            {
                RevealBottomPrizePill(track);
            }
        }

        _maxTrackLength = maxLength;
        _ladderElapsedDist = Math.Clamp(resumeProgress, 0.0, 0.999) * maxLength;
        // 33ms 틱 기준 ~2.4초에 완주
        _ladderStepDist = maxLength / 72.0;

        RenderCurrentTrackPositions(triggerRevealNotifications: false);
        _ladderAnimTimer.Start();
    }

    private void OnLadderAnimTick(object? sender, EventArgs e)
    {
        _ladderElapsedDist += _ladderStepDist;
        var allArrived = RenderCurrentTrackPositions(triggerRevealNotifications: true);
        if (allArrived)
        {
            _ladderAnimTimer.Stop();
            if (_boundVm is not null)
            {
                _ = _boundVm.CompleteLadderRevealAsync();
            }
        }
    }

    private bool RenderCurrentTrackPositions(bool triggerRevealNotifications)
    {
        var allArrived = true;

        foreach (var track in _activeTracks)
        {
            var targetDist = track.Arrived ? track.TotalLength : Math.Min(track.TotalLength, _ladderElapsedDist);
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
            Canvas.SetLeft(track.Marker, head.X - 5);
            Canvas.SetTop(track.Marker, head.Y - 5);

            if (!track.Arrived && _ladderElapsedDist >= track.TotalLength - 1e-6)
            {
                track.Arrived = true;
                RevealBottomPrizePill(track);
                if (triggerRevealNotifications)
                {
                    _boundVm?.RevealLadderParticipantResult(track.StartColumn);
                }
            }

            if (!track.Arrived)
            {
                allArrived = false;
            }
        }

        return allArrived;
    }

    private void RevealBottomPrizePill(AnimatedLadderTrack track)
    {
        if (track.EndColumn < 0 || track.EndColumn >= _bottomPrizeTexts.Count)
        {
            return;
        }

        var botText = _bottomPrizeTexts[track.EndColumn];
        var botPill = _bottomPrizePills[track.EndColumn];
        botText.Text = track.Prize;
        botText.Foreground = track.TrackBrush;
        botPill.Background = HeaderBgBrush;
        botPill.BorderBrush = track.TrackBrush;
        botPill.ToolTip = $"{track.Prize}";
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
