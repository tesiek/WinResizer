using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WinResizer.Core.Startup;

public interface IStartupStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

public sealed class RegistryStartupStore : IStartupStore
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "WinResizer";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath)
            ?? throw new IOException("The current user's startup registry key could not be opened.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public sealed class SystemStartupService
{
    private readonly IStartupStore _store;
    private readonly string _command;

    public SystemStartupService(IStartupStore store, string currentExe)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var root = string.IsNullOrWhiteSpace(currentExe) ? null : Path.GetPathRoot(currentExe);
        if (string.IsNullOrWhiteSpace(currentExe) || !Path.IsPathRooted(currentExe) ||
            root == @"\" || root == "/" || root?.EndsWith(":", StringComparison.Ordinal) == true || currentExe.IndexOf('"') >= 0)
            throw new ArgumentException("The current executable must have an absolute path without quotation marks.", nameof(currentExe));
        _command = "\"" + currentExe + "\"";
    }

    public static SystemStartupService CreateForCurrentHost()
    {
        using var process = Process.GetCurrentProcess();
        var path = process.MainModule?.FileName
            ?? throw new IOException("The current executable path could not be determined.");
        return new SystemStartupService(new RegistryStartupStore(), path);
    }

    public bool IsEnabled => _store.Read() is not null;
    public void Enable() => _store.Write(_command);
    public void Disable() => _store.Delete();

    public void RepairPathIfEnabled()
    {
        var existing = _store.Read();
        if (existing is not null && !string.Equals(existing, _command, StringComparison.OrdinalIgnoreCase))
            _store.Write(_command);
    }
}
