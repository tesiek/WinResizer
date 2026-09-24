using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Runtime;

namespace WinResizer.Presentation;

public sealed class ProfilesViewModel : IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ProfilesViewModel(WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.ProfilesChanged += OnProfilesChanged;
        ReconcileProfiles();
    }

    public ObservableCollection<ProfileRowViewModel> Profiles { get; } =
        new ObservableCollection<ProfileRowViewModel>();

    public void AddProfile(string profileName) => _runtime.AddProfile(profileName);

    public bool SwitchProfile(ProfileRowViewModel profile) =>
        _runtime.SwitchProfile(GetProfileId(profile));

    public bool RenameProfile(ProfileRowViewModel profile, string profileName) =>
        _runtime.RenameProfile(GetProfileId(profile), profileName);

    public bool RemoveProfile(ProfileRowViewModel profile) =>
        _runtime.RemoveProfile(GetProfileId(profile));

    public void Refresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ReconcileProfiles));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.ProfilesChanged -= OnProfilesChanged;
    }

    private static string GetProfileId(ProfileRowViewModel profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        return profile.ProfileId;
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void ReconcileProfiles()
    {
        if (_disposed)
        {
            return;
        }

        var profiles = _runtime.GetProfiles();
        var currentIds = new HashSet<string>(profiles.Select(profile => profile.Id), StringComparer.Ordinal);

        for (var index = Profiles.Count - 1; index >= 0; index--)
        {
            if (!currentIds.Contains(Profiles[index].ProfileId))
            {
                Profiles.RemoveAt(index);
            }
        }

        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var row = Profiles.FirstOrDefault(item =>
                item.ProfileId.Equals(profile.Id, StringComparison.Ordinal));
            if (row is null)
            {
                row = new ProfileRowViewModel(profile.Id);
                Profiles.Insert(index, row);
            }
            else
            {
                var currentIndex = Profiles.IndexOf(row);
                if (currentIndex != index)
                {
                    Profiles.Move(currentIndex, index);
                }
            }

            row.Update(profile.Name, profile.IsCurrent, profiles.Count > 1 && !profile.IsCurrent);
        }

        CollectionViewSource.GetDefaultView(Profiles)?.Refresh();
    }
}

public sealed class ProfileRowViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _isActive;
    private bool _canRemove;

    public ProfileRowViewModel(string profileId)
    {
        ProfileId = string.IsNullOrWhiteSpace(profileId)
            ? throw new ArgumentException("A profile ID is required.", nameof(profileId))
            : profileId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileId { get; }

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    public bool CanRemove
    {
        get => _canRemove;
        private set => SetField(ref _canRemove, value);
    }

    internal void Update(string name, bool isActive, bool canRemove)
    {
        Name = name;
        IsActive = isActive;
        CanRemove = canRemove;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
