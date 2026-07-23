namespace PrivacyIsland.Orchestrator;

public enum PrivacyRiskKind
{
    ScreenCapture,
    RemoteControl,
    Microphone,
    Camera,
}

public sealed record PrivacyRiskSnapshot(
    PrivacyRiskKind Kind,
    bool Active,
    int ProcessId,
    DateTime? ProcessStartTimeUtc,
    string ProcessName,
    string ExecutablePath,
    string Evidence);
