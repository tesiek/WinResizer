using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;
using static WinResizer.Base.WindowUtils;

namespace WinResizer.Base;

public static class WindowCmd
{
    public static bool Resize(string? configPath, string? profileName, string? process, string? title,
        Action<string>? onError = null,
        Action<List<TargetWindow>>? onDebug = null)
    {
        var profile = LoadConfig(configPath, profileName, onError);
        if (profile is null)
        {
            return false;
        }

        var windows = Resizer.GetOpenWindows();
        windows.Reverse();

        var targets = new List<TargetWindow>();

        foreach (var handler in windows)
        {
            if (!IsProcessAvailable(handler, out string processName, null))
            {
                continue;
            }

            var t = Resizer.GetWindowTitle(handler);

            targets.Add(new TargetWindow(handler, processName, t));
        }

        bool resizeAllProcesses = string.IsNullOrEmpty(process);

        if (!resizeAllProcesses)
        {
            targets = targets.Where(i => i.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrEmpty(title))
        {
            try
            {
                var regex = new Regex(title, RegexOptions.None, TimeSpan.FromMilliseconds(500));
                targets = targets.Where(i => !string.IsNullOrEmpty(i.Title) && regex.IsMatch(i.Title!)).ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                onError?.Invoke("Window title matching timed out. Choose a simpler --title pattern.");
                return false;
            }
        }

        var successfulResizeCount = 0;
        foreach (var tp in targets)
        {
            var succeeded = TryResizeWindow(tp.Handle, profile, (p, e) =>
            {
                tp.Result = "Elevated privileges may be required.";
                if (!resizeAllProcesses)
                {
                    onError?.Invoke($"Unable to resize process <{p}>, elevated privileges may be required.");
                }
            }, (p, t) =>
            {
                var message = $"No saved settings.";
                tp.Result = message;
                if (!resizeAllProcesses)
                {
                    onError?.Invoke($"No saved settings for <{p} :: {t}>.");
                }
            });

            if (succeeded)
            {
                successfulResizeCount++;
            }
            else if (string.IsNullOrEmpty(tp.Result))
            {
                tp.Result = "Window resize did not complete.";
            }
        }

        onDebug?.Invoke(targets);

        return successfulResizeCount > 0;
    }

    public class TargetWindow
    {
        public TargetWindow(IntPtr handle, string processName, string? title)
        {
            Handle = handle;
            ProcessName = processName;
            Title = title;
        }

        public IntPtr Handle { get; }

        public string ProcessName { get; }

        public string? Title { get; }

        public string Result { get; set; } = string.Empty;
    }

    private static Config? LoadConfig(string? configPath, string? profileName, Action<string>? onError)
    {
        if (!ConfigUtils.Load(configPath, onError))
        {
            return null;
        }

        if (string.IsNullOrEmpty(profileName))
        {
            return ConfigFactory.Current;
        }

        var p = ConfigFactory.Profiles.Configs.FirstOrDefault(i =>
            i.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (p is null)
        {
            onError?.Invoke($"Profile <{profileName}> not exists");
        }

        return p;
    }
}
