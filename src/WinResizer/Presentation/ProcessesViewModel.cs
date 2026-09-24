using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Runtime;

namespace WinResizer.Presentation;

public sealed class ProcessesViewModel : IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _refreshQueued;

    public ProcessesViewModel(WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.ProcessesChanged += OnProcessesChanged;
        _runtime.ProfilesChanged += OnProfilesChanged;
        ReconcileProcesses();
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; } =
        new ObservableCollection<ProcessRowViewModel>();

    public void Refresh()
    {
        QueueRefresh(DispatcherPriority.DataBind);
    }

    public RuntimeProcessInfo AddProcess()
    {
        try
        {
            var profile = _runtime.GetProfiles().Single(profile => profile.IsCurrent);
            return _runtime.AddProcess(profile.Id, "process.exe", string.Empty, 0, 0, 1, 1);
        }
        catch
        {
            Refresh();
            throw;
        }
    }

    public bool RemoveProcess(ProcessRowViewModel row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        try
        {
            return _runtime.RemoveProcess(row.ProfileId, row.ProcessToken);
        }
        catch
        {
            Refresh();
            throw;
        }
    }

    public bool TryCommitCell(ProcessRowViewModel row, string field, string text)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        RuntimeProcessInfo updated;
        if (field == nameof(ProcessRowViewModel.Process))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Refresh();
                return false;
            }
            updated = _runtime.UpdateProcessName(row.ProfileId, row.ProcessToken, text);
        }
        else if (field == nameof(ProcessRowViewModel.Title))
            updated = _runtime.UpdateProcessTitle(row.ProfileId, row.ProcessToken, text);
        else
        {
            if (field != nameof(ProcessRowViewModel.Top) && field != nameof(ProcessRowViewModel.Left) &&
                field != nameof(ProcessRowViewModel.Right) && field != nameof(ProcessRowViewModel.Bottom) &&
                field != nameof(ProcessRowViewModel.Delay))
                throw new ArgumentException("This Processes field is read-only.", nameof(field));
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value))
            {
                Refresh();
                return false;
            }
            updated = field switch
            {
                nameof(ProcessRowViewModel.Top) => _runtime.UpdateProcessTop(row.ProfileId, row.ProcessToken, value),
                nameof(ProcessRowViewModel.Left) => _runtime.UpdateProcessLeft(row.ProfileId, row.ProcessToken, value),
                nameof(ProcessRowViewModel.Right) => _runtime.UpdateProcessRight(row.ProfileId, row.ProcessToken, value),
                nameof(ProcessRowViewModel.Delay) => _runtime.SetProcessDelay(row.ProfileId, row.ProcessToken, value),
                _ => _runtime.UpdateProcessBottom(row.ProfileId, row.ProcessToken, value),
            };
        }
        // Updating display values is safe during EditItem; collection refresh waits for RowEditEnding.
        row.Update(updated);
        Refresh();
        return true;
    }

    public void SetAutoResize(ProcessRowViewModel row, bool enabled)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        row.Update(_runtime.SetProcessAutoResize(row.ProfileId, row.ProcessToken, enabled));
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.ProcessesChanged -= OnProcessesChanged;
        _runtime.ProfilesChanged -= OnProfilesChanged;
    }

    private void OnProcessesChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void ReconcileProcesses()
    {
        if (_disposed)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(Processes);
        if (view is IEditableCollectionView editable && (editable.IsEditingItem || editable.IsAddingNew))
        {
            // RowEditEnding queues a refresh once WPF finishes the transaction.
            return;
        }

        var currentProfile = _runtime.GetProfiles().FirstOrDefault(profile => profile.IsCurrent);
        if (currentProfile is null)
        {
            Processes.Clear();
            return;
        }

        var processes = _runtime.GetProcesses(currentProfile.Id);
        var currentTokens = new HashSet<string>(
            processes.Select(process => process.ProcessToken),
            StringComparer.Ordinal);

        for (var index = Processes.Count - 1; index >= 0; index--)
        {
            if (!Processes[index].ProfileId.Equals(currentProfile.Id, StringComparison.Ordinal) ||
                !currentTokens.Contains(Processes[index].ProcessToken))
            {
                Processes.RemoveAt(index);
            }
        }

        for (var index = 0; index < processes.Count; index++)
        {
            var process = processes[index];
            var row = Processes.FirstOrDefault(item =>
                item.ProfileId.Equals(currentProfile.Id, StringComparison.Ordinal) &&
                item.ProcessToken.Equals(process.ProcessToken, StringComparison.Ordinal));

            if (row is null)
            {
                row = new ProcessRowViewModel(currentProfile.Id, process.ProcessToken);
                Processes.Insert(index, row);
            }
            else
            {
                var currentIndex = Processes.IndexOf(row);
                if (currentIndex != index)
                {
                    Processes.Move(currentIndex, index);
                }
            }

            row.Update(process);
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
                ReconcileProcesses();
            }));
    }
}

public sealed class ProcessRowViewModel : INotifyPropertyChanged
{
    private string _process = string.Empty;
    private string _title = string.Empty;
    private int _top;
    private int _left;
    private int _right;
    private int _bottom;
    private bool _auto;
    private int _delay;

    public ProcessRowViewModel(string profileId, string processToken)
    {
        ProfileId = string.IsNullOrWhiteSpace(profileId)
            ? throw new ArgumentException("A profile ID is required.", nameof(profileId))
            : profileId;
        ProcessToken = string.IsNullOrWhiteSpace(processToken)
            ? throw new ArgumentException("A process token is required.", nameof(processToken))
            : processToken;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileId { get; }

    public string ProcessToken { get; }

    public string Process
    {
        get => _process;
        private set => SetField(ref _process, value);
    }

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public int Top
    {
        get => _top;
        private set => SetField(ref _top, value);
    }

    public int Left
    {
        get => _left;
        private set => SetField(ref _left, value);
    }

    public int Right
    {
        get => _right;
        private set => SetField(ref _right, value);
    }

    public int Bottom
    {
        get => _bottom;
        private set => SetField(ref _bottom, value);
    }

    public bool Auto
    {
        get => _auto;
        private set => SetField(ref _auto, value);
    }

    public int Delay
    {
        get => _delay;
        private set => SetField(ref _delay, value);
    }

    internal void Update(RuntimeProcessInfo process)
    {
        if (!ProfileId.Equals(process.ProfileId, StringComparison.Ordinal) ||
            !ProcessToken.Equals(process.ProcessToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The process row identity does not match the runtime record.");
        }

        Process = process.Process;
        Title = process.Title;
        Top = process.Top;
        Left = process.Left;
        Right = process.Right;
        Bottom = process.Bottom;
        Auto = process.AutoResize;
        Delay = process.Delay;
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
