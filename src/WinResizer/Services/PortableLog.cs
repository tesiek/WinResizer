using System;
using System.IO;

namespace WinResizer.Services;

internal static class PortableLog
{
    private const string FileName = "WinResizer.error.log";
    private const long MaximumSize = 10 * 1024 * 1024;
    private static readonly object Sync = new object();
    private static string _path = Path.Combine(AppContext.BaseDirectory, FileName);

    public static void SetDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var path = Path.Combine(Path.GetFullPath(directory), FileName);
            lock (Sync)
            {
                _path = path;
            }
        }
    }

    public static void Append(string message)
    {
        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_path, $"[{DateTime.Now}] Error: {message}{Environment.NewLine}");
                var file = new FileInfo(_path);
                if (file.Length <= MaximumSize)
                {
                    return;
                }

                try
                {
                    file.MoveTo($"{_path}-{DateTime.Now:yyyyMMdd-HHmmss}");
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
            // Logging is best effort and must never mask the original failure.
        }
    }
}
