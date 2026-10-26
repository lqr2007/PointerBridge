using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace PointerBridge_Server;

// 后台逐包解析/注入；日志仍只读取最新快照。
internal sealed class UdpListener : IDisposable
{
    private readonly UdpClient _client;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private readonly object _stateGate = new();
    private readonly PenReceiveState _state = new();
    private IPenInputSink? _input;
    internal Task Completion { get; }

    public PenReceiveSnapshot GetSnapshot()
    {
        lock (_stateGate)
            return _state.Snapshot(Stopwatch.GetElapsedTime(0));
    }

    public UdpListener(int port, Func<IPenInputSink>? createInput = null)
    {
        _client = new UdpClient(Socket.OSSupportsIPv6
            ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        try
        {
            _client.ExclusiveAddressUse = true;
            if (Socket.OSSupportsIPv6)
                _client.Client.DualMode = true;
            _client.Client.Bind(new IPEndPoint(Socket.OSSupportsIPv6
                ? IPAddress.IPv6Any : IPAddress.Any, port));
            _input = createInput?.Invoke();
            Completion = Task.Run(ReceiveLoopAsync);
        }
        catch
        {
            _client.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        CancellationToken token = _stop.Token;
        Task<UdpReceiveResult>? receive = null;
        Task<bool>? tick = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            receive = _client.ReceiveAsync(token).AsTask();
            tick = timer.WaitForNextTickAsync(token).AsTask();
            while (!token.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(receive, tick).ConfigureAwait(false);
                if (completed == receive)
                {
                    UdpReceiveResult received = await receive.ConfigureAwait(false);
                    lock (_stateGate)
                    {
                        TimeSpan now = Stopwatch.GetElapsedTime(0);
                        PenReceiveSnapshot before = _state.Snapshot(now);
                        if (_state.Accept(received.Buffer, received.RemoteEndPoint, now))
                        {
                            PenReceiveSnapshot after = _state.Snapshot(now);
                            if (before.Status == PenReceiveStatus.TimedOut || before.Source != after.Source
                                || before.Packet?.Session != after.Packet?.Session)
                                _input?.Release();
                            _input?.Apply(after.Packet!.Value);
                        }
                    }
                    receive = _client.ReceiveAsync(token).AsTask();
                }
                else
                {
                    await tick.ConfigureAwait(false);
                    tick = timer.WaitForNextTickAsync(token).AsTask();
                }
                lock (_stateGate)
                {
                    if (_state.Snapshot(Stopwatch.GetElapsedTime(0)).Status == PenReceiveStatus.TimedOut)
                        _input?.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel();
            // 注入异常时也要结束尚未完成的收包，观察取消异常以免遗留后台任务。
            if (receive != null)
            {
                try { await receive.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
            }
            if (tick != null)
            {
                try { await tick.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            lock (_stateGate)
            {
                try { _input?.Dispose(); }
                finally { _input = null; }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _client.Dispose(); // 立即释放绑定端口，允许再次开始监听。
        try
        {
            lock (_stateGate)
            {
                // 停止按钮/退出不等待下一个数据包或超时 tick 才抬笔。
                try { _input?.Dispose(); }
                finally { _input = null; }
            }
        }
        finally
        {
            // 等接收循环退出后再释放其取消资源。
            _ = Completion.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        }
    }
}
