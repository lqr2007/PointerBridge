using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using PointerBridge;

namespace PointerBridge_Server;

// Create, inject, recover and destroy a device on the same dedicated thread.
internal sealed class ThreadedPenInput : IPenInputSink
{
    private readonly BlockingCollection<Action> _commands = new(16);
    private readonly object _lifecycle = new();
    private readonly Thread _thread;
    private readonly Func<IPenInputSink> _create;
    private readonly Action<string, LogLevel> _report;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private IPenInputSink? _input;
    private bool _disposed;

    public ThreadedPenInput(Func<IPenInputSink> create, Action<string, LogLevel> report)
    {
        _create = create;
        _report = report;
        _thread = new Thread(Run) { IsBackground = true, Name = "PointerBridge Pen Injection" };
        _thread.Start();
        try { _started.Task.GetAwaiter().GetResult(); }
        catch
        {
            _thread.Join();
            _commands.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    private void Run()
    {
        try
        {
            // Do not block the UI if device creation itself returns Win32 87.
            // The first input/release command will retry creation in the background.
            try { _input = _create(); }
            catch (Win32Exception ex)
            {
                _report($"笔设备初始化失败，将自动重连：{ex.Message}", LogLevel.Error);
            }
            _started.SetResult();
        }
        catch (Exception ex)
        {
            _started.SetException(ex);
            return;
        }

        foreach (Action command in _commands.GetConsumingEnumerable()) command();
    }

    private Task QueueCommand(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private void Invoke(Action action)
    {
        Task completion;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            completion = QueueCommand(action);
        }
        completion.GetAwaiter().GetResult();
    }

    private void DestroyDevice()
    {
        IPenInputSink? old = _input;
        _input = null;
        try { old?.Dispose(); }
        catch (Win32Exception ex)
        {
            _report($"旧笔设备清理失败，已放弃旧设备：{ex.Message}", LogLevel.Error);
        }
    }

    private void ExecuteWithRecovery(Action<IPenInputSink> action, bool expires = false)
    {
        bool recovering = _input == null;
        long started = Stopwatch.GetTimestamp();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _input ??= _create();
                // Never replay a stale DOWN after a long reconnect; the next UDP
                // packet/heartbeat will synchronize the current pen state instead.
                if (expires && recovering && Stopwatch.GetElapsedTime(started).TotalSeconds >= 1)
                {
                    _report("笔设备已重连；已丢弃过期笔状态，等待最新数据同步。", LogLevel.Warning);
                    return;
                }
                action(_input);
                if (recovering) _report("笔设备自动重连成功，已恢复输入。", LogLevel.Info);
                return;
            }
            catch (Win32Exception ex)
            {
                // Includes DOWN/UPDATE/UP/LEAVE, release, and recreation failures.
                // No hover-only guard, retry limit, or rate-based escalation to Fatal.
                _report($"笔注入/设备错误，正在自动重连：{ex.Message}", LogLevel.Error);
                recovering = true;
                DestroyDevice();
                if (_stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250))) return;
            }
        }
    }

    public void Apply(PenPacket packet) => Invoke(() =>
        ExecuteWithRecovery(input => input.Apply(packet), expires: true));

    public void Release() => Invoke(() => ExecuteWithRecovery(input => input.Release()));

    // Cancellation must precede queued disposal, otherwise a permanent
    // Win32 87 would deadlock Stop/Exit behind recovery.
    public void CancelPending()
    {
        lock (_lifecycle)
        {
            if (!_disposed) _stop.Cancel();
        }
    }

    public void Dispose()
    {
        Task completion;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            completion = QueueCommand(DestroyDevice);
            _commands.CompleteAdding();
        }
        try { completion.GetAwaiter().GetResult(); }
        finally
        {
            _thread.Join();
            _commands.Dispose();
            _stop.Dispose();
        }
    }
}
