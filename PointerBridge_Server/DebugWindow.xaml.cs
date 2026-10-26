using System.Windows;

namespace PointerBridge_Server;

public partial class DebugWindow : Window
{
    private readonly Queue<string> _messages = new();
    public DebugWindow()
    {
        InitializeComponent();
    }

    public void AppendMessage(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        _messages.Enqueue(entry);
        if (_messages.Count > 1000)
        {
            _messages.Dequeue();
            LogTextBox.Text = string.Concat(_messages);
        }
        else LogTextBox.AppendText(entry);
        LogTextBox.ScrollToEnd();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        LogTextBox.Clear();
    }
}
