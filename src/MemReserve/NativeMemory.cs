using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MemReserve;

internal enum AcquireStatus
{
    Success,
    Cancelled,
    Failed
}

internal readonly struct AcquireAttempt
{
    public AcquireStatus Status { get; init; }
    public string? Error { get; init; }
    public long HeldBytes { get; init; }

    public static AcquireAttempt Success(long heldBytes) => new()
    {
        Status = AcquireStatus.Success,
        HeldBytes = heldBytes
    };

    public static AcquireAttempt Cancelled() => new() { Status = AcquireStatus.Cancelled };

    public static AcquireAttempt Failed(string error) => new()
    {
        Status = AcquireStatus.Failed,
        Error = error
    };
}

internal static class NativeMemory
{
    const uint MemCommit = 0x1000;
    const uint MemReserve = 0x2000;
    const uint MemRelease = 0x8000;
    const uint PageReadWrite = 0x04;
    const uint QuotaLimitsHardWsMinEnable = 0x00000001;
    const long Megabyte = 1024 * 1024;
    const long LockChunk = 64 * Megabyte;
    const long OverheadBytes = 64 * Megabyte;
    const long MaxHeadroomBytes = 256 * Megabyte;
    const long IdleMinWorkingSet = 32 * Megabyte;
    const long IdleMaxWorkingSet = 256 * Megabyte;

    static readonly object Gate = new();
    static IntPtr _address;
    static long _locked;
    static int _shuttingDown;
    static uint _pageSize;

    public static ulong GetAvailablePhysical()
    {
        return TryGetStatus(out ulong available, out _) ? available : 0;
    }

    public static ulong GetTotalPhysical()
    {
        return TryGetStatus(out _, out ulong total) ? total : 0;
    }

    public static void MarkShutdown()
    {
        Interlocked.Exchange(ref _shuttingDown, 1);
    }

    static bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;

    public static AcquireAttempt TryAcquire(long bytes, CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            if (IsShuttingDown || cancellationToken.IsCancellationRequested)
                return AcquireAttempt.Cancelled();

            if (_address != IntPtr.Zero)
                return AcquireAttempt.Failed("已经占用了一块预留内存。");

            uint pageSize = PageSize();
            long size = AlignUp(bytes, pageSize);
            long minWorkingSet = size + OverheadBytes;
            long maxWorkingSet = minWorkingSet + MaxHeadroomBytes;

            // 先抬高最小工作集，VirtualLock 的配额大约等于这个下限。
            if (!SetProcessWorkingSetSizeEx(
                    GetCurrentProcess(),
                    (UIntPtr)minWorkingSet,
                    (UIntPtr)maxWorkingSet,
                    QuotaLimitsHardWsMinEnable))
            {
                return AcquireAttempt.Failed(Win32Message("设置工作集失败"));
            }

            IntPtr address = VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MemCommit | MemReserve, PageReadWrite);
            if (address == IntPtr.Zero)
            {
                RestoreWorkingSet();
                return AcquireAttempt.Failed(Win32Message("分配内存失败"));
            }

            long locked = 0;
            try
            {
                TouchPages(address, size, pageSize, cancellationToken);

                while (locked < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsShuttingDown)
                        throw new OperationCanceledException();

                    long chunk = Math.Min(LockChunk, size - locked);
                    IntPtr chunkAddress = new(address.ToInt64() + locked);
                    if (!VirtualLock(chunkAddress, (UIntPtr)chunk))
                    {
                        int error = Marshal.GetLastWin32Error();
                        FreeRegion(address, locked);
                        RestoreWorkingSet();
                        return AcquireAttempt.Failed(Win32Message("锁定内存失败", error));
                    }

                    locked += chunk;
                }

                if (IsShuttingDown || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException();

                _address = address;
                _locked = locked;
                return AcquireAttempt.Success(size);
            }
            catch (OperationCanceledException)
            {
                FreeRegion(address, locked);
                RestoreWorkingSet();
                return AcquireAttempt.Cancelled();
            }
            catch (Exception ex)
            {
                FreeRegion(address, locked);
                RestoreWorkingSet();
                return AcquireAttempt.Failed(ex.Message);
            }
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_address == IntPtr.Zero)
            {
                RestoreWorkingSet();
                return;
            }

            FreeRegion(_address, _locked);
            _address = IntPtr.Zero;
            _locked = 0;
            RestoreWorkingSet();
        }
    }

    static void FreeRegion(IntPtr address, long locked)
    {
        if (address == IntPtr.Zero)
            return;

        if (locked > 0)
            VirtualUnlock(address, (UIntPtr)locked);

        VirtualFree(address, UIntPtr.Zero, MemRelease);
    }

    static void RestoreWorkingSet()
    {
        SetProcessWorkingSetSizeEx(
            GetCurrentProcess(),
            (UIntPtr)IdleMinWorkingSet,
            (UIntPtr)IdleMaxWorkingSet,
            QuotaLimitsHardWsMinEnable);
    }

    static unsafe void TouchPages(IntPtr address, long size, uint pageSize, CancellationToken cancellationToken)
    {
        byte* pointer = (byte*)address;
        long checkEvery = LockChunk;
        for (long offset = 0; offset < size; offset += pageSize)
        {
            if (offset % checkEvery == 0)
                cancellationToken.ThrowIfCancellationRequested();

            pointer[offset] = 1;
        }
    }

    static uint PageSize()
    {
        if (_pageSize != 0)
            return _pageSize;

        GetSystemInfo(out SystemInfo info);
        _pageSize = info.PageSize == 0 ? 4096u : info.PageSize;
        return _pageSize;
    }

    static long AlignUp(long value, uint pageSize)
    {
        long mask = pageSize - 1;
        return (value + mask) & ~mask;
    }

    static bool TryGetStatus(out ulong available, out ulong total)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            available = 0;
            total = 0;
            return false;
        }

        available = status.AvailPhys;
        total = status.TotalPhys;
        return true;
    }

    static string Win32Message(string action, int? error = null)
    {
        int code = error ?? Marshal.GetLastWin32Error();
        return $"{action}（{code}）：{new Win32Exception(code).Message}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemInfo
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public IntPtr MinimumApplicationAddress;
        public IntPtr MaximumApplicationAddress;
        public IntPtr ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern void GetSystemInfo(out SystemInfo lpSystemInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        UIntPtr dwMinimumWorkingSetSize,
        UIntPtr dwMaximumWorkingSetSize,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualLock(IntPtr lpAddress, UIntPtr dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualUnlock(IntPtr lpAddress, UIntPtr dwSize);
}
