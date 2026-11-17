using System;
using System.Collections.Generic;
using System.Windows;
using PointerBridge;

namespace PointerBridge_Client;

public partial class DebugWindow : Window
{
    private readonly Queue<string> _messages = new();
    public DebugWindow()
    {
        InitializeComponent();
        foreach (string entry in DebugLog.Entries) AppendEntry(entry);
        DebugLog.EntryWritten += OnEntryWritten;
        Closed += (_, _) => DebugLog.EntryWritten -= OnEntryWritten;
    }

    public void AppendMessage(string message, LogLevel level = LogLevel.Info) => DebugLog.Write(message, level);

    private void OnEntryWritten(string entry)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => AppendEntry(entry)));
    }

    private void AppendEntry(string entry)
    {
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
        DebugLog.Clear();
        _messages.Clear();
        LogTextBox.Clear();
    }
}
