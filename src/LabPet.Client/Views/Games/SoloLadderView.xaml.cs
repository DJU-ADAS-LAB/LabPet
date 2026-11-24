using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LabPet.Client.Games;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views.Games;

public partial class SoloLadderView : UserControl
{
    private static readonly SolidColorBrush RailBrush = CreateFrozenBrush(Color.FromRgb(218, 222, 230));
    private static readonly SolidColorBrush BridgeBrush = CreateFrozenBrush(Color.FromRgb(185, 193, 206));
    private static readonly SolidColorBrush ActivePathBrush = CreateFrozenBrush(Color.FromRgb(255, 77, 130));
    private static readonly SolidColorBrush HeaderBgBrush = CreateFrozenBrush(Color.FromRgb(255, 255, 255));
    private static readonly SolidColorBrush HeaderBorderBrush = CreateFrozenBrush(Color.FromRgb(232, 236, 242));
    private static readonly SolidColorBrush HiddenPillBgBrush = CreateFrozenBrush(Color.FromRgb(242, 244, 246));
    private static readonly SolidColorBrush HiddenPillFgBrush = CreateFrozenBrush(Color.FromRgb(107, 114, 128));
    private static readonly SolidColorBrush JackpotPillBgBrush = CreateFrozenBrush(Color.FromRgb(255, 240, 244));
    private static readonly SolidColorBrush RefundPillBgBrush = CreateFrozenBrush(Color.FromRgb(239, 246, 255));
    private static readonly SolidColorBrush RefundPillFgBrush = CreateFrozenBrush(Color.FromRgb(37, 99, 235));

    private readonly DispatcherTimer _animTimer;
    private readonly List<Point> _tracePoints = [];
    private readonly List<Border> _bottomPills = [];
    private readonly List<TextBlock> _bottomLabels = [];
    private CoinGamesViewModel? _boundVm;
    private SoloLadderPersistedSession? _currentSession;
    private Polyline? _traceLine;
    private Ellipse? _marker;
    private double _elapsedDist;
    private double _stepDist;
    private double _totalDist = 1.0;
    private int _endSlot;
    private bool _arrived;

    public SoloLadderView()
    {
        InitializeComponent();
        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animTimer.Tick += OnAnimTick;

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
        if (_boundVm is { IsSoloLadderRunning: true } && _animTimer.IsEnabled && _totalDist > 0)
        {
            _boundVm.SaveSoloLadderAnimationProgress(_elapsedDist / _totalDist);
        }

        StopAndClearCanvas();
    }

    private void AttachVmAndSync()
    {
        if (_boundVm is null)
        {
            return;
        }

        _boundVm.SoloLadderSessionStarted += OnSoloSessionStarted;
        _boundVm.SoloLadderResetRequested += OnSoloResetRequested;
        _boundVm.PropertyChanged += OnViewModelPropertyChanged;

        if (IsLoaded && IsVisible && _boundVm.IsSoloLadderRunning && _boundVm.ActiveSoloLadderSession is { } session)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsLoaded && IsVisible && _boundVm is { IsSoloLadderRunning: true } vm)
                {
                    DrawAndAnimateSession(session, vm.ActiveSoloLadderAnimationProgress, vm.MyPetPortrait);
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

        _boundVm.SoloLadderSessionStarted -= OnSoloSessionStarted;
        _boundVm.SoloLadderResetRequested -= OnSoloResetRequested;
        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm is null || !IsLoaded || !IsVisible)
        {
            return;
        }

        if (e.PropertyName == nameof(CoinGamesViewModel.SoloLadderPhase)
            && _boundVm.IsSoloLadderRunning
            && _boundVm.ActiveSoloLadderSession is { } session
            && _tracePoints.Count == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsLoaded && IsVisible && _boundVm is { IsSoloLadderRunning: true } && _tracePoints.Count == 0)
                {
                    DrawAndAnimateSession(session, _boundVm.ActiveSoloLadderAnimationProgress, _boundVm.MyPetPortrait);
                }
            });
        }
    }

    private void OnSoloSessionStarted(object? sender, SoloLadderPersistedSession session)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsLoaded && IsVisible && _boundVm is { IsSoloLadderRunning: true })
            {
                DrawAndAnimateSession(session, _boundVm.ActiveSoloLadderAnimationProgress, _boundVm.MyPetPortrait);
            }
        });
    }

    private void OnSoloResetRequested(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(StopAndClearCanvas);

    private void SoloLadderCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || _boundVm is not { IsSoloLadderRunning: true } || _currentSession is null || _tracePoints.Count == 0)
        {
            return;
        }

        var ratio = _totalDist > 0 ? Math.Clamp(_elapsedDist / _totalDist, 0.0, 0.999) : 0.0;
        DrawAndAnimateSession(_currentSession, ratio, _boundVm.MyPetPortrait);
    }

    private void StopAndClearCanvas()
    {
        _animTimer.Stop();
        _tracePoints.Clear();
        _bottomPills.Clear();
        _bottomLabels.Clear();
        _traceLine = null;
        _marker = null;
        _currentSession = null;
        _arrived = false;
        SoloLadderCanvas.Children.Clear();
    }

    private void DrawAndAnimateSession(SoloLadderPersistedSession session, double resumeProgress, ImageSource? myPortrait)
    {
        _animTimer.Stop();
        _tracePoints.Clear();
        _bottomPills.Clear();
        _bottomLabels.Clear();
        _traceLine = null;
        _marker = null;
        _currentSession = session;
        _arrived = false;
        SoloLadderCanvas.Children.Clear();

        var w = SoloLadderCanvas.ActualWidth > 80 ? SoloLadderCanvas.ActualWidth : 600;
        var h = SoloLadderCanvas.ActualHeight > 80 ? SoloLadderCanvas.ActualHeight : 290;
        var padX = Math.Clamp(w * 0.14, 52.0, 92.0);
        var chipWidth = Math.Clamp((w - padX * 1.3) / SoloLadderEngine.SlotCount - 10.0, 74.0, 128.0);
        var topY = 44.0;
        var bottomY = Math.Max(topY + 80.0, h - 42.0);
        var colStep = (w - padX * 2) / (SoloLadderEngine.SlotCount - 1);

        double ColX(double col) => padX + col * colStep;
        double NormY(double y) => topY + y * (bottomY - topY);

        for (var col = 0; col < SoloLadderEngine.SlotCount; col++)
        {
            var x = ColX(col);
            var isSelected = col == session.SelectedSlot;

            SoloLadderCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = topY,
                X2 = x,
                Y2 = bottomY,
                Stroke = RailBrush,
                StrokeThickness = 3.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            var topContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isSelected && myPortrait is not null)
            {
                topContent.Children.Add(new Image
                {
                    Source = myPortrait,
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(0, 0, 5, 0)
                });
            }

            topContent.Children.Add(new TextBlock
            {
                Text = isSelected ? $"{col + 1}번 (나)" : $"{col + 1}번",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = isSelected ? ActivePathBrush : HiddenPillFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var topChip = new Border
            {
                Width = chipWidth,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = isSelected ? JackpotPillBgBrush : HeaderBgBrush,
                BorderBrush = isSelected ? ActivePathBrush : HeaderBorderBrush,
                BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                Child = topContent
            };
            Canvas.SetLeft(topChip, Math.Clamp(x - chipWidth / 2.0, 4, Math.Max(4, w - chipWidth - 4)));
            Canvas.SetTop(topChip, 6);
            SoloLadderCanvas.Children.Add(topChip);

            var botText = new TextBlock
            {
                Text = "?",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = HiddenPillFgBrush,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var botPill = new Border
            {
                Width = chipWidth,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = HiddenPillBgBrush,
                BorderBrush = HeaderBorderBrush,
                BorderThickness = new Thickness(1),
                Child = botText
            };
            Canvas.SetLeft(botPill, Math.Clamp(x - chipWidth / 2.0, 4, Math.Max(4, w - chipWidth - 4)));
            Canvas.SetTop(botPill, bottomY + 8);
            SoloLadderCanvas.Children.Add(botPill);
            _bottomPills.Add(botPill);
            _bottomLabels.Add(botText);
        }

        foreach (var bridge in session.Bridges)
        {
            var y = NormY(bridge.NormalizedY);
            SoloLadderCanvas.Children.Add(new Line
            {
                X1 = ColX(bridge.LeftColumn),
                Y1 = y,
                X2 = ColX(bridge.LeftColumn + 1),
                Y2 = y,
                Stroke = BridgeBrush,
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        var (endSlot, rawPath) = SoloLadderEngine.TraceSinglePath(session.Bridges, session.SelectedSlot);
        _endSlot = endSlot;
        foreach (var pt in rawPath)
        {
            _tracePoints.Add(new Point(ColX(pt.Column), NormY(pt.Level)));
        }

        _totalDist = ComputePolylineLength(_tracePoints);
        _elapsedDist = Math.Clamp(resumeProgress, 0.0, 0.999) * _totalDist;
        _stepDist = Math.Max(4.0, _totalDist / 72.0);

        _traceLine = new Polyline
        {
            Stroke = ActivePathBrush,
            StrokeThickness = 4.2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        SoloLadderCanvas.Children.Add(_traceLine);

        _marker = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = ActivePathBrush,
            Stroke = Brushes.White,
            StrokeThickness = 2.4
        };
        SoloLadderCanvas.Children.Add(_marker);

        RenderTraceAtDistance(_elapsedDist);
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_traceLine is null || _marker is null)
        {
            _animTimer.Stop();
            return;
        }

        _elapsedDist += _stepDist;
        RenderTraceAtDistance(_elapsedDist);

        if (_elapsedDist >= _totalDist && !_arrived)
        {
            _arrived = true;
            _animTimer.Stop();
            RevealBottomOutcomes();
            if (_boundVm is not null)
            {
                _ = _boundVm.CompleteSoloLadderTraceAsync();
            }
        }
    }

    private void RenderTraceAtDistance(double dist)
    {
        if (_traceLine is null || _marker is null || _tracePoints.Count < 2)
        {
            return;
        }

        var clamped = Math.Clamp(dist, 0, _totalDist);
        var pts = new PointCollection { _tracePoints[0] };
        var remaining = clamped;
        var head = _tracePoints[0];

        for (var i = 1; i < _tracePoints.Count; i++)
        {
            var a = _tracePoints[i - 1];
            var b = _tracePoints[i];
            var segLen = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            if (remaining >= segLen - 0.001)
            {
                pts.Add(b);
                head = b;
                remaining -= segLen;
            }
            else if (segLen > 0.001)
            {
                var t = remaining / segLen;
                head = new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                pts.Add(head);
                break;
            }
        }

        _traceLine.Points = pts;
        Canvas.SetLeft(_marker, head.X - _marker.Width / 2.0);
        Canvas.SetTop(_marker, head.Y - _marker.Height / 2.0);
    }

    private void RevealBottomOutcomes()
    {
        if (_currentSession is null)
        {
            return;
        }

        for (var i = 0; i < Math.Min(_bottomPills.Count, SoloLadderEngine.SlotCount); i++)
        {
            var key = _currentSession.SlotOutcomeKeys[i];
            var mult = _currentSession.SlotMultipliersPercent[i];
            _bottomLabels[i].Text = SoloLadderEngine.GetOutcomeDisplayLabel(key, mult);
            var isLanded = i == _endSlot;

            (_bottomPills[i].Background, _bottomPills[i].BorderBrush, _bottomLabels[i].Foreground) = key switch
            {
                "jackpot" => (JackpotPillBgBrush, ActivePathBrush, ActivePathBrush),
                "refund" => (RefundPillBgBrush, RefundPillFgBrush, RefundPillFgBrush),
                _ => (HiddenPillBgBrush, HeaderBorderBrush, HiddenPillFgBrush)
            };
            _bottomPills[i].BorderThickness = new Thickness(isLanded ? 2 : 1);
        }
    }

    private static double ComputePolylineLength(IReadOnlyList<Point> points)
    {
        var sum = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            sum += Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
        }

        return Math.Max(1.0, sum);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
