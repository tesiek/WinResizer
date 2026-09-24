using System;
using System.Collections.Generic;
using System.Linq;

namespace WinResizer.Configuration;

/// <summary>
/// Maintains the profile-wide invariant that every rule for one process has
/// the same Auto Resize delay.
/// </summary>
public static class AutoResizeDelaySettings
{
    public const int MaxMilliseconds = 10_000;

    public static int Clamp(int delay) => Math.Max(0, Math.Min(MaxMilliseconds, delay));

    public static int GetProcessDelay(IEnumerable<WindowSize> windowSizes, string processName)
    {
        if (windowSizes is null || string.IsNullOrWhiteSpace(processName))
        {
            return 0;
        }

        var first = windowSizes.FirstOrDefault(windowSize =>
            windowSize is not null && windowSize.Name.Equals(processName, StringComparison.OrdinalIgnoreCase));
        return first is null ? 0 : Clamp(first.AutoResizeDelay);
    }

    public static void SetProcessDelay(IEnumerable<WindowSize> windowSizes, string processName, int delay)
    {
        if (windowSizes is null || string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        var normalizedDelay = Clamp(delay);
        foreach (var windowSize in windowSizes.Where(windowSize =>
                     windowSize is not null && windowSize.Name.Equals(processName, StringComparison.OrdinalIgnoreCase)))
        {
            windowSize.AutoResizeDelay = normalizedDelay;
        }
    }

    /// <summary>
    /// Uses the clamped value of the first rule for each process and applies
    /// it to every following rule for that process.
    /// </summary>
    public static bool Normalize(IEnumerable<WindowSize> windowSizes)
    {
        if (windowSizes is null)
        {
            return false;
        }

        var delays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var windowSize in windowSizes)
        {
            if (windowSize is null || string.IsNullOrWhiteSpace(windowSize.Name))
            {
                continue;
            }

            if (!delays.TryGetValue(windowSize.Name, out var delay))
            {
                delay = Clamp(windowSize.AutoResizeDelay);
                delays.Add(windowSize.Name, delay);
            }

            if (windowSize.AutoResizeDelay != delay)
            {
                windowSize.AutoResizeDelay = delay;
                changed = true;
            }
        }

        return changed;
    }
}
