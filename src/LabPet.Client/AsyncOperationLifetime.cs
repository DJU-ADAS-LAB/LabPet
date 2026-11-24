namespace LabPet.Client;

/// <summary>종료 이후 새 작업을 닫고 이미 시작한 작업이 자원을 모두 반환할 때까지 기다린다.</summary>
internal sealed class AsyncOperationLifetime
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _active;

    internal IDisposable? TryBegin()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            _active++;
            return new Operation(this);
        }
    }

    internal Task DrainAsync()
    {
        lock (_gate)
        {
            _closed = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }
            return _drained.Task;
        }
    }

    private void Complete()
    {
        lock (_gate)
        {
            if (--_active == 0 && _closed)
            {
                _drained.TrySetResult();
            }
        }
    }

    private sealed class Operation(AsyncOperationLifetime owner) : IDisposable
    {
        private AsyncOperationLifetime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Complete();
    }
}
