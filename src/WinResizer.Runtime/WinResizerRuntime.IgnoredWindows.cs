using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using WinResizer.Configuration;

namespace WinResizer.Runtime;

public sealed partial class WinResizerRuntime
{
    private readonly object _ignoredWindowSync = new();
    private readonly Dictionary<IgnoredWindowRule, string> _ignoredWindowTokens = new(new IgnoredRuleReferenceComparer());
    private BindingList<IgnoredWindowRule>? _ignoredWindowTokenList;
    private bool _ignoredWindowMutationInProgress;

    public event EventHandler? IgnoredWindowsChanged;

    public IReadOnlyList<RuntimeIgnoredWindowInfo> GetIgnoredWindows()
    {
        lock (_ignoredWindowSync)
        {
            ThrowIfNotRunning();
            ReconcileIgnoredWindowTokens();
            return ConfigFactory.Profiles.IgnoredWindows.Where(rule => rule is not null)
                .Select(rule => new RuntimeIgnoredWindowInfo(_ignoredWindowTokens[rule], rule)).ToList().AsReadOnly();
        }
    }

    /// <summary>Appends a draft by default. Empty Process is persisted but is not usable by the matcher.</summary>
    public RuntimeIgnoredWindowInfo AddIgnoredWindow(string process = "", string className = "", string title = "",
        IgnoredWindowTitleMatch titleMatch = IgnoredWindowTitleMatch.Any, bool active = true)
    {
        ValidateIgnoredString(process, nameof(process));
        ValidateIgnoredString(className, nameof(className));
        ValidateIgnoredString(title, nameof(title));
        ValidateIgnoredTitleMatch(titleMatch);
        lock (_ignoredWindowSync)
        {
            ThrowIfNotRunning();
            var list = ConfigFactory.Profiles.IgnoredWindows;
            var rule = new IgnoredWindowRule { Process = process, Class = className, Title = title,
                TitleMatch = titleMatch, Active = active };
            MutateIgnoredWindows(() => list.Add(rule), () => list.Remove(rule));
            ReconcileIgnoredWindowTokens();
            return new RuntimeIgnoredWindowInfo(_ignoredWindowTokens[rule], rule);
        }
    }

    public bool RemoveIgnoredWindow(string token)
    {
        lock (_ignoredWindowSync)
        {
            var rule = GetRequiredIgnoredWindow(token);
            var list = ConfigFactory.Profiles.IgnoredWindows;
            var index = list.IndexOf(rule);
            MutateIgnoredWindows(() => list.RemoveAt(index), () => list.Insert(index, rule));
            return true;
        }
    }

    public RuntimeIgnoredWindowInfo UpdateIgnoredWindowProcess(string token, string process)
    {
        ValidateIgnoredString(process, nameof(process));
        return UpdateIgnoredWindowField(token, process, rule => rule.Process, (rule, value) => rule.Process = value);
    }

    public RuntimeIgnoredWindowInfo UpdateIgnoredWindowClass(string token, string className)
    {
        ValidateIgnoredString(className, nameof(className));
        return UpdateIgnoredWindowField(token, className, rule => rule.Class, (rule, value) => rule.Class = value);
    }

    public RuntimeIgnoredWindowInfo UpdateIgnoredWindowTitle(string token, string title)
    {
        ValidateIgnoredString(title, nameof(title));
        return UpdateIgnoredWindowField(token, title, rule => rule.Title, (rule, value) => rule.Title = value);
    }

    public RuntimeIgnoredWindowInfo SetIgnoredWindowActive(string token, bool active) =>
        UpdateIgnoredWindowField(token, active, rule => rule.Active, (rule, value) => rule.Active = value);

    public RuntimeIgnoredWindowInfo SetIgnoredWindowTitleMatch(string token, IgnoredWindowTitleMatch titleMatch)
    {
        ValidateIgnoredTitleMatch(titleMatch);
        return UpdateIgnoredWindowField(token, titleMatch, rule => rule.TitleMatch, (rule, value) => rule.TitleMatch = value);
    }

    private RuntimeIgnoredWindowInfo UpdateIgnoredWindowField<T>(string token, T value,
        Func<IgnoredWindowRule, T> read, Action<IgnoredWindowRule, T> write)
    {
        lock (_ignoredWindowSync)
        {
            var rule = GetRequiredIgnoredWindow(token);
            var previous = read(rule);
            if (!EqualityComparer<T>.Default.Equals(previous, value))
                MutateIgnoredWindows(() => write(rule, value), () => write(rule, previous));
            return new RuntimeIgnoredWindowInfo(token, rule);
        }
    }

    private void MutateIgnoredWindows(Action mutate, Action rollback)
    {
        if (_ignoredWindowMutationInProgress) throw new InvalidOperationException("An Ignored Windows mutation is already in progress.");
        var list = ConfigFactory.Profiles.IgnoredWindows;
        var raiseEvents = list.RaiseListChangedEvents;
        _ignoredWindowMutationInProgress = true;
        list.RaiseListChangedEvents = false;
        try { mutate(); SaveConfiguration(); }
        catch { rollback(); throw; }
        finally
        {
            list.RaiseListChangedEvents = raiseEvents;
            _ignoredWindowMutationInProgress = false;
        }
        // Ordinary saves retain pending work. The fresh snapshot is read after its original delay.
        _snapshotStore?.Refresh();
        ReconcileIgnoredWindowTokens();
        list.ResetBindings();
        RaiseIgnoredWindowsChanged();
    }

    private void ReconcileIgnoredWindowTokens()
    {
        var list = ConfigFactory.Profiles.IgnoredWindows;
        if (!ReferenceEquals(list, _ignoredWindowTokenList))
        {
            _ignoredWindowTokens.Clear();
            _ignoredWindowTokenList = list;
        }
        var live = new HashSet<IgnoredWindowRule>(list.Where(rule => rule is not null), new IgnoredRuleReferenceComparer());
        foreach (var stale in _ignoredWindowTokens.Keys.Where(rule => !live.Contains(rule)).ToList())
            _ignoredWindowTokens.Remove(stale);
        foreach (var rule in live)
            if (!_ignoredWindowTokens.ContainsKey(rule)) _ignoredWindowTokens.Add(rule, Guid.NewGuid().ToString("N"));
    }

    private IgnoredWindowRule GetRequiredIgnoredWindow(string token)
    {
        ThrowIfNotRunning();
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("An ignored-window token is required.", nameof(token));
        ReconcileIgnoredWindowTokens();
        return _ignoredWindowTokens.FirstOrDefault(pair => pair.Value == token).Key
            ?? throw new ArgumentException("The ignored-window token is stale or unknown.", nameof(token));
    }

    private static void ValidateIgnoredString(string value, string parameter)
    {
        if (value is null) throw new ArgumentNullException(parameter);
    }

    private static void ValidateIgnoredTitleMatch(IgnoredWindowTitleMatch value)
    {
        if (!Enum.IsDefined(typeof(IgnoredWindowTitleMatch), value)) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private void InvalidateIgnoredWindowTokens()
    {
        lock (_ignoredWindowSync) { _ignoredWindowTokens.Clear(); _ignoredWindowTokenList = null; }
    }

    private void SubscribeIgnoredWindowsConfigurationEvents()
    {
        ConfigFactory.ConfigurationSaved += OnIgnoredWindowsConfigurationSaved;
        ConfigFactory.ConfigurationReplacing += InvalidateIgnoredWindowTokens;
        ConfigFactory.ConfigurationReplaced += OnIgnoredWindowsConfigurationReplaced;
    }

    private void UnsubscribeIgnoredWindowsConfigurationEvents()
    {
        ConfigFactory.ConfigurationSaved -= OnIgnoredWindowsConfigurationSaved;
        ConfigFactory.ConfigurationReplacing -= InvalidateIgnoredWindowTokens;
        ConfigFactory.ConfigurationReplaced -= OnIgnoredWindowsConfigurationReplaced;
    }

    private void OnIgnoredWindowsConfigurationSaved()
    {
        lock (_ignoredWindowSync)
            if (!_ignoredWindowMutationInProgress) { ReconcileIgnoredWindowTokens(); RaiseIgnoredWindowsChanged(); }
    }

    private void OnIgnoredWindowsConfigurationReplaced()
    {
        lock (_ignoredWindowSync) { ReconcileIgnoredWindowTokens(); RaiseIgnoredWindowsChanged(); }
    }

    private void RaiseIgnoredWindowsChanged()
    {
        try { IgnoredWindowsChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { _log($"IgnoredWindowsChanged handler failed: {exception}"); }
    }

    private sealed class IgnoredRuleReferenceComparer : IEqualityComparer<IgnoredWindowRule>
    {
        public bool Equals(IgnoredWindowRule? x, IgnoredWindowRule? y) => ReferenceEquals(x, y);
        public int GetHashCode(IgnoredWindowRule obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
