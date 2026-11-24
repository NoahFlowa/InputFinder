using System.Text;

namespace InputFinder;

static class ForegroundWindow
{
    static long _tick;
    static string _process = "";
    static string _title = "";

    public static (string Process, string Title) Current()
    {
        var now = Environment.TickCount64;
        if (now - _tick < 100)
            return (_process, _title);

        _tick = now;
        _process = "";
        _title = "";

        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return (_process, _title);

        var title = new StringBuilder(512);
        Win32.GetWindowText(hwnd, title, title.Capacity);
        _title = title.ToString();

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return (_process, _title);

        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero || process == Win32.InvalidHandle)
        {
            _process = "pid " + pid;
            return (_process, _title);
        }

        try
        {
            var image = new StringBuilder(1024);
            uint size = (uint)image.Capacity;
            if (Win32.QueryFullProcessImageName(process, 0, image, ref size))
            {
                var full = image.ToString();
                _process = Path.GetFileName(full);
                if (_process.Length == 0)
                    _process = full;
            }
            else
            {
                _process = "pid " + pid;
            }
        }
        finally
        {
            Win32.CloseHandle(process);
        }

        return (_process, _title);
    }
}
