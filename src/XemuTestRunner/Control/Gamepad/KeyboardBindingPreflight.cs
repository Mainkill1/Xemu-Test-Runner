using Tomlyn;
using Tomlyn.Model;

namespace XemuTestRunner.Control.Gamepad;

/// <summary>
/// Proves that legacy host-key injection has a deterministic guest controller
/// destination before xemu is allowed to start.
/// </summary>
public static class KeyboardBindingPreflight
{
    public static void Verify(IReadOnlyList<string> arguments, string workingDirectory)
    {
        string? configured = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Equals("-config_path", StringComparison.Ordinal))
            {
                if (configured is not null || ++i >= arguments.Count)
                    throw new InvalidDataException(
                        "Keyboard input requires one explicit xemu -config_path.");
                configured = arguments[i];
            }
            else if (arguments[i].StartsWith("-config_path=", StringComparison.Ordinal))
            {
                if (configured is not null)
                    throw new InvalidDataException(
                        "Keyboard input requires one explicit xemu -config_path.");
                configured = arguments[i][13..];
            }
        }

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidDataException(
                "Keyboard input requires a private xemu -config_path.");
        var path = Path.GetFullPath(configured, workingDirectory);
        TomlTable model;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException("xemu input config is missing.", path);
            if (info.Length > 1024 * 1024)
                throw new InvalidDataException("Keyboard xemu input config exceeds 1 MiB.");
            model = Toml.ToModel(File.ReadAllText(path));
        }
        catch (Exception error) when (
            error is not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException(
                "Keyboard xemu input config could not be read.", error);
        }

        var input = Table(model, "input", "[input]");
        var ports = Table(input, "virtual_ports", "[input.virtual_ports]");
        var bindings = Table(input, "bindings", "[input.bindings]");
        if (!input.TryGetValue("auto_bind", out var autoBind) || autoBind is not false ||
            !ports.TryGetValue("port1_connected", out var connected) ||
            connected is not 1L ||
            !bindings.TryGetValue("port1", out var selected) ||
            selected is not string binding || binding != "keyboard" ||
            !bindings.TryGetValue("port1_driver", out var driver) ||
            driver is not string driverName || driverName != "usb-xbox-gamepad")
            throw new InvalidDataException(
                "Keyboard input requires auto_bind=false, port1_connected=1, and " +
                "port 1 explicitly bound to keyboard with usb-xbox-gamepad.");
    }

    private static TomlTable Table(TomlTable parent, string key, string section) =>
        parent.TryGetValue(key, out var value) && value is TomlTable table
            ? table
            : throw new InvalidDataException(
                "Keyboard xemu input config is missing " + section + ".");
}
