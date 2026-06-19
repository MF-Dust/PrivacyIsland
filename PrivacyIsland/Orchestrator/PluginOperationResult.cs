namespace PrivacyIsland.Orchestrator;

public sealed record PluginOperationResult(bool Success, string Message)
{
    public static PluginOperationResult Ok(string message) => new(true, message);
    public static PluginOperationResult Fail(string message) => new(false, message);
}
