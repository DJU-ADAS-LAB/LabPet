using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

internal readonly record struct PetWindowDisplayMetrics(
    double NormalizedScale,
    double NormalizedOpacity,
    Size ViewportSize,
    Size WindowSize);

/// <summary>
/// 내 펫 창(<see cref="PetWindow"/>)과 방문 펫 창(<see cref="VisitorPetWindow"/>)이
/// 동일한 3D 뷰포트 크기·창 크기·투명도·하단 네임텍 위치를 공유하도록 계산하는 공통 헬퍼.
/// </summary>
internal static class PetDisplayLayoutHelper
{
    public const double BaseWindowWidth = 214.0;
    public const double BaseViewportHeight = 224.0;
    public const double MinimumWindowWidth = 180.0;
    public const double NameplateRowHeight = 28.0;
    public const double NameplateGap = 4.0;
    public const double MinPetOpacity = 0.25;
    public const double MaxPetOpacity = 1.0;

    /// <summary>
    /// 타이핑 미니 키보드(Y=-0.80, Z=1.04)와 바닥 회전 링 오라(Y=-0.78, Z=1.24)의 앞쪽 하단 기준점.
    /// 네임태그가 캐릭터 발바닥뿐 아니라 키보드·오라 효과도 가리지 않도록 함께 비교한다.
    /// </summary>
    public static readonly Point3D KeyboardAuraAnchor = new(0, -0.81, 1.12);

    public static double NormalizeOpacity(double opacity)
        => Math.Clamp(double.IsFinite(opacity) ? opacity : MaxPetOpacity, MinPetOpacity, MaxPetOpacity);

    public static Size ComputeViewportSize(double scale)
    {
        var clamped = PetProfile.NormalizePetScale(scale);
        return new Size(Math.Round(BaseWindowWidth * clamped), Math.Round(BaseViewportHeight * clamped));
    }

    public static PetWindowDisplayMetrics ComputeDisplayMetrics(
        double scale,
        double opacity,
        double nameplateRowHeight = NameplateRowHeight)
    {
        var clampedScale = PetProfile.NormalizePetScale(scale);
        var clampedOpacity = NormalizeOpacity(opacity);
        var viewportSize = ComputeViewportSize(clampedScale);
        var effectiveRowHeight = double.IsFinite(nameplateRowHeight) && nameplateRowHeight > 0
            ? nameplateRowHeight
            : NameplateRowHeight;
        var windowSize = new Size(
            Math.Max(MinimumWindowWidth, viewportSize.Width),
            viewportSize.Height + effectiveRowHeight);
        return new PetWindowDisplayMetrics(clampedScale, clampedOpacity, viewportSize, windowSize);
    }

    public static PetWindowDisplayMetrics ApplyScaleAndSize(
        Window window,
        FrameworkElement viewport,
        double scale,
        double opacity,
        double nameplateRowHeight = NameplateRowHeight,
        double horizontalAnchorRatio = 0.5)
    {
        var metrics = ComputeDisplayMetrics(scale, opacity, nameplateRowHeight);
        var previousWidth = double.IsFinite(window.Width) && window.Width > 0 ? window.Width : window.ActualWidth;
        var previousHeight = double.IsFinite(window.Height) && window.Height > 0 ? window.Height : window.ActualHeight;

        // 모든 배율에서 동일한 3D 뷰포트 종횡비를 유지해 상단(모자)·하단(키보드)이 잘리지 않도록 한다.
        viewport.Width = metrics.ViewportSize.Width;
        viewport.Height = metrics.ViewportSize.Height;
        window.Width = metrics.WindowSize.Width;
        window.Height = metrics.WindowSize.Height;

        if (window.IsLoaded && double.IsFinite(window.Left) && double.IsFinite(window.Top))
        {
            window.Left += (previousWidth - window.Width) * horizontalAnchorRatio;
            window.Top += previousHeight - window.Height;
        }

        return metrics;
    }

    public static bool TryComputeNameplateOffsetY(
        Viewport3D viewport,
        ModelVisual3D petVisual,
        Point3D nameplateAnchor,
        out double offsetDip)
    {
        offsetDip = 0;
        if (viewport.ActualHeight <= 0 || petVisual.Content is null)
        {
            return false;
        }

        var transform = petVisual.TransformToAncestor(viewport);
        if (!transform.TryTransform(nameplateAnchor, out var bodyAnchor))
        {
            return false;
        }

        var lowestY = bodyAnchor.Y;
        if (transform.TryTransform(KeyboardAuraAnchor, out var effectAnchor))
        {
            lowestY = Math.Max(lowestY, effectAnchor.Y);
        }

        offsetDip = Math.Min(0, lowestY + NameplateGap - viewport.ActualHeight);
        return true;
    }
}
