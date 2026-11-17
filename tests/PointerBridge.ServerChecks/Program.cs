using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PointerBridge;
using PointerBridge_Server;
using PointerBridge_Client;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _checks++;
    }

    private static async Task Main()
    {
        CheckLogging();
        CheckEraserHover();
        foreach (PenAction action in Enum.GetValues<PenAction>()) CheckActionRecovery(action);
        CheckCreationAndCleanupRecovery();
        await CheckCancellation();
        await CheckExpiredInput();
        await CheckListenerCancellation();
        await CheckSender();
        Console.WriteLine($"PASS: {_checks} checks (no native pen injection)");
    }

    private static void CheckLogging()
    {
        DebugLog.Clear();
        int fatal = 0;
        void OnFatal(string _) => fatal++;
        DebugLog.FatalReported += OnFatal;
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            DebugLog.Write("first\r\nsecond", level);
            Check(DebugLog.Entries.TakeLast(2).All(line => line.StartsWith($"[{level}] [")), "Level precedes timestamp on every line");
        }
        Check(fatal == 1, "Only Fatal requests termination");
        DebugLog.FatalReported -= OnFatal;
        Parallel.For(0, 1100, i => DebugLog.Write(i.ToString()));
        Check(DebugLog.Entries.Length == 1000, "Thread-safe bounded history before opening debug");
        DebugLog.Clear();
        Check(DebugLog.Entries.Length == 0, "Clear history");
    }

    private static PenPacket Hover => new(1, 1, 1, 100, 200, 0);
    private static PenPacket Contact => new(3, 1, 2, 100, 200, 300);

    private static void CheckEraserHover()
    {
        var frames = new List<PenFrame>();
        var state = new PenInjectionState(frames.Add);
        PenPacket eraserHover = Hover with { Flags = 5 };
        PenPacket eraserContact = Contact with { Flags = 7 };

        void ExpectHoverFrames(string name)
        {
            Check(frames.Count > 0 && frames.All(frame => frame.Eraser && !frame.Contact
                && frame.Pressure == 0 && frame.Action != PenAction.Down
                && frame.NativePenFlags == 0x2u), name);
        }

        state.Apply(eraserHover);
        Check(frames.Select(frame => frame.Action).SequenceEqual(new[] { PenAction.Enter, PenAction.Update }),
            "Eraser first enters as hover, not DOWN");
        ExpectHoverFrames("Eraser hover sets INVERTED only, never ERASER pressed");
        frames.Clear();
        state.Apply(eraserHover);
        ExpectHoverFrames("Repeated eraser hover remains unpressed");
        frames.Clear();
        state.Apply(eraserContact);
        Check(frames.Count == 1 && frames[0].Action == PenAction.Down && frames[0].Contact
            && frames[0].Pressure == eraserContact.Pressure && frames[0].NativePenFlags == 0x6u,
            "Eraser contact sets INVERTED and ERASER pressed");
        frames.Clear();
        state.Apply(eraserContact with { X = 400 });
        Check(frames.Count == 1 && frames[0].Action == PenAction.Update && frames[0].Contact
            && frames[0].NativePenFlags == 0x6u, "Eraser dragging remains pressed");
        frames.Clear();
        state.Apply(eraserHover);
        Check(frames.Count == 1 && frames[0].Action == PenAction.Up, "Eraser lift generates UP");
        ExpectHoverFrames("Eraser UP clears pressed flag immediately");
        frames.Clear();
        state.Release();
        Check(frames.Count == 1 && frames[0].Action == PenAction.Leave, "Eraser hover leaves without DOWN");
        ExpectHoverFrames("Eraser leave is unpressed");

        state.Apply(eraserContact);
        frames.Clear();
        state.Release();
        Check(frames.Select(frame => frame.Action).SequenceEqual(new[] { PenAction.Up, PenAction.Leave }),
            "Releasing eraser contact emits UP then LEAVE");
        ExpectHoverFrames("Forced eraser release clears pressed flag for both frames");
        frames.Clear();
        state.Apply(eraserContact);
        Check(frames.Count == 2 && frames[0].NativePenFlags == 0x2u
            && !frames[0].Contact && frames[1].NativePenFlags == 0x6u && frames[1].Contact,
            "Direct eraser contact still enters unpressed before DOWN");
        frames.Clear();
        state.Apply(Hover);
        Check(frames.Count == 4 && frames.Take(2).All(frame => frame.Eraser
                && !frame.Contact && frame.NativePenFlags == 0x2u)
            && frames.Skip(2).All(frame => !frame.Eraser && !frame.Contact && frame.NativePenFlags == 0),
            "Flipping eraser contact to tip hover releases eraser before switching");
        frames.Clear();
        state.Apply(eraserHover);
        Check(frames.Count == 3 && frames.All(frame => !frame.Contact
            && frame.Action != PenAction.Down && (frame.NativePenFlags & 0x4u) == 0),
            "Flipping tip hover to eraser hover never presses");
        frames.Clear();
        state.Apply(eraserHover with { Flags = 29 }); // InRange | Eraser | BarrelKnown | BarrelDown
        Check(frames.Count == 1 && !frames[0].Contact && frames[0].Pressure == 0
            && frames[0].NativePenFlags == 0x3u, "Eraser hover preserves barrel button without eraser contact");

        state.Release();
        frames.Clear();
        state.Apply(Contact);
        Check(frames.Count == 2 && frames[1].Contact && frames[1].NativePenFlags == 0,
            "Tip contact does not set eraser/inverted flags");

        byte[] bytes = new byte[UdpPenSender.PacketSize];
        UdpPenSender.Encode(bytes, 1, 1, new PenSample(100, 200, 0, PenFlags.InRange | PenFlags.Eraser));
        Check(PenPacket.TryParse(bytes, out PenPacket decoded) && decoded.Eraser && !decoded.Contact,
            "Eraser hover stays independent of contact across UDP");
        frames.Clear();
        new PenInjectionState(frames.Add).Apply(decoded);
        ExpectHoverFrames("Decoded eraser hover remains unpressed at native boundary");
    }

    private static void CheckActionRecovery(PenAction failingAction)
    {
        int creations = 0;
        bool failed = false;
        var levels = new List<LogLevel>();
        var threads = new List<int>();
        using var input = new ThreadedPenInput(() =>
        {
            creations++;
            threads.Add(Environment.CurrentManagedThreadId);
            return new Frames(frame =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                if (!failed && frame.Action == failingAction)
                {
                    failed = true;
                    throw new Win32Exception(87);
                }
            }, () => threads.Add(Environment.CurrentManagedThreadId));
        }, (_, level) => levels.Add(level));
        switch (failingAction)
        {
            case PenAction.Enter: input.Apply(Hover); break;
            case PenAction.Down: input.Apply(Contact); break;
            case PenAction.Update: input.Apply(Hover); break;
            case PenAction.Up: input.Apply(Contact); input.Apply(Hover); break;
            case PenAction.Leave: input.Apply(Contact); input.Release(); break;
        }
        Check(failed && creations == 2, $"Win32 87 recovers {failingAction}");
        Check(levels.Contains(LogLevel.Error) && !levels.Contains(LogLevel.Fatal), "87 stays Error");
        Check(threads.Distinct().Count() == 1 && threads[0] != Environment.CurrentManagedThreadId, "Device thread affinity");
    }

    private static void CheckCreationAndCleanupRecovery()
    {
        int attempts = 0;
        var levels = new List<LogLevel>();
        using (var input = new ThreadedPenInput(() =>
        {
            if (++attempts <= 6) throw new Win32Exception(87);
            return new Frames(_ => { });
        }, (_, level) => levels.Add(level))) input.Release();
        Check(attempts == 7, "Creation recovery exceeds old three-per-minute limit");
        Check(levels.Contains(LogLevel.Error) && !levels.Contains(LogLevel.Fatal), "Repeated creation 87 never escalates");

        int created = 0;
        int disposed = 0;
        using var cleanup = new ThreadedPenInput(() =>
        {
            bool first = ++created == 1;
            return new Frames(_ => { if (first) throw new Win32Exception(87); }, () =>
            {
                disposed++;
                if (first) throw new Win32Exception(87);
            });
        }, (_, level) => levels.Add(level));
        cleanup.Apply(Contact);
        Check(created == 2 && disposed == 1, "Cleanup 87 does not abort reconnection");
    }

    private static async Task CheckCancellation()
    {
        using var failed = new ManualResetEventSlim();
        using var input = new ThreadedPenInput(() => new Frames(_ => throw new Win32Exception(87)),
            (_, _) => failed.Set());
        Task apply = Task.Run(() => input.Apply(Contact));
        Check(failed.Wait(TimeSpan.FromSeconds(2)), "Permanent failure enters recovery");
        var watch = Stopwatch.StartNew();
        input.Dispose();
        await apply.WaitAsync(TimeSpan.FromSeconds(2));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Stop interrupts infinite retry promptly");
    }

    private static async Task CheckExpiredInput()
    {
        int creations = 0;
        int successes = 0;
        using var input = new ThreadedPenInput(() =>
        {
            if (++creations > 1 && creations <= 6) throw new Win32Exception(87);
            bool first = creations == 1;
            return new Frames(_ =>
            {
                if (first) throw new Win32Exception(87);
                successes++;
            });
        }, (_, _) => { });
        await Task.Run(() => input.Apply(Contact)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(successes == 0, "Long reconnect does not replay stale DOWN");
        input.Apply(Hover);
        Check(successes > 0, "Latest packet resumes after dropping stale input");
    }

    private static async Task CheckListenerCancellation()
    {
        using var failed = new ManualResetEventSlim();
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        using var listener = new UdpListener(port, () => new ThreadedPenInput(
            () => new Frames(_ => throw new Win32Exception(87)), (_, _) => failed.Set()));
        using var sender = new UdpClient();
        byte[] packet = new byte[16];
        UdpPenSender.Encode(packet, 1, 1, new PenSample(100, 200, 300, PenFlags.InRange | PenFlags.Contact));
        await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port));
        Check(failed.Wait(TimeSpan.FromSeconds(2)), "UDP triggers simulated injection failure");
        var watch = Stopwatch.StartNew();
        listener.GetSnapshot();
        Check(watch.Elapsed < TimeSpan.FromMilliseconds(500), "UI snapshot is not blocked by recovery");
        listener.Dispose();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Listener exits during permanent recovery");
        using var rebound = new UdpClient(port);
        Check(rebound.Client.IsBound, "Stop releases UDP port");
    }

    private static async Task CheckSender()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new UdpClient();
        client.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        var reports = new ConcurrentQueue<LogLevel>();
        using var sender = new UdpPenSender(client, (_, level) => reports.Enqueue(level));
        var packet = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(packet.Buffer.Length == 16, "Sender still sends initial release heartbeat");
        sender.Dispose();
        await sender.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!reports.Contains(LogLevel.Fatal), "Normal sender disposal is not Fatal");
    }

    private sealed class Frames : IPenInputSink
    {
        private readonly PenInjectionState _state;
        private readonly Action? _dispose;
        public Frames(Action<PenFrame> inject, Action? dispose = null)
        {
            _state = new PenInjectionState(inject);
            _dispose = dispose;
        }
        public void Apply(PenPacket packet) => _state.Apply(packet);
        public void Release() => _state.Release();
        public void Dispose() => _dispose?.Invoke();
    }
}
