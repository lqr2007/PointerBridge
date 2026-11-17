using System;
using System.Threading;
using System.Windows;
using PointerBridge;

namespace PointerBridge_Client;

public partial class App : Application
{
    private int _terminating;

    public App()
    {
        DebugLog.FatalReported += OnFatal;
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            DebugLog.Write($"未处理的致命异常：{e.Exception}", LogLevel.Fatal);
        };
        Exit += (_, _) => DebugLog.FatalReported -= OnFatal;
    }

    private void OnFatal(string message)
    {
        if (Interlocked.Exchange(ref _terminating, 1) != 0) return;
        void Terminate()
        {
            try
            {
                MessageBox.Show($"[Fatal] {message}\n程序即将终止。", "PointerBridge 致命错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { Shutdown(1); }
        }
        if (Dispatcher.CheckAccess()) Terminate();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Terminate));
    }
}
