using System.Net;

namespace PointerBridge_Server;

internal enum PenReceiveStatus { Waiting, Active, OutOfRange, TimedOut }

internal sealed record PenReceiveSnapshot(PenReceiveStatus Status, PenPacket? Packet,
    string? Source, TimeSpan Age, long Accepted, long Invalid, long Ignored)
{
    public string FormatDebug() => Status switch
    {
        PenReceiveStatus.Waiting => "触控笔：等待有效 UDP 数据。",
        PenReceiveStatus.TimedOut => "触控笔：接收超时（超过 1 秒无有效更新），旧状态已失效。",
        PenReceiveStatus.OutOfRange => "触控笔：不在采集区域（尚未检测到或已离开）。",
        _ => $"{Packet!.Value.FormatDebug()} | 接收于 {Age.TotalSeconds:F1}s前"
    };
}

// 由 UdpListener 的锁保护。协议没有“曾检测到笔”字段，因此不能区分
// 初始释放心跳和第一次落笔前的离开状态，不从坐标或压感猜测。
internal sealed class PenReceiveState
{
    private PenPacket? _latest;
    private IPEndPoint? _source;
    private TimeSpan _receivedAt;
    private readonly Queue<(IPEndPoint Source, uint Session)> _retired = new();
    private long _accepted, _invalid, _ignored;

    public bool Accept(ReadOnlySpan<byte> data, IPEndPoint source, TimeSpan now)
    {
        if (!PenPacket.TryParse(data, out PenPacket packet))
        {
            _invalid++;
            return false;
        }

        if (_latest is PenPacket previous)
        {
            bool sameSource = source.Equals(_source);
            bool sameSession = sameSource && previous.Session == packet.Session;
            // 同一会话按无符号回绕规则去重；旧包不刷新超时计时。
            if ((sameSession && unchecked((int)(packet.Sequence - previous.Sequence)) <= 0)
                || _retired.Contains((source, packet.Session))
                || (!sameSource && now - _receivedAt < TimeSpan.FromSeconds(1)))
            {
                _ignored++;
                return false;
            }

            if (!sameSession)
            {
                _retired.Enqueue((_source!, previous.Session));
                if (_retired.Count > 64) _retired.Dequeue();
            }
        }

        _latest = packet;
        _source = source;
        _receivedAt = now;
        _accepted++;
        return true;
    }

    public PenReceiveSnapshot Snapshot(TimeSpan now)
    {
        TimeSpan age = _latest.HasValue ? now - _receivedAt : TimeSpan.Zero;
        PenReceiveStatus status = !_latest.HasValue ? PenReceiveStatus.Waiting
            : age >= TimeSpan.FromSeconds(1) ? PenReceiveStatus.TimedOut
            : _latest.Value.InRange ? PenReceiveStatus.Active : PenReceiveStatus.OutOfRange;
        return new(status, _latest, _source?.ToString(), age, _accepted, _invalid, _ignored);
    }
}
