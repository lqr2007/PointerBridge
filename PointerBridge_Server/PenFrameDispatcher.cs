using System.Diagnostics;

namespace PointerBridge_Server;

// UDP 积压和状态切换可能连续产生多帧；在原生调用边界保证时间间隔。
internal sealed class PenFrameDispatcher
{
    private readonly Func<long> _timestamp;
    private readonly Action _wait;
    private readonly long _minimumInterval;
    private long? _lastAttempt;

    public PenFrameDispatcher() : this(Stopwatch.GetTimestamp, () => Thread.Sleep(1),
        (Stopwatch.Frequency + 999) / 1000) { }

    internal PenFrameDispatcher(Func<long> timestamp, Action wait, long minimumInterval)
    {
        _timestamp = timestamp;
        _wait = wait;
        _minimumInterval = minimumInterval;
    }

    public int LastRetryCount { get; private set; }
    public double LastGapMilliseconds { get; private set; } = -1;

    // 返回原生错误码；仅 ERROR_NOT_READY 明确允许保留状态重试同一帧。
    public int Inject(Func<long, int> inject)
    {
        LastRetryCount = 0;
        while (true)
        {
            long now = _timestamp();
            while (_lastAttempt is long previous && now - previous < _minimumInterval)
            {
                _wait();
                now = _timestamp();
            }
            LastGapMilliseconds = _lastAttempt is long last
                ? (now - last) * 1000.0 / Stopwatch.Frequency : -1;
            _lastAttempt = now;
            int error = inject(now);
            if (error != 21 || LastRetryCount == 3) return error;
            LastRetryCount++;
        }
    }
}
