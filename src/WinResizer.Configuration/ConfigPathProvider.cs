using System;
using System.IO;

namespace WinResizer.Configuration;

/// <summary>
/// Resolves the configuration path from the directory that contains the
/// running application.  The optional directory argument is only a test seam;
/// normal callers use <see cref="AppContext.BaseDirectory"/>.
/// </summary>
public static class ConfigPathProvider
{
    public const string ConfigFileName = "WinResizer.config.json";

    public static string GetPortableConfigPath(string? baseDirectory = null)
    {
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Application base directory is unavailable.");
        }

        return Path.Combine(Path.GetFullPath(directory), ConfigFileName);
    }
}
