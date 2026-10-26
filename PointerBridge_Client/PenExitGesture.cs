namespace PointerBridge_Client;

// 使用单调时钟。事件更新硬件状态，定时器负责静止悬停/接触时的时长判断。
internal sealed class PenExitGesture
{
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(1);
    private int? _device;
    private TimeSpan? _hoverStarted, _contactStarted;
    private bool _contact;

    public bool Update(TimeSpan now, int device, bool inRange, bool barrelDown, bool contact)
    {
        if (!inRange || !barrelDown)
        {
            Reset();
            return false;
        }

        if (_device != device)
        {
            Reset();
            _device = device;
        }

        if (!contact)
        {
            // 接触阶段中途抬笔：需要重新完成悬停阶段。
            if (_contact) _hoverStarted = null;
            _hoverStarted ??= now;
            _contactStarted = null;
        }
        else if (!_contact)
        {
            _contactStarted = _hoverStarted.HasValue
                && now - _hoverStarted.Value >= HoldDuration ? now : null;
            _hoverStarted = null;
        }

        _contact = contact;
        return IsComplete(now);
    }

    public bool IsComplete(TimeSpan now) => _contactStarted.HasValue
        && now - _contactStarted.Value >= HoldDuration;

    public void Reset()
    {
        _device = null;
        _hoverStarted = _contactStarted = null;
        _contact = false;
    }
}
