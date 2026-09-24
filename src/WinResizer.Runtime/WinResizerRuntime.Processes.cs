using System;
using System.Collections.Generic;
using System.Linq;
using WinResizer.Common.Windows;
using WinResizer.Configuration;

namespace WinResizer.Runtime;

public sealed partial class WinResizerRuntime
{
    private readonly object _processSync = new();
    private readonly Dictionary<string, Dictionary<WindowSize, string>> _processTokens = new(StringComparer.Ordinal);
    private bool _processMutationInProgress;

    public event EventHandler? ProcessesChanged;

    public IReadOnlyList<RuntimeProcessInfo> GetProcesses(string profileId)
    {
        lock (_processSync)
        {
            ThrowIfNotRunning();
            var profile = GetRequiredProfile(profileId);
            var tokens = GetProcessTokens(profile);
            return profile.WindowSizes.Select(rule => ToProcessInfo(profile, rule, tokens[rule])).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Manual Add creates exactly one rule with caller-supplied RECT edges,
    /// Auto OFF, Normal state, and the existing process group's delay (or zero).
    /// It does not reproduce Save's wildcard-plus-title capture behavior.
    /// </summary>
    public RuntimeProcessInfo AddProcess(string profileId, string process, string title,
        int top, int left, int right, int bottom)
    {
        lock (_processSync)
        {
            ThrowIfNotRunning();
            var profile = GetRequiredProfile(profileId);
            ValidateProcessName(process);
            if (title is null) throw new ArgumentNullException(nameof(title));
            var rule = new WindowSize
            {
                Name = process,
                Title = title,
                Rect = new Rect(left, top, right, bottom),
                AutoResize = false,
                AutoResizeDelay = AutoResizeDelaySettings.GetProcessDelay(profile.WindowSizes, process),
            };
            // Append: never reorder existing rules or change their match priority.
            MutateProcesses(profile, () => profile.WindowSizes.Add(rule), () => profile.WindowSizes.Remove(rule));
            return ToProcessInfo(profile, rule, GetProcessTokens(profile)[rule]);
        }
    }

    public bool RemoveProcess(string profileId, string processToken)
    {
        lock (_processSync)
        {
            var rule = GetRequiredProcess(profileId, processToken, out var profile);
            var index = profile.WindowSizes.IndexOf(rule);
            MutateProcesses(profile, () => profile.WindowSizes.RemoveAt(index),
                () => profile.WindowSizes.Insert(index, rule));
            GetProcessTokens(profile).Remove(rule);
            return true;
        }
    }

    public RuntimeProcessInfo UpdateProcessName(string profileId, string processToken, string process)
    {
        lock (_processSync)
        {
            var rule = GetRequiredProcess(profileId, processToken, out var profile);
            ValidateProcessName(process);
            if (rule.Name == process) return ToProcessInfo(profile, rule, processToken);
            var previousName = rule.Name;
            var previousDelay = rule.AutoResizeDelay;
            // Exclude the edited row so its old delay cannot override the destination group.
            var delay = profile.WindowSizes.Any(other => !ReferenceEquals(other, rule) &&
                other.Name.Equals(process, StringComparison.OrdinalIgnoreCase))
                ? AutoResizeDelaySettings.GetProcessDelay(
                    profile.WindowSizes.Where(other => !ReferenceEquals(other, rule)), process)
                : AutoResizeDelaySettings.Clamp(previousDelay);
            MutateProcesses(profile,
                () => { rule.Name = process; rule.AutoResizeDelay = delay; },
                () => { rule.Name = previousName; rule.AutoResizeDelay = previousDelay; });
            return ToProcessInfo(profile, rule, processToken);
        }
    }

    public RuntimeProcessInfo UpdateProcessTitle(string profileId, string processToken, string title)
    {
        if (title is null) throw new ArgumentNullException(nameof(title));
        return UpdateProcessField(profileId, processToken, title, rule => rule.Title, (rule, value) => rule.Title = value);
    }

    public RuntimeProcessInfo UpdateProcessTop(string profileId, string processToken, int top) =>
        UpdateProcessField(profileId, processToken, top, rule => rule.Top, (rule, value) => rule.Top = value);

    public RuntimeProcessInfo UpdateProcessLeft(string profileId, string processToken, int left) =>
        UpdateProcessField(profileId, processToken, left, rule => rule.Left, (rule, value) => rule.Left = value);

    public RuntimeProcessInfo UpdateProcessRight(string profileId, string processToken, int right) =>
        UpdateProcessField(profileId, processToken, right, rule => rule.Right, (rule, value) => rule.Right = value);

    public RuntimeProcessInfo UpdateProcessBottom(string profileId, string processToken, int bottom) =>
        UpdateProcessField(profileId, processToken, bottom, rule => rule.Bottom, (rule, value) => rule.Bottom = value);

    public RuntimeProcessInfo SetProcessAutoResize(string profileId, string processToken, bool enabled) =>
        UpdateProcessField(profileId, processToken, enabled, rule => rule.AutoResize, (rule, value) => rule.AutoResize = value);

    public RuntimeProcessInfo SetProcessDelay(string profileId, string processToken, int delay)
    {
        lock (_processSync)
        {
            var rule = GetRequiredProcess(profileId, processToken, out var profile);
            var normalized = AutoResizeDelaySettings.Clamp(delay);
            var group = profile.WindowSizes.Where(item =>
                item.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (group.All(item => item.AutoResizeDelay == normalized)) return ToProcessInfo(profile, rule, processToken);
            var previous = group.Select(item => item.AutoResizeDelay).ToArray();
            MutateProcesses(profile,
                () => AutoResizeDelaySettings.SetProcessDelay(group, rule.Name, normalized),
                () => { for (var i = 0; i < group.Count; i++) group[i].AutoResizeDelay = previous[i]; });
            return ToProcessInfo(profile, rule, processToken);
        }
    }

    private RuntimeProcessInfo UpdateProcessField<T>(string profileId, string processToken, T value,
        Func<WindowSize, T> read, Action<WindowSize, T> write)
    {
        lock (_processSync)
        {
            var rule = GetRequiredProcess(profileId, processToken, out var profile);
            var previous = read(rule);
            if (EqualityComparer<T>.Default.Equals(previous, value)) return ToProcessInfo(profile, rule, processToken);
            MutateProcesses(profile, () => write(rule, value), () => write(rule, previous));
            return ToProcessInfo(profile, rule, processToken);
        }
    }

    private void MutateProcesses(Config profile, Action mutate, Action rollback)
    {
        if (_processMutationInProgress) throw new InvalidOperationException("A Processes mutation is already in progress.");
        var list = profile.WindowSizes;
        var raiseEvents = list.RaiseListChangedEvents;
        _processMutationInProgress = true;
        list.RaiseListChangedEvents = false;
        try
        {
            mutate();
            SaveConfiguration();
        }
        catch
        {
            rollback();
            throw;
        }
        finally
        {
            list.RaiseListChangedEvents = raiseEvents;
            _processMutationInProgress = false;
        }

        // Also supports a persistence seam that does not emit ConfigurationSaved.
        // No generation change or CancelPending: ordinary edits retain pending semantics.
        _snapshotStore?.Refresh();
        if (IsCurrentProfile(profile)) UpdateWindowMonitoring();
        list.ResetBindings();
        RaiseProcessesChanged();
    }

    private Dictionary<WindowSize, string> GetProcessTokens(Config profile)
    {
        if (!_processTokens.TryGetValue(profile.ProfileId, out var tokens))
        {
            tokens = new Dictionary<WindowSize, string>();
            _processTokens.Add(profile.ProfileId, tokens);
        }
        // Reconcile legacy additions/removals without sorting or replacing the list.
        foreach (var stale in tokens.Keys.Where(rule => !profile.WindowSizes.Contains(rule)).ToList()) tokens.Remove(stale);
        foreach (var rule in profile.WindowSizes)
            if (!tokens.ContainsKey(rule)) tokens.Add(rule, Guid.NewGuid().ToString("N"));
        return tokens;
    }

    private WindowSize GetRequiredProcess(string profileId, string processToken, out Config profile)
    {
        ThrowIfNotRunning();
        profile = GetRequiredProfile(profileId);
        if (string.IsNullOrWhiteSpace(processToken)) throw new ArgumentException("A process token is required.", nameof(processToken));
        return GetProcessTokens(profile).FirstOrDefault(entry => entry.Value == processToken).Key
            ?? throw new ArgumentException("The process token is stale or does not belong to this profile.", nameof(processToken));
    }

    private static void ValidateProcessName(string process)
    {
        if (string.IsNullOrWhiteSpace(process)) throw new ArgumentException("A process name is required.", nameof(process));
    }

    private static RuntimeProcessInfo ToProcessInfo(Config profile, WindowSize rule, string token) =>
        new(profile.ProfileId, token, rule.Name, rule.Title, rule.Rect, rule.AutoResize,
            rule.AutoResizeDelay, rule.State, rule.MaximizedPosition);

    private void InvalidateProcessTokens()
    {
        lock (_processSync) _processTokens.Clear();
    }

    private void OnProcessesConfigurationSaved()
    {
        lock (_processSync)
        {
            if (!_processMutationInProgress) RaiseProcessesChanged();
        }
    }

    private void RaiseProcessesChanged()
    {
        try { ProcessesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { _log($"ProcessesChanged handler failed: {exception}"); }
    }
}
