namespace InputFinder;

sealed class RawSample
{
    public long Tick;
    public int Vk;
    public int Scan;
    public bool IsUp;
    public bool Extended;
    public DeviceIdentity Device = DeviceIdentity.Unattributed;
    public uint Extra;
    public bool Consumed;
}

sealed class HookSample
{
    public long Tick;
    public int Vk;
    public int Scan;
    public bool IsUp;
    public bool Extended;
    public bool Injected;
    public bool LowerIntegrityInjected;
    public ulong Extra;
    public bool Consumed;
}

sealed class KeyEvent
{
    public DateTime Timestamp;
    public long Tick;
    public int Vk;
    public int Scan;
    public bool Extended;
    public bool IsUp;
    public bool IsRepeat;
    public string KeyName = "";
    public string Source = "";
    public bool Injected;
    public DeviceIdentity Device = DeviceIdentity.Unattributed;
    public ulong Extra;
    public string ForegroundProcess = "";
    public string ForegroundTitle = "";
}

/// <summary>
/// Pairs Raw Input (which names the device) with the low-level keyboard hook
/// (which flags software injection). Events that stay unmatched for 40 ms are emitted alone.
/// </summary>
sealed class InputClassifier
{
    public const long MatchWindowMs = 40;

    readonly List<RawSample> _raw = new();
    readonly List<HookSample> _hook = new();
    readonly Dictionary<string, byte> _held = new();

    public event Action<KeyEvent>? Emitted;

    public void AddRaw(RawSample sample)
    {
        var match = FindHook(sample.Vk, sample.Scan, sample.IsUp, sample.Extended, sample.Tick);
        if (match != null)
        {
            match.Consumed = true;
            Emit(Merge(sample, match));
        }
        else
        {
            _raw.Add(sample);
        }
    }

    public void AddHook(HookSample sample)
    {
        var match = FindRaw(sample.Vk, sample.Scan, sample.IsUp, sample.Extended, sample.Tick);
        if (match != null)
        {
            match.Consumed = true;
            Emit(Merge(match, sample));
        }
        else
        {
            _hook.Add(sample);
        }
    }

    public void Expire()
    {
        var now = Environment.TickCount64;
        for (var i = _raw.Count - 1; i >= 0; i--)
        {
            if (_raw[i].Consumed || now - _raw[i].Tick < MatchWindowMs)
                continue;
            var sample = _raw[i];
            sample.Consumed = true;
            Emit(FromRaw(sample));
        }

        for (var i = _hook.Count - 1; i >= 0; i--)
        {
            if (_hook[i].Consumed || now - _hook[i].Tick < MatchWindowMs)
                continue;
            var sample = _hook[i];
            sample.Consumed = true;
            Emit(FromHook(sample));
        }

        _raw.RemoveAll(s => s.Consumed);
        _hook.RemoveAll(s => s.Consumed);
    }

    RawSample? FindRaw(int vk, int scan, bool isUp, bool extended, long tick)
    {
        RawSample? best = null;
        var bestGap = long.MaxValue;
        foreach (var sample in _raw)
        {
            if (sample.Consumed || !SameKey(sample.Vk, sample.Scan, sample.IsUp, sample.Extended, vk, scan, isUp, extended))
                continue;
            var gap = Math.Abs(sample.Tick - tick);
            if (gap <= MatchWindowMs && gap < bestGap)
            {
                best = sample;
                bestGap = gap;
            }
        }
        return best;
    }

    HookSample? FindHook(int vk, int scan, bool isUp, bool extended, long tick)
    {
        HookSample? best = null;
        var bestGap = long.MaxValue;
        foreach (var sample in _hook)
        {
            if (sample.Consumed || !SameKey(sample.Vk, sample.Scan, sample.IsUp, sample.Extended, vk, scan, isUp, extended))
                continue;
            var gap = Math.Abs(sample.Tick - tick);
            if (gap <= MatchWindowMs && gap < bestGap)
            {
                best = sample;
                bestGap = gap;
            }
        }
        return best;
    }

    static bool SameKey(int aVk, int aScan, bool aUp, bool aExt, int bVk, int bScan, bool bUp, bool bExt)
    {
        if (aUp != bUp)
            return false;
        var scanA = aScan & 0xFF;
        var scanB = bScan & 0xFF;
        if (scanA != 0 && scanB != 0 && scanA != scanB)
            return false;
        if (aExt != bExt && scanA != 0 && scanB != 0 && scanA == scanB)
            return false;
        var vkUsableA = aVk is not (0 or 0xFF);
        var vkUsableB = bVk is not (0 or 0xFF);
        if (vkUsableA && vkUsableB && aVk != bVk)
            return false;
        return scanA == scanB || (vkUsableA && vkUsableB && aVk == bVk);
    }

    KeyEvent Merge(RawSample raw, HookSample hook)
    {
        var vk = raw.Vk is 0 or 0xFF ? hook.Vk : raw.Vk;
        var scan = (raw.Scan & 0xFF) != 0 ? raw.Scan & 0xFF : hook.Scan & 0xFF;
        var injected = hook.Injected || hook.LowerIntegrityInjected || raw.Device.Handle == IntPtr.Zero;
        var hasDevice = raw.Device.Handle != IntPtr.Zero && raw.Device.Path.Length > 0 ||
                        raw.Device.Key.StartsWith("handle:", StringComparison.Ordinal);
        var device = hasDevice ? raw.Device : (injected ? DeviceIdentity.Software : DeviceIdentity.Unattributed);
        var source = hasDevice ? "Hardware" : (injected ? "Software injected" : "Unattributed");
        return Build(vk, scan, raw.Extended || hook.Extended, raw.IsUp, source, injected, device,
            hook.Extra != 0 ? hook.Extra : raw.Extra, raw.Tick);
    }

    KeyEvent FromRaw(RawSample raw)
    {
        var hasDevice = raw.Device.Handle != IntPtr.Zero;
        if (!hasDevice)
        {
            return Build(raw.Vk, raw.Scan & 0xFF, raw.Extended, raw.IsUp,
                "Software injected", injected: true, DeviceIdentity.Software, raw.Extra, raw.Tick);
        }

        return Build(raw.Vk, raw.Scan & 0xFF, raw.Extended, raw.IsUp,
            "Hardware", injected: false, raw.Device, raw.Extra, raw.Tick);
    }

    KeyEvent FromHook(HookSample hook)
    {
        var injected = hook.Injected || hook.LowerIntegrityInjected;
        var device = injected ? DeviceIdentity.Software : DeviceIdentity.Unattributed;
        var source = injected ? "Software injected" : "Unattributed";
        return Build(hook.Vk, hook.Scan & 0xFF, hook.Extended, hook.IsUp, source, injected, device, hook.Extra, hook.Tick);
    }

    KeyEvent Build(int vk, int scan, bool extended, bool isUp, string source, bool injected, DeviceIdentity device, ulong extra, long tick)
    {
        var holdKey = device.Key + "|" + vk + "|" + scan + "|" + (extended ? "e" : "");
        var isRepeat = false;
        if (!isUp)
        {
            if (_held.ContainsKey(holdKey))
                isRepeat = true;
            else
                _held[holdKey] = 1;
        }
        else
        {
            _held.Remove(holdKey);
        }

        return new KeyEvent
        {
            Timestamp = DateTime.Now,
            Tick = tick,
            Vk = vk,
            Scan = scan,
            Extended = extended,
            IsUp = isUp,
            IsRepeat = isRepeat,
            KeyName = KeyNames.Describe(vk, scan),
            Source = source,
            Injected = injected,
            Device = device,
            Extra = extra,
        };
    }

    void Emit(KeyEvent keyEvent) => Emitted?.Invoke(keyEvent);
}
