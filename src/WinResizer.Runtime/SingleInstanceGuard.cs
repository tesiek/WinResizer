using System;
using System.Threading;

namespace WinResizer.Runtime;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string ApplicationMutexName = "Global\\b3e7eb82-3db1-4f1b-9c3c-d67643ae0b00";

    private Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceGuard? TryAcquire(string mutexName = ApplicationMutexName)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            throw new ArgumentException("A mutex name is required.", nameof(mutexName));
        }

        var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstanceGuard(mutex, true);
        }
        catch
        {
            if (acquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var mutex = _mutex;
        if (mutex is null)
        {
            return;
        }

        _mutex = null;
        if (_ownsMutex)
        {
            _ownsMutex = false;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already shutting down or ownership was lost.
            }
        }

        mutex.Dispose();
    }
}
