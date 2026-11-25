namespace ErpConnect.Engine;

/// <summary>Événements du moteur relayés en notifications JSON-RPC par le service.</summary>
public interface IEngineEventSink
{
    void StatusChanged(EngineState state, DateTimeOffset at);

    void SettingsChanged(long version, string updatedBy);

    void AuditAppended(long sequence, string action, string outcome);

    void RunChanged(string runId, string flowId, ErpConnect.Core.Runs.RunStatus status);

    void AlertRaised(long id, string flowId, string severity, string message)
    {
    }
}

public sealed class NullEngineEventSink : IEngineEventSink
{
    public static NullEngineEventSink Instance { get; } = new();

    public void StatusChanged(EngineState state, DateTimeOffset at)
    {
    }

    public void SettingsChanged(long version, string updatedBy)
    {
    }

    public void AuditAppended(long sequence, string action, string outcome)
    {
    }

    public void RunChanged(string runId, string flowId, ErpConnect.Core.Runs.RunStatus status)
    {
    }
}

public enum EngineState
{
    Starting,
    Running,
    Degraded,
    Stopping,
}
