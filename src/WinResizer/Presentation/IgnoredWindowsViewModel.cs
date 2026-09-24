using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Services;

namespace WinResizer.Presentation;

public sealed class IgnoredWindowsViewModel : IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _refreshQueued;

    public IgnoredWindowsViewModel(WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.IgnoredWindowsChanged += OnIgnoredWindowsChanged;
        ReconcileIgnoredWindows();
    }

    public ObservableCollection<IgnoredWindowRowViewModel> IgnoredWindows { get; } =
        new ObservableCollection<IgnoredWindowRowViewModel>();

    public void Refresh()
    {
        QueueRefresh(DispatcherPriority.DataBind);
    }

    public IReadOnlyList<IgnoredWindowTitleMatch> TitleMatchOptions { get; } =
        Array.AsReadOnly((IgnoredWindowTitleMatch[])Enum.GetValues(typeof(IgnoredWindowTitleMatch)));

    public RuntimeIgnoredWindowInfo AddIgnoredWindow()
    {
        try { return _runtime.AddIgnoredWindow(); }
        catch { Refresh(); throw; }
    }

    internal RuntimeIgnoredWindowInfo AddCapturedIgnoredWindow(IgnoredWindowCaptureItem capture)
    {
        try
        {
            return _runtime.AddIgnoredWindow(capture.Process, capture.Class, capture.Title,
                string.IsNullOrEmpty(capture.Title) ? IgnoredWindowTitleMatch.Any : IgnoredWindowTitleMatch.Exact, active: true);
        }
        catch { Refresh(); throw; }
    }

    public bool RemoveIgnoredWindow(IgnoredWindowRowViewModel row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        try { return _runtime.RemoveIgnoredWindow(row.IgnoredWindowToken); }
        catch { Refresh(); throw; }
    }

    public void CommitCell(IgnoredWindowRowViewModel row, string field, string text) =>
        RunMutation(row, () => field switch
        {
            nameof(IgnoredWindowRowViewModel.Process) => _runtime.UpdateIgnoredWindowProcess(row.IgnoredWindowToken, text),
            nameof(IgnoredWindowRowViewModel.Class) => _runtime.UpdateIgnoredWindowClass(row.IgnoredWindowToken, text),
            nameof(IgnoredWindowRowViewModel.Title) => _runtime.UpdateIgnoredWindowTitle(row.IgnoredWindowToken, text),
            _ => throw new ArgumentException("Unknown ignored-window text field.", nameof(field)),
        });

    public void SetActive(IgnoredWindowRowViewModel row, bool active) =>
        RunMutation(row, () => _runtime.SetIgnoredWindowActive(row.IgnoredWindowToken, active));

    public void SetTitleMatch(IgnoredWindowRowViewModel row, IgnoredWindowTitleMatch mode) =>
        RunMutation(row, () => _runtime.SetIgnoredWindowTitleMatch(row.IgnoredWindowToken, mode));

    private void RunMutation(IgnoredWindowRowViewModel row, Func<RuntimeIgnoredWindowInfo> operation)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        try { row.Update(operation()); }
        catch
        {
            var current = _runtime.GetIgnoredWindows().FirstOrDefault(rule => rule.IgnoredWindowToken == row.IgnoredWindowToken);
            if (current is not null) row.Update(current);
            throw;
        }
        finally { Refresh(); }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.IgnoredWindowsChanged -= OnIgnoredWindowsChanged;
    }

    private void OnIgnoredWindowsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void ReconcileIgnoredWindows()
    {
        if (_disposed)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(IgnoredWindows);
        if (view is IEditableCollectionView editable &&
            (editable.IsEditingItem || editable.IsAddingNew))
        {
            return;
        }

        var rules = _runtime.GetIgnoredWindows();
        var currentTokens = new HashSet<string>(
            rules.Select(rule => rule.IgnoredWindowToken), StringComparer.Ordinal);

        for (var index = IgnoredWindows.Count - 1; index >= 0; index--)
        {
            if (!currentTokens.Contains(IgnoredWindows[index].IgnoredWindowToken))
            {
                IgnoredWindows.RemoveAt(index);
            }
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var row = IgnoredWindows.FirstOrDefault(item =>
                item.IgnoredWindowToken.Equals(rule.IgnoredWindowToken, StringComparison.Ordinal));

            if (row is null)
            {
                row = new IgnoredWindowRowViewModel(rule.IgnoredWindowToken);
                IgnoredWindows.Insert(index, row);
            }
            else
            {
                var currentIndex = IgnoredWindows.IndexOf(row);
                if (currentIndex != index)
                {
                    IgnoredWindows.Move(currentIndex, index);
                }
            }

            row.Update(rule);
        }

        view?.Refresh();
    }

    private void QueueRefresh(DispatcherPriority priority)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.BeginInvoke(
            priority,
            new Action(() =>
            {
                _refreshQueued = false;
                ReconcileIgnoredWindows();
            }));
    }
}

public sealed class IgnoredWindowRowViewModel : INotifyPropertyChanged
{
    private bool _active;
    private string _process = string.Empty;
    private string _class = string.Empty;
    private string _title = string.Empty;
    private IgnoredWindowTitleMatch _titleMatch;

    public IgnoredWindowRowViewModel(string ignoredWindowToken)
    {
        IgnoredWindowToken = string.IsNullOrWhiteSpace(ignoredWindowToken)
            ? throw new ArgumentException("An ignored-window token is required.", nameof(ignoredWindowToken))
            : ignoredWindowToken;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IgnoredWindowToken { get; }

    public bool Active
    {
        get => _active;
        private set => SetField(ref _active, value);
    }

    public string Process
    {
        get => _process;
        private set => SetField(ref _process, value);
    }

    public string Class
    {
        get => _class;
        private set => SetField(ref _class, value);
    }

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public IgnoredWindowTitleMatch TitleMatch
    {
        get => _titleMatch;
        private set => SetField(ref _titleMatch, value);
    }

    internal void Update(RuntimeIgnoredWindowInfo rule)
    {
        if (!IgnoredWindowToken.Equals(rule.IgnoredWindowToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The ignored-window row identity does not match the runtime record.");
        }

        Active = rule.Active;
        Process = rule.Process;
        Class = rule.Class;
        Title = rule.Title;
        TitleMatch = rule.TitleMatch;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
