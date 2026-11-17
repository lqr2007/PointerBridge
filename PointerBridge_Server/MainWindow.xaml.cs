using PointerBridge;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Globalization;
using System.Net.Sockets;
using System.Windows.Threading;
using System.ComponentModel;

namespace PointerBridge_Server
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private DebugWindow? _debugWindow;
        private UdpListener? _listener;
        private int? _desiredPort;
        private readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _debugTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private (UdpListener? Listener, PenReceiveStatus? Status, string? Source, uint? Session)? _lastDebugState;

        public MainWindow()
        {
            InitializeComponent();
            _reconnectTimer.Tick += (_, _) =>
            {
                _reconnectTimer.Stop();
                if (_desiredPort is int port) StartListening(port);
            };
            _debugTimer.Tick += (_, _) => UpdateDebug();
            Closed += (_, _) =>
            {
                _debugTimer.Stop();
                StopListening();
            };
        }

        private void Listen_Click(object sender, RoutedEventArgs e)
        {
            if (_desiredPort != null)
            {
                StopListening();
                return;
            }

            if (!int.TryParse(ListenPortTextBox.Text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            {
                const string message = "监听端口必须为 1～65535 之间的整数。";
                DebugLog.Write(message, LogLevel.Warning);
                ListenStatusTextBlock.Text = message;
                ListenPortTextBox.Focus();
                return;
            }

            _desiredPort = port;
            ListenButton.Content = "结束监听";
            ListenPortTextBox.IsEnabled = false;
            StartListening(port);
        }

        private void StartListening(int port)
        {
            try
            {
                _listener = new UdpListener(port, () => new ThreadedPenInput(
                    () => new SyntheticPenInput(), DebugLog.Write));
            }
            catch (Exception ex) when (ex is SocketException or Win32Exception)
            {
                ScheduleReconnect($"无法启动监听/笔设备：{ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"接收端初始化发生不可恢复错误：{ex}", LogLevel.Fatal);
                return;
            }

            ListenStatusTextBlock.Text = $"正在监听 UDP 端口 {port}，虚拟触控笔映射到主屏幕（仅用于可信局域网）";
            DebugLog.Write($"开始监听 UDP 端口 {port}，笔设备支持自动重连，目标为主屏幕。");
            UpdateDebug();
            _ = ObserveListenerAsync(_listener);
        }

        private void ScheduleReconnect(string message)
        {
            DebugLog.Write(message, LogLevel.Error);
            if (_desiredPort == null) return;
            ListenStatusTextBlock.Text = $"{message}；正在自动重连（点击结束监听可取消）。";
            _reconnectTimer.Start();
        }

        private async Task ObserveListenerAsync(UdpListener listener)
        {
            try
            {
                await listener.Completion;
                if (ReferenceEquals(_listener, listener) && _desiredPort != null)
                {
                    DisposeListener();
                    ScheduleReconnect("监听任务已结束。");
                }
            }
            catch (Exception ex)
            {
                // An old listener must not restart or terminate a newer listener.
                if (!ReferenceEquals(_listener, listener)) return;
                DisposeListener();
                if (ex is SocketException or Win32Exception)
                    ScheduleReconnect($"接收/笔注入失败：{ex.Message}");
                else
                    DebugLog.Write($"监听任务发生不可恢复错误：{ex}", LogLevel.Fatal);
            }
        }

        private void DisposeListener()
        {
            UdpListener? listener = _listener;
            _listener = null;
            try { listener?.Dispose(); }
            catch (Win32Exception ex)
            {
                DebugLog.Write($"释放触控笔失败，已销毁合成设备：{ex.Message}", LogLevel.Error);
            }
        }

        private void StopListening()
        {
            _desiredPort = null;
            _reconnectTimer.Stop();
            DisposeListener();
            ListenButton.Content = "开始监听";
            ListenPortTextBox.IsEnabled = true;
            ListenStatusTextBlock.Text = "未监听";
            UpdateDebug();
        }

        private void Debug_Click(object sender, RoutedEventArgs e)
        {
            if (_debugWindow != null)
            {
                if (_debugWindow.WindowState == WindowState.Minimized)
                    _debugWindow.WindowState = WindowState.Normal;
                _debugWindow.Activate();
                return;
            }

            _debugWindow = new DebugWindow { Owner = this };
            _debugWindow.Closed += (_, _) =>
            {
                _debugTimer.Stop();
                _debugWindow = null;
            };
            _debugWindow.Show();
            DebugLog.Write("笔状态每秒显示一次，等待/离开/超时仅在状态改变时提示。X/Y、压感范围 0～1；侧键未知表示发送端未提供状态。");
            _lastDebugState = null;
            UpdateDebug();
            _debugTimer.Start();
        }

        private void UpdateDebug()
        {
            if (_debugWindow == null) return;
            PenReceiveSnapshot? snapshot = _listener?.GetSnapshot();
            var state = (Listener: _listener, Status: snapshot?.Status,
                Source: snapshot?.Source, Session: snapshot?.Packet?.Session);
            bool changed = _lastDebugState != state;
            if (!changed && snapshot?.Status != PenReceiveStatus.Active) return;

            if (changed && snapshot?.Source != null
                && (_lastDebugState?.Source != state.Source || _lastDebugState?.Session != state.Session))
                DebugLog.Write($"接收来源 {snapshot.Source}，会话 {snapshot.Packet!.Value.Session:X8}。");

            _lastDebugState = state;
            DebugLog.Write(snapshot?.FormatDebug() ?? "UDP：未监听。",
                snapshot?.Status == PenReceiveStatus.TimedOut ? LogLevel.Warning : LogLevel.Info);
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this, "PointerBridge 接收端", "关于 PointerBridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
