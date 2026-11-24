namespace LabPet.Client.Solo;

/// <summary>
/// 전역 키보드·마우스 훅 이벤트에서 매크로(주입 입력), 키 꾹 누름 오토 리피트, 과속 오토클릭 어뷰징을 걸러낸다.
/// </summary>
public sealed class ActivityAntiAbuseFilter
{
    public const uint LlkfInjectedFlag = 0x00000010;
    public const uint LlkfLowerIlInjectedFlag = 0x00000002;
    public const uint LlmhfInjectedFlag = 0x00000001;
    public const uint LlmhfLowerIlInjectedFlag = 0x00000002;

    public const int DefaultMaxKeysPerSecond = 25;
    public const int DefaultMaxClicksPerSecond = 12;
    public static readonly TimeSpan DefaultMinClickInterval = TimeSpan.FromMilliseconds(40);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maxKeysPerSecond;
    private readonly int _maxClicksPerSecond;
    private readonly TimeSpan _minClickInterval;

    private readonly HashSet<uint> _pressedVkCodes = [];
    private readonly Queue<DateTimeOffset> _recentKeyDowns = new();
    private readonly Queue<DateTimeOffset> _recentClicks = new();
    private DateTimeOffset _lastAcceptedClickUtc = DateTimeOffset.MinValue;

    public ActivityAntiAbuseFilter(
        TimeProvider? timeProvider = null,
        int maxKeysPerSecond = DefaultMaxKeysPerSecond,
        int maxClicksPerSecond = DefaultMaxClicksPerSecond,
        TimeSpan? minClickInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxKeysPerSecond = Math.Max(1, maxKeysPerSecond);
        _maxClicksPerSecond = Math.Max(1, maxClicksPerSecond);
        _minClickInterval = minClickInterval ?? DefaultMinClickInterval;
    }

    public static bool IsInjectedKeyboardFlags(uint flags)
        => (flags & (LlkfInjectedFlag | LlkfLowerIlInjectedFlag)) != 0;

    public static bool IsInjectedMouseFlags(uint flags)
        => (flags & (LlmhfInjectedFlag | LlmhfLowerIlInjectedFlag)) != 0;

    /// <summary>
    /// 키 다운 이벤트를 검사해 물리 입력이며 키 누름 반복·과속 매크로가 아닐 때만 허용한다.
    /// </summary>
    public bool TryAcceptKeyDown(uint vkCode, uint flags)
    {
        if (IsInjectedKeyboardFlags(flags))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            // 동일 키를 떼지 않고 누르고 있을 때 OS 오토 리피트로 발생하는 반복 WM_KEYDOWN 무시
            if (!_pressedVkCodes.Add(vkCode))
            {
                return false;
            }

            PruneWindow(_recentKeyDowns, now - TimeSpan.FromSeconds(1));
            if (_recentKeyDowns.Count >= _maxKeysPerSecond)
            {
                return false;
            }

            _recentKeyDowns.Enqueue(now);
            return true;
        }
    }

    public void NotifyKeyUp(uint vkCode)
    {
        lock (_gate)
        {
            _pressedVkCodes.Remove(vkCode);
        }
    }

    /// <summary>
    /// 마우스 클릭 이벤트를 검사해 물리 입력이며 최소 클릭 간격 및 초당 클릭 상한을 넘지 않을 때만 허용한다.
    /// </summary>
    public bool TryAcceptMouseClick(uint flags)
    {
        if (IsInjectedMouseFlags(flags))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastAcceptedClickUtc != DateTimeOffset.MinValue && (now - _lastAcceptedClickUtc) < _minClickInterval)
            {
                return false;
            }

            PruneWindow(_recentClicks, now - TimeSpan.FromSeconds(1));
            if (_recentClicks.Count >= _maxClicksPerSecond)
            {
                return false;
            }

            _lastAcceptedClickUtc = now;
            _recentClicks.Enqueue(now);
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pressedVkCodes.Clear();
            _recentKeyDowns.Clear();
            _recentClicks.Clear();
            _lastAcceptedClickUtc = DateTimeOffset.MinValue;
        }
    }

    private static void PruneWindow(Queue<DateTimeOffset> queue, DateTimeOffset cutoff)
    {
        while (queue.Count > 0 && queue.Peek() <= cutoff)
        {
            queue.Dequeue();
        }
    }
}
