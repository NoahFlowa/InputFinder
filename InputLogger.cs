using System.Globalization;
using System.Text;

namespace InputFinder;

sealed class InputLogger : IDisposable
{
    readonly StreamWriter _writer;
    readonly object _gate = new();
    Burst? _burst;

    public string FilePath { get; }

    public InputLogger()
    {
        var dir = LogPaths.Directory;
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "input-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
        var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.WriteLine("timestamp,key,vk,scan,action,repeat_count,rate_per_sec,device_name,vid,pid,device_path,source,injected,extra_info,foreground_process,foreground_title");
    }

    public void WriteDeviceNote(string text)
    {
        lock (_gate)
        {
            _writer.WriteLine("# " + text.Replace('\r', ' ').Replace('\n', ' '));
        }
    }

    public void Write(KeyEvent keyEvent)
    {
        lock (_gate)
        {
            if (!keyEvent.IsRepeat)
            {
                CloseBurst();
                WriteRow(keyEvent, keyEvent.IsUp ? "up" : "down", 1, "");
                return;
            }

            var now = keyEvent.Tick;
            if (_burst != null && !_burst.Same(keyEvent))
                CloseBurst();

            if (_burst == null)
            {
                _burst = new Burst(keyEvent);
                return;
            }

            var interval = now - _burst.LastArrival;
            _burst.LastArrival = now;
            _burst.Prototype.ForegroundProcess = keyEvent.ForegroundProcess;
            _burst.Prototype.ForegroundTitle = keyEvent.ForegroundTitle;
            _burst.Prototype.Timestamp = keyEvent.Timestamp;

            if (!_burst.Collapsing)
            {
                if (interval >= 50)
                {
                    FlushPendingAsSingles();
                    WriteRow(keyEvent, "down", 1, "");
                    return;
                }

                _burst.Collapsing = true;
                _burst.Count = _burst.PendingSingles + 1;
                _burst.PendingSingles = 0;
                _burst.WindowStart = _burst.FirstTick;
                _burst.LastSummary = now;
                return;
            }

            if (interval >= 50)
            {
                WriteSummarySlice(now);
                _burst.Collapsing = false;
                _burst.PendingSingles = 0;
                WriteRow(keyEvent, "down", 1, "");
                return;
            }

            _burst.Count++;
            if (now - _burst.LastSummary >= 250)
                WriteSummarySlice(now);
        }
    }

    public void FlushWriter()
    {
        lock (_gate)
        {
            if (_burst is { Collapsing: true, Count: > 0 } &&
                Environment.TickCount64 - _burst.LastSummary >= 250)
            {
                WriteSummarySlice(Environment.TickCount64);
            }

            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseBurst();
            _writer.WriteLine("# capture stopped at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Flush();
            _writer.Dispose();
        }
    }

    void CloseBurst()
    {
        if (_burst == null)
            return;
        if (_burst.Collapsing && _burst.Count > 0)
            WriteSummarySlice(Environment.TickCount64);
        else
            FlushPendingAsSingles();
        _burst = null;
    }

    void FlushPendingAsSingles()
    {
        if (_burst == null || _burst.PendingSingles <= 0)
            return;
        for (var i = 0; i < _burst.PendingSingles; i++)
            WriteRow(_burst.Prototype, "down", 1, "");
        _burst.PendingSingles = 0;
    }

    void WriteSummarySlice(long now)
    {
        if (_burst == null || _burst.Count <= 0)
            return;
        var elapsed = Math.Max(1, now - _burst.WindowStart);
        var rate = (_burst.Count * 1000.0 / elapsed).ToString("0.0", CultureInfo.InvariantCulture);
        WriteRow(_burst.Prototype, "repeat", _burst.Count, rate);
        _burst.Count = 0;
        _burst.WindowStart = now;
        _burst.LastSummary = now;
    }

    void WriteRow(KeyEvent keyEvent, string action, int count, string rate)
    {
        var line = string.Join(',',
            Csv(keyEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
            Csv(keyEvent.KeyName),
            Csv("0x" + keyEvent.Vk.ToString("X2", CultureInfo.InvariantCulture)),
            Csv("0x" + (keyEvent.Scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture)),
            Csv(action),
            count.ToString(CultureInfo.InvariantCulture),
            Csv(rate),
            Csv(keyEvent.Device.Name),
            Csv(keyEvent.Device.Vid),
            Csv(keyEvent.Device.Pid),
            Csv(keyEvent.Device.Path),
            Csv(keyEvent.Source),
            keyEvent.Injected ? "yes" : "no",
            Csv("0x" + keyEvent.Extra.ToString("X", CultureInfo.InvariantCulture)),
            Csv(keyEvent.ForegroundProcess),
            Csv(keyEvent.ForegroundTitle));
        _writer.WriteLine(line);
    }

    static string Csv(string? value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    sealed class Burst
    {
        public Burst(KeyEvent prototype)
        {
            Prototype = prototype;
            FirstTick = prototype.Tick;
            LastArrival = prototype.Tick;
            WindowStart = prototype.Tick;
            PendingSingles = 1;
        }

        public KeyEvent Prototype { get; }
        public long FirstTick { get; }
        public long LastArrival { get; set; }
        public long WindowStart { get; set; }
        public long LastSummary { get; set; }
        public int Count { get; set; }
        public int PendingSingles { get; set; }
        public bool Collapsing { get; set; }

        public bool Same(KeyEvent other) =>
            Prototype.Device.Key == other.Device.Key &&
            Prototype.Vk == other.Vk &&
            (Prototype.Scan & 0xFF) == (other.Scan & 0xFF) &&
            Prototype.IsUp == other.IsUp;
    }
}
