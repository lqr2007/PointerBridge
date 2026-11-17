using System;
using System.Collections.Generic;
using System.Linq;

namespace PointerBridge;

public enum LogLevel { Info, Warning, Error, Fatal }

// Keep diagnostics even when the debug window has not been opened.
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly Queue<string> History = new();
    public static event Action<string>? EntryWritten;
    public static event Action<string>? FatalReported;

    public static string[] Entries
    {
        get { lock (Gate) return History.ToArray(); }
    }

    public static void Write(string message, LogLevel level = LogLevel.Info)
    {
        lock (Gate)
        {
            // Prefix every physical line, including multiline Win32 diagnostics.
            foreach (string line in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string entry = $"[{level}] [{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}";
                History.Enqueue(entry);
                if (History.Count > 1000) History.Dequeue();
                EntryWritten?.Invoke(entry);
            }
        }
        if (level == LogLevel.Fatal) FatalReported?.Invoke(message);
    }

    public static void Clear()
    {
        lock (Gate) History.Clear();
    }
}
