using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Contracts;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Core.Settings;
using ErpConnect.Engine;
using ErpConnect.Flows;
using ErpConnect.Rpc.Dispatch;

namespace ErpConnect.Service;

/// <summary>Branche chaque méthode du contrat sur le moteur. Aucune méthode ne renvoie de secret.</summary>
internal static class RpcEndpoints
{
    public static void Register(RpcDispatcher dispatcher, EngineRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(runtime);

        dispatcher.Register<EmptyParams, SystemStatusResult>(RpcMethods.SystemGetStatus, async (_, _, ct) =>
        {
            var s = await runtime.Status.GetAsync(ct).ConfigureAwait(false);
            return new SystemStatusResult(
                s.State.ToString().ToLowerInvariant(), s.Version, (long)s.Uptime.TotalSeconds, s.DiskFreeBytes, s.DiskTotalBytes,
                s.Databases.Select(d => new DatabaseStatusDto(d.Name, d.Ok, d.SchemaVersion)).ToArray(),
                new VaultStatusResult(s.VaultInitialized, s.VaultKeyVersion, s.SecretCount, s.VaultProtection), s.ServerTime);
        });

        dispatcher.Register<EmptyParams, SystemInfoResult>(RpcMethods.SystemGetInfo, (_, _, _) =>
            Task.FromResult(new SystemInfoResult(
                runtime.Installation.Id, Environment.MachineName, ServiceAccountName(), runtime.Paths.Data, runtime.Paths.Logs,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, runtime.Installation.InstalledAt)));

        dispatcher.Register<EmptyParams, SettingsResult>(RpcMethods.SettingsGet, async (_, _, ct) =>
            ToDto(await runtime.Settings.GetAsync(ct).ConfigureAwait(false)));

        dispatcher.Register<SettingsUpdateParams, SettingsResult>(RpcMethods.SettingsUpdate, async (ctx, p, ct) =>
        {
            var s = p.Settings;
            var model = new EngineSettings
            {
                RunHeaderRetentionDays = s.RunHeaderRetentionDays,
                SucceededLogRetentionDays = s.SucceededLogRetentionDays,
                FailedLogRetentionDays = s.FailedLogRetentionDays,
                RejectRetentionDays = s.RejectRetentionDays,
                DeduplicationRetentionDays = s.DeduplicationRetentionDays,
                LogLevel = s.LogLevel,
                Language = s.Language,
                ScriptsEnabled = s.ScriptsEnabled,
            };
            return ToDto(await Guard(() => runtime.Settings.UpdateAsync(ctx.Caller, model, p.ExpectedVersion, ctx.CorrelationId, ct)).ConfigureAwait(false));
        });

        dispatcher.Register<EmptyParams, ListAssignmentsResult>(RpcMethods.UsersListAssignments, async (_, _, ct) =>
            new ListAssignmentsResult((await runtime.Users.ListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToArray()));

        dispatcher.Register<AssignRoleParams, RoleAssignmentDto>(RpcMethods.UsersAssignRole, async (ctx, p, ct) =>
        {
            var role = ParseRole(p.Role);
            var kind = p.PrincipalKind switch
            {
                "user" => PrincipalKind.User,
                "group" => PrincipalKind.Group,
                _ => throw RpcException.Validation([new FieldErrorDto("principalKind", "Valeurs admises : user, group.")]),
            };
            return ToDto(await Guard(() => runtime.Users.AssignAsync(ctx.Caller, p.PrincipalSid, p.PrincipalName, kind, role, ctx.CorrelationId, ct))
                .ConfigureAwait(false));
        });

        dispatcher.Register<RevokeRoleParams, RevokeRoleResult>(RpcMethods.UsersRevokeRole, async (ctx, p, ct) =>
            new RevokeRoleResult(await Guard(() => runtime.Users.RevokeAsync(ctx.Caller, p.AssignmentId, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<ResolvePrincipalParams, ResolvePrincipalResult>(RpcMethods.UsersResolvePrincipal, (_, p, _) =>
        {
            var r = runtime.Users.Resolve(p.Name);
            return Task.FromResult(r is null
                ? new ResolvePrincipalResult(false, null, null, null)
                : new ResolvePrincipalResult(true, r.Sid, r.Name, r.Kind == PrincipalKind.Group ? "group" : "user"));
        });

        dispatcher.Register<AuditQueryParams, AuditQueryResult>(RpcMethods.AuditQuery, async (_, p, ct) =>
        {
            var errors = new List<FieldErrorDto>();
            if (p.Page < 1)
            {
                errors.Add(new FieldErrorDto("page", "Minimum 1."));
            }

            if (p.PageSize is < 1 or > 200)
            {
                errors.Add(new FieldErrorDto("pageSize", "Entre 1 et 200."));
            }

            if (errors.Count > 0)
            {
                throw RpcException.Validation(errors);
            }

            var page = await runtime.Audit.QueryAsync(new AuditQuery(p.Page, p.PageSize, p.Action, p.From, p.To), ct).ConfigureAwait(false);
            return new AuditQueryResult(page.Items.Select(ToDto).ToArray(), page.Total, p.Page, p.PageSize);
        });

        dispatcher.Register<EmptyParams, AuditVerifyResult>(RpcMethods.AuditVerifyChain, async (_, _, ct) =>
        {
            var v = await runtime.Audit.VerifyChainAsync(ct).ConfigureAwait(false);
            return new AuditVerifyResult(v.Valid, v.CheckedCount, v.FirstInvalidSequence, v.Reason);
        });

        dispatcher.Register<EmptyParams, VaultStatusResult>(RpcMethods.VaultGetStatus, async (_, _, ct) =>
            new VaultStatusResult(runtime.Vault.IsInitialized, runtime.Vault.ActiveKeyVersion,
                await runtime.Secrets.CountAsync(ct).ConfigureAwait(false), runtime.Vault.ProtectionDescription));

        dispatcher.Register<EventsSubscribeParams, EventsSubscribeResult>(RpcMethods.EventsSubscribe, (ctx, p, _) =>
        {
            var topics = ValidateTopics(p.Topics);
            foreach (var t in topics)
            {
                if (!ctx.Permissions.Contains(t.Permission))
                {
                    throw new RpcException(RpcErrorCodes.Forbidden, $"Permission refusée pour {t.Name}.");
                }
            }

            foreach (var t in topics)
            {
                ctx.Session.Subscriptions[t.Name] = 0;
            }

            return Task.FromResult(new EventsSubscribeResult(ctx.Session.Subscriptions.Keys.Order(StringComparer.Ordinal).ToArray()));
        });

        dispatcher.Register<EventsSubscribeParams, EventsSubscribeResult>(RpcMethods.EventsUnsubscribe, (ctx, p, _) =>
        {
            foreach (var t in ValidateTopics(p.Topics))
            {
                ctx.Session.Subscriptions.TryRemove(t.Name, out byte _);
            }

            return Task.FromResult(new EventsSubscribeResult(ctx.Session.Subscriptions.Keys.Order(StringComparer.Ordinal).ToArray()));
        });

        RegisterFlows(dispatcher, runtime);
        RegisterDesign(dispatcher, runtime);
        RegisterScheduling(dispatcher, runtime);
        RegisterScripts(dispatcher, runtime);
    }

    private static void RegisterFlows(RpcDispatcher dispatcher, EngineRuntime runtime)
    {
        dispatcher.Register<EmptyParams, ConnectorsListResult>(RpcMethods.ConnectorsList, (_, _, _) => Task.FromResult(new ConnectorsListResult(
            runtime.Connections.Connectors.Select(c => new ConnectorDto(
                c.Manifest.Id, c.Manifest.Name, c.Manifest.Version, c.Manifest.Description,
                Enum.GetValues<ConnectorCapabilities>().Where(f => f != ConnectorCapabilities.None && c.Manifest.Capabilities.HasFlag(f)).Select(f => JsonNamingPolicy.CamelCase.ConvertName(f.ToString())).ToArray(),
                c.Manifest.ConfigSchema.Select(ToDto).ToArray())).ToArray(),
            ErpConnect.Connectors.Formats.FormatRegistry.All.Select(f => new FormatDto(f.Id, f.Options.Select(ToDto).ToArray())).ToArray())));

        dispatcher.Register<EmptyParams, ConnectionsListResult>(RpcMethods.ConnectionsList, async (_, _, ct) =>
            new ConnectionsListResult((await runtime.Connections.ListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToArray()));

        dispatcher.Register<ConnectionSaveParams, ConnectionDto>(RpcMethods.ConnectionsSave, async (ctx, p, ct) =>
        {
            var values = p.Settings.GroupBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
            var secrets = p.Secrets.GroupBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
            return ToDto(await Guard(() => runtime.Connections.SaveAsync(ctx.Caller, p.Id, p.Name, p.ConnectorId, values, secrets, p.ClearSecrets, p.ExpectedVersion, ctx.CorrelationId, ct)).ConfigureAwait(false));
        });

        dispatcher.Register<IdParams, DeleteResult>(RpcMethods.ConnectionsDelete, async (ctx, p, ct) =>
            new DeleteResult(await Guard(() => runtime.Connections.DeleteAsync(ctx.Caller, p.Id, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<IdParams, ConnectionTestResultDto>(RpcMethods.ConnectionsTest, async (ctx, p, ct) =>
        {
            var r = await Guard(() => runtime.Connections.TestAsync(ctx.Caller, p.Id, ctx.CorrelationId, ct)).ConfigureAwait(false);
            return new ConnectionTestResultDto(r.Success, r.Message, (long)r.Duration.TotalMilliseconds);
        });

        dispatcher.Register<EmptyParams, FlowsListResult>(RpcMethods.FlowsList, async (_, _, ct) =>
            new FlowsListResult((await runtime.Flows.ListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToArray()));

        dispatcher.Register<FlowGetParams, FlowDetailResult>(RpcMethods.FlowsGet, async (_, p, ct) =>
        {
            var flow = await runtime.Flows.GetAsync(p.Id, ct).ConfigureAwait(false) ?? throw new RpcException(RpcErrorCodes.NotFound, "Flux introuvable.");
            var version = await runtime.Flows.GetVersionAsync(p.Id, p.Version ?? flow.DraftVersion, ct).ConfigureAwait(false)
                ?? throw new RpcException(RpcErrorCodes.NotFound, "Version introuvable.");
            return new FlowDetailResult(ToDto(flow), version.Version, ToDto(FlowDefinition.Parse(version.DefinitionJson)), version.CreatedAt, version.CreatedBy, version.PublishedAt);
        });

        dispatcher.Register<FlowSaveParams, FlowSummaryDto>(RpcMethods.FlowsSave, async (ctx, p, ct) =>
            ToDto(await Guard(() => runtime.Flows.SaveAsync(ctx.Caller, p.Id, p.Name, p.Description, FromDto(p.Definition), p.ExpectedDraftVersion, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<FlowPublishParams, FlowSummaryDto>(RpcMethods.FlowsPublish, async (ctx, p, ct) =>
            ToDto(await Guard(() => runtime.Flows.PublishAsync(ctx.Caller, p.Id, p.Version, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<RunStartParams, RunDto>(RpcMethods.RunsStart, async (ctx, p, ct) =>
        {
            if (p.UseDraft && !ctx.Permissions.Contains(Permissions.FlowsEdit))
            {
                throw new RpcException(RpcErrorCodes.Forbidden, "Tester une version non publiée exige le droit de modifier les flux.");
            }

            return ToDto(await Guard(() => runtime.Runs.StartAsync(ctx.Caller, p.FlowId, p.UseDraft, ctx.CorrelationId, ct)).ConfigureAwait(false));
        });

        dispatcher.Register<RunsListParams, RunsListResult>(RpcMethods.RunsList, async (_, p, ct) =>
        {
            if (p.Page < 1 || p.PageSize is < 1 or > 200)
            {
                throw RpcException.Validation([new FieldErrorDto("pageSize", "page ≥ 1, pageSize entre 1 et 200.")]);
            }

            RunStatus? status = p.Status is null ? null : ParseStatus(p.Status);
            var (items, total) = await runtime.Runs.QueryAsync(new RunQuery(p.FlowId, status, p.Page, p.PageSize), ct).ConfigureAwait(false);
            return new RunsListResult(items.Select(ToDto).ToArray(), total, p.Page, p.PageSize);
        });

        dispatcher.Register<IdParams, RunDetailResult>(RpcMethods.RunsGet, async (_, p, ct) =>
        {
            var run = await runtime.Runs.GetAsync(p.Id, ct).ConfigureAwait(false) ?? throw new RpcException(RpcErrorCodes.NotFound, "Exécution introuvable.");
            var rejects = await runtime.Runs.RejectsAsync(p.Id, 500, ct).ConfigureAwait(false);
            var logs = await runtime.Runs.LogsAsync(p.Id, 1000, ct).ConfigureAwait(false);
            return new RunDetailResult(ToDto(run),
                rejects.Select(r => new RunRejectDto(r.BatchNumber, r.RecordIndex, r.Stage, r.Reason, r.RecordJson, r.At)).ToArray(),
                logs.Select(l => new RunLogDto(l.At, l.Level, l.Message)).ToArray());
        });

        dispatcher.Register<IdParams, RunCancelResult>(RpcMethods.RunsCancel, async (ctx, p, ct) =>
        {
            var admin = (await runtime.Access.GetRolesAsync(ctx.Caller, ct).ConfigureAwait(false)).Contains(Role.Administrator);
            return new RunCancelResult(await Guard(() => runtime.Runs.CancelAsync(ctx.Caller, p.Id, admin, ctx.CorrelationId, ct)).ConfigureAwait(false));
        });
    }

    private static void RegisterDesign(RpcDispatcher dispatcher, EngineRuntime runtime)
    {
        dispatcher.Register<ConnectionDiscoverParams, ConnectionDiscoverResult>(RpcMethods.ConnectionsDiscover, async (_, p, ct) =>
        {
            var s = await Guard(() => runtime.Connections.DiscoverAsync(p.Id, p.Resource, ct)).ConfigureAwait(false);
            return new ConnectionDiscoverResult(s.Resources.Take(1000).ToArray(), s.Fields.Select(ToDto).ToArray());
        });

        dispatcher.Register<FlowPreviewParams, FlowPreviewResult>(RpcMethods.FlowsPreview, async (_, p, ct) =>
        {
            var definition = FromDto(p.Definition);
            if (FlowService.UsesScripts(definition))
            {
                await RequireScriptsAsync(runtime, ct).ConfigureAwait(false);
            }

            var r = await Guard(() => runtime.Preview.PreviewAsync(definition, p.Limit, ct)).ConfigureAwait(false);
            return new FlowPreviewResult(
                r.Rows.Select(x => new PreviewRowDto(x.SourceJson, x.OutputJson, JsonNamingPolicy.CamelCase.ConvertName(x.Fate.ToString()), x.Reason)).ToArray(),
                r.SourceFields.ToArray());
        });

        dispatcher.Register<EmptyParams, FunctionsListResult>(RpcMethods.FlowsFunctions, (_, _, _) => Task.FromResult(new FunctionsListResult(
            ExpressionCompiler.FunctionCatalog.Select(f => new FunctionDto(f.Name, f.Signature, f.Help)).ToArray())));
    }

    private static async Task RequireScriptsAsync(EngineRuntime runtime, CancellationToken ct)
    {
        if (!await runtime.Flows.ScriptsAllowedAsync(ct).ConfigureAwait(false))
        {
            throw new RpcException(RpcErrorCodes.EntitlementDenied, "Les scripts sont désactivés dans les paramètres.");
        }
    }

    private static void RegisterScripts(RpcDispatcher dispatcher, EngineRuntime runtime)
    {
        dispatcher.Register<ScriptTestParams, ScriptTestResult>(RpcMethods.ScriptsTest, async (_, p, ct) =>
        {
            await RequireScriptsAsync(runtime, ct).ConfigureAwait(false);
            Connectors.Sdk.Record record;
            try
            {
                record = ScriptStep.ParseRecord(p.RecordJson);
            }
            catch (ScriptException ex)
            {
                throw RpcException.Validation([new FieldErrorDto("recordJson", ex.Message)]);
            }

            ScriptStep script;
            try
            {
                script = ScriptStep.Compile(p.Code);
            }
            catch (ScriptException ex)
            {
                throw RpcException.Validation([new FieldErrorDto("code", ex.Message)]);
            }

            using (script)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var r = script.Apply(record);
                    return new ScriptTestResult(
                        JsonNamingPolicy.CamelCase.ConvertName(r.Fate.ToString()),
                        r.Output is null ? null : Connectors.Sdk.RecordJson.Serialize(r.Output, 64 * 1024),
                        r.RejectReason,
                        sw.ElapsedMilliseconds);
                }
                catch (Connectors.Sdk.ConnectorException ex)
                {
                    return new ScriptTestResult("stopped", null, ex.Message, sw.ElapsedMilliseconds);
                }
            }
        });

        dispatcher.Register<EmptyParams, ScriptLimitsResult>(RpcMethods.ScriptsLimits, (_, _, _) =>
        {
            var l = ScriptLimits.Default;
            return Task.FromResult(new ScriptLimitsResult((int)l.TimeoutPerRecord.TotalMilliseconds, l.MemoryBytes, l.MaxStatements, l.MaxRecursion, l.MaxArraySize, ScriptLimits.MaxSourceLength));
        });
    }

    private static void RegisterScheduling(RpcDispatcher dispatcher, EngineRuntime runtime)
    {
        dispatcher.Register<IdParams, RunDto>(RpcMethods.RunsResume, async (ctx, p, ct) =>
            ToDto(await Guard(() => runtime.Runs.ResumeAsync(ctx.Caller, p.Id, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<EmptyParams, SchedulesListResult>(RpcMethods.SchedulesList, async (_, _, ct) =>
            new SchedulesListResult((await runtime.Scheduler.ListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToArray()));

        dispatcher.Register<ScheduleSaveParams, ScheduleDto>(RpcMethods.SchedulesSave, async (ctx, p, ct) =>
            ToDto(await Guard(() => runtime.Scheduler.SaveAsync(ctx.Caller, p.FlowId, p.Cron, p.TimeZone, p.Enabled, p.MaxRetries, p.RetryDelaySeconds, p.ExpectedVersion, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<IdParams, DeleteResult>(RpcMethods.SchedulesDelete, async (ctx, p, ct) =>
            new DeleteResult(await Guard(() => runtime.Scheduler.DeleteAsync(ctx.Caller, p.Id, ctx.CorrelationId, ct)).ConfigureAwait(false)));

        dispatcher.Register<AlertsListParams, AlertsListResult>(RpcMethods.AlertsList, async (_, p, ct) =>
        {
            if (p.Limit is < 1 or > 500)
            {
                throw RpcException.Validation([new FieldErrorDto("limit", "Entre 1 et 500.")]);
            }

            return new AlertsListResult((await runtime.Alerts.ListAsync(p.OpenOnly, p.Limit, ct).ConfigureAwait(false))
                .Select(a => new AlertDto(a.Id, a.FlowId, a.RunId, a.Severity, a.Message, a.CreatedAt, a.AcknowledgedAt, a.AcknowledgedBy)).ToArray());
        });

        dispatcher.Register<AlertAcknowledgeParams, AlertAcknowledgeResult>(RpcMethods.AlertsAcknowledge, async (ctx, p, ct) =>
        {
            var done = await runtime.Alerts.AcknowledgeAsync(p.Id, ctx.Caller.UserName, runtime.Clock.UtcNow, ct).ConfigureAwait(false);
            if (done)
            {
                await runtime.Audit.AppendAsync(new Core.Audit.AuditEntry(ctx.Caller.UserSid, ctx.Caller.UserName, "alerts.acknowledge", $"alert/{p.Id}",
                    Core.Audit.AuditOutcomes.Success, null, ctx.CorrelationId), ct).ConfigureAwait(false);
            }

            return new AlertAcknowledgeResult(done);
        });
    }

    private static ScheduleDto ToDto(ScheduleRecord s) => new(
        s.FlowId, s.Cron, s.TimeZone, s.Enabled, s.MaxRetries, s.RetryDelaySeconds, s.Version, s.UpdatedAt, s.UpdatedBy, s.NextRunAt, s.LastRunId,
        s.RetryAttempt == int.MaxValue ? s.MaxRetries : s.RetryAttempt, s.RetryAt);

    private static DiscoveredFieldDto ToDto(ErpConnect.Connectors.Sdk.FieldInfo f) =>
        new(f.Name, f.Type, f.Children?.Select(ToDto).ToArray() ?? []);

    internal static string StatusName(RunStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    private static RunStatus ParseStatus(string s) =>
        Enum.GetValues<RunStatus>().FirstOrDefault(v => StatusName(v) == s) is var v && StatusName(v) == s
            ? v
            : throw RpcException.Validation([new FieldErrorDto("status", "Statut inconnu.")]);

    private static ConfigFieldDto ToDto(ConfigField f) => new(
        f.Name, f.Label, JsonNamingPolicy.CamelCase.ConvertName(f.Type.ToString()), f.Required, f.Secret, f.Default, f.Choices?.ToArray() ?? [], f.Help);

    private static ConnectionDto ToDto(ConnectionRecord c) => new(
        c.Id, c.Name, c.ConnectorId, c.Settings.Select(kv => new KeyValueDto(kv.Key, kv.Value)).ToArray(), c.SecretFields.ToArray(), c.Version, c.UpdatedAt, c.UpdatedBy);

    private static FlowSummaryDto ToDto(FlowRecord f) => new(f.Id, f.Name, f.Description, f.DraftVersion, f.PublishedVersion, f.UpdatedAt, f.UpdatedBy);

    private static RunDto ToDto(RunRecord r) => new(
        r.Id, r.FlowId, r.FlowVersion, StatusName(r.Status), r.Status.ToFrenchLabel(), r.TriggeredBy, r.ParentRunId, r.CreatedAt, r.StartedAt, r.EndedAt,
        r.ReadCount, r.WrittenCount, r.RejectedCount, r.SkippedCount, r.Error, r.Trigger, r.ErrorKind);

    private static KeyValueDto[] ToDto(OptionEntry[] o) => o.Select(e => new KeyValueDto(e.Key, e.Value)).ToArray();

    private static OptionEntry[] FromDto(KeyValueDto[] o) => o.Where(e => e.Value is not null).Select(e => new OptionEntry(e.Key, e.Value!)).ToArray();

    internal static FlowDefinitionDto ToDto(FlowDefinition d) => new(
        new SourceSpecDto(d.Source.ConnectionId, d.Source.Resource, ToDto(d.Source.Options)),
        new TargetSpecDto(d.Target.ConnectionId, d.Target.Resource, d.Target.Mode, ToDto(d.Target.Options)),
        new MappingSpecDto(d.Mapping.Mode, d.Mapping.Fields.Select(f => new FieldMapDto(f.Target, f.Expression)).ToArray()),
        d.BatchSize, d.MaxRejects, d.AllSteps.Select(s => new StepSpecDto(s.Kind, s.Field, s.Expression, s.Target, s.Message)).ToArray(), d.DedupKey);

    internal static FlowDefinition FromDto(FlowDefinitionDto d) => new(
        new SourceSpec(d.Source.ConnectionId, d.Source.Resource, FromDto(d.Source.Options)),
        new TargetSpec(d.Target.ConnectionId, d.Target.Resource, d.Target.Mode, FromDto(d.Target.Options)),
        new MappingSpec(d.Mapping.Mode, d.Mapping.Fields.Select(f => new FieldMap(f.Target, f.Expression)).ToArray()),
        d.BatchSize, d.MaxRejects, d.Steps is null or { Length: 0 } ? null : d.Steps.Select(s => new StepSpec(s.Kind, s.Field, s.Expression, s.Target, s.Message)).ToArray(),
        string.IsNullOrWhiteSpace(d.DedupKey) ? null : d.DedupKey);

    private static List<RpcNotificationDescriptor> ValidateTopics(string[] topics)
    {
        if (topics.Length is 0 or > 16)
        {
            throw RpcException.Validation([new FieldErrorDto("topics", "Entre 1 et 16 sujets.")]);
        }

        var list = new List<RpcNotificationDescriptor>();
        foreach (var name in topics)
        {
            list.Add(RpcNotifications.Find(name)
                ?? throw RpcException.Validation([new FieldErrorDto("topics", $"Sujet inconnu : {Truncate(name)}.")]));
        }

        return list;
    }

    private static string Truncate(string s) => s.Length > 64 ? s[..64] : s;

    private static Role ParseRole(string role) => role switch
    {
        "administrator" => Role.Administrator,
        "designer" => Role.Designer,
        "operator" => Role.Operator,
        "reader" => Role.Reader,
        "auditor" => Role.Auditor,
        _ => throw RpcException.Validation([new FieldErrorDto("role", "Valeurs admises : administrator, designer, operator, reader, auditor.")]),
    };

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (EngineException ex)
        {
            throw ex.Kind switch
            {
                EngineErrorKind.Validation => RpcException.Validation(ex.Fields.Select(f => new FieldErrorDto(f.Field, f.Message)).ToArray()),
                EngineErrorKind.Conflict => RpcException.Conflict(ex.Message),
                EngineErrorKind.NotFound => new RpcException(RpcErrorCodes.NotFound, ex.Message),
                EngineErrorKind.Forbidden => new RpcException(RpcErrorCodes.Forbidden, ex.Message),
                _ => new RpcException(RpcErrorCodes.InternalError, "Erreur interne."),
            };
        }
    }

    private static string ServiceAccountName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return id.Name;
        }

        return Environment.UserName;
    }

    private static SettingsResult ToDto(VersionedSettings v) => new(
        new SettingsDto(
            v.Settings.RunHeaderRetentionDays, v.Settings.SucceededLogRetentionDays, v.Settings.FailedLogRetentionDays,
            v.Settings.RejectRetentionDays, v.Settings.DeduplicationRetentionDays, v.Settings.LogLevel, v.Settings.Language, v.Settings.ScriptsEnabled),
        v.Version, v.UpdatedAt, v.UpdatedBy);

    private static RoleAssignmentDto ToDto(RoleAssignment a) => new(
        a.Id, a.PrincipalSid, a.PrincipalName, a.PrincipalKind == PrincipalKind.Group ? "group" : "user",
        a.Role.ToString().ToLowerInvariant(), a.ScopeKind.ToString().ToLowerInvariant(), a.CreatedAt, a.CreatedBy);

    private static AuditEventDto ToDto(AuditEvent e) => new(
        e.Sequence, e.Timestamp, e.ActorSid, e.ActorName, e.Action, e.Target, e.Outcome, e.DetailsJson, e.CorrelationId, e.Hash);

    internal static JsonSerializerOptions Json => RpcDispatcher.SerializerOptions;
}

/// <summary>Adaptateurs entre le moteur et le répartiteur RPC.</summary>
internal sealed class RuntimeAuthorizer : IRpcAuthorizer
{
    private readonly EngineRuntime _runtime;

    public RuntimeAuthorizer(EngineRuntime runtime) => _runtime = runtime;

    public Task<IReadOnlySet<string>> GetPermissionsAsync(CallerIdentity caller, CancellationToken ct) => _runtime.Access.GetPermissionsAsync(caller, ct);

    public Task<IReadOnlySet<Role>> GetRolesAsync(CallerIdentity caller, CancellationToken ct) => _runtime.Access.GetRolesAsync(caller, ct);
}

internal sealed class RuntimeAuditSink : IRpcAuditSink
{
    private readonly EngineRuntime _runtime;

    public RuntimeAuditSink(EngineRuntime runtime) => _runtime = runtime;

    public async Task DeniedAsync(CallerIdentity caller, string method, string correlationId, CancellationToken ct) =>
        await _runtime.Audit.AppendAsync(new AuditEntry(
            caller.UserSid, caller.UserName, method, "rpc", AuditOutcomes.Denied, null, correlationId), ct).ConfigureAwait(false);
}
