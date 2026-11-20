using ErpConnect.Core.Abstractions;
using ErpConnect.Storage;
using ErpConnect.Storage.Repositories;

namespace ErpConnect.Engine;

public sealed record DatabaseHealth(string Name, bool Ok, int SchemaVersion);

public sealed record EngineStatusSnapshot(
    EngineState State,
    string Version,
    TimeSpan Uptime,
    long DiskFreeBytes,
    long DiskTotalBytes,
    IReadOnlyList<DatabaseHealth> Databases,
    bool VaultInitialized,
    int? VaultKeyVersion,
    int SecretCount,
    string VaultProtection,
    DateTimeOffset ServerTime);

/// <summary>État réel du moteur : rien n'est simulé, chaque valeur est mesurée à la demande.</summary>
public sealed class EngineStatusService
{
    private readonly EnginePaths _paths;
    private readonly IReadOnlyList<SqliteDatabase> _databases;
    private readonly ISecretVault _vault;
    private readonly SecretStore _secrets;
    private readonly IClock _clock;
    private readonly string _vaultProtection;
    private readonly DateTimeOffset _startedAt;
    private EngineState _state = EngineState.Starting;

    public EngineStatusService(
        EnginePaths paths, IReadOnlyList<SqliteDatabase> databases, ISecretVault vault, SecretStore secrets, IClock clock, string vaultProtection, string version)
    {
        _paths = paths;
        _databases = databases;
        _vault = vault;
        _secrets = secrets;
        _clock = clock;
        _vaultProtection = vaultProtection;
        Version = version;
        _startedAt = clock.UtcNow;
    }

    public IEngineEventSink Events { get; set; } = NullEngineEventSink.Instance;

    public string Version { get; }

    public EngineState State => _state;

    public void SetState(EngineState state)
    {
        if (_state != state)
        {
            _state = state;
            Events.StatusChanged(state, _clock.UtcNow);
        }
    }

    public async Task<EngineStatusSnapshot> GetAsync(CancellationToken ct)
    {
        var dbs = new List<DatabaseHealth>();
        foreach (var db in _databases)
        {
            try
            {
                dbs.Add(new DatabaseHealth(db.Name, true, await MigrationRunner.GetSchemaVersionAsync(db, ct).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                dbs.Add(new DatabaseHealth(db.Name, false, 0));
            }
        }

        long free = 0, total = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_paths.Data))!);
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Espace disque inconnu : renvoyé à 0, l'interface l'affiche comme tel.
        }

        var count = 0;
        try
        {
            count = await _secrets.CountAsync(ct).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }

        var state = _state == EngineState.Running && dbs.Any(d => !d.Ok) ? EngineState.Degraded : _state;
        return new EngineStatusSnapshot(
            state, Version, _clock.UtcNow - _startedAt, free, total, dbs, _vault.IsInitialized, _vault.ActiveKeyVersion, count,
            _vaultProtection, _clock.UtcNow);
    }
}
