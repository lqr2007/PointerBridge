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
using System.Windows.Threading;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PointerBridge_Client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private PointerSensorWindow? _pointerSensorWindow;
        private DebugWindow? _debugWindow;
        private UdpClient? _udpClient;
        private UdpPenSender? _penSender;
        private IPEndPoint? _udpRemoteEndPoint;
        private readonly ClientSettingsStore _settingsStore = new();
        private (PointerSensorWindow? Window, bool HasSample, bool InWindow)? _lastPenDebugState;
        // 修改这里可调整调试信息输出间隔。
        private readonly DispatcherTimer _penDebugTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        public MainWindow()
        {
            InitializeComponent();
            TargetIpTextBox.Text = _settingsStore.LoadLastUdpIp() ?? string.Empty;
            _penDebugTimer.Tick += OnPenDebugTimerTick;
            Closed += (_, _) =>
            {
                _penDebugTimer.Stop();
                CloseUdpConnection();
            };
        }

        private void ADR_TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }

        private void COM_TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }

        private void SAR_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _pointerSensorWindow?.SetCaptureAspectRatio(GetSelectedAspectRatio());
        }

        private double GetSelectedAspectRatio() => SAR.SelectedIndex switch
        {
            1 => 16.0 / 10.0,
            2 => 4.0 / 3.0,
            _ => 16.0 / 9.0
        };

        private void Connect(object sender, RoutedEventArgs e)
        {
            if (!IPAddress.TryParse(TargetIpTextBox.Text.Trim(), out IPAddress? address)
                || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                ShowConnectionError("请输入有效的目标 IPv4 或 IPv6 地址，不能使用地址 0.0.0.0 或 ::。");
                TargetIpTextBox.Focus();
                return;
            }

            if (!int.TryParse(TargetPortTextBox.Text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            {
                ShowConnectionError("目标 UDP 端口必须为 1～65535 之间的整数。");
                TargetPortTextBox.Focus();
                return;
            }

            var remoteEndPoint = new IPEndPoint(address, port);
            if (_udpClient == null || !remoteEndPoint.Equals(_udpRemoteEndPoint))
            {
                UdpClient? candidate = null;
                try
                {
                    candidate = new UdpClient(address.AddressFamily);
                    // UDP Connect 仅设置默认远端，不握手，也不证明远端在线。
                    candidate.Connect(remoteEndPoint);
                }
                catch (SocketException ex)
                {
                    candidate?.Dispose();
                    ShowConnectionError($"无法初始化 UDP 目标 {remoteEndPoint}：{ex.Message}");
                    return;
                }

                // 新目标初始化成功后再替换，失败时不影响已有连接。
                CloseUdpConnection();
                _udpClient = candidate;
                _udpRemoteEndPoint = remoteEndPoint;
                _penSender = new UdpPenSender(candidate, message =>
                {
                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(new Action(() => _debugWindow?.AppendMessage(message)));
                });
                if (_pointerSensorWindow != null)
                    _penSender.Update(_pointerSensorWindow.LatestSample);
                _debugWindow?.AppendMessage(GetUdpStatus());
            }
            else
            {
                _debugWindow?.AppendMessage($"复用 UDP 目标 {_udpRemoteEndPoint}（未确认远端在线）。");
            }

            // 仅保存校验通过且 UDP 已成功初始化的 IP，不保存尚未连接的编辑内容。
            if (!_settingsStore.TrySaveLastUdpIp(address.ToString(), out string? saveError))
            {
                string message = $"UDP 已初始化，但无法记住本次 IP：{saveError}";
                _debugWindow?.AppendMessage(message);
                MessageBox.Show(this, message, "保存地址失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (_pointerSensorWindow != null)
            {
                _pointerSensorWindow.Activate();
                _debugWindow?.AppendMessage("已激活现有的 PointerSensorWindow。");
                return;
            }

            _pointerSensorWindow = new PointerSensorWindow
            {
                Owner = this
            };
            _pointerSensorWindow.SampleUpdated += OnPenSampleUpdated;
            _pointerSensorWindow.SetCaptureAspectRatio(GetSelectedAspectRatio());
            _pointerSensorWindow.Closed += (_, _) =>
            {
                if (_pointerSensorWindow != null)
                    _pointerSensorWindow.SampleUpdated -= OnPenSampleUpdated;
                _pointerSensorWindow = null;
                CloseUdpConnection();
                _debugWindow?.AppendMessage("PointerSensorWindow 已关闭。");
            };
            _pointerSensorWindow.Show();
            _debugWindow?.AppendMessage($"PointerSensorWindow 已打开，物理像素位置为 " +
                $"({_pointerSensorWindow.PixelLeft}, {_pointerSensorWindow.PixelTop})，" +
                $"全屏尺寸为 {_pointerSensorWindow.PixelWidth} × {_pointerSensorWindow.PixelHeight}，" +
                $"白色区域比例为 {_pointerSensorWindow.CaptureAspectRatio:F4}。按住侧键悬停 1 秒，再保持接触 1 秒退出。");
        }

        private void CloseUdpConnection()
        {
            if (_udpClient == null)
            {
                return;
            }

            // 发送器拥有套接字，在后台发送最终释放状态后负责 Dispose。
            _penSender?.Dispose();
            _penSender = null;
            _udpClient = null;
            _debugWindow?.AppendMessage($"正在释放 UDP 目标 {_udpRemoteEndPoint}，发送最后的抬笔状态。");
            _udpRemoteEndPoint = null;
        }

        private string GetUdpStatus()
        {
            return _udpClient == null
                ? "UDP 尚未初始化。"
                : $"UDP 已设置目标 {_udpRemoteEndPoint}，本地端点 {_udpClient.Client.LocalEndPoint}。" +
                  "二进制笔数据每包 16 字节，移动最高约 120Hz，关键状态优先，心跳 250ms；未确认远端在线。";
        }

        private void OnPenSampleUpdated(PenSample sample) => _penSender?.Update(sample);

        private void ShowConnectionError(string message)
        {
            _debugWindow?.AppendMessage($"连接未启动：{message}");
            MessageBox.Show(this, message, "UDP 连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Debug_Click(object sender, RoutedEventArgs e)
        {
            if (_debugWindow != null)
            {
                if (_debugWindow.WindowState == WindowState.Minimized)
                {
                    _debugWindow.WindowState = WindowState.Normal;
                }

                _debugWindow.Activate();
                return;
            }

            _debugWindow = new DebugWindow
            {
                Owner = this
            };
            _debugWindow.Closed += (_, _) =>
            {
                _penDebugTimer.Stop();
                _debugWindow = null;
            };
            _debugWindow.Show();
            _debugWindow.AppendMessage("触控笔快照每秒显示一次，等待状态仅提示一次。X/Y 为区域内归一化坐标，压感范围 0～1；侧键未知表示驱动未提供状态。");
            _debugWindow.AppendMessage(GetUdpStatus());
            // 新日志窗口应显示一次当前状态，不能沿用上个窗口的去重记录。
            _lastPenDebugState = null;
            OnPenDebugTimerTick(null, EventArgs.Empty);
            _penDebugTimer.Start();
        }

        private void OnPenDebugTimerTick(object? sender, EventArgs e)
        {
            if (_debugWindow == null)
            {
                return;
            }

            var state = (Window: _pointerSensorWindow,
                HasSample: _pointerSensorWindow?.HasPenSample ?? false,
                InWindow: _pointerSensorWindow?.IsPenInWindow ?? false);
            bool hasActivePen = state.Window != null && state.HasSample && state.InWindow;

            // 正常采集仍每秒输出；等待类状态只在进入该状态时输出一次。
            // 包含窗口实例，可区分关闭后重新创建的新一轮采集。
            if (!hasActivePen && _lastPenDebugState == state)
            {
                return;
            }

            _lastPenDebugState = state;
            _debugWindow.AppendMessage(_pointerSensorWindow?.GetPenDebugInfo()
                ?? "触控笔：采集窗口未打开。");
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
    }
}
