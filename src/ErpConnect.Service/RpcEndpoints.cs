using System.Text.Json;
using ErpConnect.Contracts;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Settings;
using ErpConnect.Engine;
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
    }

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
            v.Settings.RejectRetentionDays, v.Settings.DeduplicationRetentionDays, v.Settings.LogLevel, v.Settings.Language),
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
