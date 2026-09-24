using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinResizer.Base;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;

namespace WinResizer.Runtime;

public sealed class AutoResizeCoordinator : IDisposable
{
    private readonly Func<AutoResizeConfigurationSnapshot?> _snapshotProvider;
    private readonly Action<string> _log;
    private readonly object _pendingSync = new();
    private readonly Dictionary<WindowIdentity, PendingAutoResize> _pending = new();
    private long _generation;
    private bool _disposed;

    public AutoResizeCoordinator(Func<AutoResizeConfigurationSnapshot?> snapshotProvider)
        : this(snapshotProvider, null)
    {
    }

    public AutoResizeCoordinator(
        Func<AutoResizeConfigurationSnapshot?> snapshotProvider,
        Action<string>? log = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _log = log ?? (_ => { });
    }

    public void Schedule(IntPtr handle)
    {
        if (!Resizer.TryGetWindowProcessId(handle, out var processId))
        {
            return;
        }

        var identity = new WindowIdentity(handle, processId);
        PendingAutoResize pending;
        lock (_pendingSync)
        {
            if (_disposed || _pending.ContainsKey(identity))
            {
                return;
            }

            pending = new PendingAutoResize(_generation);
            _pending.Add(identity, pending);
        }

        _ = Task.Run(() => RunAsync(identity, pending));
    }

    public void CancelPending()
    {
        PendingAutoResize[] pending;
        lock (_pendingSync)
        {
            _generation++;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var operation in pending)
        {
            try
            {
                operation.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_pendingSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelPending();
    }

    private async Task RunAsync(WindowIdentity identity, PendingAutoResize pending)
    {
        try
        {
            var snapshot = _snapshotProvider();
            if (snapshot is null || !IsCurrent(pending) ||
                !WindowUtils.TryPrepareAutoResize(
                    identity.Handle,
                    snapshot.WindowSizes,
                    snapshot.IgnoredWindows,
                    out var processName))
            {
                return;
            }

            var delay = snapshot.EnableAutoResizeDelay
                ? AutoResizeDelaySettings.GetProcessDelay(snapshot.WindowSizes, processName)
                : 0;
            if (delay > 0)
            {
                await Task.Delay(delay, pending.Cancellation.Token).ConfigureAwait(false);
            }

            pending.Cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(pending) || !HasSameEligibleWindow(identity))
            {
                return;
            }

            snapshot = _snapshotProvider();
            if (snapshot is null || !IsCurrent(pending) || !HasSameEligibleWindow(identity))
            {
                return;
            }

            WindowUtils.TryResizeAutoWindow(
                identity.Handle,
                processName,
                snapshot.WindowSizes,
                snapshot.EnableResizeByTitle,
                snapshot.IgnoredWindows,
                snapshot.CompensateDwmFrameEffects,
                () => IsCurrent(pending) && HasSameEligibleWindow(identity));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log($"Auto resize failed for window {identity.Handle} (PID {identity.ProcessId}): {exception}");
        }
        finally
        {
            lock (_pendingSync)
            {
                if (_pending.TryGetValue(identity, out var current) && ReferenceEquals(current, pending))
                {
                    _pending.Remove(identity);
                }
            }

            pending.Cancellation.Dispose();
        }
    }

    private bool IsCurrent(PendingAutoResize pending)
    {
        lock (_pendingSync)
        {
            return !_disposed && pending.Generation == _generation && !pending.Cancellation.IsCancellationRequested;
        }
    }

    private static bool HasSameEligibleWindow(WindowIdentity identity)
    {
        return Resizer.TryGetWindowProcessId(identity.Handle, out var currentProcessId) &&
               currentProcessId == identity.ProcessId &&
               Resizer.IsEligibleForAutoResize(identity.Handle);
    }

    private readonly struct WindowIdentity : IEquatable<WindowIdentity>
    {
        public WindowIdentity(IntPtr handle, uint processId)
        {
            Handle = handle;
            ProcessId = processId;
        }

        public IntPtr Handle { get; }
        public uint ProcessId { get; }

        public bool Equals(WindowIdentity other) => Handle == other.Handle && ProcessId == other.ProcessId;
        public override bool Equals(object obj) => obj is WindowIdentity other && Equals(other);
        public override int GetHashCode() => Handle.GetHashCode() ^ ProcessId.GetHashCode();
    }

    private sealed class PendingAutoResize
    {
        public PendingAutoResize(long generation)
        {
            Generation = generation;
        }

        public long Generation { get; }
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
