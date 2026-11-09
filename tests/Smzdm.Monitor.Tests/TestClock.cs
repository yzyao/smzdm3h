namespace Smzdm.Monitor.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-09T10:00:00+08:00");
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    // Tests advance timer waits instantly, allowing deterministic retry-boundary checks.
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero) Now += dueTime;
        var timer = new TestTimer();
        ThreadPool.QueueUserWorkItem(_ => { if (!timer.Disposed) callback(state); });
        return timer;
    }

    private sealed class TestTimer : ITimer
    {
        public volatile bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
