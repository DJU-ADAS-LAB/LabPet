using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LabPet.Client.Views;

/// <summary>펫과 친구 목록이 공유하는 글자 수면 표시. 화면에 보일 때만 재생한다.</summary>
public class SleepZzzOverlay : Grid
{
    internal const double CompactWidth = 20;
    internal const double CompactHeight = 16;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(2.4);
    private static readonly Brush LetterBrush = CreateLetterBrush();
    private readonly Storyboard _storyboard = new();

    public bool IsAnimating { get; private set; }

    public SleepZzzOverlay() : this(compact: false)
    {
    }

    internal SleepZzzOverlay(bool compact)
    {
        Width = compact ? CompactWidth : 64;
        Height = compact ? CompactHeight : 62;
        IsHitTestVisible = false;

        ReadOnlySpan<double> sizes = compact ? [8, 9.5, 11] : [13, 16, 19];
        ReadOnlySpan<double> leftOffsets = compact ? [1, 6, 12] : [6, 15, 24];
        ReadOnlySpan<double> bottomOffsets = compact ? [1, 1.5, 2] : [2, 8, 14];
        for (var i = 0; i < sizes.Length; i++)
            AddLetter(i, compact, sizes[i], leftOffsets[i], bottomOffsets[i]);

        IsVisibleChanged += (_, _) => SyncAnimationState();
        Loaded += (_, _) => SyncAnimationState();
        Unloaded += (_, _) => StopAnimation();
    }

    private void AddLetter(int order, bool compact, double size, double left, double bottom)
    {
        var startOpacity = compact ? 0.72 : 0;
        var letter = new TextBlock
        {
            Text = compact || order == 0 ? "z" : "Z",
            FontSize = size,
            FontWeight = FontWeights.ExtraBold,
            Foreground = LetterBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(left, 0, 0, bottom),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } },
            Opacity = startOpacity
        };
        Children.Add(letter);

        // 글자별 주기를 독립적으로 반복해 앞 글자가 사라질 때 다음 글자가 이어진다.
        var delay = TimeSpan.FromSeconds(order * (compact ? 0.18 : 0.65));
        Animate(letter, new PropertyPath(OpacityProperty), delay, (0, startOpacity), (0.19, 1), (0.55, 0.92), (0.94, startOpacity));
        if (compact)
        {
            Animate(letter, TransformProperty(1, TranslateTransform.YProperty), delay, (0, 0), (0.52, -1.5), (1, 0));
            return;
        }

        // 투명해진 뒤 위치와 크기가 초기화되어 이동이 눈에 띄지 않는다.
        Animate(letter, TransformProperty(1, TranslateTransform.XProperty), delay, (0, 0), (0.94, 8 + order));
        Animate(letter, TransformProperty(1, TranslateTransform.YProperty), delay, (0, 2), (0.94, -25 - order * 2));
        Animate(letter, TransformProperty(0, ScaleTransform.ScaleXProperty), delay, (0, 0.72), (0.75, 1));
        Animate(letter, TransformProperty(0, ScaleTransform.ScaleYProperty), delay, (0, 0.72), (0.75, 1));
    }

    // 텍스트에 실제로 적용된 변환을 찾아 애니메이션한다.
    private static PropertyPath TransformProperty(int index, DependencyProperty property) =>
        new($"(0).(1)[{index}].(2)", RenderTransformProperty, TransformGroup.ChildrenProperty, property);

    private void Animate(TextBlock target, PropertyPath property, TimeSpan delay,
        params (double Phase, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = delay,
            Duration = Cycle,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        foreach (var (phase, value) in frames)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(Cycle * phase), ease));
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        _storyboard.Children.Add(animation);
    }

    private void SyncAnimationState()
    {
        if (!IsLoaded || !IsVisible)
        {
            StopAnimation();
            return;
        }

        if (IsAnimating) return;
        _storyboard.Begin(this, isControllable: true);
        IsAnimating = true;
    }

    private void StopAnimation()
    {
        if (!IsAnimating) return;
        _storyboard.Remove(this);
        IsAnimating = false;
    }

    private static SolidColorBrush CreateLetterBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0xE6));
        brush.Freeze();
        return brush;
    }
}
