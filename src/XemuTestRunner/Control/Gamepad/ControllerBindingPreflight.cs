using Tomlyn;
using Tomlyn.Model;
using System.Text;

namespace XemuTestRunner.Control.Gamepad;

/// <summary>Reject keyboard or ambiguous OS devices before launching xemu.</summary>
public static class ControllerBindingPreflight
{
    internal const string LinuxGuid = "0600cc4158656d752052756e6e657200";
    private static readonly byte[] GuidNamePrefix = Encoding.UTF8.GetBytes("Xemu Runner");

    public static void Verify(IReadOnlyList<string> arguments, string workingDirectory,
        string? deviceSysname, string sysfsRoot = "/sys/class/input")
    {
        if (!OperatingSystem.IsLinux())
            throw new NotSupportedException(
                "Native controller binding on this host needs qualified xemu/SDL device identity before launch.");

        string? configured = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "-config_path")
            {
                if (configured is not null || ++i >= arguments.Count)
                    throw new InvalidDataException("Native input requires one explicit xemu -config_path.");
                configured = arguments[i];
            }
            else if (arguments[i].StartsWith("-config_path=", StringComparison.Ordinal))
            {
                if (configured is not null)
                    throw new InvalidDataException("Native input requires one explicit xemu -config_path.");
                configured = arguments[i][13..];
            }
        }
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidDataException("Native input requires a private xemu -config_path.");
        var path = Path.GetFullPath(configured, workingDirectory);
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Native xemu input config exceeds 1 MiB.");
        TomlTable model;
        try { model = Toml.ToModel(File.ReadAllText(path)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new InvalidDataException("Native xemu input config could not be read.", error); }

        var input = Table(model, "input");
        var ports = Table(input, "virtual_ports");
        var bindings = Table(input, "bindings");
        if (!input.TryGetValue("auto_bind", out var autoBind) || autoBind is not false ||
            !ports.TryGetValue("port1_connected", out var connected) ||
            connected is not 1L ||
            !bindings.TryGetValue("port1", out var selected) ||
            selected is not string guid ||
            !guid.Equals(LinuxGuid, StringComparison.OrdinalIgnoreCase) ||
            !bindings.TryGetValue("port1_driver", out var driver) ||
            driver is not string { Length: > 0 } driverName ||
            driverName != "usb-xbox-gamepad")
            throw new InvalidDataException(
                "Native input requires isolated port 1 bound to the Xemu Runner Gamepad GUID and usb-xbox-gamepad.");
        for (var port = 2; port <= 4; port++)
            if (bindings.TryGetValue($"port{port}", out var other) &&
                other is string value && value.Equals(LinuxGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native controller GUID is bound to multiple xemu ports.");

        // The helper reports the kernel input incarnation it created. SDL's
        // GUID alone cannot distinguish two devices with that same identity.
        if (deviceSysname is null || !deviceSysname.StartsWith("input", StringComparison.Ordinal) ||
            deviceSysname.Length <= 5 || !deviceSysname[5..].All(char.IsAsciiDigit))
            throw new InvalidDataException("Native helper did not report a valid Linux input device identity.");
        var matching = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(sysfsRoot, "input*"))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("input", StringComparison.Ordinal) ||
                name.Length <= 5 || !name[5..].All(char.IsAsciiDigit)) continue;
            if (!MatchesConfiguredGuid(directory)) continue;
            matching.Add(name);
        }
        if (matching.Count != 1 || matching[0] != deviceSysname)
            throw new InvalidDataException(
                "Native controller binding is ambiguous: expected exactly the helper-owned Xemu Runner Gamepad device.");
        var devicePath = Path.Combine(sysfsRoot, deviceSysname, "id");
        foreach (var (field, expected) in new[] {
            ("bustype", "0006"), ("vendor", "0000"),
            ("product", "0000"), ("version", "0001") })
            if (File.ReadAllText(Path.Combine(devicePath, field)).Trim() != expected)
                throw new InvalidDataException("Native controller kernel identity does not match the provisioned mapping.");
    }

    // SDL2's zero-vendor Linux identity includes the bus, CRC16 of the full
    // product name, and the first eleven UTF-8 name bytes. A different full
    // name can therefore still select the same xemu binding.
    private static bool MatchesConfiguredGuid(string directory)
    {
        var id = Path.Combine(directory, "id");
        if (!File.Exists(Path.Combine(directory, "name")) ||
            !File.Exists(Path.Combine(id, "bustype")) ||
            !File.Exists(Path.Combine(id, "vendor")) ||
            !File.Exists(Path.Combine(directory, "capabilities", "ev")) ||
            !File.Exists(Path.Combine(directory, "capabilities", "abs")) ||
            !File.Exists(Path.Combine(directory, "capabilities", "key"))) return false;
        if (File.ReadAllText(Path.Combine(id, "bustype")).Trim() != "0006" ||
            File.ReadAllText(Path.Combine(id, "vendor")).Trim() != "0000") return false;
        // A same-named touchscreen or keyboard is not an SDL joystick. Check
        // the kernel's joystick signals, including secondary axes, rather
        // than treating any absolute X/Y device as a possible gamepad.
        var capabilities = Path.Combine(directory, "capabilities");
        var events = ReadBitmap(Path.Combine(capabilities, "ev"));
        var axes = ReadBitmap(Path.Combine(capabilities, "abs"));
        var keys = ReadBitmap(Path.Combine(capabilities, "key"));
        if (!HasBits(events, 3) || !HasBits(axes, 0, 1)) return false;
        if (!HasBits(events, 1) &&
            (HasBits(axes, 0, 1, 2) || HasBits(axes, 3, 4, 5))) return false;
        if (!HasAnyBits(keys, 0x101, 0x120, 0x130) &&
            !HasAnyBits(axes, 3, 4, 5, 6, 7, 8, 9, 10)) return false;
        // sysfs adds exactly one LF. Any preceding space or control byte
        // belongs to the kernel device name and affects SDL's full-name CRC.
        var sysfsName = File.ReadAllBytes(Path.Combine(directory, "name"));
        if (sysfsName.Length == 0 || sysfsName[^1] != (byte)'\n')
            throw new InvalidDataException("Native controller kernel name is incomplete.");
        var bytes = sysfsName.AsSpan(0, sysfsName.Length - 1);
        if (bytes.Length < GuidNamePrefix.Length ||
            !bytes[..GuidNamePrefix.Length].SequenceEqual(GuidNamePrefix)) return false;
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xa001);
        }
        return crc == 0x41cc;
    }

    private static ulong[] ReadBitmap(string path)
    {
        var fields = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = new ulong[fields.Length];
        for (var i = 0; i < fields.Length; i++)
            if (!ulong.TryParse(fields[i], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out words[fields.Length - i - 1]))
                throw new InvalidDataException("Native controller input capabilities are malformed.");
        return words;
    }

    private static bool HasBits(ulong[] words, params int[] bits) => bits.All(bit => BitSet(words, bit));
    private static bool HasAnyBits(ulong[] words, params int[] bits) => bits.Any(bit => BitSet(words, bit));
    private static bool BitSet(ulong[] words, int bit)
    {
        var width = IntPtr.Size * 8;
        return bit / width < words.Length && (words[bit / width] & (1UL << (bit % width))) != 0;
    }

    private static TomlTable Table(TomlTable parent, string key) =>
        parent.TryGetValue(key, out var value) && value is TomlTable table
            ? table : throw new InvalidDataException("Native xemu input config is missing [input." + key + "].");
}
