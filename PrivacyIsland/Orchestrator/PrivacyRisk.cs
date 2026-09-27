namespace PrivacyIsland.Orchestrator;

public enum PrivacyRiskKind
{
    ScreenCapture,
    RemoteControl,
    Microphone,
    Camera,
    LiveBroadcast,
    DeviceCamera,
    WindowChange,
}

public sealed record PrivacyRiskSnapshot(
    PrivacyRiskKind Kind,
    bool Active,
    int ProcessId,
    DateTime? ProcessStartTimeUtc,
    string ProcessName,
    string ExecutablePath,
    string Evidence,
    long SubjectId = 0);
