namespace XemuTestRunner.Control.Gamepad;

public sealed class ControllerInputDefinition
{
    public string Backend { get; set; } = "";
    public int? ControllerIndex { get; set; }
    public string MappingProfile { get; set; } = "";

    public void Validate()
    {
        if (Backend != "native-os-gamepad")
            throw new InvalidDataException("ControllerInput.Backend must be native-os-gamepad.");
        if (ControllerIndex != 0)
            throw new InvalidDataException("ControllerInput requires an explicit controller index 0.");
        if (MappingProfile != "runner-xbox-port1-v1")
            throw new InvalidDataException("ControllerInput.MappingProfile is not a provisioned binding profile.");
    }
}

public static class ControllerButtonMap
{
    public static XboxControllerState Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var button = name.Trim().ToLowerInvariant() switch
        {
            "a" => XboxControllerButtons.A,
            "b" => XboxControllerButtons.B,
            "x" => XboxControllerButtons.X,
            "y" => XboxControllerButtons.Y,
            "start" => XboxControllerButtons.Start,
            "back" => XboxControllerButtons.Back,
            "dpadup" => XboxControllerButtons.DPadUp,
            "dpaddown" => XboxControllerButtons.DPadDown,
            "dpadleft" => XboxControllerButtons.DPadLeft,
            "dpadright" => XboxControllerButtons.DPadRight,
            "leftstick" => XboxControllerButtons.LeftStick,
            "rightstick" => XboxControllerButtons.RightStick,
            "leftshoulder" or "white" => XboxControllerButtons.LeftShoulder,
            "rightshoulder" or "black" => XboxControllerButtons.RightShoulder,
            _ => XboxControllerButtons.None
        };
        if (button != XboxControllerButtons.None)
            return new XboxControllerState(button, 0, 0, 0, 0, 0, 0);

        return name.Trim().ToLowerInvariant() switch
        {
            "ltrigger" => new XboxControllerState(0, byte.MaxValue, 0, 0, 0, 0, 0),
            "rtrigger" => new XboxControllerState(0, 0, byte.MaxValue, 0, 0, 0, 0),
            "lstickleft" => new XboxControllerState(0, 0, 0, short.MinValue, 0, 0, 0),
            "lstickright" => new XboxControllerState(0, 0, 0, short.MaxValue, 0, 0, 0),
            "lstickup" => new XboxControllerState(0, 0, 0, 0, short.MaxValue, 0, 0),
            "lstickdown" => new XboxControllerState(0, 0, 0, 0, short.MinValue, 0, 0),
            "rstickleft" => new XboxControllerState(0, 0, 0, 0, 0, short.MinValue, 0),
            "rstickright" => new XboxControllerState(0, 0, 0, 0, 0, short.MaxValue, 0),
            "rstickup" => new XboxControllerState(0, 0, 0, 0, 0, 0, short.MaxValue),
            "rstickdown" => new XboxControllerState(0, 0, 0, 0, 0, 0, short.MinValue),
            _ => throw new InvalidDataException("Unsupported native controller action: " + name)
        };
    }
}

public sealed class ControllerStateDefinition
{
    public List<string> Buttons { get; set; } = [];
    public int LeftTrigger { get; set; }
    public int RightTrigger { get; set; }
    public int LeftX { get; set; }
    public int LeftY { get; set; }
    public int RightX { get; set; }
    public int RightY { get; set; }

    public XboxControllerState ToState()
    {
        if (Buttons is null || Buttons.Count > 14 ||
            LeftTrigger is < 0 or > 255 || RightTrigger is < 0 or > 255 ||
            LeftX is < short.MinValue or > short.MaxValue ||
            LeftY is < short.MinValue or > short.MaxValue ||
            RightX is < short.MinValue or > short.MaxValue ||
            RightY is < short.MinValue or > short.MaxValue)
            throw new InvalidDataException("Controller state contains an invalid analog value or button list.");
        XboxControllerButtons flags = XboxControllerButtons.None;
        foreach (var name in Buttons)
        {
            var resolved = ControllerButtonMap.Resolve(name);
            if (resolved.Buttons == XboxControllerButtons.None ||
                (flags & resolved.Buttons) != 0)
                throw new InvalidDataException("Controller state buttons must be distinct digital controls.");
            flags |= resolved.Buttons;
        }
        if ((flags & (XboxControllerButtons.DPadUp | XboxControllerButtons.DPadDown)) ==
            (XboxControllerButtons.DPadUp | XboxControllerButtons.DPadDown) ||
            (flags & (XboxControllerButtons.DPadLeft | XboxControllerButtons.DPadRight)) ==
            (XboxControllerButtons.DPadLeft | XboxControllerButtons.DPadRight))
            throw new InvalidDataException("Controller state cannot hold opposing D-pad directions.");
        return new XboxControllerState(flags, (byte)LeftTrigger, (byte)RightTrigger,
            (short)LeftX, (short)LeftY, (short)RightX, (short)RightY);
    }
}
