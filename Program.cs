namespace InputFinder;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                CrashLog.Write(ex);
        };
        Application.Run(new MainForm());
    }
}

static class CrashLog
{
    public static void Write(Exception ex)
    {
        try
        {
            var dir = LogPaths.Directory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
        }
        catch
        {
            // Last-chance log; nothing else to do.
        }
    }
}
