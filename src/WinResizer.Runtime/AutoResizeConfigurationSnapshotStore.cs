using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using WinResizer.Common.Windows;
using WinResizer.Configuration;

namespace WinResizer.Runtime;

public sealed class AutoResizeConfigurationSnapshotStore : IDisposable
{
    private AutoResizeConfigurationSnapshot? _current;
    private bool _disposed;

    public AutoResizeConfigurationSnapshotStore()
    {
        ConfigFactory.ConfigurationSaved += OnConfigurationSaved;
        ConfigFactory.ConfigurationReplaced += OnConfigurationReplaced;
        ConfigFactory.Profiles.ProfileEvents.ProfileSwitch += OnProfileSwitch;
    }

    public AutoResizeConfigurationSnapshot? Current => Volatile.Read(ref _current);

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = AutoResizeConfigurationSnapshot.Capture(
            ConfigFactory.Current,
            ConfigFactory.Profiles.GetIgnoredWindowsSnapshot(),
            ConfigFactory.Profiles.CompensateDwmFrameEffects);
        Volatile.Write(ref _current, snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ConfigFactory.ConfigurationSaved -= OnConfigurationSaved;
        ConfigFactory.ConfigurationReplaced -= OnConfigurationReplaced;
        ConfigFactory.Profiles.ProfileEvents.ProfileSwitch -= OnProfileSwitch;
    }

    private void OnConfigurationSaved() => Refresh();

    private void OnConfigurationReplaced() => Refresh();

    private void OnProfileSwitch(string profileId) => Refresh();
}

public sealed class AutoResizeConfigurationSnapshot
{
    private readonly WindowSizeData[] _windowSizes;
    private readonly IgnoredWindowRuleData[] _ignoredWindows;

    private AutoResizeConfigurationSnapshot(
        bool enableResizeByTitle,
        bool enableAutoResizeDelay,
        bool compensateDwmFrameEffects,
        WindowSizeData[] windowSizes,
        IgnoredWindowRuleData[] ignoredWindows)
    {
        EnableResizeByTitle = enableResizeByTitle;
        EnableAutoResizeDelay = enableAutoResizeDelay;
        CompensateDwmFrameEffects = compensateDwmFrameEffects;
        _windowSizes = windowSizes;
        _ignoredWindows = ignoredWindows;
    }

    public bool EnableResizeByTitle { get; }

    public bool EnableAutoResizeDelay { get; }

    public bool CompensateDwmFrameEffects { get; }

    public IReadOnlyList<WindowSize> WindowSizes => new ReadOnlyCollection<WindowSize>(
        _windowSizes.Select(windowSize => windowSize.ToWindowSize()).ToList());

    public IReadOnlyList<IgnoredWindowRule> IgnoredWindows => new ReadOnlyCollection<IgnoredWindowRule>(
        _ignoredWindows.Select(rule => rule.ToIgnoredWindowRule()).ToList());

    public static AutoResizeConfigurationSnapshot Capture(
        Config config,
        IEnumerable<IgnoredWindowRule> ignoredWindows,
        bool compensateDwmFrameEffects = false)
    {
        return new AutoResizeConfigurationSnapshot(
            config.EnableResizeByTitle,
            config.EnableAutoResizeDelay,
            compensateDwmFrameEffects,
            config.WindowSizes
                .Where(windowSize => windowSize is not null)
                .Select(windowSize => new WindowSizeData(windowSize))
                .ToArray(),
            ignoredWindows
                .Where(rule => rule is not null)
                .Select(rule => new IgnoredWindowRuleData(rule))
                .ToArray());
    }

    private sealed class WindowSizeData
    {
        public WindowSizeData(WindowSize windowSize)
        {
            Name = windowSize.Name;
            Title = windowSize.Title;
            Rect = windowSize.Rect;
            State = windowSize.State;
            MaximizedPosition = windowSize.MaximizedPosition;
            AutoResize = windowSize.AutoResize;
            AutoResizeDelay = windowSize.AutoResizeDelay;
        }

        private string Name { get; }
        private string Title { get; }
        private Rect Rect { get; }
        private WindowState State { get; }
        private Point MaximizedPosition { get; }
        private bool AutoResize { get; }
        private int AutoResizeDelay { get; }

        public WindowSize ToWindowSize() => new()
        {
            Name = Name,
            Title = Title,
            Rect = Rect,
            State = State,
            MaximizedPosition = MaximizedPosition,
            AutoResize = AutoResize,
            AutoResizeDelay = AutoResizeDelay,
        };
    }

    private sealed class IgnoredWindowRuleData
    {
        public IgnoredWindowRuleData(IgnoredWindowRule rule)
        {
            Active = rule.Active;
            Process = rule.Process;
            Class = rule.Class;
            Title = rule.Title;
            TitleMatch = rule.TitleMatch;
        }

        private bool Active { get; }
        private string Process { get; }
        private string Class { get; }
        private string Title { get; }
        private IgnoredWindowTitleMatch TitleMatch { get; }

        public IgnoredWindowRule ToIgnoredWindowRule() => new()
        {
            Active = Active,
            Process = Process,
            Class = Class,
            Title = Title,
            TitleMatch = TitleMatch,
        };
    }
}
