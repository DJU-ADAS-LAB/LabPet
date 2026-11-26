using System.Collections.Concurrent;
using LabPet.Server.Options;

namespace LabPet.Server.Services;

/// <summary>
/// 반복 공격 트래픽으로 디스크/콘솔 로그가 폭주하는 것을 방지하는 바운디드 스로틀 로거.
/// 동일 카테고리와 키의 반복 로그는 윈도우당 1회만 기록하고 억제된 건수를 함께 남긴다.
/// </summary>
public sealed class ThrottledLogService
{
    private sealed class ThrottleEntry
    {
        public readonly object Sync = new();
        public DateTimeOffset LastLoggedAtUtc;
        public int SuppressedCount;
    }

    private readonly ConcurrentDictionary<string, ThrottleEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;
    private readonly int _maxKeys;
    private readonly TimeProvider _clock;

    public ThrottledLogService(ServerOptions options, TimeProvider? clock = null)
    {
        _window = TimeSpan.FromSeconds(Math.Clamp(options.LogThrottleWindowSeconds, 1, 300));
        _maxKeys = Math.Clamp(options.MaxLogThrottleKeys, 32, 4096);
        _clock = clock ?? TimeProvider.System;
    }

    public int TrackedKeyCount => _entries.Count;

    public void LogWarning(ILogger logger, string category, string key, string template, params object?[] args)
    {
        if (!TryAcquireLogSlot($"{category}|{key}", out var suppressed))
        {
            return;
        }

        if (suppressed > 0)
        {
            logger.LogWarning(template + " (직전 {SuppressedCount}건 로그 억제됨)", [.. args, suppressed]);
        }
        else
        {
            logger.LogWarning(template, args);
        }
    }

    public bool TryAcquireLogSlot(string compositeKey, out int suppressedCount)
    {
        suppressedCount = 0;
        var now = _clock.GetUtcNow();

        if (!_entries.TryGetValue(compositeKey, out var entry))
        {
            if (_entries.Count >= _maxKeys)
            {
                PruneExpired(now);
                if (_entries.Count >= _maxKeys)
                {
                    return false;
                }
            }

            entry = _entries.GetOrAdd(compositeKey, static _ => new ThrottleEntry());
        }

        lock (entry.Sync)
        {
            if (entry.LastLoggedAtUtc != default && now - entry.LastLoggedAtUtc < _window)
            {
                entry.SuppressedCount++;
                return false;
            }

            suppressedCount = entry.SuppressedCount;
            entry.SuppressedCount = 0;
            entry.LastLoggedAtUtc = now;
            return true;
        }
    }

    public void PruneExpired(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromSeconds(_window.TotalSeconds * 3);
        foreach (var kv in _entries)
        {
            var remove = false;
            lock (kv.Value.Sync)
            {
                if (kv.Value.LastLoggedAtUtc < cutoff && kv.Value.SuppressedCount == 0)
                {
                    remove = true;
                }
            }

            if (remove)
            {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
