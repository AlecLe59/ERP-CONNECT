using ErpConnect.Connectors;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;
using ErpConnect.Security.Vault;
using ErpConnect.Storage;
using ErpConnect.Storage.Repositories;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Engine.Tests;

/// <summary>Moteur sans transport : vraies bases SQLite, vrai coffre (protecteur de test), vrais connecteurs.</summary>
internal sealed class Kernel : IAsyncDisposable
{
    private readonly MasterKeyStore _keys;

    private Kernel(TempDirectory dir, SqliteDatabase config, SqliteDatabase runs, SqliteDatabase audit, MasterKeyStore keys, SecretVault vault, ChainedAuditLog log, RunServiceOptions options)
    {
        Dir = dir;
        ConfigDb = config;
        RunsDb = runs;
        AuditDb = audit;
        _keys = keys;
        Vault = vault;
        Audit = log;
        var connections = new ConnectionRepository(config);
        var flows = new FlowRepository(config);
        Connections = new ConnectionService(connections, flows, vault, log, Clock, ConnectorRegistry.CreateDefault());
        Flows = new FlowService(flows, connections, log, Clock);
        RunRepository = new RunRepository(runs);
        Runs = new RunService(RunRepository, Flows, Connections, log, Clock, null, options);
    }

    public static IClock Clock { get; } = new SystemClock();

    public TempDirectory Dir { get; }

    public SqliteDatabase ConfigDb { get; }

    public SqliteDatabase RunsDb { get; }

    public SqliteDatabase AuditDb { get; }

    public SecretVault Vault { get; }

    public ChainedAuditLog Audit { get; }

    public ConnectionService Connections { get; }

    public FlowService Flows { get; }

    public RunRepository RunRepository { get; }

    public RunService Runs { get; private set; }

    public static CallerIdentity Admin => Identities.Admin;

    public static RunServiceOptions FastRetry => new() { Retry = new RetryPolicy(4, delay: (_, _) => Task.CompletedTask) };

    public static async Task<Kernel> StartAsync(RunServiceOptions? options = null, TempDirectory? reuse = null)
    {
        var dir = reuse ?? new TempDirectory();
        var data = Path.Combine(dir.Path, "data");
        Directory.CreateDirectory(data);
        var runner = new MigrationRunner(Path.Combine(dir.Path, "backup"));
        var config = SqliteDatabase.InDirectory(DatabaseKind.Config, data);
        var runs = SqliteDatabase.InDirectory(DatabaseKind.Runs, data);
        var audit = SqliteDatabase.InDirectory(DatabaseKind.Audit, data);
        foreach (var db in new[] { config, runs, audit })
        {
            await runner.MigrateAsync(db, default);
        }

        var keys = new MasterKeyStore(Path.Combine(dir.Path, "keys"), new TestKeyProtector());
        var vault = new SecretVault(new SecretStore(config), keys, Clock);
        await vault.InitializeAsync(default);
        var log = new ChainedAuditLog(new AuditRepository(audit), Clock);
        var k = new Kernel(dir, config, runs, audit, keys, vault, log, options ?? FastRetry);
        await k.Runs.RecoverInterruptedAsync(default);
        return k;
    }

    public string Folder(string name)
    {
        var p = Path.Combine(Dir.Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public Task<ConnectionRecord> LocalAsync(string id, string root) =>
        Connections.SaveAsync(Admin, id, id, "file.local", new Dictionary<string, string?> { ["rootPath"] = root }, new Dictionary<string, string?>(), [], 0, "t", default);

    public Task<ConnectionRecord> RestAsync(string id, string baseUrl, Dictionary<string, string?>? secrets = null) =>
        Connections.SaveAsync(Admin, id, id, "rest", new Dictionary<string, string?> { ["baseUrl"] = baseUrl, ["allowHttp"] = "true", ["maxRetries"] = "3" },
            secrets ?? new Dictionary<string, string?>(), [], 0, "t", default);

    public static FlowDefinition Definition(string source, string resource, string sourceFormat, string target, string targetResource, OptionEntry[]? targetOptions = null, MappingSpec? mapping = null, int batch = 100) =>
        new(new SourceSpec(source, resource, [new("format", sourceFormat)]),
            new TargetSpec(target, targetResource, "createNew", targetOptions ?? [new("format", "json")]),
            mapping ?? new MappingSpec("identity", []), batch);

    public async Task<RunRecord> RunToEndAsync(string flowId, bool draft = true)
    {
        var run = await Runs.StartAsync(Admin, flowId, draft, "t", default);
        await Runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromMinutes(10));
        return (await Runs.GetAsync(run.Id, default))!;
    }

    public async Task<RunRecord> FlowRunAsync(FlowDefinition def)
    {
        var flow = await Flows.SaveAsync(Admin, null, "flux-" + Guid.NewGuid().ToString("N")[..6], "", def, 0, "t", default);
        return await RunToEndAsync(flow.Id);
    }

    public async Task RestartRunsAsync(RunServiceOptions? options = null)
    {
        await Runs.DisposeAsync();
        Runs = new RunService(RunRepository, Flows, Connections, Audit, Clock, null, options ?? FastRetry);
        await Runs.RecoverInterruptedAsync(default);
    }

    public async ValueTask DisposeAsync()
    {
        await Runs.DisposeAsync();
        Vault.Dispose();
        _keys.Dispose();
        Audit.Dispose();
        Dir.Dispose();
    }

    public static async Task WriteCsvAsync(string path, int rows, Func<int, string>? line = null)
    {
        await using var w = new StreamWriter(path);
        await w.WriteLineAsync("id;nom;montant");
        for (var i = 0; i < rows; i++)
        {
            await w.WriteLineAsync(line?.Invoke(i) ?? $"{i};Client {i};{i}.50");
        }
    }
}

internal static class RunAssertions
{
    public static void ShouldHaveStatus(this RunRecord run, RunStatus status) =>
        run.Status.ShouldBe(status, $"erreur : {run.Error}");
}
