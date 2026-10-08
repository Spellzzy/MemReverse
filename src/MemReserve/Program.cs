namespace MemReserve;

internal static class Program
{
    const string MutexName = @"Local\ProTools.MemReserve";

    [STAThread]
    static void Main(string[] args)
    {
        if (!Environment.Is64BitProcess)
        {
            MessageBox.Show("内存预留需要 64 位进程。", "内存预留", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
            return;

        bool startInTray = args.Any(argument => string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase));
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startInTray));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
