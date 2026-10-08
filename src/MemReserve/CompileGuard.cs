using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemReserve;

internal sealed class CompileGuard : IDisposable
{
    public static readonly string[] ProcessNames = ["msbuild", "cl", "node", "rustc", "java"];

    const uint JobObjectExtendedLimitClass = 9;
    const uint JobObjectLimitJobMemory = 0x00000200;
    const uint ProcessSetQuota = 0x0100;
    const uint ProcessTerminate = 0x0001;
    const uint ProcessSuspendResume = 0x0800;
    const uint CreateSuspended = 0x00000004;
    const uint CreateNewConsole = 0x00000010;

    readonly HashSet<int> _assigned = new();
    readonly HashSet<int> _rejected = new();
    IntPtr _job;
    int _suspendedPid;
    IntPtr _suspendedHandle;

    public bool IsEnabled => _job != IntPtr.Zero;
    public bool HasSuspended => _suspendedPid != 0;
    public string? SuspendedName { get; private set; }
    public string? LastError { get; private set; }

    public bool Enable(long limitBytes, out string? error)
    {
        error = null;
        if (_job != IntPtr.Zero)
        {
            UpdateLimit(limitBytes);
            return true;
        }

        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            error = Win32("创建硬上限失败");
            LastError = error;
            return false;
        }

        if (!TrySetLimit(job, limitBytes, out error))
        {
            CloseHandle(job);
            LastError = error;
            return false;
        }

        _job = job;
        LastError = null;
        return true;
    }

    public void UpdateLimit(long limitBytes)
    {
        if (_job == IntPtr.Zero)
            return;

        if (!TrySetLimit(_job, limitBytes, out string? error))
            LastError = error;
    }

    public void Disable()
    {
        if (_job == IntPtr.Zero)
            return;

        CloseHandle(_job);
        _job = IntPtr.Zero;
        _assigned.Clear();
        _rejected.Clear();
        LastError = null;
    }

    public void AttachNewProcesses()
    {
        if (_job == IntPtr.Zero)
            return;

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                if (pid == Environment.ProcessId || _assigned.Contains(pid) || _rejected.Contains(pid))
                    continue;

                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (Exception)
                {
                    continue;
                }

                if (!IsTarget(name))
                    continue;

                if (TryAssign(pid, out string? error))
                {
                    _assigned.Add(pid);
                    continue;
                }

                _rejected.Add(pid);
                LastError = $"{name}：{error}";
            }
        }
    }

    public bool TryLaunch(string commandLine, out string? error)
    {
        error = null;
        commandLine = commandLine.Trim();
        if (_job == IntPtr.Zero)
        {
            error = "请先打开编译硬上限。";
            return false;
        }

        if (commandLine.Length == 0)
        {
            error = "请输入要启动的命令。";
            return false;
        }

        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNewConsole, IntPtr.Zero, directory, ref startup, out ProcessInformation info))
        {
            error = Win32("启动失败");
            return false;
        }

        try
        {
            if (!AssignProcessToJobObject(_job, info.hProcess))
            {
                error = Win32("无法把新进程放入硬上限");
                TerminateProcess(info.hProcess, 1);
                return false;
            }

            _assigned.Add(info.dwProcessId);
            if (ResumeThread(info.hThread) == uint.MaxValue)
            {
                error = Win32("恢复新进程失败");
                TerminateProcess(info.hProcess, 1);
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(info.hProcess);
            CloseHandle(info.hThread);
        }
    }

    public bool TrySuspendHeaviest(out string? name, out string? error)
    {
        name = null;
        error = null;
        if (HasSuspended)
            return false;

        Process? heaviest = null;
        long best = -1;
        foreach (Process process in Process.GetProcesses())
        {
            bool keep = false;
            try
            {
                if (IsTarget(process.ProcessName))
                {
                    long workingSet = process.WorkingSet64;
                    if (workingSet > best)
                    {
                        heaviest?.Dispose();
                        heaviest = process;
                        best = workingSet;
                        keep = true;
                    }
                }
            }
            catch (Exception)
            {
                keep = false;
            }

            if (!keep)
                process.Dispose();
        }

        if (heaviest == null)
        {
            error = "没有可暂停的编译进程。";
            return false;
        }

        using (heaviest)
        {
            IntPtr handle = OpenProcess(ProcessSuspendResume, false, heaviest.Id);
            if (handle == IntPtr.Zero)
            {
                error = Win32($"无法打开 {heaviest.ProcessName}");
                return false;
            }

            int status = NtSuspendProcess(handle);
            if (status < 0)
            {
                CloseHandle(handle);
                error = $"暂停 {heaviest.ProcessName} 失败（{status}）。";
                return false;
            }

            _suspendedHandle = handle;
            _suspendedPid = heaviest.Id;
            SuspendedName = heaviest.ProcessName;
            name = SuspendedName;
            return true;
        }
    }

    public void PollSuspended()
    {
        if (!HasSuspended)
            return;

        try
        {
            using Process process = Process.GetProcessById(_suspendedPid);
            if (!process.HasExited)
                return;
        }
        catch (ArgumentException)
        {
        }
        catch (Exception)
        {
            return;
        }

        ClearSuspended();
    }

    public void ResumeSuspended()
    {
        if (!HasSuspended)
            return;

        NtResumeProcess(_suspendedHandle);
        ClearSuspended();
    }

    public void KillSuspended()
    {
        if (!HasSuspended)
            return;

        try
        {
            using Process process = Process.GetProcessById(_suspendedPid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }

        ClearSuspended();
    }

    public void Dispose()
    {
        ResumeSuspended();
        Disable();
    }

    void ClearSuspended()
    {
        if (_suspendedHandle != IntPtr.Zero)
            CloseHandle(_suspendedHandle);

        _suspendedHandle = IntPtr.Zero;
        _suspendedPid = 0;
        SuspendedName = null;
    }

    bool TryAssign(int pid, out string? error)
    {
        error = null;
        IntPtr process = OpenProcess(ProcessSetQuota | ProcessTerminate, false, pid);
        if (process == IntPtr.Zero)
        {
            error = Win32("无法打开进程");
            return false;
        }

        try
        {
            if (AssignProcessToJobObject(_job, process))
                return true;

            error = Win32("无法放入硬上限");
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static bool TrySetLimit(IntPtr job, long limitBytes, out string? error)
    {
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitJobMemory
            },
            JobMemoryLimit = (UIntPtr)limitBytes
        };

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (SetInformationJobObject(job, JobObjectExtendedLimitClass, ref info, (uint)size))
        {
            error = null;
            return true;
        }

        error = Win32("设置硬上限失败");
        return false;
    }

    public static string HeaviestName()
    {
        int self = Environment.ProcessId;
        string? compiler = null;
        long compilerBytes = -1;
        string? other = null;
        long otherBytes = -1;
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == self)
                    continue;

                long workingSet = process.WorkingSet64;
                if (IsTarget(process.ProcessName))
                {
                    if (workingSet > compilerBytes)
                    {
                        compilerBytes = workingSet;
                        compiler = process.ProcessName;
                    }
                }
                else if (!IsBackground(process.ProcessName) && workingSet > otherBytes)
                {
                    otherBytes = workingSet;
                    other = process.ProcessName;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (!string.IsNullOrEmpty(compiler))
            return compiler;

        return string.IsNullOrEmpty(other) ? "未知" : other;
    }

    static bool IsTarget(string processName)
    {
        foreach (string name in ProcessNames)
        {
            if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static bool IsBackground(string processName)
    {
        return processName is "Idle" or "System" or "Registry" or "Memory Compression" or "Secure System"
            or "csrss" or "smss" or "wininit" or "winlogon" or "services" or "lsass" or "fontdrvhost" or "dwm";
    }

    static string Win32(string action)
    {
        int code = Marshal.GetLastWin32Error();
        return $"{action}（{code}）：{new Win32Exception(code).Message}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, uint jobObjectInformationClass, ref JobObjectExtendedLimitInformation lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr hThread);

    [DllImport("ntdll.dll")]
    static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    static extern int NtResumeProcess(IntPtr processHandle);
}
