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
        private readonly DispatcherTimer _debugTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private (UdpListener? Listener, PenReceiveStatus? Status, string? Source, uint? Session)? _lastDebugState;

        public MainWindow()
        {
            InitializeComponent();
            _debugTimer.Tick += (_, _) => UpdateDebug();
            Closed += (_, _) =>
            {
                _debugTimer.Stop();
                StopListening();
            };
        }

        private void Listen_Click(object sender, RoutedEventArgs e)
        {
            if (_listener != null)
            {
                StopListening();
                return;
            }

            if (!int.TryParse(ListenPortTextBox.Text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "监听端口必须为 1～65535 之间的整数。", "监听端口",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ListenPortTextBox.Focus();
                return;
            }

            try
            {
                _listener = new UdpListener(port, () => new ThreadedPenInput(
                    () => new SyntheticPenInput(), message =>
                    {
                        if (!Dispatcher.HasShutdownStarted)
                            Dispatcher.BeginInvoke(new Action(() => _debugWindow?.AppendMessage(message)));
                    }));
            }
            catch (SocketException ex)
            {
                MessageBox.Show(this, $"无法监听端口 {port}，请检查端口是否已被占用。\n{ex.Message}",
                    "监听失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException
                or EntryPointNotFoundException or DllNotFoundException)
            {
                MessageBox.Show(this, ex.Message, "虚拟触控笔初始化失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ListenButton.Content = "结束监听";
            ListenPortTextBox.IsEnabled = false;
            ListenStatusTextBlock.Text = $"正在监听 UDP 端口 {port}，虚拟触控笔映射到主屏幕（仅用于可信局域网）";
            _debugWindow?.AppendMessage($"开始监听 UDP 端口 {port}，虚拟触控笔已启用，目标为主屏幕。");
            UpdateDebug();
            _ = ObserveListenerAsync(_listener);
        }

        private async Task ObserveListenerAsync(UdpListener listener)
        {
            try
            {
                await listener.Completion;
            }
            catch (Exception ex) when (ex is SocketException or Win32Exception)
            {
                // 旧监听任务的结束不能影响后来新建的监听。
                if (!ReferenceEquals(_listener, listener)) return;
                StopListening();
                ListenStatusTextBlock.Text = $"监听已停止：{ex.Message}";
                _debugWindow?.AppendMessage($"接收/笔注入失败：{ex.Message}");
                MessageBox.Show(this, ex.Message, "接收/笔注入失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void StopListening()
        {
            UdpListener? listener = _listener;
            _listener = null;
            try { listener?.Dispose(); }
            catch (Win32Exception ex)
            {
                _debugWindow?.AppendMessage($"释放触控笔失败，已销毁合成设备：{ex.Message}");
            }
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
            _debugWindow.AppendMessage("笔状态每秒显示一次，等待/离开/超时仅在状态改变时提示。X/Y、压感范围 0～1；侧键未知表示发送端未提供状态。");
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
                _debugWindow.AppendMessage($"接收来源 {snapshot.Source}，会话 {snapshot.Packet!.Value.Session:X8}。");

            _lastDebugState = state;
            _debugWindow.AppendMessage(snapshot?.FormatDebug() ?? "UDP：未监听。");
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
