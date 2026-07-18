namespace RockcliffeCourtBooker.Worker;

internal sealed class TargetDateLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private TargetDateLock(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static TargetDateLock TryAcquire(DateOnly targetDate)
    {
        var scope = OperatingSystem.IsWindows() ? @"Local\" : string.Empty;
        var mutex = new Mutex(
            initiallyOwned: false,
            $"{scope}RockcliffeCourtBooker-{targetDate:yyyyMMdd}");
        try
        {
            return new TargetDateLock(mutex, mutex.WaitOne(TimeSpan.Zero));
        }
        catch (AbandonedMutexException)
        {
            return new TargetDateLock(mutex, ownsMutex: true);
        }
    }

    public bool Acquired => _ownsMutex;

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
