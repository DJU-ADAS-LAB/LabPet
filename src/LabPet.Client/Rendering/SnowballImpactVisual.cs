using System.Windows;
using System.Windows.Media;

namespace LabPet.Client.Rendering;

/// <summary>공용 UI 갱신 루프가 진행시키는 DIP 좌표의 명중 효과. 자체 창·타이머·이벤트 구독 없음.</summary>
internal sealed class SnowballImpactVisual : FrameworkElement
{
    internal const double LifetimeSeconds = 0.55;
    private static readonly Pen CrystalPen = CreatePen();
    private double _elapsed;
    internal bool IsComplete => _elapsed >= LifetimeSeconds;

    internal SnowballImpactVisual()
    {
        Width = Height = 100;
        IsHitTestVisible = false;
        Focusable = false;
    }

    internal void Advance(double deltaSeconds)
    {
        Dispatcher.VerifyAccess();
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || IsComplete) return;
        _elapsed = Math.Min(LifetimeSeconds, _elapsed + deltaSeconds);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (IsComplete) return;
        var t = _elapsed / LifetimeSeconds;
        dc.PushOpacity((1 - t) * (1 - t));
        for (var i = 0; i < 18; i++)
        {
            var angle = i * Math.PI * 2 / 18;
            var distance = (25 + i % 4 * 5) * t;
            var center = new Point(50 + Math.Cos(angle) * distance,
                50 + Math.Sin(angle) * distance + 12 * t * t);
            var radius = (i % 3 == 0 ? 4 : 2.5) * (1 - 0.35 * t);
            if (i % 3 != 0)
            {
                dc.DrawEllipse(Brushes.White, null, center, radius, radius);
                continue;
            }
            for (var arm = 0; arm < 3; arm++)
            {
                var a = angle + arm * Math.PI / 3 + t;
                var offset = new Vector(Math.Cos(a) * radius, Math.Sin(a) * radius);
                dc.DrawLine(CrystalPen, center - offset, center + offset);
            }
        }
        dc.Pop();
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(Brushes.AliceBlue, 1.3);
        pen.Freeze();
        return pen;
    }
}
