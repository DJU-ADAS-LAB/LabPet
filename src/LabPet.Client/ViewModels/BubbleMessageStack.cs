using System.Collections.ObjectModel;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 최대 개수를 제한하고 각 항목의 표시 시간을 독립적으로 관리하는 말풍선 메시지 스택.
/// 오래된 메시지가 위에 위치하고 새 메시지가 아래에 추가되며, 초과 시 가장 오래된 항목부터 제거한다.
/// </summary>
public sealed class BubbleMessageStack : IDisposable
{
    public const int DefaultMaxCount = 5;

    private sealed class TimerEntry
    {
        private int _disposed;

        public CancellationTokenSource Cts { get; } = new();
        public bool IsCancellationRequested => Cts.IsCancellationRequested;

        public CancellationToken TryGetToken()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new CancellationToken(canceled: true);
            }

            try
            {
                return Cts.Token;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(canceled: true);
            }
        }

        public void CancelAndDisposeOnce()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Cts.Dispose();
        }
    }

    private readonly int _maxCount;
    private readonly TimeProvider _timeProvider;
    private readonly object _syncLock = new();
    private readonly Dictionary<BubbleLine, TimerEntry> _entries = new(ReferenceEqualityComparer.Instance);
    private bool _isDisposed;

    public ObservableCollection<BubbleLine> Items { get; } = [];

    public bool HasItems
    {
        get
        {
            lock (_syncLock)
            {
                return Items.Count > 0;
            }
        }
    }

    public IReadOnlyList<BubbleLine> SnapshotItems()
    {
        lock (_syncLock)
        {
            return [.. Items];
        }
    }

    internal int ActiveTimerCount
    {
        get
        {
            lock (_syncLock)
            {
                return _entries.Count;
            }
        }
    }

    public event EventHandler? Changed;

    public BubbleMessageStack(int maxCount = DefaultMaxCount, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        _maxCount = maxCount;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 새 말풍선 항목을 추가하고 개별 만료 타이머를 시작한다. 기존 항목의 만료 시간은 유지한다.
    /// </summary>
    public bool Add(BubbleLine line, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(line);

        List<TimerEntry>? evictedEntries = null;
        TimerEntry entry;

        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            Items.Add(line);
            while (Items.Count > _maxCount)
            {
                var evicted = Items[0];
                Items.RemoveAt(0);
                if (_entries.Remove(evicted, out var evictedEntry))
                {
                    evictedEntries ??= [];
                    evictedEntries.Add(evictedEntry);
                }
            }

            if (_entries.Remove(line, out var previousEntry))
            {
                evictedEntries ??= [];
                evictedEntries.Add(previousEntry);
            }

            entry = new TimerEntry();
            _entries[line] = entry;
        }

        if (evictedEntries is not null)
        {
            foreach (var evictedEntry in evictedEntries)
            {
                evictedEntry.CancelAndDisposeOnce();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = ExpireAfterAsync(line, Math.Max(1, durationMs), entry);
        return true;
    }

    /// <summary>
    /// 표시 중인 모든 말풍선과 타이머를 즉시 정리한다.
    /// </summary>
    public void Clear()
    {
        List<TimerEntry> entriesToDispose;
        bool hadItems;

        lock (_syncLock)
        {
            hadItems = Items.Count > 0 || _entries.Count > 0;
            entriesToDispose = [.. _entries.Values];
            _entries.Clear();
            Items.Clear();
        }

        foreach (var entry in entriesToDispose)
        {
            entry.CancelAndDisposeOnce();
        }

        if (hadItems)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        Clear();
    }

    private async Task ExpireAfterAsync(BubbleLine line, int durationMs, TimerEntry entry)
    {
        try
        {
            var token = entry.TryGetToken();
            if (token.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(durationMs), _timeProvider, token);

            var removed = false;
            lock (_syncLock)
            {
                if (!_isDisposed && !entry.IsCancellationRequested
                    && _entries.TryGetValue(line, out var current)
                    && ReferenceEquals(current, entry))
                {
                    _entries.Remove(line);
                    Items.Remove(line);
                    removed = true;
                }
            }

            if (removed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (_syncLock)
            {
                if (_entries.TryGetValue(line, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(line);
                }
            }

            entry.CancelAndDisposeOnce();
        }
    }
}
