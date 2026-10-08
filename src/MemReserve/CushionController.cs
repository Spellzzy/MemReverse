namespace MemReserve;

internal enum CushionPhase
{
    Waiting,
    Acquiring,
    Holding
}

internal sealed class CushionController
{
    public const int MarginMb = 512;
    public const int RecoverHoldSeconds = 30;
    const long Megabyte = 1024 * 1024;

    readonly CancellationTokenSource _cancellation = new();
    SynchronizationContext? _ui;
    Task? _inflight;
    int _operationId;
    int _cushionMb;
    int _thresholdMb;
    long _heldBytes;
    ulong _availableBytes;
    bool _shuttingDown;
    bool _recovery;
    bool _suppressAuto;
    int _recoverSeconds;

    public CushionController(int cushionMb, int thresholdMb)
    {
        _cushionMb = cushionMb;
        _thresholdMb = thresholdMb;
        StatusText = "等待";
    }

    public CushionPhase Phase { get; private set; } = CushionPhase.Waiting;
    public string StatusText { get; private set; }
    public string DetailText { get; private set; } = "";
    public int CushionMb => _cushionMb;
    public int ThresholdMb => _thresholdMb;
    public long AvailableMb => (long)(_availableBytes / (ulong)Megabyte);
    public long HeldMb => _heldBytes / Megabyte;

    public event EventHandler? StateChanged;
    public event EventHandler? ReservationReleased;
    public event EventHandler<string>? ReservationFailed;

    public void UseUiContext(SynchronizationContext context)
    {
        _ui = context;
    }

    public void Tick()
    {
        if (_shuttingDown)
            return;

        _availableBytes = NativeMemory.GetAvailablePhysical();

        if (Phase == CushionPhase.Acquiring)
        {
            Notify();
            return;
        }

        if (Phase == CushionPhase.Holding)
        {
            if (_availableBytes < (ulong)_thresholdMb * (ulong)Megabyte)
            {
                ReleaseHolding(recovery: true);
                ReservationReleased?.Invoke(this, EventArgs.Empty);
                return;
            }

            StatusText = $"已预留 {HeldMb} MB";
            Notify();
            return;
        }

        if (_recovery)
        {
            if (_availableBytes >= (ulong)NeedBytes(_cushionMb, _thresholdMb))
                _recoverSeconds++;
            else
                _recoverSeconds = 0;

            StatusText = "已释放";
            DetailText = RecoverDetail();
            Notify();

            if (_recoverSeconds >= RecoverHoldSeconds)
            {
                _recovery = false;
                _recoverSeconds = 0;
                _suppressAuto = false;
                TryStartAcquire();
            }

            return;
        }

        if (_suppressAuto)
        {
            Notify();
            return;
        }

        TryStartAcquire();
    }

    public void AcquireNow()
    {
        if (_shuttingDown || Phase is CushionPhase.Acquiring or CushionPhase.Holding)
            return;

        _availableBytes = NativeMemory.GetAvailablePhysical();
        _recovery = false;
        _recoverSeconds = 0;
        _suppressAuto = false;
        TryStartAcquire();
    }

    public void ReleaseNow()
    {
        if (_shuttingDown || Phase != CushionPhase.Holding)
            return;

        ReleaseHolding(recovery: true);
        ReservationReleased?.Invoke(this, EventArgs.Empty);
    }

    public bool TrySetCushionMb(int megabytes)
    {
        if (megabytes == _cushionMb)
            return true;

        if (_shuttingDown || Phase == CushionPhase.Acquiring)
            return false;

        if (Phase != CushionPhase.Holding)
        {
            _cushionMb = megabytes;
            _recoverSeconds = 0;
            Notify();
            return true;
        }

        ulong availableIfReleased = _availableBytes + (ulong)Math.Max(_heldBytes, 0);
        if (availableIfReleased < (ulong)NeedBytes(megabytes, _thresholdMb))
        {
            DetailText = "剩余内存不够改成这个预留大小，仍保持当前预留。";
            Notify();
            return false;
        }

        _cushionMb = megabytes;
        _recoverSeconds = 0;
        ReleaseHolding(recovery: false);
        _suppressAuto = false;
        TryStartAcquire();
        return true;
    }

    public void SetThresholdMb(int megabytes)
    {
        if (megabytes == _thresholdMb || _shuttingDown)
            return;

        _thresholdMb = megabytes;
        _recoverSeconds = 0;

        if (Phase == CushionPhase.Holding && _availableBytes < (ulong)_thresholdMb * (ulong)Megabyte)
        {
            ReleaseHolding(recovery: true);
            ReservationReleased?.Invoke(this, EventArgs.Empty);
            return;
        }

        Notify();
    }

    public void Shutdown()
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        _cancellation.Cancel();
        NativeMemory.MarkShutdown();
        try
        {
            _inflight?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
        }

        NativeMemory.Release();
    }

    void TryStartAcquire()
    {
        if (_shuttingDown || Phase is CushionPhase.Acquiring or CushionPhase.Holding)
            return;

        if (_availableBytes < (ulong)NeedBytes(_cushionMb, _thresholdMb))
        {
            Phase = CushionPhase.Waiting;
            StatusText = "等待";
            DetailText = $"当前剩余不足以同时预留并保住阈值（需要至少 {NeedMegabytes(_cushionMb, _thresholdMb)} MB，当前 {AvailableMb} MB）";
            Notify();
            return;
        }

        Phase = CushionPhase.Acquiring;
        StatusText = "正在占用";
        DetailText = "";
        int operationId = ++_operationId;
        long bytes = (long)_cushionMb * Megabyte;
        var token = _cancellation.Token;
        var ui = _ui ??= SynchronizationContext.Current;
        Notify();

        _inflight = Task.Run(() =>
        {
            AcquireAttempt result;
            try
            {
                result = NativeMemory.TryAcquire(bytes, token);
            }
            catch (Exception ex)
            {
                result = AcquireAttempt.Failed(ex.Message);
            }

            void Complete() => FinishAcquire(operationId, result);
            if (ui != null)
                ui.Post(_ => Complete(), null);
            else
                Complete();
        });
    }

    void FinishAcquire(int operationId, AcquireAttempt result)
    {
        if (_shuttingDown)
        {
            if (result.Status == AcquireStatus.Success)
                NativeMemory.Release();
            return;
        }

        if (operationId != _operationId)
        {
            if (result.Status == AcquireStatus.Success)
                NativeMemory.Release();
            return;
        }

        _inflight = null;
        if (result.Status == AcquireStatus.Success)
        {
            _heldBytes = result.HeldBytes;
            Phase = CushionPhase.Holding;
            _recovery = false;
            _suppressAuto = false;
            _recoverSeconds = 0;
            _availableBytes = NativeMemory.GetAvailablePhysical();
            StatusText = $"已预留 {HeldMb} MB";
            DetailText = "";
            Notify();
            return;
        }

        _heldBytes = 0;
        Phase = CushionPhase.Waiting;
        if (result.Status == AcquireStatus.Cancelled)
        {
            StatusText = _recovery ? "已释放" : "等待";
            DetailText = _recovery ? RecoverDetail() : "";
            Notify();
            return;
        }

        _suppressAuto = true;
        _recovery = false;
        StatusText = "失败：" + result.Error;
        DetailText = result.Error ?? "";
        Notify();
        ReservationFailed?.Invoke(this, DetailText);
    }

    void ReleaseHolding(bool recovery)
    {
        long held = _heldBytes;
        NativeMemory.Release();
        ulong observed = NativeMemory.GetAvailablePhysical();
        ulong estimated = _availableBytes + (ulong)Math.Max(held, 0);
        _availableBytes = Math.Max(observed, estimated);
        _heldBytes = 0;
        Phase = CushionPhase.Waiting;
        _recovery = recovery;
        _suppressAuto = recovery;
        _recoverSeconds = 0;
        StatusText = recovery ? "已释放" : "等待";
        DetailText = recovery ? RecoverDetail() : "";
        Notify();
    }

    string RecoverDetail()
    {
        return $"恢复倒计时 {_recoverSeconds}/{RecoverHoldSeconds} 秒（恢复线 {NeedMegabytes(_cushionMb, _thresholdMb)} MB）";
    }

    static long NeedMegabytes(int cushionMb, int thresholdMb)
    {
        return (long)cushionMb + thresholdMb + MarginMb;
    }

    static long NeedBytes(int cushionMb, int thresholdMb)
    {
        return NeedMegabytes(cushionMb, thresholdMb) * Megabyte;
    }

    void Notify()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
