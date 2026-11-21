using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabPet.Client.Interop;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

public partial class GameInviteCardWindow : Window
{
    private Window? _anchorPetWindow;

    public GameInviteCardWindow(CoinGamesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => Win32WindowHelper.ApplyNonActivatingToolWindowStyle(this);
        Loaded += (_, _) => AnimateEntrance();
        SizeChanged += (_, _) =>
        {
            if (IsVisible && _anchorPetWindow is not null)
            {
                PositionNearPet(_anchorPetWindow);
            }
        };
        Closed += (_, _) => _anchorPetWindow = null;
    }

    public void PositionNearPet(Window petWindow)
    {
        _anchorPetWindow = petWindow;
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

        var origin = CalculatePlacement(petBounds, cardPhysicalSize, workArea, gapPhysical);
        Win32WindowHelper.MoveWindowPhysical(this, (int)Math.Round(origin.X), (int)Math.Round(origin.Y));
    }

    internal void ReplayEntrance()
    {
        if (IsLoaded)
        {
            AnimateEntrance();
        }
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
    /// 펫 창의 물리 픽셀 경계를 기준으로 왼쪽 -> 오른쪽 -> 위 -> 아래 순으로 후보 위치를 계산하고,
    /// 모니터 작업 영역을 벗어나지 않도록 보정한다.
    /// </summary>
    internal static Point CalculatePlacement(
        Rect petPhysicalBounds,
        Size cardPhysicalSize,
        Rect workAreaPhysical,
        double gapPhysical = 12.0)
    {
        var alignedY = petPhysicalBounds.Top + (petPhysicalBounds.Height - cardPhysicalSize.Height) * 0.28;
        var centeredX = petPhysicalBounds.Left + (petPhysicalBounds.Width - cardPhysicalSize.Width) * 0.5;

        var candidates = new[]
        {
            new Point(petPhysicalBounds.Left - cardPhysicalSize.Width - gapPhysical, alignedY),
            new Point(petPhysicalBounds.Right + gapPhysical, alignedY),
            new Point(centeredX, petPhysicalBounds.Top - cardPhysicalSize.Height - gapPhysical),
            new Point(centeredX, petPhysicalBounds.Bottom + gapPhysical)
        };

        foreach (var candidate in candidates)
        {
            var rect = new Rect(candidate, cardPhysicalSize);
            if (workAreaPhysical.Contains(rect))
            {
                return candidate;
            }
        }

        var maxLeft = Math.Max(workAreaPhysical.Left, workAreaPhysical.Right - cardPhysicalSize.Width);
        var maxTop = Math.Max(workAreaPhysical.Top, workAreaPhysical.Bottom - cardPhysicalSize.Height);
        var fallback = candidates[0].X >= workAreaPhysical.Left - 8 ? candidates[0] : candidates[1];

        return new Point(
            Math.Clamp(fallback.X, workAreaPhysical.Left, maxLeft),
            Math.Clamp(fallback.Y, workAreaPhysical.Top, maxTop));
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
