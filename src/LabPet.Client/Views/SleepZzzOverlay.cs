using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LabPet.Client.Views;

/// <summary>
/// 펫이 수면(IsSleeping) 상태일 때 머리 위로 'z -> Z -> Z' 3단 글자 방울이
/// 시차를 두고 살랑거리며 떠올랐다가 사라지는 저부하 WPF 애니메이션 오버레이.
/// 실제 화면에 보일 때만 애니메이션을 재생한다.
/// </summary>
public sealed class SleepZzzOverlay : Grid
{
    private static readonly Brush BubbleBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0xEE, 0xED, 0xF2, 0xFF));
    private static readonly Brush BubbleBorderBrush = CreateFrozenBrush(Color.FromArgb(0xFF, 0xC7, 0xD2, 0xFE));
    private static readonly Brush BubbleTextBrush = CreateFrozenBrush(Color.FromArgb(0xFF, 0x4F, 0x46, 0xE5));

    private readonly Border[] _bubbles = new Border[3];
    private readonly ScaleTransform[] _scales = new ScaleTransform[3];
    private readonly RotateTransform[] _rotates = new RotateTransform[3];
    private readonly TranslateTransform[] _translates = new TranslateTransform[3];
    private Storyboard? _storyboard;

    /// <summary>
    /// 현재 ZZZ 플로팅 애니메이션이 재생 중인지 여부(단위 테스트 및 상태 확인용).
    /// </summary>
    public bool IsAnimating { get; private set; }

    /// <summary>
    /// 생성된 Z 방울 개수 (항상 3: z, Z, Z).
    /// </summary>
    public int BubbleCount => _bubbles.Length;

    public SleepZzzOverlay()
    {
        Width = 64;
        Height = 62;
        IsHitTestVisible = false;
        ClipToBounds = false;

        BuildVisualBubbles();
        RebuildStoryboard();
        IsVisibleChanged += (_, _) => SyncAnimationState();
        Loaded += (_, _) => SyncAnimationState();
        Unloaded += (_, _) => StopAnimation();
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BuildVisualBubbles()
    {
        Children.Clear();

        ReadOnlySpan<string> labels = ["z", "Z", "Z"];
        ReadOnlySpan<double> fontSizes = [10.5, 13.0, 15.5];
        ReadOnlySpan<double> baseAngles = [-9.0, 7.0, -11.0];
        ReadOnlySpan<double> leftOffsets = [6.0, 13.0, 20.0];
        ReadOnlySpan<double> bottomOffsets = [2.0, 6.0, 10.0];

        for (var i = 0; i < _bubbles.Length; i++)
        {
            var scale = new ScaleTransform(0.5, 0.5);
            var rotate = new RotateTransform(baseAngles[i]);
            var translate = new TranslateTransform(0, 0);

            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(rotate);
            group.Children.Add(translate);

            var text = new TextBlock
            {
                Text = labels[i],
                FontSize = fontSizes[i],
                FontWeight = FontWeights.ExtraBold,
                Foreground = BubbleTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0)
            };

            var bubble = new Border
            {
                Child = text,
                Background = BubbleBackgroundBrush,
                BorderBrush = BubbleBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7 + i),
                Padding = new Thickness(4.5 + i * 0.5, 0.5, 4.5 + i * 0.5, 1.0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(leftOffsets[i], 0, 0, bottomOffsets[i]),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = group,
                Opacity = 0.0
            };

            _scales[i] = scale;
            _rotates[i] = rotate;
            _translates[i] = translate;
            _bubbles[i] = bubble;
            Children.Add(bubble);
        }
    }

    private void RebuildStoryboard()
    {
        var sb = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        const double cycleSeconds = 2.4;
        ReadOnlySpan<double> delays = [0.0, 0.8, 1.6];
        ReadOnlySpan<double> baseAngles = [-9.0, 7.0, -11.0];

        for (var i = 0; i < _bubbles.Length; i++)
        {
            var delay = TimeSpan.FromSeconds(delays[i]);
            var bubble = _bubbles[i];
            var scale = _scales[i];
            var rotate = _rotates[i];
            var translate = _translates[i];

            // 1) 투명도: 서서히 나타나 떠오른 뒤 위쪽에서 부드럽게 페이드아웃
            var opacityAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.42)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.90, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.45))));
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.25)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(opacityAnim, bubble);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(opacityAnim);

            // 2) 수직 상승 (TranslateY: +4 -> -30)
            var moveYAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            moveYAnim.KeyFrames.Add(new LinearDoubleKeyFrame(4.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            moveYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-28.0 - i * 2.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.25)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            moveYAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-28.0 - i * 2.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(moveYAnim, translate);
            Storyboard.SetTargetProperty(moveYAnim, new PropertyPath(TranslateTransform.YProperty));
            sb.Children.Add(moveYAnim);

            // 3) 좌우 살랑거림 (TranslateX: S자 곡선 부유)
            var moveXAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            moveXAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            var endX = 8.0 + i * 1.5;
            moveXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(endX, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.25)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            moveXAnim.KeyFrames.Add(new LinearDoubleKeyFrame(endX, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(moveXAnim, translate);
            Storyboard.SetTargetProperty(moveXAnim, new PropertyPath(TranslateTransform.XProperty));
            sb.Children.Add(moveXAnim);

            // 4) 작은 방울에서 시작해 일정한 속도로 커진다.
            foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            {
                var scaleAnim = new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = delay,
                    Duration = TimeSpan.FromSeconds(cycleSeconds)
                };
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.48, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.25)), new SineEase { EasingMode = EasingMode.EaseOut }));
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.04, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.25))));
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.48, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
                Storyboard.SetTarget(scaleAnim, scale);
                Storyboard.SetTargetProperty(scaleAnim, new PropertyPath(prop));
                sb.Children.Add(scaleAnim);
            }

            // 5) 살짝 기울어지며 흔들리는 각도 (RotateTransform.Angle)
            var baseAngle = baseAngles[i];
            var rotAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            rotAnim.KeyFrames.Add(new LinearDoubleKeyFrame(baseAngle, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            rotAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-baseAngle * 0.8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.15)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            rotAnim.KeyFrames.Add(new EasingDoubleKeyFrame(baseAngle * 1.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.25)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            rotAnim.KeyFrames.Add(new LinearDoubleKeyFrame(baseAngle, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(rotAnim, rotate);
            Storyboard.SetTargetProperty(rotAnim, new PropertyPath(RotateTransform.AngleProperty));
            sb.Children.Add(rotAnim);
        }

        _storyboard = sb;
    }

    private void SyncAnimationState()
    {
        if (IsLoaded && IsVisible)
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    private void StartAnimation()
    {
        if (IsAnimating || _storyboard is null)
        {
            return;
        }

        IsAnimating = true;
        _storyboard.Begin(this, isControllable: true);
    }

    private void StopAnimation()
    {
        if (!IsAnimating || _storyboard is null)
        {
            return;
        }

        IsAnimating = false;
        _storyboard.Remove(this);
        foreach (var bubble in _bubbles)
        {
            bubble.Opacity = 0.0;
        }
    }
}
