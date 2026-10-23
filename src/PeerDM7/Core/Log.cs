using System.Diagnostics;
using System.IO;

namespace PeerDM7.Core;

/// <summary>
/// Diagnostics go to the debugger only. A file is written solely when the app is started
/// with --log PATH, so a normal session leaves nothing behind.
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    public static string? FilePath { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        Debug.WriteLine(line);
        if (FilePath is null) return;
        try { lock (Gate) File.AppendAllText(FilePath, line + Environment.NewLine); }
        catch { }
    }
}
