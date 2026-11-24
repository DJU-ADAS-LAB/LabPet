using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LabPet.Client.Views;

/// <summary>친구 행의 고정 슬롯에서 세 글자가 차례로 살짝 떠오르는 수면 표시.</summary>
public sealed class MiniSleepZzzIndicator : Grid
{
    public const double SlotWidth = 20;
    public const double SlotHeight = 16;

    private static readonly Brush LetterBrush = CreateLetterBrush();
    private readonly Storyboard _storyboard = new() { RepeatBehavior = RepeatBehavior.Forever };
    internal bool IsAnimating { get; private set; }
    internal int LetterCount => Children.Count;

    public MiniSleepZzzIndicator()
    {
        Width = SlotWidth;
        Height = SlotHeight;
        IsHitTestVisible = false;

        AddLetter("z", 7.5, 1, 0);
        AddLetter("z", 8.5, 6, 1);
        AddLetter("z", 10, 11, 2);

        Loaded += (_, _) => SyncAnimation();
        IsVisibleChanged += (_, _) => SyncAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    private void AddLetter(string value, double size, double left, int order)
    {
        var offset = new TranslateTransform();
        var letter = new TextBlock
        {
            Text = value,
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = LetterBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(left, 0, 0, 1 + order),
            Opacity = 0.72,
            RenderTransform = offset
        };

        Children.Add(letter);

        // 글자 위치와 간격을 고정하고 높이와 밝기만 시차를 두고 바꾼다.
        var rise = new DoubleAnimation(0, -1.8, TimeSpan.FromMilliseconds(850));
        var brighten = new DoubleAnimation(0.72, 1.0, TimeSpan.FromMilliseconds(850));
        foreach (var animation in new[] { rise, brighten })
        {
            animation.BeginTime = TimeSpan.FromMilliseconds(160 * order);
            animation.AutoReverse = true;
            animation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        }
        Storyboard.SetTarget(rise, offset);
        Storyboard.SetTargetProperty(rise, new PropertyPath(TranslateTransform.YProperty));
        Storyboard.SetTarget(brighten, letter);
        Storyboard.SetTargetProperty(brighten, new PropertyPath(OpacityProperty));
        _storyboard.Children.Add(rise);
        _storyboard.Children.Add(brighten);
    }

    private void SyncAnimation()
    {
        if (IsLoaded && IsVisible)
        {
            if (!IsAnimating)
            {
                _storyboard.Begin(this, isControllable: true);
                IsAnimating = true;
            }
        }
        else
        {
            StopAnimation();
        }
    }

    private void StopAnimation()
    {
        if (IsAnimating)
        {
            _storyboard.Remove(this);
            IsAnimating = false;
        }
    }

    private static SolidColorBrush CreateLetterBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(75, 82, 194));
        brush.Freeze();
        return brush;
    }
}
