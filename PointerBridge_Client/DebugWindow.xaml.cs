using System.Windows;

namespace PointerBridge_Client
{
    public partial class DebugWindow : Window
    {
        private const int MaxMessages = 1000;
        private readonly Queue<string> _messages = new();

        public DebugWindow()
        {
            InitializeComponent();
        }

        public void AppendMessage(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendMessage(message)));
                return;
            }

            string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            _messages.Enqueue(entry);
            if (_messages.Count > MaxMessages)
            {
                _messages.Dequeue();
                LogTextBox.Text = string.Concat(_messages);
            }
            else
            {
                LogTextBox.AppendText(entry);
            }

            LogTextBox.ScrollToEnd();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _messages.Clear();
            LogTextBox.Clear();
        }
    }
}
