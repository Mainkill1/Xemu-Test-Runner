namespace XemuTestRunner.Authoring;

public enum AuthoringSessionState
{
    Created,
    Preflight,
    LaunchingXemu,
    StartingCapture,
    NegotiatingWebRtc,
    VerifyingController,
    Ready,
    Recording,
    Reviewing,
    ReplayingDraft,
    Published,
    Failed,
    Ended
}

public sealed record AuthoringSessionSnapshot(
    string SessionId,
    AuthoringSessionState State,
    DateTimeOffset StateChangedUtc,
    string? FailureCode,
    string? FailureDetail);

public sealed record AuthoringReadinessSnapshot(
    bool XemuProcessAlive,
    bool QmpReady,
    bool CaptureTargetOwned,
    bool CaptureRateQualified,
    bool EncodeRateQualified,
    bool BrowserDecoding,
    bool InputStateChannelOpen,
    bool SessionControlChannelOpen,
    bool VirtualGamepadReady,
    bool FrameProgressing,
    bool InputLatencyQualified,
    bool SecureContext,
    bool StandardGamepadMapped)
{
    public bool IsReady => GetUnmetRequirements().Count == 0;

    public IReadOnlyList<string> GetUnmetRequirements()
    {
        var unmet = new List<string>(13);
        if (!XemuProcessAlive) unmet.Add("xemu_process_alive");
        if (!QmpReady) unmet.Add("qmp_ready");
        if (!CaptureTargetOwned) unmet.Add("capture_target_owned");
        if (!CaptureRateQualified) unmet.Add("capture_rate_qualified");
        if (!EncodeRateQualified) unmet.Add("encode_rate_qualified");
        if (!BrowserDecoding) unmet.Add("browser_decoding");
        if (!InputStateChannelOpen) unmet.Add("input_state_channel_open");
        if (!SessionControlChannelOpen) unmet.Add("session_control_channel_open");
        if (!VirtualGamepadReady) unmet.Add("virtual_gamepad_ready");
        if (!FrameProgressing) unmet.Add("frame_progressing");
        if (!InputLatencyQualified) unmet.Add("input_latency_qualified");
        if (!SecureContext) unmet.Add("secure_context");
        if (!StandardGamepadMapped) unmet.Add("standard_gamepad_mapped");
        return unmet;
    }
}
