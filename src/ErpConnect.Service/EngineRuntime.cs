using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Flows;
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
        RunsDb = SqliteDatabase.InDirectory(DatabaseKind.Runs, paths.Data);
        AuditDb = SqliteDatabase.InDirectory(DatabaseKind.Audit, paths.Data);
    }

    public EnginePaths Paths { get; }

    public IPrincipalResolver Principals { get; }

    public IClock Clock { get; }

    public string Version { get; }

    public SqliteDatabase Config { get; }

    public SqliteDatabase RunsDb { get; }

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

    public ConnectionService Connections { get; private set; } = null!;

    public PreviewService Preview { get; private set; } = null!;

    public FlowService Flows { get; private set; } = null!;

    public RunService Runs { get; private set; } = null!;

    public SchedulerService Scheduler { get; private set; } = null!;

    public IAlertRepository Alerts { get; private set; } = null!;

    public LicenseService License { get; private set; } = null!;

    /// <summary>Clés de l'éditeur (celle de la compilation ; remplaçable par les tests).</summary>
    public Security.Licensing.LicenseVerifier LicenseVerifier { get; init; } = Security.Licensing.LicenseVerifier.FromBuild();

    /// <summary>Connecteurs installés (remplaçables par les tests).</summary>
    public IReadOnlyList<Connectors.Sdk.IConnector> ConnectorCatalog { get; init; } = ErpConnect.Connectors.ConnectorRegistry.CreateDefault();

    public RunServiceOptions RunOptions { get; init; } = new();

    public EngineEventHub Events { get; } = new();

    /// <summary>Démarrage : toute erreur ici doit arrêter le service avec un message clair.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Paths.EnsureCreated();
        var runner = new MigrationRunner(Paths.Backup);
        MigrationReports =
        [
            await runner.MigrateAsync(Config, ct).ConfigureAwait(false),
            await runner.MigrateAsync(RunsDb, ct).ConfigureAwait(false),
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
        Status = new EngineStatusService(Paths, [Config, RunsDb, AuditDb], _vault, Secrets, Clock, protector.Description, Version)
        {
            Events = Events,
        };

        var connections = new ConnectionRepository(Config);
        var flows = new FlowRepository(Config);
        Connections = new ConnectionService(connections, flows, _vault, _audit, Clock, ConnectorCatalog);
        Preview = new PreviewService(Connections);
        var settings = Settings;
        License = new LicenseService(new LicenseRepository(Config), LicenseVerifier, flows, _audit, Clock, Installation.Id, Installation.InstalledAt);
        var license = License;
        Flows = new FlowService(flows, connections, _audit, Clock)
        {
            ScriptsAllowed = async token => (await settings.GetAsync(token).ConfigureAwait(false)).Settings.ScriptsEnabled
                && (await license.CheckAsync(Core.Licensing.Features.Scripts, token).ConfigureAwait(false)).Allowed,
            Entitlements = license,
        };
        var runRepository = new RunRepository(RunsDb);
        Alerts = runRepository;
        Runs = new RunService(runRepository, Flows, Connections, _audit, Clock, Events, RunOptions) { Entitlements = license };
        Scheduler = new SchedulerService(new ScheduleRepository(Config), Flows, Runs, _audit, Clock, runRepository, Events);
        await Runs.RecoverInterruptedAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Scheduler is not null)
        {
            await Scheduler.DisposeAsync().ConfigureAwait(false);
        }

        if (Runs is not null)
        {
            await Runs.DisposeAsync().ConfigureAwait(false);
        }

        _vault?.Dispose();
        _keys?.Dispose();
        _audit?.Dispose();
        _users?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    public event Action<string, string, Core.Runs.RunStatus>? OnRunChanged;

    public void RunChanged(string runId, string flowId, Core.Runs.RunStatus status) => OnRunChanged?.Invoke(runId, flowId, status);

    public event Action<long, string, string, string>? OnAlertRaised;

    public void AlertRaised(long id, string flowId, string severity, string message) => OnAlertRaised?.Invoke(id, flowId, severity, message);
}
