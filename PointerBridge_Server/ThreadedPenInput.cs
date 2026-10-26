using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace PointerBridge_Server;

// 一个设备的整个生命周期固定到一个线程，避免 async 接收线程池切换。
// 同步确认每次命令，无额外轨迹积压；调用方仍是后台 UDP 接收循环。
internal sealed class ThreadedPenInput : IPenInputSink
{
    private readonly BlockingCollection<Action> _commands = new(16);
    private readonly object _lifecycle = new();
    private readonly Thread _thread;
    private readonly Func<IPenInputSink> _create;
    private readonly Action<string> _report;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<long> _recoveries = new();
    private IPenInputSink? _input;
    private bool _contact;
    private bool _disposed;

    public ThreadedPenInput(Func<IPenInputSink> create, Action<string> report)
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
            throw;
        }
    }

    private void Run()
    {
        try
        {
            _input = _create();
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

    public void Apply(PenPacket packet) => Invoke(() =>
    {
        try { _input!.Apply(packet); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 87
            && packet.InRange && !packet.Contact && !_contact && CanRecover())
        {
            // 仅恢复纯悬空，不重试 DOWN/接触，不伪装成已修复的正常更新。
            _recoveries.Enqueue(Stopwatch.GetTimestamp());
            _report($"悬空注入异常，尝试重建笔设备（最近一分钟 {_recoveries.Count}/3 次）：{ex.Message}");
            try { _input!.Dispose(); }
            catch (Win32Exception cleanup) { _report($"旧笔设备清理异常：{cleanup.Message}"); }
            _input = null;
            _input = _create();
            try { _input.Apply(packet); }
            catch (Win32Exception retry)
            {
                throw new Win32Exception(retry.NativeErrorCode,
                    $"悬空恢复失败，停止监听。首次错误：{ex.Message}；重试错误：{retry.Message}");
            }
            _report("合成笔设备重建成功，已恢复悬空输入。");
        }
        _contact = packet.InRange && packet.Contact;
    });

    private bool CanRecover()
    {
        while (_recoveries.TryPeek(out long time) && Stopwatch.GetElapsedTime(time).TotalMinutes >= 1)
            _recoveries.Dequeue();
        return _recoveries.Count < 3;
    }

    public void Release() => Invoke(() =>
    {
        _input?.Release();
        _contact = false;
    });

    public void Dispose()
    {
        Task completion;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            completion = QueueCommand(() =>
            {
                try { _input?.Dispose(); }
                finally { _input = null; }
            });
            _commands.CompleteAdding();
        }
        try { completion.GetAwaiter().GetResult(); }
        finally
        {
            _thread.Join();
            _commands.Dispose();
        }
    }
}
