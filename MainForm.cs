using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace InputFinder;

sealed class MainForm : Form
{
    const int MaxEvents = 400;

    readonly DeviceResolver _devices = new();
    readonly InputClassifier _classifier = new();
    readonly ConcurrentQueue<HookPacket> _hookQueue = new();
    readonly Dictionary<string, DeviceStats> _stats = new();
    readonly Dictionary<string, DataGridViewRow> _deviceRows = new();
    readonly List<DisplayedEvent> _events = new();
    readonly HashSet<string> _watchHeld = new();

    readonly Panel _banner = new();
    readonly Label _bannerTitle = new();
    readonly Label _bannerDetail = new();
    readonly CheckBox _alwaysOnTop = new();
    readonly CheckBox _pauseView = new();
    readonly Button _watchButton = new();
    readonly Button _clearButton = new();
    readonly Button _openLogButton = new();
    readonly SplitContainer _split = new();
    readonly DataGridView _deviceGrid = new();
    readonly DataGridView _eventGrid = new();
    readonly ListBox _processList = new();
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _statusText = new();
    readonly ToolStripStatusLabel _statusLog = new();

    readonly DataGridViewTextBoxColumn _watchColumn;
    readonly System.Windows.Forms.Timer _expireTimer = new();
    readonly System.Windows.Forms.Timer _flushTimer = new();
    readonly System.Windows.Forms.Timer _processTimer = new();

    InputLogger? _logger;
    Win32.LowLevelKeyboardProc? _hookProc;
    IntPtr _hook;
    bool _captureReady;
    bool _devicesDirty;
    bool _sortDevices;
    long _lastDeviceSort;
    long _lastRateRefresh;
    long _lastBannerPaint;
    long _lastWatchTick;
    string _lastWatchDevice = "";
    int _watchTotal;
    int _eventTotal;
    string _captureProblem = "";
    bool _armingWatch;
    WatchedKey? _watched;

    public MainForm()
    {
        Text = "InputFinder — who is sending keys";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1180, 760);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(232, 232, 232);
        Font = new Font("Segoe UI", 10f);
        TopMost = true;

        _banner.Dock = DockStyle.Top;
        _banner.Height = 78;
        _banner.BackColor = Color.FromArgb(32, 48, 40);
        _bannerTitle.AutoSize = false;
        _bannerTitle.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
        _bannerTitle.ForeColor = Color.FromArgb(190, 230, 200);
        _bannerTitle.Text = "Choose a key to watch";
        _bannerTitle.Location = new Point(16, 8);
        _bannerTitle.Size = new Size(1100, 36);
        _bannerTitle.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _bannerDetail.AutoSize = false;
        _bannerDetail.ForeColor = Color.FromArgb(210, 210, 210);
        _bannerDetail.Text = "Click Watch key, then press the key you want to track. Other keys are still recorded.";
        _bannerDetail.Location = new Point(18, 46);
        _bannerDetail.Size = new Size(1100, 24);
        _bannerDetail.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _banner.Controls.Add(_bannerTitle);
        _banner.Controls.Add(_bannerDetail);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(37, 37, 38),
            Padding = new Padding(8, 8, 8, 4),
        };
        _alwaysOnTop.Text = "Always on top";
        _alwaysOnTop.Checked = true;
        _alwaysOnTop.AutoSize = true;
        _alwaysOnTop.ForeColor = ForeColor;
        _alwaysOnTop.Margin = new Padding(4, 6, 12, 0);
        _alwaysOnTop.CheckedChanged += (_, _) => TopMost = _alwaysOnTop.Checked;
        _pauseView.Text = "Pause view";
        _pauseView.AutoSize = true;
        _pauseView.ForeColor = ForeColor;
        _pauseView.Margin = new Padding(4, 6, 12, 0);
        _pauseView.CheckedChanged += (_, _) =>
        {
            if (!_pauseView.Checked)
                RebuildEventGrid();
            SetStatus(_pauseView.Checked
                ? "View paused. Logging is still running."
                : "Listening. This window does not need focus.");
        };
        _watchButton.Text = "Watch key";
        _watchButton.AutoSize = true;
        _watchButton.Margin = new Padding(4, 2, 8, 0);
        _watchButton.Click += (_, _) =>
        {
            _armingWatch = true;
            UpdateWatchButton();
            UpdateBanner();
            SetStatus("Press the key you want to watch.");
        };
        _clearButton.Text = "Clear";
        _clearButton.AutoSize = true;
        _clearButton.Margin = new Padding(4, 2, 8, 0);
        _clearButton.Click += (_, _) => ClearView();
        _openLogButton.Text = "Open log folder";
        _openLogButton.AutoSize = true;
        _openLogButton.Margin = new Padding(4, 2, 8, 0);
        _openLogButton.Click += (_, _) => OpenLogFolder();
        StyleButton(_watchButton);
        StyleButton(_clearButton);
        StyleButton(_openLogButton);
        toolbar.Controls.Add(_alwaysOnTop);
        toolbar.Controls.Add(_pauseView);
        toolbar.Controls.Add(_watchButton);
        toolbar.Controls.Add(_clearButton);
        toolbar.Controls.Add(_openLogButton);

        _status.BackColor = Color.FromArgb(37, 37, 38);
        _status.ForeColor = ForeColor;
        _status.SizingGrip = false;
        _statusText.Spring = false;
        _statusText.Text = "Starting…";
        _statusLog.Spring = true;
        _statusLog.TextAlign = ContentAlignment.MiddleRight;
        _status.Items.Add(_statusText);
        _status.Items.Add(_statusLog);

        ConfigureGrid(_deviceGrid);
        ConfigureGrid(_eventGrid);
        _deviceGrid.Columns.Add(TextColumn("Name", 180, fill: 160));
        _deviceGrid.Columns.Add(TextColumn("VID", 60));
        _deviceGrid.Columns.Add(TextColumn("PID", 60));
        _deviceGrid.Columns.Add(TextColumn("Path", 220, fill: 220));
        _deviceGrid.Columns.Add(TextColumn("Last key", 140));
        _deviceGrid.Columns.Add(NumberColumn("Total downs", 90));
        _watchColumn = NumberColumn("Watched", 80);
        _deviceGrid.Columns.Add(_watchColumn);
        _deviceGrid.Columns.Add(NumberColumn("Watched / sec", 100));

        _eventGrid.Columns.Add(TextColumn("Time", 110));
        _eventGrid.Columns.Add(TextColumn("Key", 140));
        _eventGrid.Columns.Add(TextColumn("Action", 70));
        _eventGrid.Columns.Add(NumberColumn("Count", 70));
        _eventGrid.Columns.Add(TextColumn("Rate", 70));
        _eventGrid.Columns.Add(TextColumn("Device", 220, fill: 180));
        _eventGrid.Columns.Add(TextColumn("Source", 150));
        _eventGrid.Columns.Add(TextColumn("Foreground", 220, fill: 160));

        var processHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8, 6, 8, 0),
            Text = "Programs that often send keys",
            ForeColor = Color.FromArgb(200, 200, 200),
        };
        _processList.Dock = DockStyle.Fill;
        _processList.BackColor = Color.FromArgb(30, 30, 30);
        _processList.ForeColor = Color.FromArgb(232, 232, 232);
        _processList.BorderStyle = BorderStyle.None;
        _processList.IntegralHeight = false;
        var processPanel = new Panel { Dock = DockStyle.Right, Width = 280, Padding = new Padding(0, 0, 0, 0) };
        processPanel.Controls.Add(_processList);
        processPanel.Controls.Add(processHeader);

        var deviceHost = new Panel { Dock = DockStyle.Fill };
        var deviceHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(8, 4, 0, 0),
            Text = "Keyboards and other devices that can send keys",
            ForeColor = Color.FromArgb(200, 200, 200),
        };
        deviceHost.Controls.Add(_deviceGrid);
        deviceHost.Controls.Add(deviceHeader);

        var top = new Panel { Dock = DockStyle.Fill };
        top.Controls.Add(deviceHost);
        top.Controls.Add(processPanel);

        var eventHost = new Panel { Dock = DockStyle.Fill };
        var eventHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(8, 4, 0, 0),
            Text = "Recent keys    repeats stay on one row",
            ForeColor = Color.FromArgb(200, 200, 200),
        };
        eventHost.Controls.Add(_eventGrid);
        eventHost.Controls.Add(eventHeader);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.BackColor = Color.FromArgb(30, 30, 30);
        _split.Panel1.Controls.Add(top);
        _split.Panel2.Controls.Add(eventHost);

        Controls.Add(_split);
        Controls.Add(toolbar);
        Controls.Add(_banner);
        Controls.Add(_status);

        _classifier.Emitted += OnKeyEvent;
        _expireTimer.Interval = 15;
        _expireTimer.Tick += (_, _) =>
        {
            _classifier.Expire();
            var now = Environment.TickCount64;
            if (now - _lastRateRefresh > 250)
            {
                _lastRateRefresh = now;
                _devicesDirty = true;
            }

            RefreshDeviceGrid();
            RefreshBannerQuiet();
        };
        _flushTimer.Interval = 200;
        _flushTimer.Tick += (_, _) => _logger?.FlushWriter();
        _processTimer.Interval = 2000;
        _processTimer.Tick += (_, _) => RefreshProcesses();
        _watched = WatchSettings.Load();
        UpdateWatchButton();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_captureReady)
            return;

        try
        {
            _logger = new InputLogger();
            _statusLog.Text = _logger.FilePath;
        }
        catch (Exception ex)
        {
            _captureProblem = "Log file failed: " + ex.Message;
        }

        RegisterKeyboard();
        InstallHook();
        _logger?.WriteDeviceNote(_hook == IntPtr.Zero ? "keyboard hook failed" : "keyboard hook installed");
        _logger?.WriteDeviceNote(string.IsNullOrEmpty(_captureProblem) ? "raw input registered" : _captureProblem);
        EnumerateKeyboards();
        _logger?.FlushWriter();
        RefreshProcesses();
        _expireTimer.Start();
        _flushTimer.Start();
        _processTimer.Start();
        _captureReady = true;
        SetStatus(string.IsNullOrEmpty(_captureProblem)
            ? "Listening. This window does not need focus."
            : _captureProblem);
        UpdateBanner();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var distance = (int)(_split.Height * 0.46);
            if (distance > 80 && distance < _split.Height - 80)
                _split.SplitterDistance = distance;
        }
        catch
        {
            // The split stays at the control default if the window is still too short.
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _expireTimer.Stop();
        _flushTimer.Stop();
        _processTimer.Stop();
        _classifier.Expire();
        RemoveHook();
        UnregisterKeyboard();
        _logger?.Dispose();
        _logger = null;
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_INPUT)
        {
            ReadRawInput(m.LParam);
            return;
        }

        if (m.Msg == Win32.WM_INPUT_DEVICE_CHANGE)
        {
            HandleDeviceChange(m.WParam.ToInt32(), m.LParam);
            return;
        }

        if (m.Msg == Win32.WM_APP_HOOK)
        {
            DrainHookQueue();
            return;
        }

        base.WndProc(ref m);
    }

    void RegisterKeyboard()
    {
        if (Marshal.SizeOf<Win32.RAWINPUTHEADER>() != 24)
        {
            _captureProblem = "This build is not 64-bit, so device names cannot be read.";
            return;
        }

        var devices = new[]
        {
            new Win32.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = Win32.RIDEV_INPUTSINK | Win32.RIDEV_DEVNOTIFY,
                hwndTarget = Handle,
            },
        };

        if (!Win32.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>()))
            _captureProblem = "Raw Input registration failed (Win32 " + Marshal.GetLastWin32Error() + "). Injected keys can still be logged.";
    }

    void UnregisterKeyboard()
    {
        var devices = new[]
        {
            new Win32.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = Win32.RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            },
        };
        Win32.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>());
    }

    void InstallHook()
    {
        _hookProc = HookCallback;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            var error = "Keyboard hook failed (Win32 " + Marshal.GetLastWin32Error() + ").";
            _captureProblem = string.IsNullOrEmpty(_captureProblem) ? error : _captureProblem + " " + error;
        }
    }

    void RemoveHook()
    {
        if (_hook == IntPtr.Zero)
            return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == Win32.HC_ACTION && lParam != IntPtr.Zero)
        {
            var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;
            var isUp = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP || (data.flags & Win32.LLKHF_UP) != 0;
            _hookQueue.Enqueue(new HookPacket(
                (int)data.vkCode,
                (int)data.scanCode,
                isUp,
                (data.flags & Win32.LLKHF_EXTENDED) != 0,
                (data.flags & Win32.LLKHF_INJECTED) != 0,
                (data.flags & Win32.LLKHF_LOWER_IL_INJECTED) != 0,
                data.dwExtraInfo.ToUInt64(),
                Environment.TickCount64));
            Win32.PostMessage(Handle, Win32.WM_APP_HOOK, IntPtr.Zero, IntPtr.Zero);
        }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void DrainHookQueue()
    {
        while (_hookQueue.TryDequeue(out var packet))
        {
            _classifier.AddHook(new HookSample
            {
                Tick = packet.Tick,
                Vk = packet.Vk,
                Scan = packet.Scan,
                IsUp = packet.IsUp,
                Extended = packet.Extended,
                Injected = packet.Injected,
                LowerIntegrityInjected = packet.LowerIntegrity,
                Extra = packet.Extra,
            });
        }
    }

    void ReadRawInput(IntPtr hRaw)
    {
        try
        {
            uint headerSize = (uint)Marshal.SizeOf<Win32.RAWINPUTHEADER>();
            uint size = 0;
            Win32.GetRawInputData(hRaw, Win32.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0)
                return;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var got = size;
                var read = Win32.GetRawInputData(hRaw, Win32.RID_INPUT, buffer, ref got, headerSize);
                if (read == 0 || read == uint.MaxValue)
                    return;

                var header = Marshal.PtrToStructure<Win32.RAWINPUTHEADER>(buffer);
                if (header.dwType != Win32.RIM_TYPEKEYBOARD)
                    return;

                var keyboard = Marshal.PtrToStructure<Win32.RAWKEYBOARD>(IntPtr.Add(buffer, 24));
                var device = header.hDevice == IntPtr.Zero
                    ? DeviceIdentity.Software
                    : _devices.Resolve(header.hDevice);
                RememberDevice(device);
                _classifier.AddRaw(new RawSample
                {
                    Tick = Environment.TickCount64,
                    Vk = keyboard.VKey,
                    Scan = keyboard.MakeCode,
                    IsUp = (keyboard.Flags & Win32.RI_KEY_BREAK) != 0,
                    Extended = (keyboard.Flags & Win32.RI_KEY_E0) != 0,
                    Device = device,
                    Extra = keyboard.ExtraInformation,
                });
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Raw input read failed: " + ex.Message);
        }
    }

    void HandleDeviceChange(int kind, IntPtr handle)
    {
        if (kind == Win32.GIDC_REMOVAL)
        {
            var removed = _devices.NoteRemoval(handle);
            if (removed == null)
                return;
            if (_stats.TryGetValue(removed.Key, out var stats))
            {
                if (stats.TotalDowns == 0 && stats.WatchCount == 0)
                {
                    _stats.Remove(removed.Key);
                    if (_deviceRows.TryGetValue(removed.Key, out var row))
                    {
                        _deviceGrid.Rows.Remove(row);
                        _deviceRows.Remove(removed.Key);
                    }
                }
                else
                {
                    stats.Connected = false;
                    _devicesDirty = true;
                }
            }

            _logger?.WriteDeviceNote("removed " + removed.Name + " " + removed.Path);
            return;
        }

        var identity = _devices.Resolve(handle);
        var known = _stats.ContainsKey(identity.Key);
        RememberDevice(identity);
        if (!known)
            _logger?.WriteDeviceNote("arrived " + identity.Name + " VID " + identity.VidText + " PID " + identity.PidText + " " + identity.Path);
    }

    void EnumerateKeyboards()
    {
        foreach (var device in _devices.ListKeyboards())
        {
            RememberDevice(device);
            _logger?.WriteDeviceNote("connected " + device.Name + " VID " + device.VidText + " PID " + device.PidText + " " + device.Path);
        }

        RefreshDeviceGrid();
    }

    void OnKeyEvent(KeyEvent keyEvent)
    {
        var foreground = ForegroundWindow.Current();
        keyEvent.ForegroundProcess = foreground.Process;
        keyEvent.ForegroundTitle = foreground.Title;

        if (_armingWatch && !keyEvent.IsUp && !keyEvent.IsRepeat)
        {
            _armingWatch = false;
            _watched = WatchedKey.Capture(keyEvent.Vk, keyEvent.Scan);
            try
            {
                WatchSettings.Save(_watched);
            }
            catch (Exception ex)
            {
                SetStatus("Could not save the watched key: " + ex.Message);
            }

            ResetWatchCounts();
            UpdateWatchButton();
            RepaintEventRows();
        }

        var watched = _watched != null && _watched.Matches(keyEvent.Vk, keyEvent.Scan);
        var stats = StatsFor(keyEvent.Device);
        var action = keyEvent.IsUp ? "up" : "down";
        stats.LastKey = keyEvent.KeyName + " " + action;
        stats.Connected = !keyEvent.Device.Key.StartsWith("source:", StringComparison.Ordinal) && keyEvent.Device.Connected;
        if (!keyEvent.IsUp)
        {
            stats.TotalDowns++;
            if (watched)
            {
                stats.WatchCount++;
                stats.WatchTicks.Enqueue(keyEvent.Tick);
                _watchTotal++;
                _lastWatchTick = keyEvent.Tick;
                _lastWatchDevice = keyEvent.Device.Name;
                _watchHeld.Add(keyEvent.Device.Key);
            }
        }
        else if (watched)
        {
            _watchHeld.Remove(keyEvent.Device.Key);
        }

        _eventTotal++;
        _devicesDirty = true;
        _sortDevices = watched || _sortDevices;
        _logger?.Write(keyEvent);
        RecordEvent(keyEvent);
        if (!keyEvent.IsRepeat || keyEvent.Tick - _lastBannerPaint >= 50)
        {
            _lastBannerPaint = keyEvent.Tick;
            UpdateBanner();
        }
    }

    DeviceStats StatsFor(DeviceIdentity device)
    {
        if (!_stats.TryGetValue(device.Key, out var stats))
        {
            stats = new DeviceStats
            {
                Key = device.Key,
                Name = device.Name,
                Vid = device.VidText,
                Pid = device.PidText,
                Path = device.Path,
                Connected = device.Connected,
            };
            _stats[device.Key] = stats;
        }
        else
        {
            stats.Name = device.Name;
            stats.Vid = device.VidText;
            stats.Pid = device.PidText;
            stats.Path = device.Path;
        }

        return stats;
    }

    void RememberDevice(DeviceIdentity device)
    {
        var stats = StatsFor(device);
        stats.Connected = device.Connected;
        _devicesDirty = true;
    }

    void RecordEvent(KeyEvent keyEvent)
    {
        if (keyEvent.IsRepeat && _events.Count > 0)
        {
            var last = _events[^1];
            if (last.OpenBurst &&
                last.DeviceKey == keyEvent.Device.Key &&
                last.Vk == keyEvent.Vk &&
                last.Scan == (keyEvent.Scan & 0xFF))
            {
                last.Count++;
                last.EndTick = keyEvent.Tick;
                last.Rate = RateText(last.Count, last.StartTick, last.EndTick);
                last.Foreground = FormatForeground(keyEvent);
                if (keyEvent.Tick - last.LastPaint >= 50)
                {
                    last.LastPaint = keyEvent.Tick;
                    TouchEventRow(last);
                }
                return;
            }
        }

        var shown = new DisplayedEvent
        {
            Time = keyEvent.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Key = keyEvent.KeyName,
            Action = keyEvent.IsUp ? "Up" : keyEvent.IsRepeat ? "Repeat" : "Down",
            Count = 1,
            Rate = "",
            Device = keyEvent.Device.Name,
            Source = keyEvent.Injected && keyEvent.Source == "Hardware"
                ? "Hardware, injected flag"
                : keyEvent.Source,
            Foreground = FormatForeground(keyEvent),
            OpenBurst = keyEvent.IsRepeat,
            DeviceKey = keyEvent.Device.Key,
            Vk = keyEvent.Vk,
            Scan = keyEvent.Scan & 0xFF,
            StartTick = keyEvent.Tick,
            EndTick = keyEvent.Tick,
            LastPaint = keyEvent.Tick,
        };
        if (keyEvent.IsUp)
            CloseOpenBurst(keyEvent);

        _events.Add(shown);
        var trimmed = false;
        while (_events.Count > MaxEvents)
        {
            _events.RemoveAt(0);
            trimmed = true;
        }

        if (_pauseView.Checked)
            return;
        if (trimmed)
            RebuildEventGrid();
        else
            AddEventRow(shown);
    }

    void CloseOpenBurst(KeyEvent keyEvent)
    {
        if (_events.Count == 0)
            return;
        var shown = _events[^1];
        if (!shown.OpenBurst ||
            shown.DeviceKey != keyEvent.Device.Key ||
            shown.Vk != keyEvent.Vk ||
            shown.Scan != (keyEvent.Scan & 0xFF))
            return;
        shown.OpenBurst = false;
        TouchEventRow(shown);
    }

    void TouchEventRow(DisplayedEvent shown)
    {
        if (_pauseView.Checked || shown.GridRow == null)
            return;
        shown.GridRow.Cells[3].Value = shown.Count;
        shown.GridRow.Cells[4].Value = shown.Rate;
        shown.GridRow.Cells[7].Value = shown.Foreground;
    }

    void AddEventRow(DisplayedEvent shown)
    {
        var index = _eventGrid.Rows.Add(
            shown.Time,
            shown.Key,
            shown.Action,
            shown.Count,
            shown.Rate,
            shown.Device,
            shown.Source,
            shown.Foreground);
        var row = _eventGrid.Rows[index];
        shown.GridRow = row;
        PaintEventRow(row, shown);
        if (_eventGrid.Rows.Count > 0)
            _eventGrid.FirstDisplayedScrollingRowIndex = _eventGrid.Rows.Count - 1;
    }

    void RebuildEventGrid()
    {
        _eventGrid.Rows.Clear();
        foreach (var shown in _events)
        {
            shown.GridRow = null;
            AddEventRow(shown);
        }
    }

    void PaintEventRow(DataGridViewRow row, DisplayedEvent shown)
    {
        var watched = _watched != null && _watched.Matches(shown.Vk, shown.Scan);
        Color back = watched ? Color.FromArgb(120, 28, 28) : Color.FromArgb(30, 30, 30);
        Color fore = Color.FromArgb(240, 240, 240);
        row.DefaultCellStyle.BackColor = back;
        row.DefaultCellStyle.ForeColor = fore;
        row.DefaultCellStyle.SelectionBackColor = ControlPaint.Light(back, 0.15f);
        row.DefaultCellStyle.SelectionForeColor = fore;
    }

    void RefreshDeviceGrid()
    {
        if (!_devicesDirty || _pauseView.Checked)
            return;
        _devicesDirty = false;
        var now = Environment.TickCount64;
        foreach (var stats in _stats.Values)
        {
            var name = stats.Connected || stats.Key.StartsWith("source:", StringComparison.Ordinal)
                ? stats.Name
                : stats.Name + " (disconnected)";
            var rate = stats.Rate(now);
            if (_deviceRows.TryGetValue(stats.Key, out var row))
            {
                row.Cells[0].Value = name;
                row.Cells[1].Value = stats.Vid;
                row.Cells[2].Value = stats.Pid;
                row.Cells[3].Value = stats.Path;
                row.Cells[4].Value = stats.LastKey;
                row.Cells[5].Value = stats.TotalDowns;
                row.Cells[6].Value = stats.WatchCount;
                row.Cells[7].Value = rate.ToString("0", CultureInfo.InvariantCulture);
            }
            else
            {
                var index = _deviceGrid.Rows.Add(
                    name, stats.Vid, stats.Pid, stats.Path, stats.LastKey,
                    stats.TotalDowns, stats.WatchCount, rate.ToString("0", CultureInfo.InvariantCulture));
                row = _deviceGrid.Rows[index];
                row.Tag = stats.Key;
                _deviceRows[stats.Key] = row;
            }

            var alert = stats.WatchCount > 0;
            var back = alert ? Color.FromArgb(120, 28, 28) : Color.FromArgb(30, 30, 30);
            var fore = Color.FromArgb(240, 240, 240);
            row.DefaultCellStyle.BackColor = back;
            row.DefaultCellStyle.ForeColor = fore;
            row.DefaultCellStyle.SelectionBackColor = ControlPaint.Light(back, 0.15f);
            row.DefaultCellStyle.SelectionForeColor = fore;
        }

        if (_sortDevices && _deviceGrid.Rows.Count > 0 && now - _lastDeviceSort > 500 && _watchColumn.DataGridView != null)
        {
            _deviceGrid.Sort(_watchColumn, System.ComponentModel.ListSortDirection.Descending);
            _lastDeviceSort = now;
            _sortDevices = false;
        }
    }

    void RefreshBannerQuiet()
    {
        if (_armingWatch || _watched == null)
            return;
        if (_watchHeld.Count == 0 && Environment.TickCount64 - _lastWatchTick > 300 && _banner.BackColor.R > 80)
            UpdateBanner();
    }

    void UpdateBanner()
    {
        var now = Environment.TickCount64;
        if (_armingWatch)
        {
            _banner.BackColor = Color.FromArgb(48, 48, 32);
            _bannerTitle.ForeColor = Color.FromArgb(255, 214, 140);
            _bannerTitle.Text = "Press the key to watch";
            _bannerDetail.ForeColor = Color.FromArgb(230, 230, 210);
            _bannerDetail.Text = "The next key you press is the one InputFinder will highlight and count.";
        }
        else if (_watched == null)
        {
            _banner.BackColor = Color.FromArgb(32, 48, 40);
            _bannerTitle.ForeColor = Color.FromArgb(190, 230, 200);
            _bannerTitle.Text = "Choose a key to watch";
            _bannerDetail.ForeColor = Color.FromArgb(210, 210, 210);
            _bannerDetail.Text = "Click Watch key, then press the key you want to track. Other keys are still recorded.";
        }
        else
        {
            var active = _watchHeld.Count > 0 || (_watchTotal > 0 && now - _lastWatchTick <= 300);
            var rate = CombinedWatchRate(now);
            var name = _watched.Name;
            if (active)
            {
                _banner.BackColor = Color.FromArgb(140, 24, 24);
                _bannerTitle.ForeColor = Color.White;
                _bannerTitle.Text = name + " is active";
                _bannerDetail.ForeColor = Color.FromArgb(255, 230, 230);
                _bannerDetail.Text = _watchTotal.ToString("N0", CultureInfo.InvariantCulture) +
                                     " events · " + rate.ToString("0.#", CultureInfo.InvariantCulture) +
                                     "/s · last device: " + (_lastWatchDevice.Length == 0 ? "unknown" : _lastWatchDevice);
            }
            else
            {
                _banner.BackColor = Color.FromArgb(32, 48, 40);
                _bannerTitle.ForeColor = Color.FromArgb(190, 230, 200);
                _bannerTitle.Text = "Watching for input";
                _bannerDetail.ForeColor = Color.FromArgb(210, 210, 210);
                _bannerDetail.Text = _watchTotal > 0
                    ? name + " is quiet. " + _watchTotal.ToString("N0", CultureInfo.InvariantCulture) +
                      " events so far" + (_lastWatchDevice.Length == 0 ? "." : ", last from " + _lastWatchDevice + ".")
                    : "No " + name + " yet. Keys are recorded even when this window is in the background.";
            }
        }

        _statusText.Text = _pauseView.Checked
            ? "View paused. Logging is still running.  Events: " + _eventTotal.ToString("N0", CultureInfo.InvariantCulture)
            : "Listening. Events: " + _eventTotal.ToString("N0", CultureInfo.InvariantCulture);
    }

    void UpdateWatchButton()
    {
        _watchButton.Text = _armingWatch
            ? "Press a key…"
            : _watched == null
                ? "Watch key"
                : "Watch key: " + _watched.Name;
    }

    void ResetWatchCounts()
    {
        _watchTotal = 0;
        _lastWatchTick = 0;
        _lastWatchDevice = "";
        _watchHeld.Clear();
        foreach (var stats in _stats.Values)
        {
            stats.WatchCount = 0;
            stats.WatchTicks.Clear();
        }

        if (_watched == null)
            return;

        foreach (var shown in _events)
        {
            if (shown.Action == "Up" || !_watched.Matches(shown.Vk, shown.Scan))
                continue;
            _watchTotal += shown.Count;
            _lastWatchDevice = shown.Device;
            if (_stats.TryGetValue(shown.DeviceKey, out var stats))
                stats.WatchCount += shown.Count;
        }

        _devicesDirty = true;
    }

    void RepaintEventRows()
    {
        if (_pauseView.Checked)
            return;
        foreach (var shown in _events)
        {
            if (shown.GridRow != null)
                PaintEventRow(shown.GridRow, shown);
        }
    }

    double CombinedWatchRate(long now)
    {
        var count = 0;
        foreach (var stats in _stats.Values)
            count += stats.Rate(now);
        return count;
    }

    void RefreshProcesses()
    {
        var running = KnownInputProcesses.Running();
        _processList.BeginUpdate();
        _processList.Items.Clear();
        if (running.Count == 0)
            _processList.Items.Add("(none of the usual macro tools are running)");
        else
        {
            foreach (var line in running)
                _processList.Items.Add(line);
        }
        _processList.EndUpdate();
    }

    void ClearView()
    {
        _events.Clear();
        _eventGrid.Rows.Clear();
        _watchTotal = 0;
        _eventTotal = 0;
        _lastWatchDevice = "";
        _lastWatchTick = 0;
        _watchHeld.Clear();
        foreach (var stats in _stats.Values)
        {
            stats.TotalDowns = 0;
            stats.WatchCount = 0;
            stats.LastKey = "";
            stats.WatchTicks.Clear();
        }

        _devicesDirty = true;
        _logger?.WriteDeviceNote("view cleared");
        RefreshDeviceGrid();
        UpdateBanner();
    }

    void OpenLogFolder()
    {
        try
        {
            var path = _logger?.FilePath;
            if (string.IsNullOrEmpty(path))
                path = LogPaths.Directory;
            Directory.CreateDirectory(LogPaths.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + LogPaths.Directory + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the log folder: " + ex.Message);
        }
    }

    void SetStatus(string text) => _statusText.Text = text;

    static string FormatForeground(KeyEvent keyEvent)
    {
        if (keyEvent.ForegroundProcess.Length == 0 && keyEvent.ForegroundTitle.Length == 0)
            return "";
        if (keyEvent.ForegroundTitle.Length == 0)
            return keyEvent.ForegroundProcess;
        if (keyEvent.ForegroundProcess.Length == 0)
            return keyEvent.ForegroundTitle;
        return keyEvent.ForegroundProcess + " — " + keyEvent.ForegroundTitle;
    }

    static string RateText(int count, long start, long end)
    {
        var elapsed = Math.Max(1, end - start);
        return (count * 1000.0 / elapsed).ToString("0.0", CultureInfo.InvariantCulture) + "/s";
    }

    static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.BackgroundColor = Color.FromArgb(30, 30, 30);
        grid.GridColor = Color.FromArgb(63, 63, 70);
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.RowTemplate.Height = 28;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 45, 48);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(220, 220, 220);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(45, 45, 48);
        grid.DefaultCellStyle.BackColor = Color.FromArgb(30, 30, 30);
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(232, 232, 232);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 70, 90);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.ShowCellToolTips = true;
        typeof(DataGridView).InvokeMember(
            "DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.SetProperty,
            null,
            grid,
            [true]);
    }

    static DataGridViewTextBoxColumn TextColumn(string header, int min, int fill = 0)
    {
        return new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Name = header,
            MinimumWidth = min,
            FillWeight = fill == 0 ? min : fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
    }

    static DataGridViewTextBoxColumn NumberColumn(string header, int min)
    {
        return new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Name = header,
            MinimumWidth = min,
            FillWeight = min,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleRight,
            },
        };
    }

    static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(55, 55, 58);
        button.ForeColor = Color.FromArgb(232, 232, 232);
        button.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 84);
        button.Height = 28;
    }

    readonly record struct HookPacket(
        int Vk,
        int Scan,
        bool IsUp,
        bool Extended,
        bool Injected,
        bool LowerIntegrity,
        ulong Extra,
        long Tick);
}

sealed class DeviceStats
{
    public string Key = "";
    public string Name = "";
    public string Vid = "";
    public string Pid = "";
    public string Path = "";
    public string LastKey = "";
    public int TotalDowns;
    public int WatchCount;
    public bool Connected = true;
    public readonly Queue<long> WatchTicks = new();

    public int Rate(long now)
    {
        while (WatchTicks.Count > 0 && now - WatchTicks.Peek() > 1000)
            WatchTicks.Dequeue();
        return WatchTicks.Count;
    }
}

sealed class DisplayedEvent
{
    public string Time = "";
    public string Key = "";
    public string Action = "";
    public int Count;
    public string Rate = "";
    public string Device = "";
    public string Source = "";
    public string Foreground = "";
    public bool OpenBurst;
    public string DeviceKey = "";
    public int Vk;
    public int Scan;
    public long StartTick;
    public long EndTick;
    public long LastPaint;
    public DataGridViewRow? GridRow;
}
