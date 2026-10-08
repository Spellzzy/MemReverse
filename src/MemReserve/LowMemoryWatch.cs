namespace MemReserve;

internal sealed class LowMemoryWatch : IDisposable
{
    const int LowMemoryResourceNotification = 0;
    const uint WaitObject = 0;

    IntPtr _handle;
    CancellationTokenSource? _cancellation;
    Thread? _thread;
    bool _disposed;

    public event Action? Signaled;

    public void Start()
    {
        _handle = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        if (_handle == IntPtr.Zero)
            return;

        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "MemReserve.LowMemory"
        };
        _thread.Start();
    }

    void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            uint wait = WaitForSingleObject(_handle, 1000);
            if (token.IsCancellationRequested || wait != WaitObject)
                continue;

            Signaled?.Invoke();
            while (!token.IsCancellationRequested && WaitForSingleObject(_handle, 0) == WaitObject)
            {
                if (token.WaitHandle.WaitOne(1000))
                    return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation?.Cancel();
        _thread?.Join(1500);
        if (_handle != IntPtr.Zero)
            CloseHandle(_handle);
        _cancellation?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
