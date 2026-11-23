namespace Ignixa.Federation.Tests.Integration.Budgets;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock moves only when a test says so. Timers fire from <see cref="Advance"/>,
/// on the advancing thread, once their due time is reached. Timers are one-shot; periodic timers are not supported.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    /// <summary>Gets or sets whether <see cref="Advance"/> fires due timers. When off, a due timer stays pending.</summary>
    public bool TimersEnabled { get; set; } = true;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(period, Timeout.InfiniteTimeSpan);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Schedule(dueTime, _ticks);
        }

        return timer;
    }

    /// <summary>Moves the clock forward and fires every enabled timer that became due.</summary>
    /// <param name="by">How far to move the clock.</param>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due = [];
        lock (_gate)
        {
            _ticks += by.Ticks;
            if (TimersEnabled)
            {
                foreach (ManualTimer timer in _timers)
                {
                    if (timer.TryFire(_ticks))
                    {
                        due.Add(timer);
                    }
                }
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Invoke();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long? _dueTicks;

        public void Schedule(TimeSpan dueTime, long now) =>
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime.Ticks;

        public bool TryFire(long now)
        {
            if (_dueTicks is { } due && due <= now)
            {
                _dueTicks = null;
                return true;
            }

            return false;
        }

        public void Invoke() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Schedule(dueTime, owner._ticks);
            }

            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
