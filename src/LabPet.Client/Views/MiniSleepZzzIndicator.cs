using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LabPet.Client.Views;

/// <summary>
/// 친구 오버레이 행의 왼쪽 상태 인디케이터(14×14) 안에서 수면 상태를 표시하는 초소형 대각선 'zzz' 애니메이션 컨트롤.
/// 고정 크기를 유지하므로 행 너비나 우측 버튼 레이아웃에 영향을 주지 않는다.
/// </summary>
public sealed class MiniSleepZzzIndicator : Grid
{
    private static readonly Brush ZzzTextBrush = CreateFrozenBrush(Color.FromArgb(0xFF, 0x5B, 0x63, 0xE6));

    private readonly TextBlock[] _letters = new TextBlock[3];
    private readonly ScaleTransform[] _scales = new ScaleTransform[3];
    private readonly RotateTransform[] _rotates = new RotateTransform[3];
    private readonly TranslateTransform[] _translates = new TranslateTransform[3];
    private Storyboard? _storyboard;

    /// <summary>
    /// 현재 미니 ZZZ 애니메이션 재생 여부(단위 테스트 및 상태 확인용).
    /// </summary>
    public bool IsAnimating { get; private set; }

    /// <summary>
    /// 생성된 Z 글자 수 (항상 3).
    /// </summary>
    public int LetterCount => _letters.Length;

    static MiniSleepZzzIndicator()
    {
        VisibilityProperty.OverrideMetadata(
            typeof(MiniSleepZzzIndicator),
            new FrameworkPropertyMetadata(Visibility.Visible, (d, _) => ((MiniSleepZzzIndicator)d).SyncAnimationState()));
    }

    public MiniSleepZzzIndicator()
    {
        Width = 14;
        Height = 14;
        IsHitTestVisible = false;
        ClipToBounds = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        BuildVisualLetters();
        RebuildStoryboard();
        SyncAnimationState();

        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += (_, _) => SyncAnimationState();
        Unloaded += (_, _) => StopAnimation();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && Visibility == Visibility.Visible)
        {
            StartAnimation();
        }
        else if (e.NewValue is false)
        {
            StopAnimation();
        }
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BuildVisualLetters()
    {
        Children.Clear();

        ReadOnlySpan<string> labels = ["z", "z", "Z"];
        ReadOnlySpan<double> fontSizes = [6.5, 8.0, 9.5];
        ReadOnlySpan<double> baseAngles = [-14.0, -8.0, -12.0];

        for (var i = 0; i < _letters.Length; i++)
        {
            var scale = new ScaleTransform(0.65, 0.65);
            var rotate = new RotateTransform(baseAngles[i]);
            var translate = new TranslateTransform(-2.0, 3.0);

            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(rotate);
            group.Children.Add(translate);

            var letter = new TextBlock
            {
                Text = labels[i],
                FontSize = fontSizes[i],
                FontWeight = FontWeights.ExtraBold,
                Foreground = ZzzTextBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1, 0, 0, 1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = group,
                Opacity = 0.0
            };

            _scales[i] = scale;
            _rotates[i] = rotate;
            _translates[i] = translate;
            _letters[i] = letter;
            Children.Add(letter);
        }
    }

    private void RebuildStoryboard()
    {
        var sb = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        const double cycleSeconds = 1.85;
        ReadOnlySpan<double> delays = [0.0, 0.55, 1.10];
        ReadOnlySpan<double> baseAngles = [-14.0, -8.0, -12.0];

        for (var i = 0; i < _letters.Length; i++)
        {
            var delay = TimeSpan.FromSeconds(delays[i]);
            var letter = _letters[i];
            var scale = _scales[i];
            var rotate = _rotates[i];
            var translate = _translates[i];

            var opacityAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.32)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.10))));
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.70)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(opacityAnim, letter);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(opacityAnim);

            // 좌하단 -> 우상단 대각선 궤적
            var moveXAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            moveXAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-1.5, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            moveXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(6.5 + i * 0.8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.70)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            moveXAnim.KeyFrames.Add(new LinearDoubleKeyFrame(6.5 + i * 0.8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(moveXAnim, translate);
            Storyboard.SetTargetProperty(moveXAnim, new PropertyPath(TranslateTransform.XProperty));
            sb.Children.Add(moveXAnim);

            var moveYAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            moveYAnim.KeyFrames.Add(new LinearDoubleKeyFrame(2.5, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            moveYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-6.5 - i * 1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.70)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            moveYAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-6.5 - i * 1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(moveYAnim, translate);
            Storyboard.SetTargetProperty(moveYAnim, new PropertyPath(TranslateTransform.YProperty));
            sb.Children.Add(moveYAnim);

            foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            {
                var scaleAnim = new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = delay,
                    Duration = TimeSpan.FromSeconds(cycleSeconds)
                };
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.90)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.70))));
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
                Storyboard.SetTarget(scaleAnim, scale);
                Storyboard.SetTargetProperty(scaleAnim, new PropertyPath(prop));
                sb.Children.Add(scaleAnim);
            }

            var baseAngle = baseAngles[i];
            var rotAnim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay,
                Duration = TimeSpan.FromSeconds(cycleSeconds)
            };
            rotAnim.KeyFrames.Add(new LinearDoubleKeyFrame(baseAngle, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            rotAnim.KeyFrames.Add(new EasingDoubleKeyFrame(baseAngle + 10.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.90)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            rotAnim.KeyFrames.Add(new LinearDoubleKeyFrame(baseAngle, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleSeconds))));
            Storyboard.SetTarget(rotAnim, rotate);
            Storyboard.SetTargetProperty(rotAnim, new PropertyPath(RotateTransform.AngleProperty));
            sb.Children.Add(rotAnim);
        }

        _storyboard = sb;
    }

    public void SyncAnimationState()
    {
        if (Visibility == Visibility.Visible)
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    public void StartAnimation()
    {
        if (IsAnimating || _storyboard is null)
        {
            return;
        }

        IsAnimating = true;
        _storyboard.Begin(this, isControllable: true);
    }

    public void StopAnimation()
    {
        if (!IsAnimating || _storyboard is null)
        {
            return;
        }

        IsAnimating = false;
        _storyboard.Stop(this);
        foreach (var letter in _letters)
        {
            letter.Opacity = 0.0;
        }
    }
}
