using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabPet.Client.Interop;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

public partial class GameInviteCardWindow : Window
{
    internal event EventHandler? CardSizeChanged;

    public GameInviteCardWindow(CoinGamesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => Win32WindowHelper.ApplyNonActivatingToolWindowStyle(this);
        Loaded += (_, _) => AnimateEntrance();
        SizeChanged += (_, _) => CardSizeChanged?.Invoke(this, EventArgs.Empty);
        DpiChanged += (_, _) => CardSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void PositionNearPet(Window petWindow, Rect obstacle, Point? head)
    {
        if (!Win32WindowHelper.TryGetWindowPhysicalBounds(petWindow, out var petBounds))
        {
            return;
        }

        var workArea = Win32WindowHelper.TryGetNearestWorkArea(petWindow, out var area)
            ? area
            : GetScaledPrimaryWorkArea();

        var dpi = VisualTreeHelper.GetDpi(this);
        var logicalSize = ActualWidth > 0 && ActualHeight > 0
            ? new Size(ActualWidth, ActualHeight)
            : (DesiredSize.Width > 0 ? DesiredSize : new Size(316, 210));
        var cardPhysicalSize = new Size(logicalSize.Width * dpi.DpiScaleX, logicalSize.Height * dpi.DpiScaleY);
        var gapPhysical = 10.0 * dpi.DpiScaleX;

        var origin = CalculatePlacement(petBounds, cardPhysicalSize, workArea, gapPhysical, obstacle, head);
        var x = (int)Math.Round(origin.X);
        var y = (int)Math.Round(origin.Y);
        if (Win32WindowHelper.TryGetWindowPhysicalBounds(this, out var current) &&
            (current.Left != x || current.Top != y))
            Win32WindowHelper.MoveWindowPhysical(this, x, y);
    }

    private void AnimateEntrance()
    {
        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        InviteCardRoot.BeginAnimation(OpacityProperty, fade);
        CardSlideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// 펫 창의 물리 픽셀 경계를 기준으로 위 → 왼쪽 → 오른쪽 → 아래 순으로 후보 위치를 계산하고,
    /// 모니터 작업 영역을 벗어나지 않도록 보정한다.
    /// </summary>
    internal static Point CalculatePlacement(
        Rect petPhysicalBounds,
        Size cardPhysicalSize,
        Rect workAreaPhysical,
        double gapPhysical = 12.0,
        Rect? obstacle = null,
        Point? head = null)
    {
        var alignedY = petPhysicalBounds.Top + (petPhysicalBounds.Height - cardPhysicalSize.Height) * 0.28;
        var centeredX = head is { } anchor ? anchor.X - cardPhysicalSize.Width / 2
            : petPhysicalBounds.Left + (petPhysicalBounds.Width - cardPhysicalSize.Width) * 0.5;
        var topY = head?.Y ?? petPhysicalBounds.Top;

        var candidates = new[]
        {
            new Point(centeredX, topY - cardPhysicalSize.Height - gapPhysical),
            new Point(petPhysicalBounds.Left - cardPhysicalSize.Width - gapPhysical, alignedY),
            new Point(petPhysicalBounds.Right + gapPhysical, alignedY),
            new Point(centeredX, petPhysicalBounds.Bottom + gapPhysical)
        };

        foreach (var candidate in candidates)
        {
            var rect = new Rect(candidate, cardPhysicalSize);
            if (workAreaPhysical.Contains(rect) && !rect.IntersectsWith(obstacle ?? Rect.Empty))
            {
                return candidate;
            }
        }

        var maxLeft = Math.Max(workAreaPhysical.Left, workAreaPhysical.Right - cardPhysicalSize.Width);
        var maxTop = Math.Max(workAreaPhysical.Top, workAreaPhysical.Bottom - cardPhysicalSize.Height);
        return candidates.Select(p => new Point(
                Math.Clamp(p.X, workAreaPhysical.Left, maxLeft),
                Math.Clamp(p.Y, workAreaPhysical.Top, maxTop)))
            .MinBy(p =>
            {
                var bounds = new Rect(p, cardPhysicalSize);
                var overlap = Rect.Intersect(bounds, obstacle ?? Rect.Empty);
                var petOverlap = Rect.Intersect(bounds, petPhysicalBounds);
                return (overlap.IsEmpty ? 0 : overlap.Width * overlap.Height)
                    + (petOverlap.IsEmpty ? 0 : petOverlap.Width * petOverlap.Height);
            });
    }

    private Rect GetScaledPrimaryWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var wa = SystemParameters.WorkArea;
        return new Rect(
            wa.Left * dpi.DpiScaleX,
            wa.Top * dpi.DpiScaleY,
            wa.Width * dpi.DpiScaleX,
            wa.Height * dpi.DpiScaleY);
    }
}
