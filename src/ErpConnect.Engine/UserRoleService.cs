using System.Text.Json;
using System.Text.RegularExpressions;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Settings;

namespace ErpConnect.Engine;

/// <summary>Attribution des rôles à des comptes ou groupes Windows. Chaque changement est audité.</summary>
public sealed partial class UserRoleService : IDisposable
{
    private readonly IRoleAssignmentRepository _repository;
    private readonly IAuditLog _audit;
    private readonly IPrincipalResolver _resolver;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserRoleService(IRoleAssignmentRepository repository, IAuditLog audit, IPrincipalResolver resolver, IClock clock)
    {
        _repository = repository;
        _audit = audit;
        _resolver = resolver;
        _clock = clock;
    }

    public Task<IReadOnlyList<RoleAssignment>> ListAsync(CancellationToken ct) => _repository.ListAsync(ct);

    public ResolvedPrincipal? Resolve(string name) => _resolver.Resolve(name);

    public async Task<RoleAssignment> AssignAsync(
        CallerIdentity caller, string principalSid, string principalName, PrincipalKind kind, Role role, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(principalSid) || !SidFormat().IsMatch(principalSid))
        {
            errors.Add(new FieldError("principalSid", "SID Windows invalide."));
        }

        if (string.IsNullOrWhiteSpace(principalName) || principalName.Length > 256)
        {
            errors.Add(new FieldError("principalName", "Nom invalide."));
        }

        if (errors.Count > 0)
        {
            throw new EngineException(EngineErrorKind.Validation, "Paramètres invalides.", errors);
        }

        var assignment = await _repository.AddAsync(
            new RoleAssignment(0, principalSid, principalName.Trim(), kind, role, ScopeKind.Global, null, _clock.UtcNow, caller.UserName), ct)
            .ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(
            caller.UserSid, caller.UserName, "users.assignRole", $"principal/{principalSid}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { role = role.ToString(), principalName, assignmentId = assignment.Id }), correlationId), ct)
            .ConfigureAwait(false);
        return assignment;
    }

    public async Task<bool> RevokeAsync(CallerIdentity caller, long assignmentId, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await _repository.ListAsync(ct).ConfigureAwait(false);
            var target = all.FirstOrDefault(a => a.Id == assignmentId)
                ?? throw new EngineException(EngineErrorKind.NotFound, "Attribution introuvable.");

            if (target.Role == Role.Administrator && target.ScopeKind == ScopeKind.Global
                && all.Count(a => a.Role == Role.Administrator && a.ScopeKind == ScopeKind.Global) <= 1)
            {
                await _audit.AppendAsync(new AuditEntry(
                    caller.UserSid, caller.UserName, "users.revokeRole", $"principal/{target.PrincipalSid}", AuditOutcomes.Denied,
                    JsonSerializer.Serialize(new { reason = "last-administrator", assignmentId }), correlationId), ct).ConfigureAwait(false);
                throw new EngineException(EngineErrorKind.Conflict, "Impossible de retirer le dernier Administrateur.");
            }

            var removed = await _repository.RemoveAsync(assignmentId, ct).ConfigureAwait(false);
            await _audit.AppendAsync(new AuditEntry(
                caller.UserSid, caller.UserName, "users.revokeRole", $"principal/{target.PrincipalSid}", AuditOutcomes.Success,
                JsonSerializer.Serialize(new { role = target.Role.ToString(), assignmentId }), correlationId), ct).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [GeneratedRegex(@"^S-1-[0-9]{1,14}(-[0-9]{1,10}){1,15}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SidFormat();
}
