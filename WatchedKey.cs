using System.Globalization;
using System.Text.Json;

namespace InputFinder;

sealed class WatchedKey
{
    public int Vk { get; init; }
    public int Scan { get; init; }

    public string Name => KeyNames.Describe(Vk, Scan);

    public bool Matches(int vk, int scan)
    {
        var scanByte = scan & 0xFF;
        if (Vk is not (0 or 0xFF) && vk is not (0 or 0xFF))
        {
            if (vk != Vk)
                return false;
            if (Scan != 0 && scanByte != 0 && scanByte != Scan)
                return false;
            return true;
        }

        return Scan != 0 && scanByte == Scan;
    }

    public static WatchedKey Capture(int vk, int scan) => new()
    {
        Vk = vk,
        Scan = scan & 0xFF,
    };
}

static class WatchSettings
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InputFinder",
        "settings.json");

    public static WatchedKey? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("vk", out var vkProp) || !vkProp.TryGetInt32(out var vk))
                return null;
            if (!root.TryGetProperty("scan", out var scanProp) || !scanProp.TryGetInt32(out var scan))
                return null;

            scan &= 0xFF;
            if (vk is 0 or 0xFF && scan == 0)
                return null;

            return new WatchedKey { Vk = vk, Scan = scan };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(WatchedKey key)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = "{\r\n  \"vk\": " + key.Vk.ToString(CultureInfo.InvariantCulture) +
                   ",\r\n  \"scan\": " + key.Scan.ToString(CultureInfo.InvariantCulture) + "\r\n}\r\n";
        File.WriteAllText(FilePath, json);
    }
}
