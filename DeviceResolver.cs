using System.Text;
using Microsoft.Win32;

namespace InputFinder;

sealed class DeviceIdentity
{
    public static readonly DeviceIdentity Software = new()
    {
        Key = "source:software",
        Name = "Software injected (no hardware device)",
        Path = "",
        Vid = "",
        Pid = "",
    };

    public static readonly DeviceIdentity Unattributed = new()
    {
        Key = "source:unattributed",
        Name = "Unattributed (no device handle)",
        Path = "",
        Vid = "",
        Pid = "",
    };

    public IntPtr Handle { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "Unknown device";
    public string Path { get; init; } = "";
    public string Vid { get; init; } = "";
    public string Pid { get; init; } = "";
    public bool Connected { get; set; } = true;

    public string VidText => Vid.Length == 0 ? "—" : Vid;
    public string PidText => Pid.Length == 0 ? "—" : Pid;
}

sealed class DeviceResolver
{
    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HID Keyboard Device",
        "HID-compliant device",
        "HID-compliant mouse",
        "USB Input Device",
        "USB Composite Device",
        "USB Device",
        "Keyboard",
    };

    readonly Dictionary<string, DeviceIdentity> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<IntPtr, string> _pathByHandle = new();

    public DeviceIdentity Resolve(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return DeviceIdentity.Software;

        if (_pathByHandle.TryGetValue(handle, out var cachedPath) &&
            _byPath.TryGetValue(cachedPath, out var cached))
        {
            return cached;
        }

        var path = ReadDevicePath(handle);
        if (string.IsNullOrEmpty(path))
        {
            return new DeviceIdentity
            {
                Handle = handle,
                Key = "handle:" + handle.ToInt64(),
                Name = "Unknown device " + handle.ToInt64(),
            };
        }

        if (_byPath.TryGetValue(path, out var existing))
        {
            _pathByHandle[handle] = path;
            existing.Connected = true;
            return existing;
        }

        var (vid, pid) = ParseVidPid(path);
        var name = ReadFriendlyName(path) ?? FallbackName(vid, pid);
        var identity = new DeviceIdentity
        {
            Handle = handle,
            Key = path,
            Name = name,
            Path = path,
            Vid = vid,
            Pid = pid,
            Connected = true,
        };
        _byPath[path] = identity;
        _pathByHandle[handle] = path;
        return identity;
    }

    public DeviceIdentity? NoteRemoval(IntPtr handle)
    {
        if (!_pathByHandle.TryGetValue(handle, out var path))
            return null;
        _pathByHandle.Remove(handle);
        if (!_byPath.TryGetValue(path, out var identity))
            return null;
        identity.Connected = false;
        return identity;
    }

    public IReadOnlyList<DeviceIdentity> ListKeyboards()
    {
        var list = new List<DeviceIdentity>();
        uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.RAWINPUTDEVICELIST>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            Win32.GetRawInputDeviceList(IntPtr.Zero, ref count, size);
            if (count == 0)
                return list;

            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)(count * size));
            try
            {
                uint got = count;
                var result = Win32.GetRawInputDeviceList(buffer, ref got, size);
                if (result == uint.MaxValue)
                    continue;

                list.Clear();
                for (var i = 0; i < got; i++)
                {
                    var item = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.RAWINPUTDEVICELIST>(
                        IntPtr.Add(buffer, (int)(i * size)));
                    if (item.dwType != Win32.RIM_TYPEKEYBOARD)
                        continue;
                    list.Add(Resolve(item.hDevice));
                }

                return list;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }

        return list;
    }

    static string? ReadDevicePath(IntPtr handle)
    {
        uint chars = 0;
        Win32.GetRawInputDeviceInfo(handle, Win32.RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0)
            return null;

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            uint size = chars;
            var written = Win32.GetRawInputDeviceInfo(handle, Win32.RIDI_DEVICENAME, buffer, ref size);
            if (written == 0 || written == uint.MaxValue)
                return null;
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    static string? ReadFriendlyName(string devicePath)
    {
        var hid = ReadHidStrings(devicePath);
        if (hid != null)
            return hid;

        var fromTree = ReadDeviceTreeName(devicePath);
        if (fromTree != null)
            return fromTree;

        return ReadRegistryName(devicePath);
    }

    static string? ReadHidStrings(string devicePath)
    {
        var handle = OpenHid(devicePath);
        if (handle == Win32.InvalidHandle || handle == IntPtr.Zero)
            return null;

        try
        {
            var product = ReadHidString(handle, product: true);
            var manufacturer = ReadHidString(handle, product: false);
            if (!IsUseful(product) && !IsUseful(manufacturer))
                return null;
            if (IsUseful(manufacturer) && IsUseful(product) &&
                product!.Contains(manufacturer!, StringComparison.OrdinalIgnoreCase) == false)
                return manufacturer + " " + product;
            return IsUseful(product) ? product : manufacturer;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    static IntPtr OpenHid(string devicePath)
    {
        var handle = Win32.CreateFile(devicePath, 0,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle != Win32.InvalidHandle && handle != IntPtr.Zero)
            return handle;

        if (devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var alt = @"\\.\" + devicePath[4..];
            handle = Win32.CreateFile(alt, 0,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        return handle;
    }

    static string? ReadHidString(IntPtr handle, bool product)
    {
        var buffer = new byte[512];
        var ok = product
            ? Win32.HidD_GetProductString(handle, buffer, (uint)buffer.Length)
            : Win32.HidD_GetManufacturerString(handle, buffer, (uint)buffer.Length);
        if (!ok)
            return null;
        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    static string? ReadRegistryName(string devicePath)
    {
        var instance = ToEnumInstance(devicePath);
        if (instance == null)
            return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance);
            if (key == null)
                return null;
            var friendly = CleanInf(key.GetValue("FriendlyName") as string);
            if (IsUseful(friendly))
                return friendly;
            var desc = CleanInf(key.GetValue("DeviceDesc") as string);
            return IsUseful(desc) ? desc : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string? ReadDeviceTreeName(string devicePath)
    {
        var instance = ToEnumInstance(devicePath);
        if (instance == null)
            return null;
        if (Win32.CM_Locate_DevNode(out var node, instance, 0) != 0)
            return null;

        string? fallback = null;
        for (var depth = 0; depth < 6; depth++)
        {
            var bus = ReadDevProp(node, Win32.BusReportedDeviceDesc);
            if (IsUseful(bus))
                return bus;

            var friendly = ReadDevProp(node, Win32.DeviceFriendlyName);
            if (fallback == null && IsUseful(friendly))
                fallback = friendly;

            var desc = CleanInf(ReadDevProp(node, Win32.DeviceDescription));
            if (fallback == null && IsUseful(desc))
                fallback = desc;

            if (Win32.CM_Get_Parent(out var parent, node, 0) != 0)
                break;
            node = parent;
        }

        return fallback;
    }

    static string? ReadDevProp(uint node, Win32.DEVPROPKEY key)
    {
        var buffer = new byte[512];
        uint size = (uint)buffer.Length;
        var copy = key;
        var cr = Win32.CM_Get_DevNode_Property(node, ref copy, out var type, buffer, ref size, 0);
        if (cr != 0 || type != 0x12 || size < 2)
            return null;
        var text = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    public static string? ToEnumInstance(string devicePath)
    {
        var path = devicePath;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            path = path[4..];

        var guid = path.LastIndexOf("#{");
        if (guid >= 0)
            path = path[..guid];

        path = path.Replace('#', '\\');
        return path.Length == 0 ? null : path;
    }

    public static (string Vid, string Pid) ParseVidPid(string devicePath)
    {
        string vid = "";
        string pid = "";
        var upper = devicePath.ToUpperInvariant();
        var vidAt = upper.IndexOf("VID_", StringComparison.Ordinal);
        if (vidAt >= 0 && vidAt + 8 <= upper.Length)
            vid = upper.Substring(vidAt + 4, 4);
        var pidAt = upper.IndexOf("PID_", StringComparison.Ordinal);
        if (pidAt >= 0 && pidAt + 8 <= upper.Length)
            pid = upper.Substring(pidAt + 4, 4);
        return (vid, pid);
    }

    static string FallbackName(string vid, string pid)
    {
        if (vid.Length == 0 && pid.Length == 0)
            return "Keyboard";
        return $"Keyboard VID_{vid} PID_{pid}";
    }

    static string? CleanInf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var semi = value.LastIndexOf(';');
        if (semi >= 0 && semi < value.Length - 1)
            return value[(semi + 1)..].Trim();
        return value.Trim();
    }

    static bool IsUseful(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return !GenericNames.Contains(name.Trim());
    }
}
