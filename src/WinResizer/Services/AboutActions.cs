using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using WinResizer.Configuration;

namespace WinResizer.Services;

public static class AboutActions
{
    public const string GitHubUrl = "https://github.com/tesiek/WinResizer";
    public const string OriginalProjectUrl = "https://github.com/caoyue/WindowResizer";
    public static ProcessStartInfo CreateGitHubStartInfo() => new(GitHubUrl) { UseShellExecute = true };
    public static ProcessStartInfo CreateOriginalProjectStartInfo() => new(OriginalProjectUrl) { UseShellExecute = true };

    public static ProcessStartInfo CreateConfigStartInfo()
    {
        var path = ConfigFactory.ConfigPath;
        if (!File.Exists(path)) throw new FileNotFoundException("The active configuration file does not exist.", path);
        return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            "/select,\"" + path + "\"") { UseShellExecute = true };
    }

    public static OpenFileDialog CreateImportDialog() => new()
    {
        InitialDirectory = ConfigFactory.ConfigDirectory,
        Filter = "JSON configuration (*.json)|*.json", DefaultExt = ".json",
        CheckFileExists = true, Multiselect = false, Title = "Import WinResizer configuration",
    };

    public static SaveFileDialog CreateExportDialog() => new()
    {
        InitialDirectory = ConfigFactory.ConfigDirectory,
        Filter = "JSON configuration (*.json)|*.json", DefaultExt = ".json",
        AddExtension = true, OverwritePrompt = true, FileName = "WinResizer.config.export.json",
        Title = "Export WinResizer configuration",
    };

    public static string? Import(string path) => ConfigFactory.LoadWithBackup(path);
    public static string ImportSuccessMessage(string? backupPath) =>
        "Configuration was imported successfully." + Environment.NewLine + Environment.NewLine +
        (backupPath is null ? "There was no previous configuration to back up." :
            "The previous configuration was backed up to:" + Environment.NewLine + backupPath);
    public static void Export(string path) => ConfigFactory.Export(path);
}
