using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PointerBridge_Server;

// Windows 10 1809+ 的合成 PT_PEN 设备，不安装虚拟 HID 驱动。
internal sealed class SyntheticPenInput : IPenInputSink
{
    private IntPtr _device;
    private readonly PenInjectionState _state;
    private readonly PointerTypeInfo[] _buffer = new PointerTypeInfo[1];
    private bool _barrelDown;
    private readonly Queue<string> _recentFrames = new();
    private int _successfulFrames;
    private bool _injectionFailed;
    private readonly int _creationThreadId = Environment.CurrentManagedThreadId;
    private long _lastFrameTime;

    public SyntheticPenInput()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("合成触控笔需要 Windows 10 1809 或更新版本。");

        // POINTER_FEEDBACK_NONE = 3；2 是合法的 INDIRECT，但不是无反馈模式。
        _device = CreateSyntheticPointerDevice(3, 1, 3); // PT_PEN, 一支笔, 无系统反馈
        if (_device == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"无法创建 Windows 合成触控笔设备（Win32 {error}）：{new Win32Exception(error).Message}");
        }
        _state = new PenInjectionState(Inject);
    }

    public void Apply(PenPacket packet) => _state.Apply(packet);
    public void Release() => _state.Release();

    private void Inject(PenFrame frame)
    {
        // 主屏幕区域，物理像素坐标；DPI manifest 禁止窗口坐标虚拟化。
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(default, 1), ref monitor))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int width = monitor.Monitor.Right - monitor.Monitor.Left;
        int height = monitor.Monitor.Bottom - monitor.Monitor.Top;
        var point = new NativePoint
        {
            X = monitor.Monitor.Left + (int)Math.Round(frame.X / 65535.0 * (width - 1)),
            Y = monitor.Monitor.Top + (int)Math.Round(frame.Y / 65535.0 * (height - 1))
        };

        // 使用常规接触按钮标志；UP 不再表示保持接触。
        uint flags = 0;
        if (frame.Action != PenAction.Leave) flags |= 0x2; // INRANGE
        if (frame.Contact) flags |= 0x4 | 0x10; // INCONTACT | FIRSTBUTTON
        if (frame.BarrelDown) flags |= 0x20; // SECONDBUTTON
        flags |= frame.Action switch
        {
            PenAction.Down => 0x10000u,
            PenAction.Up => 0x40000u,
            _ => 0x20000u // UPDATE；首次悬停和离开均由范围标志表达。
        };

        _buffer[0] = new PointerTypeInfo
        {
            Type = 3,
            Data = new PointerUnion
            {
                Pen = new PointerPenInfo
                {
                    Pointer = new PointerInfo
                    {
                        Type = 3,
                        Id = 0, // 单笔设备使用槽位 0。
                        Flags = flags,
                        PixelLocation = point,
                        ButtonChange = frame.Action == PenAction.Down ? 1u
                            : frame.Action == PenAction.Up ? 2u
                            : frame.BarrelDown != _barrelDown ? (frame.BarrelDown ? 3u : 4u) : 0u
                    },
                    PenFlags = (frame.Eraser ? 0x2u | 0x4u : 0u) | (frame.BarrelDown ? 1u : 0u),
                    PenMask = 1, // PEN_MASK_PRESSURE；未传输倾斜/旋转，不伪造有效值。
                    Pressure = frame.Contact ? (uint)Math.Round(frame.Pressure / 65535.0 * 1024) : 0
                }
            }
        };
        string description = $"{frame.Action}:flags={flags:X},xy={point.X}/{point.Y}," +
            $"pen={_buffer[0].Data.Pen.PenFlags:X},pressure={_buffer[0].Data.Pen.Pressure}," +
            $"thread={Environment.CurrentManagedThreadId}";
        if (!InjectSyntheticPointerInput(_device, _buffer, 1))
        {
            int error = Marshal.GetLastWin32Error();
            _injectionFailed = true;
            throw new Win32Exception(error,
                $"Pen injection failed: Win32={error}, Action={frame.Action}, " +
                $"Flags=0x{flags:X}, XY=({point.X},{point.Y}), " +
                $"PenFlags=0x{_buffer[0].Data.Pen.PenFlags:X}, Pressure={_buffer[0].Data.Pen.Pressure}, " +
                $"ABI={Marshal.SizeOf<PointerInfo>()}/{Marshal.SizeOf<PointerTypeInfo>()}. " +
                $"OS={Environment.OSVersion.Version}, SuccessCount={_successfulFrames}, " +
                $"Threads={_creationThreadId}/{Environment.CurrentManagedThreadId}, " +
                $"GapMs={(_lastFrameTime == 0 ? -1 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrameTime).TotalMilliseconds):F1}, " +
                $"Monitor=({monitor.Monitor.Left},{monitor.Monitor.Top},{width},{height}), " +
                $"LastFrames=[{string.Join("; ", _recentFrames)}]. " +
                new Win32Exception(error).Message);
        }
        _barrelDown = frame.BarrelDown;
        _successfulFrames++;
        _lastFrameTime = System.Diagnostics.Stopwatch.GetTimestamp();
        _recentFrames.Enqueue(description);
        if (_recentFrames.Count > 8) _recentFrames.Dequeue();
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        // 失败后设备状态已不可信，直接销毁；不要让清理失败覆盖第一条注入异常。
        try { if (!_injectionFailed) _state.Release(); }
        finally
        {
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
    }

    // POINTER_TYPE_INFO 是 type + union{touchInfo,penInfo}。
    // 不能仅声明 penInfo：较大的 touchInfo 决定原生 union 的大小和数组步长。
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerTypeInfo { public uint Type; public PointerUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct PointerUnion
    {
        [FieldOffset(0)] public PointerPenInfo Pen;
        [FieldOffset(0)] public PointerTouchInfo Touch;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerInfo
    {
        public uint Type, Id, FrameId, Flags;
        public IntPtr SourceDevice, Target;
        public NativePoint PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerPenInfo
    {
        public PointerInfo Pointer;
        public uint PenFlags, PenMask, Pressure, Rotation;
        public int TiltX, TiltY;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerTouchInfo
    {
        public PointerInfo Pointer;
        public uint TouchFlags, TouchMask;
        public NativeRect Contact, ContactRaw;
        public uint Orientation, Pressure;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint type, uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device,
        [In] PointerTypeInfo[] info, uint count);
    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
