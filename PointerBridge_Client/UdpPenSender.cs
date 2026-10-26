using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PointerBridge_Client;

// 独立于 WPF/Debug 的后台发送器；接管传入 UdpClient 的释放责任。
internal sealed class UdpPenSender : IDisposable
{
    internal const int PacketSize = 16;
    internal const int MaxMovePacketsPerSecond = 120;
    private const int MaxPendingTransitions = 256;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private readonly UdpClient _client;
    private readonly object _gate = new();
    private readonly Queue<PenSample> _transitions = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly uint _session = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private readonly Action<string> _reportError;
    private PenSample _latest;
    private uint _sequence;
    private bool _disposed;
    internal Task Completion { get; }

    public UdpPenSender(UdpClient client, Action<string> reportError)
    {
        _client = client;
        _reportError = reportError;
        _transitions.Enqueue(default); // 新会话先发送释放状态，不等待第一笔输入。
        Completion = Task.Run(RunAsync);
    }

    public void Update(PenSample sample)
    {
        lock (_gate)
        {
            if (_disposed || sample == _latest)
                return;

            if (sample.Flags != _latest.Flags)
            {
                // 不把快速落笔/抬笔合并成最后一个状态。
                if (_transitions.Count >= MaxPendingTransitions)
                {
                    // 避免拥塞时无限排队及重放过期轨迹，先释放再同步当前状态。
                    _transitions.Clear();
                    _transitions.Enqueue(_latest.Released);
                    _reportError("UDP 状态队列拥塞，已丢弃积压状态并安排释放/重新同步。");
                }
                _transitions.Enqueue(sample);
                if (_wake.CurrentCount == 0)
                    _wake.Release();
            }
            _latest = sample;
        }
    }

    private async Task RunAsync()
    {
        byte[] packet = new byte[PacketSize];
        PenSample? lastSent = null;
        long lastSendTime = 0;
        var moveInterval = TimeSpan.FromSeconds(1.0 / MaxMovePacketsPerSecond);
        long lastErrorTime = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                PenSample sample;
                bool transition;
                lock (_gate)
                {
                    transition = _transitions.Count > 0;
                    sample = transition ? _transitions.Dequeue() : _latest;
                }

                TimeSpan elapsed = Stopwatch.GetElapsedTime(lastSendTime);
                if (transition || (sample != lastSent && elapsed >= moveInterval)
                    || elapsed >= HeartbeatInterval)
                {
                    try
                    {
                        Encode(packet, _session, unchecked(++_sequence), sample);
                        await _client.SendAsync(packet.AsMemory(), _stop.Token).ConfigureAwait(false);
                        lastSent = sample;
                    }
                    catch (SocketException ex)
                    {
                        // 例如远端端口未监听；保留状态，后续心跳可重新同步。
                        if (lastErrorTime == 0 || Stopwatch.GetElapsedTime(lastErrorTime).TotalSeconds >= 5)
                        {
                            lastErrorTime = Stopwatch.GetTimestamp();
                            _reportError($"UDP 发送失败：{ex.Message}");
                        }
                    }
                    lastSendTime = Stopwatch.GetTimestamp();
                }

                lock (_gate)
                {
                    if (_transitions.Count > 0)
                        continue;
                }
                // 普通移动只保留最新快照；关键状态可以唤醒等待，优先发送。
                await _wake.WaitAsync(moveInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            // 关闭/换目标时尽力发出释放包；UDP 不保证到达，接收端仍须超时抬笔。
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            PenSample released;
            lock (_gate) { released = _latest.Released; }
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    Encode(packet, _session, unchecked(++_sequence), released);
                    await _client.SendAsync(packet.AsMemory(), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
            finally { _client.Dispose(); }
        }
    }

    internal static void Encode(Span<byte> packet, uint session, uint sequence, PenSample sample)
    {
        packet[0] = 1; // 协议版本
        packet[1] = (byte)sample.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(packet[2..6], session);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[6..10], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[10..12], sample.X);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[12..14], sample.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[14..16], sample.Pressure);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
        }
        // 不阻塞 UI；后台任务发送最后的释放包并关闭套接字。
    }
}
