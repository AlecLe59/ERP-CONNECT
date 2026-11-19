using ErpConnect.Core.Abstractions;
using ErpConnect.Engine;
using ErpConnect.Security.Vault;
using ErpConnect.Storage;
using ErpConnect.Storage.Repositories;

namespace ErpConnect.Service;

/// <summary>
/// Racine de composition du moteur, indépendante du transport : migrations, coffre, droits par défaut, services.
/// Le service Windows l'héberge ; les tests d'intégration l'utilisent avec un protecteur de clé de test.
/// </summary>
internal sealed class EngineRuntime : IAsyncDisposable
{
    private readonly Func<byte[], IKeyProtector> _protectorFactory;
    private MasterKeyStore? _keys;
    private SecretVault? _vault;
    private ChainedAuditLog? _audit;
    private UserRoleService? _users;

    public EngineRuntime(EnginePaths paths, Func<byte[], IKeyProtector> protectorFactory, IPrincipalResolver principals, IClock clock, string version)
    {
        Paths = paths;
        _protectorFactory = protectorFactory;
        Principals = principals;
        Clock = clock;
        Version = version;
        Config = SqliteDatabase.InDirectory(DatabaseKind.Config, paths.Data);
        Runs = SqliteDatabase.InDirectory(DatabaseKind.Runs, paths.Data);
        AuditDb = SqliteDatabase.InDirectory(DatabaseKind.Audit, paths.Data);
    }

    public EnginePaths Paths { get; }

    public IPrincipalResolver Principals { get; }

    public IClock Clock { get; }

    public string Version { get; }

    public SqliteDatabase Config { get; }

    public SqliteDatabase Runs { get; }

    public SqliteDatabase AuditDb { get; }

    public IReadOnlyList<MigrationReport> MigrationReports { get; private set; } = [];

    public InstallationRecord Installation { get; private set; } = null!;

    public SecretVault Vault => _vault ?? throw NotStarted();

    public SecretStore Secrets { get; private set; } = null!;

    public ChainedAuditLog Audit => _audit ?? throw NotStarted();

    public AccessService Access { get; private set; } = null!;

    public SettingsService Settings { get; private set; } = null!;

    public UserRoleService Users => _users ?? throw NotStarted();

    public EngineStatusService Status { get; private set; } = null!;

    public EngineEventHub Events { get; } = new();

    /// <summary>Démarrage : toute erreur ici doit arrêter le service avec un message clair.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Paths.EnsureCreated();
        var runner = new MigrationRunner(Paths.Backup);
        MigrationReports =
        [
            await runner.MigrateAsync(Config, ct).ConfigureAwait(false),
            await runner.MigrateAsync(Runs, ct).ConfigureAwait(false),
            await runner.MigrateAsync(AuditDb, ct).ConfigureAwait(false),
        ];

        Installation = await new InstallationRepository(Config).GetOrCreateAsync(ct).ConfigureAwait(false);
        Secrets = new SecretStore(Config);
        var protector = _protectorFactory(Installation.KeyEntropy);
        _keys = new MasterKeyStore(Paths.Keys, protector);
        _vault = new SecretVault(Secrets, _keys, Clock);
        await _vault.InitializeAsync(ct).ConfigureAwait(false);

        _audit = new ChainedAuditLog(new AuditRepository(AuditDb), Clock, Events);
        var assignments = new RoleAssignmentRepository(Config);
        Access = new AccessService(assignments);
        if (await Access.EnsureDefaultAdministratorAsync(Clock, ct).ConfigureAwait(false))
        {
            await _audit.AppendAsync(new Core.Audit.AuditEntry(
                "S-1-5-18", "ERP Connect Engine", "users.assignRole", $"principal/{Core.Authorization.WellKnownSids.BuiltinAdministrators}",
                Core.Audit.AuditOutcomes.Success, "{\"reason\":\"first-start\",\"role\":\"Administrator\"}", "startup"), ct).ConfigureAwait(false);
        }

        Settings = new SettingsService(new SettingsRepository(Config, Clock), _audit, Events);
        _users = new UserRoleService(assignments, _audit, Principals, Clock);
        Status = new EngineStatusService(Paths, [Config, Runs, AuditDb], _vault, Secrets, Clock, protector.Description, Version)
        {
            Events = Events,
        };
    }

    public ValueTask DisposeAsync()
    {
        _vault?.Dispose();
        _keys?.Dispose();
        _audit?.Dispose();
        _users?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static InvalidOperationException NotStarted() => new("Moteur non démarré.");
}

/// <summary>Relais des événements du moteur vers les abonnés (le serveur de pipe).</summary>
internal sealed class EngineEventHub : IEngineEventSink
{
    public event Action<EngineState, DateTimeOffset>? OnStatusChanged;

    public event Action<long, string>? OnSettingsChanged;

    public event Action<long, string, string>? OnAuditAppended;

    public void StatusChanged(EngineState state, DateTimeOffset at) => OnStatusChanged?.Invoke(state, at);

    public void SettingsChanged(long version, string updatedBy) => OnSettingsChanged?.Invoke(version, updatedBy);

    public void AuditAppended(long sequence, string action, string outcome) => OnAuditAppended?.Invoke(sequence, action, outcome);
}
