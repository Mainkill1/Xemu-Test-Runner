namespace XemuTestRunner.Authoring;

public sealed class AuthoringSessionStateMachine
{
    private readonly object _gate = new();
    private AuthoringSessionSnapshot _snapshot;

    public AuthoringSessionStateMachine(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("An authoring session ID is required.", nameof(sessionId));

        _snapshot = new AuthoringSessionSnapshot(
            sessionId,
            AuthoringSessionState.Created,
            DateTimeOffset.UtcNow,
            FailureCode: null,
            FailureDetail: null);
    }

    public AuthoringSessionSnapshot Snapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    public AuthoringSessionSnapshot Transition(AuthoringSessionState next)
    {
        lock (_gate)
        {
            if (_snapshot.State is AuthoringSessionState.Failed or AuthoringSessionState.Ended)
                throw new InvalidOperationException(
                    $"Authoring session '{_snapshot.SessionId}' is terminal in state '{_snapshot.State}'.");
            if (next is AuthoringSessionState.Failed or AuthoringSessionState.Ended)
                throw new InvalidOperationException("Use Fail or End for terminal authoring transitions.");
            if (!IsAllowed(_snapshot.State, next))
                throw new InvalidOperationException(
                    $"Authoring session cannot transition from '{_snapshot.State}' to '{next}'.");

            _snapshot = _snapshot with
            {
                State = next,
                StateChangedUtc = DateTimeOffset.UtcNow,
                FailureCode = null,
                FailureDetail = null
            };
            return _snapshot;
        }
    }

    public AuthoringSessionSnapshot Fail(string code, string detail)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("An authoring failure code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException("Authoring failure detail is required.", nameof(detail));

        lock (_gate)
        {
            if (_snapshot.State == AuthoringSessionState.Ended)
                throw new InvalidOperationException(
                    $"Authoring session '{_snapshot.SessionId}' has already ended.");
            if (_snapshot.State == AuthoringSessionState.Failed)
            {
                if (string.Equals(_snapshot.FailureCode, code, StringComparison.Ordinal) &&
                    string.Equals(_snapshot.FailureDetail, detail, StringComparison.Ordinal))
                {
                    return _snapshot;
                }

                throw new InvalidOperationException(
                    $"Authoring session '{_snapshot.SessionId}' has already failed.");
            }
            if (_snapshot.State == AuthoringSessionState.Published)
                throw new InvalidOperationException(
                    $"Published authoring session '{_snapshot.SessionId}' cannot be failed.");

            _snapshot = _snapshot with
            {
                State = AuthoringSessionState.Failed,
                StateChangedUtc = DateTimeOffset.UtcNow,
                FailureCode = code,
                FailureDetail = detail
            };
            return _snapshot;
        }
    }

    public AuthoringSessionSnapshot End()
    {
        lock (_gate)
        {
            if (_snapshot.State == AuthoringSessionState.Ended)
                return _snapshot;

            _snapshot = _snapshot with
            {
                State = AuthoringSessionState.Ended,
                StateChangedUtc = DateTimeOffset.UtcNow
            };
            return _snapshot;
        }
    }

    private static bool IsAllowed(
        AuthoringSessionState current,
        AuthoringSessionState next) =>
        (current, next) switch
        {
            (AuthoringSessionState.Created, AuthoringSessionState.Preflight) => true,
            (AuthoringSessionState.Preflight, AuthoringSessionState.LaunchingXemu) => true,
            (AuthoringSessionState.LaunchingXemu, AuthoringSessionState.StartingCapture) => true,
            (AuthoringSessionState.StartingCapture, AuthoringSessionState.NegotiatingWebRtc) => true,
            (AuthoringSessionState.NegotiatingWebRtc, AuthoringSessionState.VerifyingController) => true,
            (AuthoringSessionState.VerifyingController, AuthoringSessionState.Ready) => true,
            (AuthoringSessionState.Ready, AuthoringSessionState.Recording) => true,
            (AuthoringSessionState.Recording, AuthoringSessionState.Reviewing) => true,
            (AuthoringSessionState.Reviewing, AuthoringSessionState.Recording) => true,
            (AuthoringSessionState.Reviewing, AuthoringSessionState.ReplayingDraft) => true,
            (AuthoringSessionState.ReplayingDraft, AuthoringSessionState.Reviewing) => true,
            (AuthoringSessionState.ReplayingDraft, AuthoringSessionState.Published) => true,
            _ => false
        };
}
