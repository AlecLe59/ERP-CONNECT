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
    private static readonly System.Text.Json.JsonSerializerOptions RequestJson = new(System.Text.Json.JsonSerializerDefaults.Web) { MaxDepth = 8 };
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

    public BackupService Backup { get; private set; } = null!;

    public RetentionService Retention { get; private set; } = null!;

    /// <summary>Paramètres Argon2id de l'export des clés (réduits par les tests).</summary>
    public Security.Backup.Argon2Parameters KeyExportParameters { get; init; } = Security.Backup.Argon2Parameters.Default;

    /// <summary>Clés de l'éditeur (celle de la compilation ; remplaçable par les tests).</summary>
    public Security.Licensing.LicenseVerifier LicenseVerifier { get; init; } = Security.Licensing.LicenseVerifier.FromBuild();

    /// <summary>Connecteurs installés (remplaçables par les tests).</summary>
    public IReadOnlyList<Connectors.Sdk.IConnector> ConnectorCatalog { get; init; } = ErpConnect.Connectors.ConnectorRegistry.CreateDefault();

    public RunServiceOptions RunOptions { get; init; } = new();

    public EngineEventHub Events { get; } = new();

    /// <summary>Démarrage : toute erreur ici doit arrêter le service avec un message clair.</summary>
    /// <summary>Déprotection de la phrase de passe d'une demande de restauration (DPAPI machine sous Windows).</summary>
    public Func<byte[], byte[]>? UnprotectRestorePassphrase { get; init; }

    /// <summary>Résultat de la restauration traitée à ce démarrage (null si aucune demande).</summary>
    public string? RestoreOutcome { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        Paths.EnsureCreated();
        await ProcessRestoreRequestAsync(ct).ConfigureAwait(false);
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

        await RecordRestoreAsync(ct).ConfigureAwait(false);
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
        Retention = new RetentionService(Settings, runRepository, _audit, Clock);
        Backup = new BackupService(Paths, [Config, RunsDb, AuditDb], _keys, _audit, Clock, Installation.Id, Version) { KeyExportParameters = KeyExportParameters };
        await Runs.RecoverInterruptedAsync(ct).ConfigureAwait(false);
    }

    private async Task ProcessRestoreRequestAsync(CancellationToken ct)
    {
        var path = Path.Combine(Paths.Data, RestoreRequest.FileName);
        if (!File.Exists(path))
        {
            return;
        }

        var request = System.Text.Json.JsonSerializer.Deserialize<RestoreRequest>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
            RequestJson)!;
        File.Delete(path); // une seule tentative, la phrase de passe protégée disparaît du disque
        string? passphrase = null;
        try
        {
            if (request.ProtectedPassphrase is not null)
            {
                var bytes = (UnprotectRestorePassphrase ?? throw new InvalidOperationException("Déprotection de la phrase de passe indisponible."))(Convert.FromBase64String(request.ProtectedPassphrase));
                passphrase = System.Text.Encoding.UTF8.GetString(bytes);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }

            var report = await BackupService.RestoreAsync(request.ArchivePath, Paths, _protectorFactory, passphrase, request.DiscardSecrets, Clock.UtcNow, ct).ConfigureAwait(false);
            RestoreOutcome = $"Restauration de la sauvegarde du {report.Manifest.CreatedAt:yyyy-MM-dd HH:mm} faite ; état précédent conservé dans {report.PreviousStateFolder}.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException or IOException or FormatException)
        {
            // Rien n'a été modifié si la vérification échoue : le service démarre sur l'état actuel et l'échec est inscrit au journal.
            RestoreOutcome = "Restauration refusée : " + ex.Message;
            await File.WriteAllTextAsync(Path.Combine(Paths.Data, "restore-failed.json"), System.Text.Json.JsonSerializer.Serialize(new { request.ArchivePath, error = ex.Message, at = Clock.UtcNow }), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Après une restauration (service arrêté), le premier démarrage l'inscrit au journal d'audit puis retire le marqueur.</summary>
    private async Task RecordRestoreAsync(CancellationToken ct)
    {
        var failed = Path.Combine(Paths.Data, "restore-failed.json");
        if (File.Exists(failed))
        {
            var why = await File.ReadAllTextAsync(failed, ct).ConfigureAwait(false);
            await _audit!.AppendAsync(new Core.Audit.AuditEntry("S-1-5-18", "ERP Connect Engine", "backup.restore", "backup", Core.Audit.AuditOutcomes.Failure,
                why.Length <= 4096 ? why : "{}", "startup"), ct).ConfigureAwait(false);
            File.Delete(failed);
        }

        var marker = Path.Combine(Paths.Data, BackupService.RestoreMarker);
        if (!File.Exists(marker))
        {
            return;
        }

        var details = await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false);
        await _audit!.AppendAsync(new Core.Audit.AuditEntry("S-1-5-18", "ERP Connect Engine", "backup.restore", "backup", Core.Audit.AuditOutcomes.Success,
            details.Length <= 4096 ? details : "{}", "startup"), ct).ConfigureAwait(false);
        File.Delete(marker);
    }

    public async ValueTask DisposeAsync()
    {
        if (Retention is not null)
        {
            await Retention.DisposeAsync().ConfigureAwait(false);
        }

        if (Scheduler is not null)
        {
            await Scheduler.DisposeAsync().ConfigureAwait(false);
        }

        if (Runs is not null)
        {
            await Runs.DisposeAsync().ConfigureAwait(false);
        }

        Backup?.Dispose();
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
