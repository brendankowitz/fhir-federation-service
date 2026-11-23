namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// A wall clock that reads exactly what the test last set. Timers it creates (for example a
/// <see cref="CancellationTokenSource"/> timeout) are manual: they fire only when <see cref="Now"/> is set at or past
/// their due instant, on the thread that sets it. One-shot timers only.
/// </summary>
internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = now;

    public DateTimeOffset Now
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }

        set
        {
            lock (_lock)
            {
                _now = value;
            }

            FireDue();
        }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueAt = _now + dueTime;
                _timers.Add(timer);
            }
        }

        FireDue();
    }

    private void Unschedule(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private void FireDue()
    {
        ManualTimer[] due;
        lock (_lock)
        {
            due = [.. _timers.Where(timer => timer.DueAt <= _now)];
            foreach (ManualTimer timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(SettableClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("Only one-shot timers are supported.");
            }

            clock.Schedule(this, dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Unschedule(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}