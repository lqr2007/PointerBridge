namespace PointerBridge_Server;

internal interface IPenInputSink : IDisposable
{
    void Apply(PenPacket packet);
    void Release();
}

internal enum PenAction { Enter, Down, Update, Up, Leave }
internal readonly record struct PenFrame(PenAction Action, ushort X, ushort Y,
    ushort Pressure, bool Contact, bool Eraser, bool BarrelDown);

// 不依赖 Win32 的状态机，可用假注入器测试，不操作本机指针。
internal sealed class PenInjectionState(Action<PenFrame> inject)
{
    private PenFrame? _current;

    private void Send(PenFrame frame)
    {
        inject(frame); // 注入成功后才提交状态。
        _current = frame.Action == PenAction.Leave ? null : frame;
    }

    public void Apply(PenPacket packet)
    {
        if (!packet.InRange)
        {
            Release();
            return;
        }

        // 一支笔的两个端点不能在同一个接触序列中直接互换。
        if (_current is PenFrame old && old.Eraser != packet.Eraser)
            Release();

        bool barrel = packet.BarrelKnown && packet.BarrelDown;
        if (_current == null)
            Send(new(PenAction.Enter, packet.X, packet.Y, 0, false, packet.Eraser, barrel));

        PenFrame previous = _current!.Value;
        PenAction action = packet.Contact
            ? previous.Contact ? PenAction.Update : PenAction.Down
            : previous.Contact ? PenAction.Up : PenAction.Update;
        Send(new(action, packet.X, packet.Y, packet.Contact ? packet.Pressure : (ushort)0,
            packet.Contact, packet.Eraser, barrel));
    }

    public void Release()
    {
        if (_current is not PenFrame previous) return;
        if (previous.Contact)
            Send(previous with { Action = PenAction.Up, Pressure = 0, Contact = false, BarrelDown = false });
        Send(previous with { Action = PenAction.Leave, Pressure = 0, Contact = false, BarrelDown = false });
    }
}
