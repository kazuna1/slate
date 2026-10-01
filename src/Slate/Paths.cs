using System;
using System.IO;

namespace Slate;

internal static class Paths
{
    public static string AppDir { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Slate")).FullName;

    public static string Config => Path.Combine(AppDir, "config.json");
    public static string History => Path.Combine(AppDir, "history.txt");
    public static string Log => Path.Combine(AppDir, "slate.log");
}

internal static class Log
{
    public static void Error(string context, Exception ex) => Write($"{context}: {ex}");

    public static void Write(string line)
    {
        try
        {
            File.AppendAllText(Paths.Log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // logging must never take the app down
        }
    }
}
