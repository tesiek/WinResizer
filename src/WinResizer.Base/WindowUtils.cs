using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using WinResizer.Common.Shortcuts;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;

namespace WinResizer.Base;

public static class WindowUtils
{
    /// <summary>
    ///     Resize window
    /// </summary>
    /// <param name="handle"></param>
    /// <param name="config"></param>
    /// <param name="onFailed"></param>
    /// <param name="onConfigNoMatch"></param>
    /// <param name="onlyAuto"></param>
    public static void ResizeWindow(
        IntPtr handle,
        Config config,
        Action<Process, Exception>? onFailed,
        Action<string, string>? onConfigNoMatch,
        bool onlyAuto = false)
    {
        if (onlyAuto)
        {
            var ignoredWindows = ConfigFactory.Profiles.GetIgnoredWindowsSnapshot();
            if (!TryPrepareAutoResize(handle, config.WindowSizes, ignoredWindows, out var autoProcessName))
            {
                return;
            }

            TryResizeAutoWindow(
                handle,
                autoProcessName,
                config.WindowSizes,
                config.EnableResizeByTitle,
                ignoredWindows,
                ConfigFactory.Profiles.CompensateDwmFrameEffects,
                () => true);
            return;
        }

        TryResizeWindow(handle, config, onFailed, onConfigNoMatch);
    }

    public static bool TryResizeWindow(
        IntPtr handle,
        Config config,
        Action<Process, Exception>? onFailed,
        Action<string, string>? onConfigNoMatch)
    {
        if (!IsProcessAvailable(handle, out string processName, onFailed))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(processName)) return false;

        var windowTitle = Resizer.GetWindowTitle(handle) ?? string.Empty;
        var match = GetMatchWindowSize(config.WindowSizes, processName, windowTitle, config.EnableResizeByTitle);
        if (!match.NoMatch)
        {
            return MoveMatchWindow(match, handle, ConfigFactory.Profiles.CompensateDwmFrameEffects);
        }

        onConfigNoMatch?.Invoke(processName, windowTitle);
        return false;
    }

    public static bool ResizeAllWindow(Config profile, Action<string>? onError)
    {
        var windows = Resizer.GetOpenUserWindows();
        windows.Reverse();
        foreach (var window in windows)
        {
            try
            {
                if (!profile.RestoreAllIncludeMinimized && Resizer.GetWindowState(window) == WindowState.Minimized)
                {
                    continue;
                }

                ResizeWindow(window, profile, null, null);
            }
            catch (Exception e)
            {
                onError?.Invoke($"Window disappeared while restoring: {e.Message}");
            }
        }

        return true;
    }

    /// <summary>
    ///     Update or save window size
    /// </summary>
    /// <param name="handle"></param>
    /// <param name="config"></param>
    /// <param name="onFailed"></param>
    /// <param name="onSuccess"></param>
    public static void UpdateOrSaveWindowSize(
        IntPtr handle,
        Config config,
        Action<Process, Exception>? onFailed,
        Action<string>? onSuccess = null)
    {
        if (!TryUpdateWindowSize(handle, config, onFailed, out var processName))
        {
            return;
        }

        ConfigFactory.Save();
        onSuccess?.Invoke(processName);
    }

    /// <summary>
    ///     Update a window-size rule without persisting the configuration.
    /// </summary>
    public static bool TryUpdateWindowSize(
        IntPtr handle,
        Config config,
        Action<Process, Exception>? onFailed,
        out string processName)
    {
        if (!IsProcessAvailable(handle, out processName, onFailed))
        {
            return false;
        }

        var windowTitle = Resizer.GetWindowTitle(handle);
        var match = GetMatchWindowSize(config.WindowSizes, processName, windowTitle, config.EnableResizeByTitle);

        WindowPlacement place;
        try
        {
            place = Resizer.GetPlacement(handle, ConfigFactory.Profiles.CompensateDwmFrameEffects);
        }
        catch (Exception)
        {
            // The foreground window may close between discovery and reading its placement.
            return false;
        }

        if (!HasValidWindowGeometry(place.Rect))
        {
            return false;
        }

        UpdateConfig(config, match, processName, windowTitle, place, config.EnableResizeByTitle);
        return true;
    }

    public static bool HasValidWindowGeometry(Rect rect) =>
        (long)rect.Right - rect.Left > 0 && (long)rect.Bottom - rect.Top > 0;

    public static bool IsProcessAvailable(
        IntPtr handle,
        out string processName,
        Action<Process, Exception>? onFailed,
        bool autoResize = false)
    {
        processName = string.Empty;
        if (!autoResize && Resizer.IsChildWindow(handle))
        {
            return false;
        }

        using var process = Resizer.GetRealProcess(handle);
        if (process is null)
        {
            return false;
        }

        var success = TryGetProcessName(process, out processName, onFailed);
        if (!success)
        {
            return false;
        }

        return !Resizer.IsInvisibleProcess(processName);
    }

    /// <summary>
    /// Performs the phase that is safe before the Auto Resize delay.  It uses
    /// only a caller-owned configuration snapshot.
    /// </summary>
    public static bool TryPrepareAutoResize(
        IntPtr handle,
        IEnumerable<WindowSize> windowSizes,
        IEnumerable<IgnoredWindowRule> ignoredWindows,
        out string processName)
    {
        processName = string.Empty;
        if (!Resizer.IsEligibleForAutoResize(handle) ||
            !IsProcessAvailable(handle, out processName, null, true) ||
            string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var resolvedProcessName = processName;
        var hasAutoResizeRule = windowSizes.Any(windowSize =>
            windowSize.AutoResize && windowSize.Name.Equals(resolvedProcessName, StringComparison.OrdinalIgnoreCase));
        return hasAutoResizeRule && !IgnoredWindowMatcher.IsIgnoredBeforeTitle(
            ignoredWindows,
            resolvedProcessName,
            () => Resizer.GetWindowClassName(handle));
    }

    /// <summary>
    /// Performs the post-delay Auto Resize phase.  The title is read once and
    /// is shared by ignored-window matching and process-rule matching.
    /// </summary>
    public static bool TryResizeAutoWindow(
        IntPtr handle,
        string processName,
        IEnumerable<WindowSize> windowSizes,
        bool enableResizeByTitle,
        IEnumerable<IgnoredWindowRule> ignoredWindows,
        bool compensateDwmFrameEffects,
        Func<bool> canPlace)
    {
        if (!Resizer.IsEligibleForAutoResize(handle) || string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var windowSizesSnapshot = windowSizes.ToList();
        if (!windowSizesSnapshot.Any(windowSize =>
                windowSize.AutoResize && windowSize.Name.Equals(processName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var windowTitle = Resizer.GetWindowTitle(handle) ?? string.Empty;
        if (IgnoredWindowMatcher.IsIgnoredForAutoResize(
                ignoredWindows,
                processName,
                () => Resizer.GetWindowClassName(handle),
                windowTitle))
        {
            return false;
        }

        var match = GetMatchWindowSize(windowSizesSnapshot, processName, windowTitle, enableResizeByTitle, true);
        if (match.NoMatch || !canPlace())
        {
            return false;
        }

        MoveMatchWindow(match, handle, compensateDwmFrameEffects);
        return true;
    }

    public static Hotkeys? GetKeys(HotkeysType type) =>
        ConfigFactory.Current.GetKeys(type);

    #region private functions

    private static bool MoveMatchWindow(MatchWindowSize match, IntPtr handle, bool compensateDwmFrameEffects)
    {
        if (match.FullMatch != null)
        {
            return MoveWindow(handle, match.FullMatch, compensateDwmFrameEffects);
        }

        if (match.PrefixMatch != null)
        {
            return MoveWindow(handle, match.PrefixMatch, compensateDwmFrameEffects);
        }

        if (match.SuffixMatch != null)
        {
            return MoveWindow(handle, match.SuffixMatch, compensateDwmFrameEffects);
        }

        if (match.WildcardMatch != null)
        {
            return MoveWindow(handle, match.WildcardMatch, compensateDwmFrameEffects);
        }

        return false;
    }

    private static bool TryGetProcessName(Process process, out string processName, Action<Process, Exception>? onFailed)
    {
        try
        {
            processName = process.MainModule?.ModuleName ?? string.Empty;
            return true;
        }
        catch (Exception e)
        {
            onFailed?.Invoke(process, e);
            processName = string.Empty;
            return false;
        }
    }

    private static MatchWindowSize GetMatchWindowSize(
        IEnumerable<WindowSize> windowSizes,
        string processName,
        string? title,
        bool enableResizeByTitle,
        bool onlyAuto = false)
    {
        var windows = windowSizes.Where(w =>
                                     w.Name.Equals(processName, StringComparison.OrdinalIgnoreCase))
                                 .ToList();

        if (!enableResizeByTitle)
        {
            windows = windows.Where(w => w.Title.Equals("*")).ToList();

            if (onlyAuto)
            {
                windows = windows.Where(w => w.AutoResize).ToList();
            }

            return new MatchWindowSize
            {
                WildcardMatch = windows.FirstOrDefault()
            };
        }

        if (onlyAuto)
        {
            windows = windows.Where(w => w.AutoResize).ToList();
        }

        if (string.IsNullOrEmpty(title))
        {
            title = "*";
        }

        return new MatchWindowSize
        {
            FullMatch = windows.FirstOrDefault(w => w.Title == title),
            PrefixMatch = windows.FirstOrDefault(w =>
                w.Title.StartsWith("*") && w.Title.Length > 1 && title!.EndsWith(w.Title.TrimStart('*'))),
            SuffixMatch = windows.FirstOrDefault(w =>
                w.Title.EndsWith("*") && w.Title.Length > 1 && title!.StartsWith(w.Title.TrimEnd('*'))),
            WildcardMatch = windows.FirstOrDefault(w => w.Title.Equals("*"))
        };
    }


    private static void UpdateConfig(Config config, MatchWindowSize match, string processName, string? title, WindowPlacement placement, bool enableResizeByTitle)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        if (!enableResizeByTitle)
        {
            if (match.NoMatch || match.WildcardMatch is null)
            {
                InsertOrder(config.WindowSizes, new WindowSize
                {
                    Name = processName,
                    Title = "*",
                    Rect = placement.Rect,
                    State = placement.WindowState,
                    MaximizedPosition = placement.MaximizedPosition,
                });
            }
            else
            {
                match.WildcardMatch.Rect = placement.Rect;
                match.WildcardMatch.State = placement.WindowState;
                match.WildcardMatch.MaximizedPosition = placement.MaximizedPosition;
            }

            return;
        }

        if (match.NoMatch)
        {
            // Add a wildcard match for all titles
            InsertOrder(config.WindowSizes, new WindowSize
            {
                Name = processName,
                Title = "*",
                Rect = placement.Rect,
                State = placement.WindowState,
                MaximizedPosition = placement.MaximizedPosition,
            });

            if (!string.IsNullOrWhiteSpace(title))
            {
                InsertOrder(config.WindowSizes, new WindowSize
                {
                    Name = processName,
                    Title = title!,
                    Rect = placement.Rect,
                    State = placement.WindowState,
                    MaximizedPosition = placement.MaximizedPosition,
                });
            }

            return;
        }

        if (match.FullMatch != null)
        {
            match.FullMatch.Rect = placement.Rect;
            match.FullMatch.State = placement.WindowState;
            match.FullMatch.MaximizedPosition = placement.MaximizedPosition;
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            InsertOrder(config.WindowSizes, new WindowSize
            {
                Name = processName,
                Title = title!,
                Rect = placement.Rect,
                State = placement.WindowState,
                MaximizedPosition = placement.MaximizedPosition,
            });
        }

        if (match.SuffixMatch != null)
        {
            match.SuffixMatch.Rect = placement.Rect;
            match.SuffixMatch.State = placement.WindowState;
            match.SuffixMatch.MaximizedPosition = placement.MaximizedPosition;
        }

        if (match.PrefixMatch != null)
        {
            match.PrefixMatch.Rect = placement.Rect;
            match.PrefixMatch.State = placement.WindowState;
            match.PrefixMatch.MaximizedPosition = placement.MaximizedPosition;
        }

        if (match.WildcardMatch != null)
        {
            match.WildcardMatch.Rect = placement.Rect;
            match.WildcardMatch.State = placement.WindowState;
            match.WildcardMatch.MaximizedPosition = placement.MaximizedPosition;
        }
        else
        {
            InsertOrder(config.WindowSizes, new WindowSize
            {
                Name = processName,
                Title = "*",
                Rect = placement.Rect,
                State = placement.WindowState,
                MaximizedPosition = placement.MaximizedPosition,
            });
        }

    }

    public static bool ApplyPreset(IntPtr handle, WindowPreset preset)
    {
        if (handle == IntPtr.Zero || Resizer.IsChildWindow(handle))
        {
            return false;
        }

        var processName = Resizer.GetRealProcessName(handle);
        if (processName is not null && Resizer.IsInvisibleProcess(processName))
        {
            return false;
        }

        return Resizer.SetPlacement(
            handle,
            preset.Rect,
            new Point(0, 0),
            WindowState.Normal,
            ConfigFactory.Profiles.CompensateDwmFrameEffects);
    }

    private static bool MoveWindow(IntPtr handle, WindowSize match, bool compensateDwmFrameEffects)
    {
        return Resizer.SetPlacement(
            handle,
            match.Rect,
            match.MaximizedPosition,
            match.State,
            compensateDwmFrameEffects);
    }

/*
        private static void MoveWindow(IntPtr handle, WindowSize match)
        {
            Resizer.MoveWindow(handle, match.Rect);
            if (match.State == WindowState.Maximized)
            {
                Resizer.MaximizeWindow(handle);
            }
        }
*/

    private static void InsertOrder(BindingList<WindowSize> list, WindowSize item)
    {
        item.AutoResizeDelay = AutoResizeDelaySettings.GetProcessDelay(list, item.Name);
        var backing = list.ToList();
        backing.Add(item);
        var index = backing.OrderBy(l => l.Name).ThenBy(l => l.Title).ToList().IndexOf(item);
        list.Insert(index, item);
    }

    #endregion
}
