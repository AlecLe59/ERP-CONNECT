using System.Runtime.Versioning;
using ErpConnect.Contracts;
using ErpConnect.Engine;
using ErpConnect.Rpc.Dispatch;
using ErpConnect.Rpc.Transport;
using ErpConnect.Rpc.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace ErpConnect.Service;

/// <summary>Hébergement du moteur dans le service Windows ERPConnectEngine.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class EngineWorker : IHostedService, IAsyncDisposable
{
    private readonly EngineRuntime _runtime;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly LoggingLevelSwitch _level;
    private readonly IHostApplicationLifetime _lifetime;
    private PipeServer? _server;
    private RpcDispatcher? _dispatcher;

    public EngineWorker(EngineRuntime runtime, ILoggerFactory loggers, LoggingLevelSwitch level, IHostApplicationLifetime lifetime)
    {
        _runtime = runtime;
        _loggers = loggers;
        _logger = loggers.CreateLogger<EngineWorker>();
        _level = level;
        _lifetime = lifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            foreach (var r in _runtime.MigrationReports)
            {
                LogMigration(_logger, r.Database, r.FromVersion, r.ToVersion, r.BackupPath ?? "aucune");
            }

            ApplyLogLevel((await _runtime.Settings.GetAsync(cancellationToken).ConfigureAwait(false)).Settings.LogLevel);

            _dispatcher = new RpcDispatcher(
                new RuntimeAuthorizer(_runtime), new RuntimeAuditSink(_runtime), _loggers.CreateLogger<RpcDispatcher>(), _runtime.Version);
            RpcEndpoints.Register(_dispatcher, _runtime);

            var security = WindowsPipeFactory.BuildSecurity(missing => LogMissingGroup(_logger, missing));
            _server = new PipeServer(
                new PipeServerOptions
                {
                    CreatePipe = WindowsPipeFactory.Factory(security, Protocol.MaxConnections),
                    IdentityOf = WindowsPipeFactory.IdentityOf,
                },
                _dispatcher,
                _loggers.CreateLogger<PipeServer>());
            BridgeEvents(_server);
            _server.Start();
            _runtime.Scheduler.Start();
            _runtime.Status.SetState(EngineState.Running);
            LogStarted(_logger, _runtime.Version, Protocol.PipeName);
        }
        catch (Exception ex)
        {
            LogStartFailed(_logger, ex);
            Environment.ExitCode = 1;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runtime.Status.SetState(EngineState.Stopping);
        if (_dispatcher is not null)
        {
            _dispatcher.Available = false;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        LogStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        await _runtime.DisposeAsync().ConfigureAwait(false);
    }

    private void BridgeEvents(PipeServer server)
    {
        var authorizer = new RuntimeAuthorizer(_runtime);
        var token = _lifetime.ApplicationStopping;
        void Fire(RpcNotificationDescriptor n, object payload) =>
            _ = Task.Run(() => server.BroadcastAsync(n, payload, authorizer, token), token);

        _runtime.Events.OnStatusChanged += (state, at) =>
            Fire(RpcNotifications.EngineStatusChanged, new EngineStatusChangedPayload(state.ToString().ToLowerInvariant(), at));
        _runtime.Events.OnSettingsChanged += (version, by) =>
        {
            Fire(RpcNotifications.SettingsChanged, new SettingsChangedPayload(version, by));
            _ = Task.Run(async () => ApplyLogLevel((await _runtime.Settings.GetAsync(token).ConfigureAwait(false)).Settings.LogLevel), token);
        };
        _runtime.Events.OnAuditAppended += (seq, action, outcome) =>
            Fire(RpcNotifications.AuditAppended, new AuditAppendedPayload(seq, action, outcome));
        _runtime.Events.OnRunChanged += (runId, flowId, status) =>
            Fire(RpcNotifications.RunChanged, new RunChangedPayload(runId, flowId, RpcEndpoints.StatusName(status)));
        _runtime.Events.OnAlertRaised += (id, flowId, severity, message) =>
        {
            // Supervision externe : les alertes vont aussi au journal du service et, en service Windows, au journal des événements (source « ERP Connect », niveau Erreur ou Avertissement).
            if (severity == "error")
            {
                LogAlertError(_logger, id, flowId, message);
            }
            else
            {
                LogAlertWarning(_logger, id, flowId, message);
            }

            Fire(RpcNotifications.AlertRaised, new AlertRaisedPayload(id, flowId, severity, message));
        };
    }

    private void ApplyLogLevel(string level) => _level.MinimumLevel = level switch
    {
        "Debug" => LogEventLevel.Debug,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ERP Connect Engine {Version} démarré, pipe {Pipe}")]
    private static partial void LogStarted(ILogger logger, string version, string pipe);

    [LoggerMessage(EventId = 2, Level = LogLevel.Critical, Message = "Démarrage du moteur impossible")]
    private static partial void LogStartFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Base {Database} : schéma {From} → {To}, sauvegarde {Backup}")]
    private static partial void LogMigration(ILogger logger, string database, int from, int to, string backup);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Groupe local absent, ignoré dans l'ACL du pipe : {Group}")]
    private static partial void LogMissingGroup(ILogger logger, string group);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "ERP Connect Engine arrêté")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Alerte {AlertId}, flux {FlowId} : {Message}")]
    private static partial void LogAlertError(ILogger logger, long alertId, string flowId, string message);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Alerte {AlertId}, flux {FlowId} : {Message}")]
    private static partial void LogAlertWarning(ILogger logger, long alertId, string flowId, string message);
}
