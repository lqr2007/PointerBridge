using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PointerBridge_Client
{
    public partial class PointerSensorWindow : Window
    {
        // 首次创建 HWND 前的占位尺寸，显示前会替换为采集屏完整物理像素区域。
        private const int InitialPixelLeft = 100;
        private const int InitialPixelTop = 100;
        private const int InitialPixelWidth = 840;
        private const int InitialPixelHeight = 600;

        public int PixelLeft { get; private set; } = InitialPixelLeft;
        public int PixelTop { get; private set; } = InitialPixelTop;
        public int PixelWidth { get; private set; } = InitialPixelWidth;
        public int PixelHeight { get; private set; } = InitialPixelHeight;

        private IntPtr _windowHandle;
        private string? _latestPenInfo;
        private DateTime _lastPenUpdate;
        private bool _penInWindow;
        private readonly PenExitGesture _exitGesture = new();
        private readonly DispatcherTimer _gestureTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private double _aspectRatio = 16.0 / 9.0;
        private bool _hasPenSample;
        private bool _closing;
        internal PenSample LatestSample { get; private set; }
        internal event Action<PenSample>? SampleUpdated;

        // 提供独立的状态供日志去重，不比较含时间、坐标的动态文本。
        public bool HasPenSample => _hasPenSample;
        public bool IsPenInWindow => _penInWindow;
        public double CaptureAspectRatio => _aspectRatio;

        public PointerSensorWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) =>
            {
                _windowHandle = new WindowInteropHelper(this).Handle;
                SelectCaptureMonitor();
                ApplyPhysicalBounds();
            };
            // 首次显示的 WPF 布局可能再次调整窗口，显示完成后重新保证物理尺寸。
            ContentRendered += (_, _) => ApplyPhysicalBounds();
            _gestureTimer.Tick += (_, _) =>
            {
                if (_exitGesture.IsComplete(Stopwatch.GetElapsedTime(0))) Close();
            };
            Loaded += (_, _) => _gestureTimer.Start();
            Deactivated += (_, _) => ResetPenInput();
            Closing += (_, _) =>
            {
                _closing = true;
                _gestureTimer.Stop();
                ResetPenInput();
            };
            Closed += (_, _) => _windowHandle = IntPtr.Zero;
        }

        public void SetCaptureAspectRatio(double aspectRatio)
        {
            Dispatcher.VerifyAccess();
            if (!double.IsFinite(aspectRatio) || aspectRatio <= 0)
                throw new ArgumentOutOfRangeException(nameof(aspectRatio));
            ResetPenInput();
            _aspectRatio = aspectRatio;
            UpdateCaptureLayout();
        }

        private void CaptureHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCaptureLayout();

        private void UpdateCaptureLayout()
        {
            if (CaptureArea == null || CaptureHost.ActualWidth <= 0 || CaptureHost.ActualHeight <= 0) return;
            CaptureLayout layout = CaptureLayout.Fit(CaptureHost.ActualWidth, CaptureHost.ActualHeight, _aspectRatio);
            CaptureArea.Width = layout.Width;
            CaptureArea.Height = layout.Height;
            // 区域尺寸改变时，不能继续发送使用旧区域映射的接触状态。
            ResetPenInput();
        }

        private void ResetPenInput()
        {
            _exitGesture.Reset();
            _penInWindow = false;
            PublishSample(LatestSample.Released);
        }

        private void OnPenInput(object sender, StylusEventArgs e)
        {
            // 排除 WPF 以 Stylus 事件上报的手指触摸，只记录触控笔。
            if (_closing || e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus)
            {
                return;
            }

            bool penOverWindow = e.RoutedEvent != Stylus.StylusLeaveEvent
                && e.RoutedEvent != Stylus.StylusOutOfRangeEvent;

            if (!penOverWindow)
            {
                ResetPenInput();
                return;
            }

            // 相对于白色区域取点，黑边不参与映射。
            StylusPointCollection points = e.GetStylusPoints(CaptureArea);
            StylusPoint? latestPoint = points.Count > 0 ? points[points.Count - 1] : null;
            bool inAir = e.StylusDevice.InAir || e.RoutedEvent == Stylus.PreviewStylusUpEvent;
            if (e.RoutedEvent == Stylus.PreviewStylusDownEvent)
                inAir = false;
            bool? barrelDown = null;
            foreach (StylusButton button in e.StylusDevice.StylusButtons)
            {
                // 用标准标识识别侧键，不依赖驱动提供的按钮名称。
                if (button.Guid == StylusPointProperties.BarrelButton.Id)
                {
                    barrelDown = button.StylusButtonState == StylusButtonState.Down;
                    break;
                }
            }

            if (barrelDown == null && latestPoint is StylusPoint buttonPoint
                && buttonPoint.Description.HasProperty(StylusPointProperties.BarrelButton))
            {
                barrelDown = buttonPoint.GetPropertyValue(StylusPointProperties.BarrelButton) != 0;
            }

            // 按钮事件本身是该次状态变化的权威来源。
            if (e is StylusButtonEventArgs buttonEvent
                && buttonEvent.StylusButton.Guid == StylusPointProperties.BarrelButton.Id)
                barrelDown = e.RoutedEvent == Stylus.PreviewStylusButtonDownEvent;

            // 退出手势覆盖整个屏幕，包括黑边；鼠标和手指不能触发。
            if (_exitGesture.Update(Stopwatch.GetElapsedTime(0), e.StylusDevice.Id,
                e.StylusDevice.InRange, barrelDown == true, !inAir))
            {
                Close();
                return;
            }

            if (latestPoint is not StylusPoint point || CaptureArea.ActualWidth <= 0
                || CaptureArea.ActualHeight <= 0) return;

            _hasPenSample = true;
            _penInWindow = point.X >= 0 && point.Y >= 0
                && point.X < CaptureArea.ActualWidth && point.Y < CaptureArea.ActualHeight;
            double normalizedX = Math.Clamp(point.X / CaptureArea.ActualWidth, 0.0, 1.0);
            double normalizedY = Math.Clamp(point.Y / CaptureArea.ActualHeight, 0.0, 1.0);
            float pressure = inAir || !_penInWindow ? 0 : point.PressureFactor;

            PenFlags flags = e.StylusDevice.Inverted ? PenFlags.Eraser : PenFlags.None;
            if (barrelDown.HasValue) flags |= PenFlags.BarrelKnown;
            if (_penInWindow)
            {
                flags |= PenFlags.InRange;
                if (!inAir) flags |= PenFlags.Contact;
                if (barrelDown == true) flags |= PenFlags.BarrelDown;
            }
            if (!_penInWindow)
            {
                // 黑边只发送释放状态，不把黑边坐标夹到白色边缘继续绘制。
                PublishSample(LatestSample.Released);
                return;
            }
            PublishSample(new PenSample(PenSample.Quantize(normalizedX), PenSample.Quantize(normalizedY),
                PenSample.Quantize(pressure), flags));

            string tool = e.StylusDevice.Inverted ? "橡皮" : "笔尖";
            string contact = inAir ? "悬空" : "接触";
            string barrel = barrelDown.HasValue ? (barrelDown.Value ? "按下" : "松开") : "未知";
            _latestPenInfo = $"{tool} | {contact} | X={normalizedX:F4} Y={normalizedY:F4} " +
                $"| 压感={pressure:F3} | 侧键={barrel}";
            _lastPenUpdate = DateTime.Now;
        }

        private void PublishSample(PenSample sample)
        {
            LatestSample = sample;
            SampleUpdated?.Invoke(sample);
        }

        public string GetPenDebugInfo()
        {
            if (!_hasPenSample)
            {
                return "触控笔：未检测到。";
            }

            if (!_penInWindow)
            {
                return "触控笔：已离开采集区域。";
            }

            return $"{_latestPenInfo} | 更新于 {(DateTime.Now - _lastPenUpdate).TotalSeconds:F1}s前";
        }

        /// <summary>按物理像素设置窗口矩形，可在打开前或打开后调用。</summary>
        public void SetPhysicalBounds(int left, int top, int width, int height)
        {
            Dispatcher.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            PixelLeft = left;
            PixelTop = top;
            PixelWidth = width;
            PixelHeight = height;
            ApplyPhysicalBounds();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            // WPF 完成 DPI 布局后，仍保持指定的物理尺寸，而不是固定 DIP 尺寸。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing) return;
                SelectCaptureMonitor();
                ApplyPhysicalBounds();
                UpdateCaptureLayout();
            }));
        }

        private void SelectCaptureMonitor()
        {
            IntPtr anchor = Owner != null ? new WindowInteropHelper(Owner).Handle : _windowHandle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(anchor, 2), ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // 使用 rcMonitor 而不是 rcWork，因此包括任务栏区域。
            PixelLeft = info.Monitor.Left;
            PixelTop = info.Monitor.Top;
            PixelWidth = info.Monitor.Right - info.Monitor.Left;
            PixelHeight = info.Monitor.Bottom - info.Monitor.Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, Work;
            public uint Flags;
        }
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private void ApplyPhysicalBounds()
        {
            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            const uint SwpNoZOrder = 0x0004;
            const uint SwpNoActivate = 0x0010;
            if (!SetWindowPos(_windowHandle, IntPtr.Zero, PixelLeft, PixelTop,
                PixelWidth, PixelHeight, SwpNoZOrder | SwpNoActivate))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);
    }
}
