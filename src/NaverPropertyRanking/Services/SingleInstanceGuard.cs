namespace NaverPropertyRanking.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static bool TryAcquire(string mutexName, out SingleInstanceGuard guard)
    {
        var mutex = new Mutex(true, mutexName, out var createdNew);
        guard = new SingleInstanceGuard(mutex, createdNew);
        return createdNew;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already shutting down or ownership was lost.
            }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
