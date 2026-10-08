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

    public const int RecentReleaseLimit = 4;

    readonly CancellationTokenSource _cancellation = new();
    readonly CompileGuard _guard = new();
    readonly List<ReleaseNote> _recentReleases = new();
    SynchronizationContext? _ui;
    Task? _inflight;
    int _operationId;
    int _cushionMb;
    int _thresholdMb;
    long _heldBytes;
    ulong _availableBytes;
    long _lastSliceAt;
    bool _shuttingDown;
    bool _recovery;
    bool _suppressAuto;
    bool _suspendArmed = true;
    bool _announcedSlice;
    int _recoverSeconds;

    public bool PauseOnThreshold { get; private set; }
    public bool HardLimit { get; private set; }

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
    public bool HasPausedProcess => _guard.HasSuspended;
    public string GuardNote => _guard.LastError ?? "";
    public IReadOnlyList<ReleaseNote> RecentReleases => _recentReleases;

    public event EventHandler? StateChanged;
    public event EventHandler? ReservationReleased;
    public event EventHandler? SliceReleased;
    public event EventHandler<string>? ProcessPaused;
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
        _guard.PollSuspended();
        if (HardLimit)
            _guard.AttachNewProcesses();
        if (_availableBytes >= (ulong)_thresholdMb * (ulong)Megabyte)
            _suspendArmed = true;

        if (Phase == CushionPhase.Acquiring)
        {
            Notify();
            return;
        }

        if (Phase == CushionPhase.Holding)
        {
            if (_availableBytes < (ulong)_thresholdMb * (ulong)Megabyte)
            {
                TryPause();
                ReleaseOneSlice(immediate: false);
                return;
            }

            _announcedSlice = false;
            StatusText = $"已预留 {HeldMb} MB";
            DetailText = "";
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
            TryPause();
            ReleaseOneSlice(immediate: true);
            return;
        }

        if (HardLimit)
            _guard.UpdateLimit(JobLimitBytes());

        Notify();
    }

    public bool SetHardLimit(bool enabled, out string? error)
    {
        error = null;
        if (!enabled)
        {
            _guard.Disable();
            HardLimit = false;
            Notify();
            return true;
        }

        if (!_guard.Enable(JobLimitBytes(), out error))
        {
            HardLimit = false;
            Notify();
            return false;
        }

        HardLimit = true;
        _guard.AttachNewProcesses();
        Notify();
        return true;
    }

    public void SetPauseOnThreshold(bool enabled)
    {
        PauseOnThreshold = enabled;
        if (!enabled)
        {
            _suspendArmed = true;
            _guard.ResumeSuspended();
        }

        Notify();
    }

    public bool TryLaunchCompile(string commandLine, out string? error)
    {
        return _guard.TryLaunch(commandLine, out error);
    }

    public void ResumePaused()
    {
        _guard.ResumeSuspended();
        Notify();
    }

    public void KillPaused()
    {
        _guard.KillSuspended();
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
        _guard.Dispose();
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
        RememberAvailable(held);
        _heldBytes = 0;
        _announcedSlice = false;
        EnterRecovery(recovery);
    }

    void ReleaseOneSlice(bool immediate)
    {
        long now = Environment.TickCount64;
        if (!immediate && now - _lastSliceAt < 900)
        {
            Notify();
            return;
        }

        long released = NativeMemory.ReleaseSlice(NativeMemory.SliceBytes);
        _lastSliceAt = now;
        if (released > 0)
            NotePressureRelease();
        _heldBytes = NativeMemory.HeldBytes;
        RememberAvailable(released);
        if (_heldBytes <= 0)
        {
            _announcedSlice = false;
            EnterRecovery(recovery: true);
            ReservationReleased?.Invoke(this, EventArgs.Empty);
            return;
        }

        StatusText = $"已预留 {HeldMb} MB";
        DetailText = $"已放开 {Math.Max(released, 0) / Megabyte} MB";
        Notify();
        if (released > 0 && !_announcedSlice)
        {
            _announcedSlice = true;
            SliceReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    void TryPause()
    {
        if (!PauseOnThreshold || !_suspendArmed || _guard.HasSuspended)
            return;

        if (_guard.TrySuspendHeaviest(out string? name, out _))
        {
            _suspendArmed = false;
            if (!string.IsNullOrEmpty(name))
                ProcessPaused?.Invoke(this, name);
        }
    }

    void EnterRecovery(bool recovery)
    {
        Phase = CushionPhase.Waiting;
        _recovery = recovery;
        _suppressAuto = recovery;
        _recoverSeconds = 0;
        StatusText = recovery ? "已释放" : "等待";
        DetailText = recovery ? RecoverDetail() : "";
        Notify();
    }

    void NotePressureRelease()
    {
        _recentReleases.Insert(0, new ReleaseNote(DateTime.Now, CompileGuard.HeaviestName(), AvailableMb));
        if (_recentReleases.Count > RecentReleaseLimit)
            _recentReleases.RemoveAt(_recentReleases.Count - 1);
    }

    void RememberAvailable(long releasedBytes)
    {
        ulong observed = NativeMemory.GetAvailablePhysical();
        ulong estimated = _availableBytes + (ulong)Math.Max(releasedBytes, 0);
        _availableBytes = Math.Max(observed, estimated);
    }

    long JobLimitBytes()
    {
        long total = (long)NativeMemory.GetTotalPhysical();
        long keep = (long)_thresholdMb * Megabyte;
        long limit = total - keep;
        return limit < NativeMemory.SliceBytes ? NativeMemory.SliceBytes : limit;
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

internal readonly record struct ReleaseNote(DateTime Time, string ProcessName, long AvailableMb);
